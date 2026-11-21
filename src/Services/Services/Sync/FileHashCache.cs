using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace ModManager.Services.Sync;

/// <summary>
/// SHA-256 of files, cached by (full path, size, last write time) so unchanged paks aren't rehashed on every sync.
/// </summary>
public sealed class FileHashCache
{
	private sealed record Entry(long Size, long LastWriteUtcTicks, string Sha256);

	private readonly string? _cacheFile;
	private readonly Dictionary<string, Entry> _entries;
	private readonly Lock _lock = new();
	private bool _dirty;

	public FileHashCache(string? cacheFile)
	{
		_cacheFile = cacheFile;
		_entries = [];
		if (cacheFile != null && File.Exists(cacheFile))
		{
			try
			{
				_entries = JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(cacheFile)) ?? [];
			}
			catch (Exception ex)
			{
				DivinityApp.Log($"Ignoring unreadable hash cache '{cacheFile}': {ex.Message}");
			}
		}
	}

	public static async Task<string> ComputeSha256Async(string path, CancellationToken token)
	{
		await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.Asynchronous | FileOptions.SequentialScan);
		var hash = await SHA256.HashDataAsync(stream, token);
		return Convert.ToHexStringLower(hash);
	}

	public async Task<string> GetSha256Async(string path, CancellationToken token)
	{
		var info = new FileInfo(path);
		if (!info.Exists) throw new FileNotFoundException("File not found", path);
		var key = info.FullName;
		var size = info.Length;
		var ticks = info.LastWriteTimeUtc.Ticks;

		lock (_lock)
		{
			if (_entries.TryGetValue(key, out var cached) && cached.Size == size && cached.LastWriteUtcTicks == ticks)
			{
				return cached.Sha256;
			}
		}

		var sha = await ComputeSha256Async(path, token);
		lock (_lock)
		{
			_entries[key] = new Entry(size, ticks, sha);
			_dirty = true;
		}
		return sha;
	}

	/// <summary>Records a hash we already verified (e.g. right after downloading), avoiding a second full read.</summary>
	public void Set(string path, string sha256)
	{
		var info = new FileInfo(path);
		if (!info.Exists) return;
		lock (_lock)
		{
			_entries[info.FullName] = new Entry(info.Length, info.LastWriteTimeUtc.Ticks, sha256);
			_dirty = true;
		}
	}

	public void Save()
	{
		if (_cacheFile == null) return;
		string json;
		lock (_lock)
		{
			if (!_dirty) return;
			// Drop entries for files that no longer exist so the cache doesn't grow forever.
			foreach (var key in _entries.Keys.Where(k => !File.Exists(k)).ToList()) _entries.Remove(key);
			json = JsonSerializer.Serialize(_entries);
			_dirty = false;
		}
		try
		{
			Directory.CreateDirectory(Path.GetDirectoryName(_cacheFile)!);
			var tmp = _cacheFile + ".tmp";
			File.WriteAllText(tmp, json);
			File.Move(tmp, _cacheFile, true);
		}
		catch (Exception ex)
		{
			DivinityApp.Log($"Failed to save hash cache '{_cacheFile}': {ex.Message}");
		}
	}
}
