using ModManager.Services.Sync;

using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

using Xunit;

namespace ModManager.Tests;

public sealed class ModSyncTests : BaseTest, IAsyncLifetime
{
	private readonly string _root;
	private readonly string _hostMods;
	private readonly string _clientMods;
	private ModSyncHostService? _host;

	public ModSyncTests(ITestOutputHelper output) : base(output)
	{
		_root = Path.Join(Path.GetTempPath(), "bg3mm-sync-tests", Guid.NewGuid().ToString("N"));
		_hostMods = Path.Join(_root, "host", "Mods");
		_clientMods = Path.Join(_root, "client", "Mods");
		Directory.CreateDirectory(_hostMods);
		Directory.CreateDirectory(_clientMods);
	}

	public ValueTask InitializeAsync() => ValueTask.CompletedTask;

	public async ValueTask DisposeAsync()
	{
		if (_host != null) await _host.DisposeAsync();
		try { Directory.Delete(_root, true); } catch { }
	}

	private static int GetFreePort()
	{
		var l = new TcpListener(IPAddress.Loopback, 0);
		l.Start();
		var port = ((IPEndPoint)l.LocalEndpoint).Port;
		l.Stop();
		return port;
	}

	private static string WriteRandomFile(string folder, string name, int size, int seed)
	{
		var data = new byte[size];
		new Random(seed).NextBytes(data);
		var path = Path.Join(folder, name);
		File.WriteAllBytes(path, data);
		return path;
	}

	private static string Sha(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

	private async Task<ModSyncHostService> StartHostAsync(params SharedModFile[] files)
	{
		var identity = ModSyncIdentity.LoadOrCreate(Path.Join(_root, "host", "identity"));
		identity.Settings.Port = GetFreePort();
		identity.Settings.UseNatPortMapping = false;
		_host = new ModSyncHostService(identity, new FileHashCache(null));
		await _host.UpdateSharedSetAsync(files, [.. files.Select(f => new SyncOrderEntry(f.Uuid!, f.Name!))], TestContext.Current.CancellationToken);
		await _host.StartAsync(TestContext.Current.CancellationToken);
		return _host;
	}

	private ModSyncInvite LocalInvite(ModSyncHostService host, string? token = null, string? fingerprint = null) =>
		// An unroutable TEST-NET address first, to exercise "try every address, keep the one that works".
		new(["192.0.2.1", "127.0.0.1"], host.Port, token ?? host.Identity.Settings.Token, fingerprint ?? host.Identity.Fingerprint, "test-host");

	private async Task<List<LocalModFile>> ScanClientAsync(Dictionary<string, string>? uuids = null)
	{
		List<LocalModFile> list = [];
		foreach (var f in Directory.EnumerateFiles(_clientMods, "*.pak"))
		{
			list.Add(new LocalModFile(f, await FileHashCache.ComputeSha256Async(f, CancellationToken.None), new FileInfo(f).Length, uuids?.GetValueOrDefault(Path.GetFileName(f))));
		}
		return list;
	}

	[Fact]
	public async Task FullSync_DownloadsUpdatesReplacesAndBacksUpExtras()
	{
		var ct = TestContext.Current.CancellationToken;
		var a = WriteRandomFile(_hostMods, "ModA.pak", 3_000_000, 1);
		var b = WriteRandomFile(_hostMods, "ModB_v2.pak", 50_000, 2);
		var c = WriteRandomFile(_hostMods, "ModC.pak", 10_000, 3);
		var host = await StartHostAsync(
			new SharedModFile(a, "uuid-a", "Mod A", "1"),
			new SharedModFile(b, "uuid-b", "Mod B", "2"),
			new SharedModFile(c, "uuid-c", "Mod C", "1"));

		// Client: has an old ModB under a different name (same UUID -> update), a different file named ModC.pak
		// (no UUID -> replace), and an extra mod the host doesn't have.
		WriteRandomFile(_clientMods, "ModB_v1.pak", 40_000, 20);
		WriteRandomFile(_clientMods, "ModC.pak", 9_000, 30);
		var extra = WriteRandomFile(_clientMods, "OnlyMine.pak", 1_000, 40);

		var (conn, manifest) = await ModSyncConnection.ConnectAsync(LocalInvite(host), ct);
		using var _ = conn;
		Assert.Equal("127.0.0.1", conn.Address);
		Assert.Equal(3, manifest.Files.Count);

		var plan = ModSyncPlanner.BuildPlan(manifest, await ScanClientAsync(new() { ["ModB_v1.pak"] = "uuid-b" }));
		Assert.Equal(SyncActionKind.Download, plan.Actions.Single(x => x.Remote?.FileName == "ModA.pak").Kind);
		Assert.Equal(SyncActionKind.Update, plan.Actions.Single(x => x.Remote?.FileName == "ModB_v2.pak").Kind);
		Assert.Equal(SyncActionKind.Replace, plan.Actions.Single(x => x.Remote?.FileName == "ModC.pak").Kind);
		Assert.Equal("OnlyMine.pak", plan.Extras.Single().Local!.FileName);

		var backupRoot = Path.Join(_root, "client", "Backup");
		var result = await ModSyncExecutor.ExecuteAsync(conn, plan, _clientMods, Path.Join(_root, "client", "work"), backupRoot, true, null, null, ct);

		Assert.Equal(3, result.Downloaded);
		Assert.Equal(3, result.MovedToBackup); // old ModB, old ModC, extra
		var clientFiles = Directory.GetFiles(_clientMods).Select(Path.GetFileName).Order().ToList();
		Assert.Equal(["ModA.pak", "ModB_v2.pak", "ModC.pak"], clientFiles);
		Assert.Equal(Sha(a), Sha(Path.Join(_clientMods, "ModA.pak")));
		Assert.Equal(Sha(b), Sha(Path.Join(_clientMods, "ModB_v2.pak")));
		Assert.Equal(Sha(c), Sha(Path.Join(_clientMods, "ModC.pak")));
		Assert.False(File.Exists(extra));
		Assert.True(File.Exists(Path.Join(result.BackupFolder!, "OnlyMine.pak")));
		Assert.True(File.Exists(Path.Join(result.BackupFolder!, "ModB_v1.pak")));

		// Second pass: everything is up to date now.
		var again = ModSyncPlanner.BuildPlan(manifest, await ScanClientAsync());
		Assert.True(again.IsUpToDate);
		Assert.Equal(["uuid-a", "uuid-b", "uuid-c"], manifest.LoadOrder.Select(x => x.Uuid));
	}

	[Fact]
	public async Task WrongToken_IsRejected()
	{
		var a = WriteRandomFile(_hostMods, "ModA.pak", 100, 1);
		var host = await StartHostAsync(new SharedModFile(a, "uuid-a", "Mod A", "1"));
		var ex = await Assert.ThrowsAsync<ModSyncConnectException>(() => ModSyncConnection.ConnectAsync(LocalInvite(host, token: "nope"), TestContext.Current.CancellationToken));
		Output.WriteLine(ex.Message);
		Assert.Contains("rejected", ex.Message);
	}

	[Fact]
	public async Task WrongFingerprint_IsRejected()
	{
		var a = WriteRandomFile(_hostMods, "ModA.pak", 100, 1);
		var host = await StartHostAsync(new SharedModFile(a, "uuid-a", "Mod A", "1"));
		var ex = await Assert.ThrowsAsync<ModSyncConnectException>(() => ModSyncConnection.ConnectAsync(LocalInvite(host, fingerprint: new string('0', 64)), TestContext.Current.CancellationToken));
		Output.WriteLine(ex.Message);
		Assert.Contains("identity didn't match", ex.Message);
	}

	[Fact]
	public async Task Download_ResumesPartialFile()
	{
		var ct = TestContext.Current.CancellationToken;
		var a = WriteRandomFile(_hostMods, "Big.pak", 2_000_000, 7);
		var host = await StartHostAsync(new SharedModFile(a, "uuid-a", "Big", "1"));
		var (conn, manifest) = await ModSyncConnection.ConnectAsync(LocalInvite(host), ct);
		using var _ = conn;

		var part = Path.Join(_root, "big.part");
		File.WriteAllBytes(part, File.ReadAllBytes(a).AsSpan(0, 1_234_567).ToArray());
		long received = 0;
		await conn.DownloadAsync(manifest.Files[0], part, n => received += n, ct);
		Assert.Equal(Sha(a), Sha(part));
		Assert.Equal(2_000_000, received); // 1,234,567 already there + the rest fetched with a Range request
	}

	[Theory]
	[InlineData("Mod.pak", true)]
	[InlineData("My Cool Mod (v2).pak", true)]
	[InlineData("../evil.pak", false)]
	[InlineData("..\\evil.pak", false)]
	[InlineData("sub/dir.pak", false)]
	[InlineData("C:evil.pak", false)]
	[InlineData("CON.pak", false)]
	[InlineData("trailing.pak.", false)]
	[InlineData("notapak.exe", false)]
	[InlineData(".hidden.pak", false)]
	public void SafePakFileName(string name, bool expected) => Assert.Equal(expected, ModSyncValidation.IsSafePakFileName(name));

	[Fact]
	public void Manifest_WithTraversal_FailsValidation()
	{
		var m = new SyncManifest { Files = [new SyncFileEntry("../../.bashrc.pak", 1, new string('a', 64), null, null, null)] };
		Assert.Throws<InvalidDataException>(() => ModSyncValidation.Validate(m));
	}

	[Fact]
	public void Invite_RoundTrips()
	{
		var invite = new ModSyncInvite(["10.8.0.1", "203.0.113.5", "my.ddns.net"], 47630, "tok", new string('f', 64), "PC");
		Assert.True(ModSyncInvite.TryDecode(invite.Encode(), out var decoded, out var error), error);
		Assert.Equal(invite.Addresses, decoded!.Addresses);
		Assert.Equal(invite.Port, decoded.Port);
		Assert.Equal(invite.Token, decoded.Token);
		Assert.False(ModSyncInvite.TryDecode("BG3SYNC1-!!!", out _, out _));
		Assert.False(ModSyncInvite.TryDecode("hello", out _, out _));
	}
}
