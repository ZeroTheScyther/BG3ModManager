using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text.Json;

namespace ModManager.Launcher;

internal sealed record ReleaseInfo(string Tag, Version Version, string AssetName, string DownloadUrl, long Size, string? Sha256);

/// <summary>
/// Install layout:
/// <code>
/// root/
///   BG3ModManager(.exe)   this launcher (+ its native libs)
///   app/                  the mod manager
///   app.old/              previous version, kept until the updated one has started fine
///   .update/              download/extract staging
/// </code>
/// </summary>
internal sealed class Updater
{
	public const string Owner = "ZeroTheScyther";
	public const string Repo = "BG3ModManager";

	/// <summary>Folders in app/ that hold user data and must survive an update.</summary>
	private static readonly string[] UserDataFolders = ["Data", "Orders", "_Logs"];

	private static readonly string AppExeName = OperatingSystem.IsWindows() ? "BG3ModManager.exe" : "BG3ModManager";
	private static readonly string Rid = OperatingSystem.IsWindows() ? "win-x64" : "linux-x64";
	private static readonly string PackageExt = OperatingSystem.IsWindows() ? "zip" : "tar.gz";

	public string Root { get; }
	public string AppDir { get; }
	public string AppExe { get; }
	public string NewDir { get; }
	public string OldDir { get; }
	public string StagingDir { get; }
	/// <summary>Tag of a release that failed to start; not offered again.</summary>
	private string SkipFile => Path.Join(Root, ".skip-release");

	private static readonly HttpClient Http = CreateHttp();

	/// <summary>BG3MM_LAUNCHER_RELEASE_URL overrides the release endpoint (testing).</summary>
	private static string LatestReleaseUrl => Environment.GetEnvironmentVariable("BG3MM_LAUNCHER_RELEASE_URL") is { Length: > 0 } url
		? url
		: $"https://api.github.com/repos/{Owner}/{Repo}/releases/latest";

	public Updater()
	{
		var self = Environment.ProcessPath ?? throw new InvalidOperationException("No process path.");
		Root = Path.GetDirectoryName(Path.GetFullPath(self))!;
		AppDir = Path.Join(Root, "app");
		AppExe = Path.Join(AppDir, AppExeName);
		NewDir = Path.Join(Root, "app.new");
		OldDir = Path.Join(Root, "app.old");
		StagingDir = Path.Join(Root, ".update");
	}

	public bool AppInstalled => File.Exists(AppExe);

	/// <summary>Set after <see cref="InstallAsync"/>.</summary>
	public string? InstalledTag { get; private set; }

	public bool IsSkipped(ReleaseInfo release)
	{
		try
		{
			return File.Exists(SkipFile) && File.ReadAllText(SkipFile).Trim() == release.Tag;
		}
		catch
		{
			return false;
		}
	}

	private static HttpClient CreateHttp()
	{
		var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
		http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("BG3ModManager-Launcher", "1.0"));
		return http;
	}

	/// <summary>Removes leftovers from an earlier run and moves user data from the old flat layout into app/.</summary>
	public void Prepare()
	{
		TryDeleteDirectory(StagingDir);
		TryDeleteDirectory(NewDir);
		TryDeleteDirectory(Path.Join(Root, "app.broken"));
		foreach (var file in Directory.EnumerateFiles(Root, "*.old"))
		{
			TryDeleteFile(file);
		}
		if (!AppInstalled) return;

		TryDeleteDirectory(OldDir);

		// Before the launcher existed, the mod manager lived directly in root/. If the new package was extracted over
		// (or next to) an old install, its settings and orders are still in root/.
		foreach (var name in UserDataFolders)
		{
			var legacy = Path.Join(Root, name);
			var target = Path.Join(AppDir, name);
			if (Directory.Exists(legacy) && !Directory.Exists(target))
			{
				Log.Write($"Moving {legacy} -> {target}");
				Directory.Move(legacy, target);
			}
		}
	}

	public Version? GetInstalledVersion()
	{
		var dll = Path.Join(AppDir, "BG3ModManager.dll");
		if (!File.Exists(dll)) return null;
		try
		{
			using var stream = File.OpenRead(dll);
			using var pe = new PEReader(stream);
			return pe.GetMetadataReader().GetAssemblyDefinition().Version;
		}
		catch (Exception ex)
		{
			Log.Write($"Reading installed version failed: {ex.Message}");
			return null;
		}
	}

	public bool IsAppRunning()
	{
		var name = Path.GetFileNameWithoutExtension(AppExeName);
		var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
		foreach (var p in Process.GetProcessesByName(name))
		{
			using (p)
			{
				if (p.Id == Environment.ProcessId) continue;
				try
				{
					if (string.Equals(p.MainModule?.FileName, AppExe, comparison)) return true;
				}
				catch
				{
					// No access to the process; not ours.
				}
			}
		}
		return false;
	}

	public async Task<ReleaseInfo?> GetLatestReleaseAsync(CancellationToken ct)
	{
		using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseUrl);
		request.Headers.Accept.ParseAdd("application/vnd.github+json");
		using var response = await Http.SendAsync(request, ct);
		if (!response.IsSuccessStatusCode)
		{
			Log.Write($"Release check: HTTP {(int)response.StatusCode}");
			return null;
		}

		using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
		var tag = doc.RootElement.GetProperty("tag_name").GetString() ?? "";
		if (!TryParseTag(tag, out var version))
		{
			Log.Write($"Release check: unparseable tag '{tag}'");
			return null;
		}

		var assetName = $"BG3ModManager-{Rid}.{PackageExt}";
		foreach (var asset in doc.RootElement.GetProperty("assets").EnumerateArray())
		{
			if (asset.GetProperty("name").GetString() != assetName) continue;
			string? sha = null;
			if (asset.TryGetProperty("digest", out var digest) && digest.GetString() is { } d && d.StartsWith("sha256:", StringComparison.Ordinal))
			{
				sha = d["sha256:".Length..];
			}
			return new ReleaseInfo(tag, version, assetName, asset.GetProperty("browser_download_url").GetString()!, asset.GetProperty("size").GetInt64(), sha);
		}

		Log.Write($"Release {tag} has no {assetName}");
		return null;
	}

	/// <summary>"v2.3.0-sync" -> 2.3.0</summary>
	public static bool TryParseTag(string tag, out Version version)
	{
		var s = tag.TrimStart('v', 'V');
		var end = s.IndexOfAny(['-', '+']);
		if (end >= 0) s = s[..end];
		if (Version.TryParse(s, out var v))
		{
			version = Normalize(v);
			return true;
		}
		version = new Version();
		return false;
	}

	public static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0), Math.Max(v.Revision, 0));

	/// <summary>Downloads and extracts the release, then swaps it in. Progress is (bytes, total).</summary>
	public async Task InstallAsync(ReleaseInfo release, Action<long, long> progress, Action<string> status, CancellationToken ct)
	{
		Directory.CreateDirectory(StagingDir);
		var package = Path.Join(StagingDir, release.AssetName);

		status("Downloading…");
		await DownloadAsync(release, package, progress, ct);

		status("Installing…");
		var extracted = Path.Join(StagingDir, "x");
		await Task.Run(() => Extract(package, extracted), ct);

		// Archives contain a top-level BG3ModManager/ folder.
		var pkgRoot = Path.Join(extracted, "BG3ModManager");
		if (!Directory.Exists(pkgRoot)) pkgRoot = extracted;
		var pkgApp = Path.Join(pkgRoot, "app");
		if (!File.Exists(Path.Join(pkgApp, AppExeName)))
		{
			throw new InvalidDataException("The downloaded package has no app/ folder.");
		}

		// Past this point, don't stop halfway.
		await Task.Run(() => Apply(pkgRoot, pkgApp), CancellationToken.None);
		TryDeleteDirectory(StagingDir);
		InstalledTag = release.Tag;
	}

	private static async Task DownloadAsync(ReleaseInfo release, string path, Action<long, long> progress, CancellationToken ct)
	{
		using var response = await Http.GetAsync(release.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
		response.EnsureSuccessStatusCode();
		var total = response.Content.Headers.ContentLength ?? release.Size;

		using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
		await using (var input = await response.Content.ReadAsStreamAsync(ct))
		await using (var output = File.Create(path))
		{
			var buffer = new byte[1 << 16];
			long received = 0;
			int read;
			while ((read = await input.ReadAsync(buffer, ct)) > 0)
			{
				await output.WriteAsync(buffer.AsMemory(0, read), ct);
				sha.AppendData(buffer, 0, read);
				received += read;
				progress(received, total);
			}
			if (received != release.Size)
			{
				throw new InvalidDataException($"Download incomplete ({received} of {release.Size} bytes).");
			}
		}

		var hash = Convert.ToHexStringLower(sha.GetHashAndReset());
		if (release.Sha256 != null && !string.Equals(hash, release.Sha256, StringComparison.OrdinalIgnoreCase))
		{
			throw new InvalidDataException("Download is corrupt (checksum mismatch).");
		}
		Log.Write($"Downloaded {release.AssetName} ({release.Size} bytes, sha256 {hash})");
	}

	private static void Extract(string package, string destination)
	{
		TryDeleteDirectory(destination);
		Directory.CreateDirectory(destination);
		if (package.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
		{
			ZipFile.ExtractToDirectory(package, destination);
		}
		else
		{
			using var file = File.OpenRead(package);
			using var gz = new GZipStream(file, CompressionMode.Decompress);
			TarFile.ExtractToDirectory(gz, destination, overwriteFiles: true);
		}
	}

	private void Apply(string pkgRoot, string pkgApp)
	{
		Directory.Move(pkgApp, NewDir);

		if (Directory.Exists(AppDir))
		{
			foreach (var name in UserDataFolders)
			{
				var source = Path.Join(AppDir, name);
				if (Directory.Exists(source)) CopyDirectory(source, Path.Join(NewDir, name));
			}

			TryDeleteDirectory(OldDir);
			Retry(() => Directory.Move(AppDir, OldDir));
		}

		try
		{
			Retry(() => Directory.Move(NewDir, AppDir));
		}
		catch
		{
			if (!Directory.Exists(AppDir) && Directory.Exists(OldDir)) Directory.Move(OldDir, AppDir);
			throw;
		}
		Log.Write("Swapped in new app/");

		UpdateLauncherFiles(pkgRoot);
	}

	/// <summary>
	/// Replaces the launcher's own files (exe, native libs, icon) when they changed. Running/loaded files can't be
	/// overwritten on Windows but can be renamed, so the current file is moved to *.old (deleted on next start).
	/// </summary>
	private void UpdateLauncherFiles(string pkgRoot)
	{
		foreach (var source in Directory.EnumerateFiles(pkgRoot))
		{
			var dest = Path.Join(Root, Path.GetFileName(source));
			try
			{
				if (File.Exists(dest))
				{
					if (SameContent(source, dest)) continue;
					File.Move(dest, dest + ".old", true);
				}
				File.Copy(source, dest, true);
				if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(dest, File.GetUnixFileMode(source));
				Log.Write($"Updated {dest}");
			}
			catch (Exception ex)
			{
				Log.Write($"Updating {dest} failed: {ex.Message}");
			}
		}
	}

	/// <summary>Starts the mod manager with the given args.</summary>
	public Process? Launch(IEnumerable<string> args)
	{
		var info = new ProcessStartInfo(AppExe) { WorkingDirectory = AppDir, UseShellExecute = false };
		foreach (var a in args) info.ArgumentList.Add(a);
		Log.Write($"Starting {AppExe}");
		return Process.Start(info);
	}

	/// <summary>
	/// After an update: if the new version dies within a few seconds, put the previous one back and start that.
	/// Otherwise drop the previous version.
	/// </summary>
	public void WatchAfterUpdate(Process? process, IEnumerable<string> args)
	{
		if (process == null) return;
		using (process)
		{
			if (process.WaitForExit(TimeSpan.FromSeconds(15)) && process.ExitCode != 0 && Directory.Exists(OldDir))
			{
				Log.Write($"New version exited with {process.ExitCode}; rolling back");
				try
				{
					if (InstalledTag != null) File.WriteAllText(SkipFile, InstalledTag);
					var broken = Path.Join(Root, "app.broken");
					TryDeleteDirectory(broken);
					Retry(() => Directory.Move(AppDir, broken));
					Retry(() => Directory.Move(OldDir, AppDir));
					Launch(args)?.Dispose();
				}
				catch (Exception ex)
				{
					Log.Write($"Rollback failed: {ex}");
				}
				return;
			}
		}
		TryDeleteDirectory(OldDir);
	}

	private static bool SameContent(string a, string b)
	{
		if (new FileInfo(a).Length != new FileInfo(b).Length) return false;
		using var fa = File.OpenRead(a);
		using var fb = File.OpenRead(b);
		return SHA256.HashData(fa).AsSpan().SequenceEqual(SHA256.HashData(fb));
	}

	private static void CopyDirectory(string source, string dest)
	{
		Directory.CreateDirectory(dest);
		foreach (var dir in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
		{
			Directory.CreateDirectory(Path.Join(dest, Path.GetRelativePath(source, dir)));
		}
		foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
		{
			File.Copy(file, Path.Join(dest, Path.GetRelativePath(source, file)), true);
		}
	}

	/// <summary>Windows: antivirus/indexers briefly hold handles on freshly written files.</summary>
	private static void Retry(Action action)
	{
		for (var i = 0; ; i++)
		{
			try
			{
				action();
				return;
			}
			catch (IOException) when (i < 10)
			{
				Thread.Sleep(500);
			}
			catch (UnauthorizedAccessException) when (i < 10)
			{
				Thread.Sleep(500);
			}
		}
	}

	private static void TryDeleteDirectory(string path)
	{
		try
		{
			if (Directory.Exists(path)) Directory.Delete(path, true);
		}
		catch (Exception ex)
		{
			Log.Write($"Couldn't delete {path}: {ex.Message}");
		}
	}

	private static void TryDeleteFile(string path)
	{
		try
		{
			File.Delete(path);
		}
		catch
		{
			// Still in use (e.g. the previous launcher exe); try again next time.
		}
	}
}

internal static class Log
{
	private static StreamWriter? _writer;

	public static void Open(string path)
	{
		try
		{
			_writer = new StreamWriter(path, false) { AutoFlush = true };
		}
		catch
		{
			_writer = null;
		}
	}

	public static void Write(string message)
	{
		try
		{
			_writer?.WriteLine($"{DateTime.Now:HH:mm:ss} {message}");
		}
		catch
		{
		}
	}
}
