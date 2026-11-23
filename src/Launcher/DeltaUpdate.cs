using System.Buffers.Binary;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;

namespace ModManager.Launcher;

/// <summary>
/// Builds the new package folder from the installed files plus only the zip entries that changed. Reads the release
/// zip's central directory and fetches changed entries with HTTP range requests, so nothing but the delta is downloaded.
/// Any failure makes the caller fall back to the full download.
/// </summary>
internal static class DeltaUpdate
{
	private sealed record Entry(string Name, uint Crc, long CompressedSize, long Size, long Offset, ushort Method, int UnixMode)
	{
		/// <summary>Local header offset of the next entry (or the central directory): end of this entry's bytes.</summary>
		public long End { get; set; }
	}

	/// <summary>Entries closer than this are fetched in one request.</summary>
	private const long MergeGap = 256 * 1024;
	private const long MaxRequest = 32 * 1024 * 1024;

	/// <summary>
	/// Fills <paramref name="destination"/> (layout: BG3ModManager/..., BG3ModManager/app/...) with the release's files.
	/// Returns false when a delta isn't worth it; throws on errors.
	/// </summary>
	public static async Task<bool> TryBuildAsync(HttpClient http, ReleaseInfo release, string root, string appDir, string destination,
		Action<long, long> progress, CancellationToken ct)
	{
		var entries = await ReadCentralDirectoryAsync(http, release, ct);

		var pkgRoot = "BG3ModManager/";
		var needed = new List<Entry>();
		long reused = 0;
		await Task.Run(() =>
		{
			foreach (var e in entries)
			{
				if (e.Name.EndsWith('/')) continue;
				if (!e.Name.StartsWith(pkgRoot, StringComparison.Ordinal)) throw new InvalidDataException($"Unexpected entry {e.Name}");
				var rel = e.Name[pkgRoot.Length..];
				var local = rel.StartsWith("app/", StringComparison.Ordinal) ? Path.Join(appDir, rel[4..]) : Path.Join(root, rel);
				var target = SafeJoin(destination, e.Name);
				if (File.Exists(local) && new FileInfo(local).Length == e.Size && Crc32.OfFile(local) == e.Crc)
				{
					Directory.CreateDirectory(Path.GetDirectoryName(target)!);
					File.Copy(local, target, true);
					reused += e.Size;
				}
				else
				{
					needed.Add(e);
				}
			}
		}, ct);

		var groups = Group(needed).ToList();
		var downloadBytes = groups.Sum(g => g[^1].End - g[0].Offset);
		Log.Write($"Delta: {needed.Count} of {entries.Count} files changed, {downloadBytes} of {release.Size} bytes to download ({reused} bytes reused)");
		if (downloadBytes > release.Size * 7 / 10) return false;

		long received = 0;
		progress(0, downloadBytes);
		foreach (var group in groups)
		{
			var start = group[0].Offset;
			var end = group[^1].End;
			var data = await GetRangeAsync(http, release.DownloadUrl, start, end, ct);
			foreach (var e in group)
			{
				ExtractEntry(e, data.AsSpan((int)(e.Offset - start), (int)(e.End - e.Offset)), SafeJoin(destination, e.Name));
			}
			received += end - start;
			progress(received, downloadBytes);
		}
		return true;
	}

	private static IEnumerable<List<Entry>> Group(List<Entry> needed)
	{
		var group = new List<Entry>();
		foreach (var e in needed.OrderBy(e => e.Offset))
		{
			if (group.Count > 0 && (e.Offset - group[^1].End > MergeGap || e.End - group[0].Offset > MaxRequest))
			{
				yield return group;
				group = [];
			}
			group.Add(e);
		}
		if (group.Count > 0) yield return group;
	}

	private static void ExtractEntry(Entry e, ReadOnlySpan<byte> bytes, string target)
	{
		if (BinaryPrimitives.ReadUInt32LittleEndian(bytes) != 0x04034b50) throw new InvalidDataException($"Bad local header for {e.Name}");
		var nameLen = BinaryPrimitives.ReadUInt16LittleEndian(bytes[26..]);
		var extraLen = BinaryPrimitives.ReadUInt16LittleEndian(bytes[28..]);
		var data = bytes.Slice(30 + nameLen + extraLen, checked((int)e.CompressedSize)).ToArray();

		Directory.CreateDirectory(Path.GetDirectoryName(target)!);
		using (var output = File.Create(target))
		{
			if (e.Method == 0)
			{
				output.Write(data);
			}
			else if (e.Method == 8)
			{
				using var deflate = new DeflateStream(new MemoryStream(data), CompressionMode.Decompress);
				deflate.CopyTo(output);
			}
			else
			{
				throw new InvalidDataException($"Unsupported compression {e.Method} for {e.Name}");
			}
		}
		if (new FileInfo(target).Length != e.Size || Crc32.OfFile(target) != e.Crc) throw new InvalidDataException($"Checksum mismatch for {e.Name}");
		if (!OperatingSystem.IsWindows() && e.UnixMode != 0) File.SetUnixFileMode(target, (UnixFileMode)(e.UnixMode & 0x1FF));
	}

	private static async Task<List<Entry>> ReadCentralDirectoryAsync(HttpClient http, ReleaseInfo release, CancellationToken ct)
	{
		var size = release.Size;
		var tailLen = Math.Min(size, 65557 + 20 + 56);
		var tail = await GetRangeAsync(http, release.DownloadUrl, size - tailLen, size, ct);

		var eocd = LastIndexOf(tail, 0x06054b50, tail.Length - 22);
		if (eocd < 0) throw new InvalidDataException("No end of central directory.");
		long count = BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(eocd + 10));
		long cdSize = BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(eocd + 12));
		long cdOffset = BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(eocd + 16));
		if (count == 0xFFFF || cdSize == 0xFFFFFFFF || cdOffset == 0xFFFFFFFF)
		{
			var z64 = LastIndexOf(tail, 0x06064b50, eocd - 56);
			if (z64 < 0) throw new InvalidDataException("No zip64 end of central directory.");
			count = (long)BinaryPrimitives.ReadUInt64LittleEndian(tail.AsSpan(z64 + 32));
			cdSize = (long)BinaryPrimitives.ReadUInt64LittleEndian(tail.AsSpan(z64 + 40));
			cdOffset = (long)BinaryPrimitives.ReadUInt64LittleEndian(tail.AsSpan(z64 + 48));
		}

		var cd = await GetRangeAsync(http, release.DownloadUrl, cdOffset, cdOffset + cdSize, ct);
		var entries = new List<Entry>((int)count);
		var p = 0;
		for (var i = 0; i < count; i++)
		{
			var s = cd.AsSpan(p);
			if (BinaryPrimitives.ReadUInt32LittleEndian(s) != 0x02014b50) throw new InvalidDataException("Bad central directory.");
			var madeBy = BinaryPrimitives.ReadUInt16LittleEndian(s[4..]);
			var flags = BinaryPrimitives.ReadUInt16LittleEndian(s[8..]);
			var method = BinaryPrimitives.ReadUInt16LittleEndian(s[10..]);
			var crc = BinaryPrimitives.ReadUInt32LittleEndian(s[16..]);
			long csize = BinaryPrimitives.ReadUInt32LittleEndian(s[20..]);
			long usize = BinaryPrimitives.ReadUInt32LittleEndian(s[24..]);
			var nameLen = BinaryPrimitives.ReadUInt16LittleEndian(s[28..]);
			var extraLen = BinaryPrimitives.ReadUInt16LittleEndian(s[30..]);
			var commentLen = BinaryPrimitives.ReadUInt16LittleEndian(s[32..]);
			var external = BinaryPrimitives.ReadUInt32LittleEndian(s[38..]);
			long offset = BinaryPrimitives.ReadUInt32LittleEndian(s[42..]);
			if ((flags & 1) != 0) throw new InvalidDataException("Encrypted entry.");
			var name = System.Text.Encoding.UTF8.GetString(s.Slice(46, nameLen)).Replace('\\', '/');

			// Zip64 extra field: present values replace the 0xFFFFFFFF placeholders, in this order.
			var extra = s.Slice(46 + nameLen, extraLen);
			while (extra.Length >= 4)
			{
				var id = BinaryPrimitives.ReadUInt16LittleEndian(extra);
				var len = BinaryPrimitives.ReadUInt16LittleEndian(extra[2..]);
				if (id == 1)
				{
					var z = extra.Slice(4, len);
					if (usize == 0xFFFFFFFF) { usize = (long)BinaryPrimitives.ReadUInt64LittleEndian(z); z = z[8..]; }
					if (csize == 0xFFFFFFFF) { csize = (long)BinaryPrimitives.ReadUInt64LittleEndian(z); z = z[8..]; }
					if (offset == 0xFFFFFFFF) { offset = (long)BinaryPrimitives.ReadUInt64LittleEndian(z); }
				}
				extra = extra[Math.Min(extra.Length, 4 + len)..];
			}

			var unixMode = (madeBy >> 8) == 3 ? (int)(external >> 16) : 0;
			entries.Add(new Entry(name, crc, csize, usize, offset, method, unixMode));
			p += 46 + nameLen + extraLen + commentLen;
		}

		var sorted = entries.OrderBy(e => e.Offset).ToList();
		for (var i = 0; i < sorted.Count; i++)
		{
			sorted[i].End = i + 1 < sorted.Count ? sorted[i + 1].Offset : cdOffset;
		}
		return entries;
	}

	private static int LastIndexOf(byte[] data, uint signature, int from)
	{
		for (var i = Math.Min(from, data.Length - 4); i >= 0; i--)
		{
			if (BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(i)) == signature) return i;
		}
		return -1;
	}

	/// <summary>Bytes [start, end). Throws unless the server honours the range.</summary>
	private static async Task<byte[]> GetRangeAsync(HttpClient http, string url, long start, long end, CancellationToken ct)
	{
		using var request = new HttpRequestMessage(HttpMethod.Get, url);
		request.Headers.Range = new RangeHeaderValue(start, end - 1);
		using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
		if (response.StatusCode != HttpStatusCode.PartialContent) throw new InvalidDataException($"Range request returned HTTP {(int)response.StatusCode}.");
		var data = await response.Content.ReadAsByteArrayAsync(ct);
		if (data.Length != end - start) throw new InvalidDataException($"Range request returned {data.Length} of {end - start} bytes.");
		return data;
	}

	private static string SafeJoin(string root, string name)
	{
		var full = Path.GetFullPath(Path.Join(root, name));
		var rootFull = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
		if (!full.StartsWith(rootFull, StringComparison.Ordinal)) throw new InvalidDataException($"Entry escapes the package: {name}");
		return full;
	}
}

internal static class Crc32
{
	private static readonly uint[] Table = CreateTable();

	private static uint[] CreateTable()
	{
		var table = new uint[256 * 8];
		for (uint i = 0; i < 256; i++)
		{
			var c = i;
			for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
			table[i] = c;
		}
		for (var i = 0; i < 256; i++)
		{
			for (var t = 1; t < 8; t++) table[t * 256 + i] = (table[(t - 1) * 256 + i] >> 8) ^ table[table[(t - 1) * 256 + i] & 0xFF];
		}
		return table;
	}

	public static uint OfFile(string path)
	{
		using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
		var buffer = new byte[1 << 16];
		var crc = 0xFFFFFFFFu;
		int read;
		while ((read = stream.Read(buffer)) > 0) crc = Update(crc, buffer.AsSpan(0, read));
		return ~crc;
	}

	/// <summary>Slicing-by-8.</summary>
	private static uint Update(uint crc, ReadOnlySpan<byte> data)
	{
		var t = Table;
		while (data.Length >= 8)
		{
			var lo = BinaryPrimitives.ReadUInt32LittleEndian(data) ^ crc;
			var hi = BinaryPrimitives.ReadUInt32LittleEndian(data[4..]);
			crc = t[7 * 256 + (lo & 0xFF)] ^ t[6 * 256 + ((lo >> 8) & 0xFF)] ^ t[5 * 256 + ((lo >> 16) & 0xFF)] ^ t[4 * 256 + (lo >> 24)]
				^ t[3 * 256 + (hi & 0xFF)] ^ t[2 * 256 + ((hi >> 8) & 0xFF)] ^ t[256 + ((hi >> 16) & 0xFF)] ^ t[hi >> 24];
			data = data[8..];
		}
		foreach (var b in data) crc = t[(crc ^ b) & 0xFF] ^ (crc >> 8);
		return crc;
	}
}
