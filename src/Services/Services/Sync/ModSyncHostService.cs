using System.IO;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

using System.Collections.Concurrent;
using System.Net;

namespace ModManager.Services.Sync;

/// <summary>
/// Serves the host's shared mod set to friends over HTTPS, from inside the mod manager.
/// Endpoints (all require "Authorization: Bearer &lt;token&gt;"):
///   GET /api/v1/manifest        - the <see cref="SyncManifest"/>
///   GET /api/v1/files/{sha256}  - raw pak bytes; supports HTTP Range for resuming
/// Only files listed in the current manifest can be requested, and only by hash, so no path ever comes from the client.
/// </summary>
public sealed class ModSyncHostService : IAsyncDisposable
{
	private sealed record SharedFile(string FullPath, long Size, long LastWriteUtcTicks);
	private sealed record Snapshot(SyncManifest Manifest, Dictionary<string, SharedFile> FilesBySha);

	private sealed class FailureWindow
	{
		public int Count;
		public DateTime WindowStart = DateTime.UtcNow;
		public DateTime BlockedUntil = DateTime.MinValue;
	}

	private const int MaxAuthFailures = 10;
	private static readonly TimeSpan FailureWindowLength = TimeSpan.FromMinutes(10);

	private readonly ModSyncIdentity _identity;
	private readonly FileHashCache _hashes;
	private readonly ConcurrentDictionary<IPAddress, FailureWindow> _failures = new();
	private readonly SemaphoreSlim _lifecycle = new(1, 1);
	private volatile Snapshot _snapshot = new(new SyncManifest(), []);
	private WebApplication? _app;
	private NatPortMapper? _nat;

	public ModSyncHostService(ModSyncIdentity identity, FileHashCache hashes)
	{
		_identity = identity;
		_hashes = hashes;
	}

	public ModSyncIdentity Identity => _identity;
	public bool IsRunning => _app != null;
	public int Port { get; private set; }
	public SyncManifest Manifest => _snapshot.Manifest;
	public NatPortMapper? Nat => _nat;

	/// <summary>Human-readable activity log lines (connections, downloads, errors). Raised on a background thread.</summary>
	public event Action<string>? Activity;

	private void Report(string message)
	{
		DivinityApp.Log($"[ModSync] {message}");
		Activity?.Invoke($"{DateTime.Now:HH:mm:ss}  {message}");
	}

	/// <summary>Rebuilds the shared manifest from the given mod files (hashing new/changed ones) and load order.</summary>
	public async Task<SyncManifest> UpdateSharedSetAsync(IReadOnlyList<SharedModFile> files, IReadOnlyList<SyncOrderEntry> order, CancellationToken token)
	{
		List<SyncFileEntry> entries = [];
		Dictionary<string, SharedFile> bySha = [];
		var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

		foreach (var file in files)
		{
			token.ThrowIfCancellationRequested();
			var info = new FileInfo(file.FullPath);
			if (!info.Exists) continue;
			if (!ModSyncValidation.IsSafePakFileName(info.Name))
			{
				Report($"Not sharing '{info.Name}': the file name isn't valid on Windows. Rename it to share it.");
				continue;
			}
			if (!names.Add(info.Name)) continue;

			var sha = await _hashes.GetSha256Async(info.FullName, token);
			if (bySha.ContainsKey(sha)) continue; // identical copy under another name
			bySha[sha] = new SharedFile(info.FullName, info.Length, info.LastWriteTimeUtc.Ticks);
			entries.Add(new SyncFileEntry(info.Name, info.Length, sha, file.Uuid, file.Name, file.Version));
		}
		_hashes.Save();

		var manifest = new SyncManifest
		{
			HostName = Environment.MachineName,
			Created = DateTimeOffset.UtcNow,
			Files = entries,
			LoadOrder = [.. order],
		};
		_snapshot = new Snapshot(manifest, bySha);
		return manifest;
	}

	public async Task StartAsync(CancellationToken token)
	{
		await _lifecycle.WaitAsync(token);
		try
		{
			if (_app != null) return;
			var port = _identity.Settings.Port;

			var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
			{
				Args = [],
				ContentRootPath = AppContext.BaseDirectory,
				EnvironmentName = "Production",
			});
			builder.Logging.ClearProviders();
			builder.WebHost.ConfigureKestrel(k =>
			{
				k.AddServerHeader = false;
				k.Limits.MaxRequestBodySize = 0; // clients never upload anything
				k.Limits.MaxConcurrentConnections = 64;
				k.ListenAnyIP(port, l => l.UseHttps(_identity.Certificate));
			});

			var app = builder.Build();
			app.Use(AuthorizeAsync);
			app.MapGet("/api/v1/manifest", (HttpContext ctx) =>
			{
				var manifest = _snapshot.Manifest;
				Report($"{ClientIp(ctx)} fetched the mod list ({manifest.Files.Count} files).");
				return Results.Json(manifest);
			});
			app.MapGet("/api/v1/files/{sha}", (string sha, HttpContext ctx) =>
			{
				if (!ModSyncValidation.IsValidSha256(sha) || !_snapshot.FilesBySha.TryGetValue(sha, out var file)) return Results.NotFound();
				var info = new FileInfo(file.FullPath);
				if (!info.Exists || info.Length != file.Size || info.LastWriteTimeUtc.Ticks != file.LastWriteUtcTicks)
				{
					// The file changed after the manifest was built; make the client refetch the manifest.
					return Results.Conflict("File changed on host; refresh and try again.");
				}
				if (!ctx.Request.Headers.Range.Any() || ctx.Request.Headers.Range.ToString().StartsWith("bytes=0-"))
				{
					Report($"{ClientIp(ctx)} is downloading '{info.Name}'.");
				}
				return Results.File(info.FullName, "application/octet-stream", info.Name, enableRangeProcessing: true);
			});

			try
			{
				await app.StartAsync(token);
			}
			catch (IOException ex)
			{
				await app.DisposeAsync();
				throw new InvalidOperationException($"Couldn't listen on port {port}. Is another program (or another copy of the mod manager) using it? ({ex.Message})", ex);
			}
			_app = app;
			Port = port;
			Report($"Hosting on port {port}.");

			if (_identity.Settings.UseNatPortMapping)
			{
				_nat = new NatPortMapper();
				// Don't block start-up on router discovery.
				_ = Task.Run(async () =>
				{
					try
					{
						await _nat.MapAsync(port, TimeSpan.FromSeconds(6), CancellationToken.None);
					}
					catch (Exception ex)
					{
						DivinityApp.Log($"NAT mapping failed: {ex}");
					}
					Report(_nat.Status);
					NatStatusChanged?.Invoke();
				});
			}
		}
		finally
		{
			_lifecycle.Release();
		}
	}

	/// <summary>Raised once router port mapping finishes (successfully or not).</summary>
	public event Action? NatStatusChanged;

	private static IPAddress ClientIp(HttpContext ctx)
	{
		var ip = ctx.Connection.RemoteIpAddress ?? IPAddress.None;
		return ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip;
	}

	private async Task AuthorizeAsync(HttpContext ctx, Func<Task> next)
	{
		var ip = ClientIp(ctx);
		var window = _failures.GetOrAdd(ip, _ => new FailureWindow());

		lock (window)
		{
			if (window.BlockedUntil > DateTime.UtcNow)
			{
				ctx.Response.StatusCode = StatusCodes.Status429TooManyRequests;
				return;
			}
		}

		var header = ctx.Request.Headers.Authorization.ToString();
		var presented = header.StartsWith("Bearer ", StringComparison.Ordinal) ? header["Bearer ".Length..].Trim() : null;
		if (!_identity.IsValidToken(presented))
		{
			var blocked = false;
			lock (window)
			{
				if (DateTime.UtcNow - window.WindowStart > FailureWindowLength)
				{
					window.WindowStart = DateTime.UtcNow;
					window.Count = 0;
				}
				if (++window.Count >= MaxAuthFailures)
				{
					window.BlockedUntil = DateTime.UtcNow + FailureWindowLength;
					blocked = true;
				}
			}
			Report(blocked
				? $"Blocked {ip} for 10 minutes after repeated invalid invite codes."
				: $"Rejected a connection from {ip} (invalid or outdated invite code).");
			await Task.Delay(500); // slow down guessing
			ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
			return;
		}
		await next();
	}

	/// <summary>Addresses to put in an invite: detected VPN/LAN addresses, the router's public IP, then user-added ones.</summary>
	public List<HostAddress> GetInviteAddresses()
	{
		var settings = _identity.Settings;
		List<HostAddress> list = [.. NetworkAddresses.GetLocalAddresses().Where(a => !settings.HiddenAddresses.Contains(a.Address))];
		if (_nat?.ExternalAddress is IPAddress ext) list.Add(new HostAddress(ext.ToString(), HostAddressKind.Internet, "Router (UPnP/NAT-PMP)"));
		list.AddRange(settings.ExtraAddresses.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => new HostAddress(x.Trim(), HostAddressKind.Custom, "Added manually")));
		return [.. list.DistinctBy(x => x.Address)];
	}

	public ModSyncInvite CreateInvite() => new(
		[.. GetInviteAddresses().Select(x => x.Address)],
		Port > 0 ? Port : _identity.Settings.Port,
		_identity.Settings.Token,
		_identity.Fingerprint,
		Environment.MachineName);

	public async Task StopAsync()
	{
		await _lifecycle.WaitAsync();
		try
		{
			if (_nat != null)
			{
				await _nat.DisposeAsync();
				_nat = null;
			}
			if (_app != null)
			{
				using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
				try { await _app.StopAsync(cts.Token); } catch (Exception ex) { DivinityApp.Log($"Error stopping sync host: {ex.Message}"); }
				await _app.DisposeAsync();
				_app = null;
				Report("Stopped hosting.");
			}
			Port = 0;
		}
		finally
		{
			_lifecycle.Release();
		}
	}

	public async ValueTask DisposeAsync() => await StopAsync();
}
