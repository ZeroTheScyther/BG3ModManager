using System.IO;
using System.Net.Http;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Security;
using System.Text.Json;

namespace ModManager.Services.Sync;

public sealed class ModSyncConnectException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>A live connection to a host, established from an invite code.</summary>
public sealed class ModSyncConnection : IDisposable
{
	private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

	private readonly HttpClient _http;

	public ModSyncInvite Invite { get; }
	public string Address { get; }

	private ModSyncConnection(HttpClient http, ModSyncInvite invite, string address)
	{
		_http = http;
		Invite = invite;
		Address = address;
	}

	private static HttpClient CreateClient(ModSyncInvite invite, string address)
	{
		var handler = new SocketsHttpHandler
		{
			ConnectTimeout = TimeSpan.FromSeconds(6),
			AutomaticDecompression = DecompressionMethods.None,
			PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
			SslOptions = new SslClientAuthenticationOptions
			{
				// The host's certificate is self-signed; trust it only if it matches the fingerprint from the invite.
				RemoteCertificateValidationCallback = (_, cert, _, _) =>
					cert != null && string.Equals(ModSyncIdentity.GetFingerprint(cert), invite.Fingerprint, StringComparison.OrdinalIgnoreCase),
			},
		};
		var http = new HttpClient(handler)
		{
			BaseAddress = new Uri($"https://{NetworkAddresses.ToUriHost(address)}:{invite.Port}/"),
			Timeout = Timeout.InfiniteTimeSpan, // per-request timeouts below; downloads can take a long time
		};
		http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", invite.Token);
		return http;
	}

	/// <summary>Tries every address in the invite in parallel and keeps the first that answers with the pinned certificate.</summary>
	public static async Task<(ModSyncConnection Connection, SyncManifest Manifest)> ConnectAsync(ModSyncInvite invite, CancellationToken token)
	{
		using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
		List<string> errors = [];

		async Task<(ModSyncConnection, SyncManifest)> TryAddress(string address)
		{
			var http = CreateClient(invite, address);
			try
			{
				using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
				timeout.CancelAfter(TimeSpan.FromSeconds(10));
				var conn = new ModSyncConnection(http, invite, address);
				var manifest = await conn.GetManifestAsync(timeout.Token);
				return (conn, manifest);
			}
			catch
			{
				http.Dispose();
				throw;
			}
		}

		var pending = invite.Addresses.Select(a => (Address: a, Task: TryAddress(a))).ToList();
		while (pending.Count > 0)
		{
			var done = await Task.WhenAny(pending.Select(p => p.Task));
			var entry = pending.First(p => p.Task == done);
			pending.Remove(entry);
			if (done.IsCompletedSuccessfully)
			{
				cts.Cancel(); // abandon the slower addresses
				foreach (var p in pending) _ = p.Task.ContinueWith(t => { if (t.IsCompletedSuccessfully) t.Result.Item1.Dispose(); }, TaskScheduler.Default);
				return done.Result;
			}
			token.ThrowIfCancellationRequested();
			errors.Add($"{entry.Address}: {Describe(done.Exception?.GetBaseException())}");
		}
		throw new ModSyncConnectException(
			$"Couldn't reach {(invite.HostName.Length > 0 ? invite.HostName : "the host")} at any address in the invite:\n  " + string.Join("\n  ", errors) +
			"\n\nCheck that hosting is running on their end, that you're connected to the VPN, or that their port is forwarded.");
	}

	private static string Describe(Exception? ex) => ex switch
	{
		ModSyncConnectException m => m.Message,
		HttpRequestException { StatusCode: HttpStatusCode.Unauthorized } => "invite code was rejected (the host may have regenerated it)",
		HttpRequestException { StatusCode: HttpStatusCode.TooManyRequests } => "temporarily blocked after too many failed attempts",
		HttpRequestException { InnerException: System.Security.Authentication.AuthenticationException } => "the host's identity didn't match the invite (wrong PC, or the host reset its identity)",
		HttpRequestException h when h.InnerException is System.Net.Sockets.SocketException se => se.SocketErrorCode switch
		{
			System.Net.Sockets.SocketError.ConnectionRefused => "connection refused (hosting not running?)",
			System.Net.Sockets.SocketError.TimedOut or System.Net.Sockets.SocketError.HostUnreachable or System.Net.Sockets.SocketError.NetworkUnreachable => "unreachable",
			_ => se.Message,
		},
		OperationCanceledException => "timed out",
		InvalidDataException d => d.Message,
		null => "unknown error",
		_ => ex.Message,
	};

	public async Task<SyncManifest> GetManifestAsync(CancellationToken token)
	{
		using var response = await _http.GetAsync("api/v1/manifest", HttpCompletionOption.ResponseHeadersRead, token);
		response.EnsureSuccessStatusCode();
		if (response.Content.Headers.ContentLength > 64 * 1024 * 1024) throw new InvalidDataException("Manifest is unreasonably large.");
		var manifest = await response.Content.ReadFromJsonAsync<SyncManifest>(_json, token) ?? throw new InvalidDataException("Empty manifest.");
		ModSyncValidation.Validate(manifest);
		return manifest;
	}

	/// <summary>
	/// Downloads a file to <paramref name="partPath"/>, resuming if a partial file exists, and verifies its SHA-256.
	/// </summary>
	public async Task DownloadAsync(SyncFileEntry file, string partPath, Action<long> onBytes, CancellationToken token)
	{
		for (var attempt = 0; ; attempt++)
		{
			long existing = File.Exists(partPath) ? new FileInfo(partPath).Length : 0;
			if (existing > file.Size) { File.Delete(partPath); existing = 0; }

			if (existing < file.Size)
			{
				using var req = new HttpRequestMessage(HttpMethod.Get, $"api/v1/files/{file.Sha256}");
				if (existing > 0) req.Headers.Range = new RangeHeaderValue(existing, null);
				using var response = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, token);
				if (response.StatusCode == HttpStatusCode.Conflict)
					throw new InvalidOperationException($"'{file.FileName}' changed on the host while syncing. Click Connect again to refresh the list.");
				response.EnsureSuccessStatusCode();

				var append = existing > 0 && response.StatusCode == HttpStatusCode.PartialContent;
				if (!append) existing = 0;
				onBytes(existing);

				await using var input = await response.Content.ReadAsStreamAsync(token);
				await using var output = new FileStream(partPath, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, true);
				var buffer = new byte[1 << 20];
				int read;
				var total = existing;
				while ((read = await input.ReadAsync(buffer, token)) > 0)
				{
					total += read;
					if (total > file.Size) throw new InvalidDataException($"Host sent more data than expected for '{file.FileName}'.");
					await output.WriteAsync(buffer.AsMemory(0, read), token);
					onBytes(read);
				}
			}

			var sha = await FileHashCache.ComputeSha256Async(partPath, token);
			if (sha == file.Sha256) return;

			// Corrupt (or a stale partial from an older version of the file): start over once.
			File.Delete(partPath);
			if (attempt >= 1) throw new InvalidDataException($"'{file.FileName}' failed verification after downloading. Try again.");
			onBytes(-1); // negative = reset this file's progress to zero
		}
	}

	public void Dispose() => _http.Dispose();
}

public static class ModSyncPlanner
{
	/// <summary>
	/// Works out what to download, given the host's manifest and the paks currently in the client's Mods folder.
	/// Matching: identical contents (hash) = up to date; same mod UUID = update; same file name = replace; otherwise new.
	/// Local paks matched by nothing are "extras" (mods the host doesn't have).
	/// </summary>
	public static SyncPlan BuildPlan(SyncManifest manifest, IReadOnlyList<LocalModFile> local)
	{
		List<SyncAction> actions = [];
		var claimed = new HashSet<LocalModFile>(ReferenceEqualityComparer.Instance);

		var bySha = local.GroupBy(x => x.Sha256).ToDictionary(g => g.Key, g => g.ToList());
		var byUuid = local.Where(x => !string.IsNullOrEmpty(x.Uuid)).GroupBy(x => x.Uuid!, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
		var byName = local.GroupBy(x => x.FileName, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

		LocalModFile? Take(List<LocalModFile>? candidates)
		{
			var match = candidates?.FirstOrDefault(c => !claimed.Contains(c));
			if (match != null) claimed.Add(match);
			return match;
		}

		// Pass 1: exact content matches first, so a hash match is never stolen by a UUID/name match of another entry.
		Dictionary<SyncFileEntry, LocalModFile> upToDate = [];
		foreach (var remote in manifest.Files)
		{
			if (Take(bySha.GetValueOrDefault(remote.Sha256)) is LocalModFile same) upToDate[remote] = same;
		}

		foreach (var remote in manifest.Files)
		{
			if (upToDate.TryGetValue(remote, out var same))
			{
				actions.Add(new SyncAction(SyncActionKind.UpToDate, remote, same));
			}
			else if (remote.ModUuid != null && Take(byUuid.GetValueOrDefault(remote.ModUuid)) is LocalModFile older)
			{
				actions.Add(new SyncAction(SyncActionKind.Update, remote, older));
			}
			else if (Take(byName.GetValueOrDefault(remote.FileName)) is LocalModFile sameName)
			{
				actions.Add(new SyncAction(SyncActionKind.Replace, remote, sameName));
			}
			else
			{
				actions.Add(new SyncAction(SyncActionKind.Download, remote, null));
			}
		}

		foreach (var extra in local.Where(x => !claimed.Contains(x)))
		{
			actions.Add(new SyncAction(SyncActionKind.Extra, null, extra));
		}
		return new SyncPlan(manifest, actions);
	}
}

public static class ModSyncExecutor
{
	/// <summary>
	/// Applies a plan: downloads into <paramref name="workFolder"/>, verifies, then moves into the Mods folder.
	/// Local files being replaced (and extras, if <paramref name="moveExtrasToBackup"/>) are moved to a timestamped
	/// backup folder, never deleted.
	/// </summary>
	public static async Task<SyncResult> ExecuteAsync(ModSyncConnection connection, SyncPlan plan, string modsFolder, string workFolder, string backupRoot,
		bool moveExtrasToBackup, FileHashCache? hashes, IProgress<SyncProgress>? progress, CancellationToken token)
	{
		var downloads = plan.Downloads.ToList();
		var totalBytes = downloads.Sum(x => x.Remote!.Size);
		long doneBytes = 0;
		Directory.CreateDirectory(workFolder);
		Directory.CreateDirectory(modsFolder);

		string? backupFolder = null;
		var moved = 0;
		string Backup(string path)
		{
			backupFolder ??= Path.Join(backupRoot, DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss"));
			Directory.CreateDirectory(backupFolder);
			var dest = Path.Join(backupFolder, Path.GetFileName(path));
			for (var i = 1; File.Exists(dest); i++) dest = Path.Join(backupFolder, $"{Path.GetFileNameWithoutExtension(path)} ({i}){Path.GetExtension(path)}");
			File.Move(path, dest);
			moved++;
			return dest;
		}

		// 1. Download and verify everything before touching the Mods folder, so a failed sync leaves it untouched.
		for (var i = 0; i < downloads.Count; i++)
		{
			var remote = downloads[i].Remote!;
			var part = Path.Join(workFolder, remote.Sha256 + ".part");
			progress?.Report(new SyncProgress(remote.ModName ?? remote.FileName, i + 1, downloads.Count, doneBytes, totalBytes));
			var fileStart = doneBytes;
			await connection.DownloadAsync(remote, part, n =>
			{
				doneBytes = n < 0 ? fileStart : doneBytes + n;
				progress?.Report(new SyncProgress(remote.ModName ?? remote.FileName, i + 1, downloads.Count, doneBytes, totalBytes));
			}, token);
			doneBytes = fileStart + remote.Size;
		}

		token.ThrowIfCancellationRequested();

		// 2. Swap files in.
		var downloaded = 0;
		foreach (var action in downloads)
		{
			var remote = action.Remote!;
			if (action.Local != null && File.Exists(action.Local.FullPath)) Backup(action.Local.FullPath);

			var target = Path.Join(modsFolder, remote.FileName);
			// Anything else already at the target name (e.g. a case-only difference on Windows) gets backed up too.
			if (File.Exists(target)) Backup(target);

			var part = Path.Join(workFolder, remote.Sha256 + ".part");
			File.Move(part, target);
			hashes?.Set(target, remote.Sha256);
			downloaded++;
		}

		if (moveExtrasToBackup)
		{
			foreach (var extra in plan.Extras)
			{
				if (File.Exists(extra.Local!.FullPath)) Backup(extra.Local.FullPath);
			}
		}

		hashes?.Save();
		try { if (!Directory.EnumerateFileSystemEntries(workFolder).Any()) Directory.Delete(workFolder); } catch { /* best effort */ }
		progress?.Report(new SyncProgress("Done", downloads.Count, downloads.Count, totalBytes, totalBytes));
		return new SyncResult(downloaded, moved, backupFolder);
	}
}
