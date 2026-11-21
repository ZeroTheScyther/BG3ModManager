using System.IO;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace ModManager.Services.Sync;

/// <summary>
/// What a host shares: the mod files (by content hash) plus the load order to apply.
/// Everything here is received from the network on the client side, so it must pass <see cref="ModSyncValidation"/>
/// before being used to touch the file system.
/// </summary>
public sealed record SyncManifest
{
	public const int CurrentVersion = 1;

	public int Version { get; init; } = CurrentVersion;
	public string HostName { get; init; } = "";
	public DateTimeOffset Created { get; init; }
	public List<SyncFileEntry> Files { get; init; } = [];
	/// <summary>Active mods, in load order (top first). Mods without a pak in Files (e.g. Larian built-ins) may appear here.</summary>
	public List<SyncOrderEntry> LoadOrder { get; init; } = [];
}

public sealed record SyncFileEntry(string FileName, long Size, string Sha256, string? ModUuid, string? ModName, string? ModVersion);

public sealed record SyncOrderEntry(string Uuid, string Name);

/// <summary>A mod pak file the host wants to share.</summary>
public sealed record SharedModFile(string FullPath, string? Uuid, string? Name, string? Version);

/// <summary>A pak file in the client's Mods folder.</summary>
public sealed record LocalModFile(string FullPath, string Sha256, long Size, string? Uuid)
{
	public string FileName => Path.GetFileName(FullPath);
}

public enum SyncActionKind
{
	UpToDate,
	/// <summary>Not present locally.</summary>
	Download,
	/// <summary>Same mod UUID locally, but different file contents.</summary>
	Update,
	/// <summary>A different file with the same file name exists locally.</summary>
	Replace,
	/// <summary>Local pak the host doesn't have.</summary>
	Extra,
}

public sealed record SyncAction(SyncActionKind Kind, SyncFileEntry? Remote, LocalModFile? Local)
{
	public bool NeedsDownload => Kind is SyncActionKind.Download or SyncActionKind.Update or SyncActionKind.Replace;
	[JsonIgnore] public string DisplayName => Remote?.ModName ?? Remote?.FileName ?? Local?.FileName ?? "";
}

public sealed record SyncPlan(SyncManifest Manifest, List<SyncAction> Actions)
{
	public IEnumerable<SyncAction> Downloads => Actions.Where(x => x.NeedsDownload);
	public IEnumerable<SyncAction> Extras => Actions.Where(x => x.Kind == SyncActionKind.Extra);
	public long DownloadBytes => Downloads.Sum(x => x.Remote!.Size);
	public bool IsUpToDate => !Downloads.Any();
}

public sealed record SyncProgress(string CurrentFile, int FileIndex, int FileCount, long BytesDone, long BytesTotal)
{
	public double Percent => BytesTotal <= 0 ? 100 : Math.Clamp(BytesDone * 100d / BytesTotal, 0, 100);
}

public sealed record SyncResult(int Downloaded, int MovedToBackup, string? BackupFolder);

public static partial class ModSyncValidation
{
	public const long MaxFileSize = 32L * 1024 * 1024 * 1024; // 32 GiB, far above any real pak
	public const int MaxFiles = 10_000;

	private static readonly HashSet<string> _reservedWindowsNames = new(StringComparer.OrdinalIgnoreCase)
	{
		"CON", "PRN", "AUX", "NUL",
		"COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
		"LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
	};

	[GeneratedRegex("^[0-9a-f]{64}$")]
	private static partial Regex Sha256Regex();

	public static bool IsValidSha256(string? sha) => sha != null && Sha256Regex().IsMatch(sha);

	/// <summary>
	/// A bare .pak file name that is safe to create on both Windows and Linux: no directories, no traversal,
	/// no characters/names Windows rejects. Validated against Windows rules even on Linux, since hosts and
	/// clients may be on different OSes.
	/// </summary>
	public static bool IsSafePakFileName(string? name)
	{
		if (string.IsNullOrWhiteSpace(name) || name.Length > 200) return false;
		if (!name.EndsWith(".pak", StringComparison.OrdinalIgnoreCase)) return false;
		if (name.EndsWith(' ') || name.EndsWith('.') || name.StartsWith(' ') || name.StartsWith('.')) return false;
		foreach (var c in name)
		{
			if (c < 32 || c is '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|') return false;
		}
		var stem = name[..name.IndexOf('.')];
		if (_reservedWindowsNames.Contains(stem)) return false;
		return true;
	}

	/// <summary>Throws <see cref="InvalidDataException"/> if a manifest received from a host is malformed or unsafe.</summary>
	public static void Validate(SyncManifest manifest)
	{
		if (manifest.Version != SyncManifest.CurrentVersion)
			throw new InvalidDataException($"Unsupported sync manifest version {manifest.Version} (expected {SyncManifest.CurrentVersion}). Make sure both of you are running the same mod manager version.");
		if (manifest.Files.Count > MaxFiles) throw new InvalidDataException("Manifest has too many files.");

		var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (var f in manifest.Files)
		{
			if (!IsSafePakFileName(f.FileName)) throw new InvalidDataException($"Unsafe file name in manifest: '{f.FileName}'");
			if (!IsValidSha256(f.Sha256)) throw new InvalidDataException($"Invalid hash for '{f.FileName}'");
			if (f.Size < 0 || f.Size > MaxFileSize) throw new InvalidDataException($"Invalid size for '{f.FileName}'");
			if (!names.Add(f.FileName)) throw new InvalidDataException($"Duplicate file name in manifest: '{f.FileName}'");
		}
	}
}
