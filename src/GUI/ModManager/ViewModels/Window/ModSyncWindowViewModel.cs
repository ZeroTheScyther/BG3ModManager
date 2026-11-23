using DynamicData.Binding;

using ModManager.Models.Mod;
using ModManager.Models.Mod.Order;
using ModManager.Services;
using ModManager.Services.Sync;
using ModManager.Util;

using System.Collections.ObjectModel;
using System.Reactive.Threading.Tasks;
using System.Text.Json;

using IOFile = System.IO.File;
using IOPath = System.IO.Path;
using IODirectory = System.IO.Directory;

namespace ModManager.ViewModels;

public sealed record SyncAddressRow(string Address, string Kind, string Source);

public sealed record SyncPlanRow(string Status, string Name, string FileName, string Size, string StatusColor);

/// <summary>Entry of the Join tab's "Sync to" dropdown. Order == null means "Create new order".</summary>
public sealed record SyncTargetOption(string Label, ModOrder? Order)
{
	public override string ToString() => Label;
}

/// <summary>
/// Mod Sync window: host your active mods + load order for friends, or join a friend's session with an invite code.
/// Both sides run the same app; there's no separate server.
/// </summary>
public partial class ModSyncWindowViewModel : ReactiveObject, IClosableViewModel, IRoutableViewModel
{
	#region IClosableViewModel/IRoutableViewModel
	public string UrlPathSegment => "modsync";
	public IScreen HostScreen { get; }
	[Reactive] public partial bool IsVisible { get; set; }
	public RxCommandUnit CloseCommand { get; }
	#endregion

	/// <param name="SyncTarget">File path of the order last synced to; null = create a new order.</param>
	private sealed record ClientState(string? LastInvite, bool ExportToGame, string? SyncTarget = null);

	private readonly IModManagerService _manager;
	private readonly IPathwaysService _pathways;
	private readonly IGlobalCommandsService _commands;
	private readonly Lazy<(ModSyncIdentity Identity, FileHashCache Hashes, ModSyncHostService Host)> _core;
	private CancellationTokenSource? _syncCts;
	private ModSyncConnection? _connection;
	private SyncPlan? _plan;
	private volatile bool _scanning;

	private static string DataFolder => DivinityApp.GetAppDirectory("Data", "ModSync");
	private static string ClientStateFile => IOPath.Join(DataFolder, "client.json");

	#region Host
	[Reactive] public partial bool IsHosting { get; private set; }
	[Reactive] public partial bool IsHostBusy { get; private set; }
	[Reactive] public partial int Port { get; set; }
	[Reactive] public partial bool UseNatPortMapping { get; set; }
	[Reactive] public partial string? ExtraAddressesText { get; set; }
	[Reactive] public partial string? HostStatus { get; private set; }
	[Reactive] public partial string? NatStatus { get; private set; }
	[Reactive] public partial string? SharedSummary { get; private set; }
	[Reactive] public partial string? InviteCode { get; private set; }
	public ObservableCollection<SyncAddressRow> Addresses { get; } = [];
	public ObservableCollection<string> Activity { get; } = [];

	public RxCommandUnit StartHostingCommand { get; }
	public RxCommandUnit StopHostingCommand { get; }
	public RxCommandUnit CopyInviteCommand { get; }
	public RxCommandUnit RegenerateTokenCommand { get; }
	public RxCommandUnit RefreshSharedCommand { get; }
	#endregion

	#region Join
	[Reactive] public partial string? InviteInput { get; set; }
	[Reactive] public partial bool IsConnecting { get; private set; }
	[Reactive] public partial bool IsSyncing { get; private set; }
	[Reactive] public partial bool HasPlan { get; private set; }
	[Reactive] public partial string? JoinStatus { get; private set; }
	[Reactive] public partial string? PlanSummary { get; private set; }
	[Reactive] public partial bool ExportToGame { get; set; }
	[Reactive] public partial SyncTargetOption? SyncTarget { get; set; }
	[Reactive] public partial string? NewOrderName { get; set; }
	[ObservableAsProperty] public partial bool IsNewOrderTarget { get; }
	public ObservableCollection<SyncTargetOption> SyncTargets { get; } = [];
	private string? _savedSyncTarget;
	private string? _hostName;
	/// <summary>Last auto-filled NewOrderName; replaced when the host changes unless the user typed their own.</summary>
	private string? _suggestedName;
	[Reactive] public partial double ProgressPercent { get; private set; }
	[Reactive] public partial string? ProgressText { get; private set; }
	[Reactive] public partial string? LastBackupFolder { get; private set; }
	public ObservableCollection<SyncPlanRow> PlanRows { get; } = [];

	public RxCommandUnit ConnectCommand { get; }
	public RxCommandUnit SyncCommand { get; }
	public RxCommandUnit CancelCommand { get; }
	public RxCommandUnit OpenBackupFolderCommand { get; }
	#endregion

	/// <summary>Handled by the window: puts text on the clipboard (kept out of toasts/logs since invites are secrets).</summary>
	public Interaction<string, Unit> CopyToClipboard { get; } = new();
	public ReactiveCommand<ShowAlertRequest, ShowAlertRequest> ShowAlertCommand { get; }

	private void Alert(string message, AlertType type, int timeout = 10) =>
		RxApp.MainThreadScheduler.Schedule(() => ShowAlertCommand.Execute(new ShowAlertRequest(message, type, timeout)).Subscribe());

	private static string FormatSize(long bytes) => bytes switch
	{
		>= 1L << 30 => $"{bytes / (double)(1L << 30):0.00} GB",
		>= 1L << 20 => $"{bytes / (double)(1L << 20):0.0} MB",
		>= 1L << 10 => $"{bytes / 1024d:0} KB",
		_ => $"{bytes} B",
	};

	public ModSyncWindowViewModel(IModManagerService manager, IPathwaysService pathways, IGlobalCommandsService commands, IScreen? host = null)
	{
		HostScreen = host ?? AppLocator.Current.GetService<IScreen>()!;
		CloseCommand = this.CreateCloseCommand();
		_manager = manager;
		_pathways = pathways;
		_commands = commands;
		ShowAlertCommand = ReactiveCommand.Create<ShowAlertRequest, ShowAlertRequest>(r => r);

		_core = new(() =>
		{
			var identity = ModSyncIdentity.LoadOrCreate(DataFolder);
			var hashes = new FileHashCache(IOPath.Join(DataFolder, "hashcache.json"));
			var hostService = new ModSyncHostService(identity, hashes);
			hostService.Activity += line => RxApp.MainThreadScheduler.Schedule(() =>
			{
				Activity.Insert(0, line);
				while (Activity.Count > 200) Activity.RemoveAt(Activity.Count - 1);
			});
			hostService.NatStatusChanged += () => RxApp.MainThreadScheduler.Schedule(() =>
			{
				NatStatus = hostService.Nat?.Status;
				RefreshInvite();
			});
			return (identity, hashes, hostService);
		});

		// Host settings come from the persisted identity; the Join options from client.json.
		var settings = TryGetCore()?.Identity.Settings ?? new ModSyncHostSettings();
		Port = settings.Port;
		UseNatPortMapping = settings.UseNatPortMapping;
		ExtraAddressesText = string.Join(", ", settings.ExtraAddresses);
		HostStatus = null;
		LoadClientState();

		var notBusy = this.WhenAnyValue(x => x.IsHosting, x => x.IsHostBusy, (h, b) => !h && !b);
		StartHostingCommand = ReactiveCommand.CreateFromTask(StartHostingAsync, notBusy);
		StopHostingCommand = ReactiveCommand.CreateFromTask(StopHostingAsync, this.WhenAnyValue(x => x.IsHosting, x => x.IsHostBusy, (h, b) => h && !b));
		CopyInviteCommand = ReactiveCommand.CreateFromTask(async () =>
		{
			if (string.IsNullOrEmpty(InviteCode)) return;
			await CopyToClipboard.Handle(InviteCode);
			Alert("Invite code copied.", AlertType.Success, 5);
		}, this.WhenAnyValue(x => x.InviteCode).Select(x => !string.IsNullOrEmpty(x)));
		RegenerateTokenCommand = ReactiveCommand.Create(() =>
		{
			TryGetCore()?.Identity.RegenerateToken();
			RefreshInvite();
			Alert("New invite code created.", AlertType.Info, 5);
		});
		RefreshSharedCommand = ReactiveCommand.CreateFromTask(RefreshSharedAsync, this.WhenAnyValue(x => x.IsHosting));

		var canConnect = this.WhenAnyValue(x => x.InviteInput, x => x.IsConnecting, x => x.IsSyncing, (i, c, s) => !string.IsNullOrWhiteSpace(i) && !c && !s);
		ConnectCommand = ReactiveCommand.CreateFromTask(ConnectAsync, canConnect);
		SyncCommand = ReactiveCommand.CreateFromTask(SyncAsync, this.WhenAnyValue(x => x.HasPlan, x => x.IsSyncing, x => x.IsConnecting, (p, s, c) => p && !s && !c));
		CancelCommand = ReactiveCommand.Create(() => _syncCts?.Cancel(), this.WhenAnyValue(x => x.IsSyncing, x => x.IsConnecting, (s, c) => s || c));
		OpenBackupFolderCommand = ReactiveCommand.Create(() => { _commands.OpenInFileExplorer(LastBackupFolder); }, this.WhenAnyValue(x => x.LastBackupFolder).Select(x => x != null));

		// Keep the shared set current while hosting: re-snapshot when the active list changes.
		ViewModelLocator.ModOrder.ActiveMods.ToObservableChangeSet()
			.Throttle(TimeSpan.FromSeconds(2))
			.ObserveOn(RxApp.MainThreadScheduler)
			.Where(_ => IsHosting)
			.Select(_ => Unit.Default) // RefreshSharedCommand takes no parameter; passing the change set crashes the app
			.InvokeCommand(RefreshSharedCommand);

		// Stop hosting on exit so the router port mapping is removed rather than left open until its lease expires.
		if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
		{
			desktop.Exit += (_, _) =>
			{
				try { ShutdownAsync().Wait(TimeSpan.FromSeconds(4)); }
				catch (Exception ex) { DivinityApp.Log($"Error stopping mod sync on exit: {ex.Message}"); }
			};
		}

		_isNewOrderTargetHelper = this.WhenAnyValue(x => x.SyncTarget).Select(t => t?.Order == null).ToUIPropertyImmediate(this, x => x.IsNewOrderTarget, true);

		this.WhenAnyValue(x => x.ExportToGame).Skip(1).Subscribe(_ => SaveClientState());
		this.WhenAnyValue(x => x.SyncTarget).Skip(1).WhereNotNull().Subscribe(t =>
		{
			_savedSyncTarget = t.Order?.FilePath;
			SaveClientState();
		});
		this.WhenAnyValue(x => x.IsVisible).Where(v => v).Subscribe(_ =>
		{
			if (!IsHosting) RefreshAddresses();
			RefreshSyncTargets();
		});
	}

	private (ModSyncIdentity Identity, FileHashCache Hashes, ModSyncHostService Host)? TryGetCore()
	{
		try { return _core.Value; }
		catch (Exception ex)
		{
			DivinityApp.Log($"Failed to initialize mod sync: {ex}");
			HostStatus = $"Mod sync couldn't start: {ex.Message}";
			return null;
		}
	}

	#region Host
	private void SaveHostSettings()
	{
		if (TryGetCore() is not { } core) return;
		var s = core.Identity.Settings;
		s.Port = Port is >= 1024 and <= 65535 ? Port : ModSyncHostSettings.DefaultPort;
		Port = s.Port;
		s.UseNatPortMapping = UseNatPortMapping;
		s.ExtraAddresses = [.. (ExtraAddressesText ?? "").Split([',', ';', ' ', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
		core.Identity.Save();
	}

	private void RefreshAddresses()
	{
		Addresses.Clear();
		if (TryGetCore() is not { } core) return;
		SaveHostSettings();
		foreach (var a in core.Host.GetInviteAddresses()) Addresses.Add(new SyncAddressRow(a.Address, a.KindLabel, a.Source));
	}

	private void RefreshInvite()
	{
		RefreshAddresses();
		if (TryGetCore() is { } core && core.Host.IsRunning)
		{
			InviteCode = Addresses.Count > 0 ? core.Host.CreateInvite().Encode() : null;
		}
		else
		{
			InviteCode = null;
		}
	}

	private (List<SharedModFile> Files, List<SyncOrderEntry> Order) CollectShared()
	{
		var modsDir = _pathways.Data.AppDataModsPath;
		var modsFull = modsDir.IsValid() ? IOPath.GetFullPath(modsDir!) : null;
		List<SharedModFile> files = [];
		List<SyncOrderEntry> order = [];

		bool IsSharable(ModData m) => !m.IsLarianMod && m.FilePath.IsValid()
			&& m.FilePath!.EndsWith(".pak", StringComparison.OrdinalIgnoreCase)
			&& modsFull != null && string.Equals(IOPath.GetDirectoryName(IOPath.GetFullPath(m.FilePath)), modsFull, StringComparison.Ordinal)
			&& IOFile.Exists(m.FilePath);

		void Visit(IEnumerable<IModEntry> entries)
		{
			foreach (var entry in entries)
			{
				if (entry is ModEntry { Data: ModData m })
				{
					// Skip override paks without meta.lsx (placeholder, non-GUID UUIDs like "Patch8_HotFix9"); they never belong in a load order.
					if (Guid.TryParse(m.UUID, out _)) order.Add(new SyncOrderEntry(m.UUID, m.DisplayName ?? m.Name ?? m.UUID));
					if (IsSharable(m)) files.Add(new SharedModFile(m.FilePath!, m.UUID, m.DisplayName ?? m.Name, m.Version?.ToString()));
				}
				else if (entry.Children != null)
				{
					Visit(entry.Children);
				}
			}
		}
		Visit(ViewModelLocator.ModOrder.ActiveMods);

		// Override paks in the Mods folder always load (they aren't part of the order), so share them too.
		foreach (var m in _manager.OverridePakMods)
		{
			if (IsSharable(m) && !files.Any(f => f.FullPath == m.FilePath)) files.Add(new SharedModFile(m.FilePath!, m.UUID, m.DisplayName ?? m.Name, m.Version?.ToString()));
		}
		return (files, order);
	}

	private async Task RefreshSharedAsync(CancellationToken token)
	{
		if (TryGetCore() is not { } core) return;
		var (files, order) = CollectShared();
		SharedSummary = $"Reading {files.Count} mods...";
		try
		{
			var manifest = await Task.Run(() => core.Host.UpdateSharedSetAsync(files, order, token), token);
			SharedSummary = $"Sharing {manifest.Files.Count} mods ({FormatSize(manifest.Files.Sum(x => x.Size))}).";
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			SharedSummary = $"Couldn't read mods: {ex.Message}";
		}
	}

	private async Task StartHostingAsync(CancellationToken token)
	{
		if (TryGetCore() is not { } core) return;
		IsHostBusy = true;
		try
		{
			SaveHostSettings();
			await RefreshSharedAsync(token);
			HostStatus = "Starting...";
			NatStatus = UseNatPortMapping ? "Searching for router..." : null;
			await core.Host.StartAsync(token);
			IsHosting = true;
			HostStatus = $"Hosting on port {core.Host.Port}.";
			RefreshInvite();
		}
		catch (Exception ex)
		{
			HostStatus = ex.Message;
			Alert(ex.Message, AlertType.Danger, 20);
		}
		finally
		{
			IsHostBusy = false;
		}
	}

	private async Task StopHostingAsync()
	{
		if (TryGetCore() is not { } core) return;
		IsHostBusy = true;
		try
		{
			await core.Host.StopAsync();
		}
		finally
		{
			IsHosting = false;
			IsHostBusy = false;
			InviteCode = null;
			HostStatus = null;
			NatStatus = null;
			RefreshAddresses();
		}
	}

	public async Task ShutdownAsync()
	{
		if (_core.IsValueCreated) await _core.Value.Host.StopAsync();
	}
	#endregion

	#region Join
	private void LoadClientState()
	{
		ExportToGame = true;
		try
		{
			if (!IOFile.Exists(ClientStateFile)) return;
			var state = JsonSerializer.Deserialize<ClientState>(IOFile.ReadAllText(ClientStateFile));
			if (state == null) return;
			InviteInput = state.LastInvite;
			ExportToGame = state.ExportToGame;
			_savedSyncTarget = state.SyncTarget;
		}
		catch (Exception ex)
		{
			DivinityApp.Log($"Failed to read '{ClientStateFile}': {ex.Message}");
		}
	}

	private void SaveClientState()
	{
		try
		{
			IODirectory.CreateDirectory(DataFolder);
			IOFile.WriteAllText(ClientStateFile, JsonSerializer.Serialize(new ClientState(InviteInput?.Trim(), ExportToGame, _savedSyncTarget)));
			if (!OperatingSystem.IsWindows()) IOFile.SetUnixFileMode(ClientStateFile, System.IO.UnixFileMode.UserRead | System.IO.UnixFileMode.UserWrite);
		}
		catch (Exception ex)
		{
			DivinityApp.Log($"Failed to save '{ClientStateFile}': {ex.Message}");
		}
	}

	/// <summary>Rebuilds the "Sync to" dropdown from the saved orders, keeping the remembered choice selected.</summary>
	private void RefreshSyncTargets()
	{
		var orders = ViewModelLocator.ModOrder.ExternalModOrders
			.Where(o => o.FilePath.IsValid())
			.OrderBy(o => o.Name, StringComparer.OrdinalIgnoreCase)
			.ToList();
		var wanted = _savedSyncTarget;

		SyncTargets.Clear();
		SyncTargets.Add(new SyncTargetOption("Create new order", null));
		foreach (var o in orders) SyncTargets.Add(new SyncTargetOption(o.Name ?? IOPath.GetFileNameWithoutExtension(o.FilePath!), o));

		SyncTarget = SyncTargets.FirstOrDefault(t => t.Order != null && wanted != null && SamePath(t.Order.FilePath, wanted)) ?? SyncTargets[0];
		UpdateSuggestedName();
	}

	private void UpdateSuggestedName()
	{
		if (NewOrderName.IsValid() && NewOrderName != _suggestedName) return;
		_suggestedName = SuggestNewOrderName();
		NewOrderName = _suggestedName;
	}

	private static bool SamePath(string? a, string? b) => a.IsValid() && b.IsValid()
		&& string.Equals(IOPath.GetFullPath(a!), IOPath.GetFullPath(b!), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

	private string SuggestNewOrderName()
	{
		var baseName = $"Sync_{(_hostName.IsValid() ? _hostName : "Host")}";
		var name = baseName;
		for (var i = 2; OrderNameTaken(name); i++) name = $"{baseName} {i}";
		return name;
	}

	private static bool OrderNameTaken(string name)
	{
		var modOrder = ViewModelLocator.ModOrder;
		var path = modOrder.GetOrderFilePath(name);
		return IOFile.Exists(path) || modOrder.ExternalModOrders.Any(o => SamePath(o.FilePath, path));
	}

	private async Task<List<LocalModFile>> ScanLocalModsAsync(string modsDir, FileHashCache hashes, CancellationToken token)
	{
		// Map pak paths to mod UUIDs using what the manager has already loaded.
		var uuidByPath = _manager.AllMods
			.Where(m => m.FilePath.IsValid() && !m.IsLarianMod)
			.GroupBy(m => IOPath.GetFullPath(m.FilePath!))
			.ToDictionary(g => g.Key, g => g.First().UUID);

		List<LocalModFile> local = [];
		if (!IODirectory.Exists(modsDir)) return local;
		_scanning = true;
		try
		{
		var paks = IODirectory.EnumerateFiles(modsDir, "*.pak").ToList();
		for (var i = 0; i < paks.Count; i++)
		{
			token.ThrowIfCancellationRequested();
			var path = IOPath.GetFullPath(paks[i]);
			var idx = i + 1;
			RxApp.MainThreadScheduler.Schedule(() => { if (_scanning) JoinStatus = $"Checking your mods ({idx}/{paks.Count})..."; });
			var sha = await hashes.GetSha256Async(path, token);
			local.Add(new LocalModFile(path, sha, new System.IO.FileInfo(path).Length, uuidByPath.GetValueOrDefault(path)));
		}
		hashes.Save();
		return local;
		}
		finally
		{
			_scanning = false;
		}
	}

	private async Task ConnectAsync()
	{
		if (!ModSyncInvite.TryDecode(InviteInput, out var invite, out var error))
		{
			JoinStatus = error;
			return;
		}
		if (TryGetCore() is not { } core) return;
		var modsDir = _pathways.Data.AppDataModsPath;
		if (!modsDir.IsValid())
		{
			JoinStatus = "Mods folder not found. Check Settings.";
			return;
		}

		_syncCts?.Dispose();
		_syncCts = new CancellationTokenSource();
		var token = _syncCts.Token;
		IsConnecting = true;
		HasPlan = false;
		PlanRows.Clear();
		PlanSummary = null;
		_connection?.Dispose();
		_connection = null;
		try
		{
			JoinStatus = $"Connecting to {(invite!.HostName.Length > 0 ? invite.HostName : "host")}...";
			var (conn, manifest) = await ModSyncConnection.ConnectAsync(invite, token);
			_connection = conn;
			SaveClientState();
			if (manifest.HostName.IsValid() && manifest.HostName != _hostName)
			{
				_hostName = manifest.HostName;
				UpdateSuggestedName();
			}

			var local = await Task.Run(() => ScanLocalModsAsync(modsDir!, core.Hashes, token), token);
			var plan = ModSyncPlanner.BuildPlan(manifest, local);
			_plan = plan;
			ShowPlan(plan);
			HasPlan = true;
			JoinStatus = $"Connected to {manifest.HostName}.";
		}
		catch (OperationCanceledException)
		{
			JoinStatus = "Cancelled.";
		}
		catch (Exception ex)
		{
			JoinStatus = ex.Message;
			DivinityApp.Log($"Mod sync connect failed: {ex}");
		}
		finally
		{
			IsConnecting = false;
		}
	}

	private void ShowPlan(SyncPlan plan)
	{
		PlanRows.Clear();
		foreach (var a in plan.Actions.OrderBy(x => x.Kind == SyncActionKind.UpToDate).ThenBy(x => x.Kind).ThenBy(x => x.DisplayName, StringComparer.OrdinalIgnoreCase))
		{
			var (status, color) = a.Kind switch
			{
				SyncActionKind.Download => ("New", "#4CAF50"),
				SyncActionKind.Update => ("Update", "#2196F3"),
				SyncActionKind.Replace => ("Replace", "#FF9800"),
				SyncActionKind.Extra => ("Only you have (kept)", "#9E9E9E"),
				_ => ("Up to date", "#607D8B"),
			};
			var size = a.Remote != null ? FormatSize(a.Remote.Size) : a.Local != null ? FormatSize(a.Local.Size) : "";
			PlanRows.Add(new SyncPlanRow(status, a.DisplayName, a.Remote?.FileName ?? a.Local?.FileName ?? "", size, color));
		}

		var downloads = plan.Downloads.Count();
		PlanSummary = plan.IsUpToDate
			? "Your mods already match."
			: $"{downloads} to download ({FormatSize(plan.DownloadBytes)}).";
	}

	private async Task SyncAsync()
	{
		if (_connection == null || _plan == null || TryGetCore() is not { } core) return;
		var target = SyncTarget?.Order;
		var newName = NewOrderName?.Trim();
		if (target == null)
		{
			if (!newName.IsValid())
			{
				JoinStatus = "Enter a name for the new order.";
				return;
			}
			if (OrderNameTaken(newName!))
			{
				JoinStatus = $"'{newName}' already exists.";
				return;
			}
		}
		var pathways = _pathways.Data;
		var modsDir = pathways.AppDataModsPath!;
		var gameFolder = pathways.AppDataGameFolder.IsValid() ? pathways.AppDataGameFolder! : IOPath.GetDirectoryName(modsDir)!;
		var syncFolder = IOPath.Join(gameFolder, "ModSync");

		_syncCts?.Dispose();
		_syncCts = new CancellationTokenSource();
		var token = _syncCts.Token;
		IsSyncing = true;
		ProgressPercent = 0;
		var progress = new Progress<SyncProgress>(p =>
		{
			ProgressPercent = p.Percent;
			ProgressText = p.FileCount == 0 ? "" : $"{p.CurrentFile}  ({p.FileIndex}/{p.FileCount}, {FormatSize(p.BytesDone)} / {FormatSize(p.BytesTotal)})";
		});

		try
		{
			// Refetch the manifest so we sync against the host's current state, not what it was when we connected.
			JoinStatus = "Checking host...";
			var manifest = await _connection.GetManifestAsync(token);
			var plan = manifest.Created == _plan.Manifest.Created ? _plan
				: ModSyncPlanner.BuildPlan(manifest, await Task.Run(() => ScanLocalModsAsync(modsDir, core.Hashes, token), token));
			_plan = plan;
			ShowPlan(plan);

			JoinStatus = "Downloading...";
			var result = await Task.Run(() => ModSyncExecutor.ExecuteAsync(_connection, plan, modsDir, IOPath.Join(syncFolder, "Downloads"), IOPath.Join(syncFolder, "Backups"),
				core.Hashes, progress, token), token);
			LastBackupFolder = result.BackupFolder;

			var summary = $"Synced: {result.Downloaded} downloaded" + (result.MovedToBackup > 0 ? $", {result.MovedToBackup} moved to backup." : ".");
			JoinStatus = "Reloading mods...";
			await ReloadModsAsync(token);

			// Make sure the manager actually picked up what we downloaded. On Windows, antivirus scans of freshly written
			// paks can briefly lock them, so the first reload may silently skip them; retry a few times before giving up.
			var expected = plan.Downloads.Select(d => d.Remote!.ModUuid).Where(u => Guid.TryParse(u, out _)).Select(u => u!).ToHashSet(StringComparer.OrdinalIgnoreCase);
			for (var attempt = 1; attempt <= 3 && expected.Any(u => !_manager.ModExists(u)); attempt++)
			{
				var notYet = plan.Downloads.Where(d => d.Remote!.ModUuid != null && expected.Contains(d.Remote.ModUuid) && !_manager.ModExists(d.Remote.ModUuid)).Select(d => d.DisplayName).ToList();
				DivinityApp.Log($"Mod sync: {notYet.Count} downloaded mod(s) not loaded yet ({string.Join(", ", notYet)}); reloading again (attempt {attempt}/3).");
				JoinStatus = $"Waiting for new mods ({attempt}/3)...";
				await Task.Delay(TimeSpan.FromSeconds(2 * attempt), token);
				await ReloadModsAsync(token);
			}
			var stillMissing = plan.Downloads.Where(d => d.Remote!.ModUuid != null && expected.Contains(d.Remote.ModUuid) && !_manager.ModExists(d.Remote.ModUuid)).Select(d => d.Remote!.FileName).ToList();
			if (stillMissing.Count > 0)
			{
				summary += $" Couldn't load {string.Join(", ", stillMissing)}; press Refresh.";
			}

			var (orderName, missing) = await ApplyHostLoadOrderAsync(plan.Manifest, target, newName, token);
			summary += $" Order: {orderName}.";
			if (missing.Count > 0) summary += $" Missing: {string.Join(", ", missing.Take(5))}{(missing.Count > 5 ? ", ..." : "")}.";
			if (ExportToGame)
			{
				if (!await ViewModelLocator.ModOrder.ExportLoadOrderAsync(token)) summary += " Export to game failed.";
			}
			JoinStatus = summary;
			Alert(summary, AlertType.Success, 15);
			HasPlan = false;
			PlanRows.Clear();
			PlanSummary = null;
		}
		catch (OperationCanceledException)
		{
			JoinStatus = "Sync cancelled. Nothing was changed.";
		}
		catch (Exception ex)
		{
			JoinStatus = $"Sync failed: {ex.Message}";
			DivinityApp.Log($"Mod sync failed: {ex}");
		}
		finally
		{
			IsSyncing = false;
		}
	}

	private static async Task ReloadModsAsync(CancellationToken token)
	{
		var main = ViewModelLocator.Main;
		main.RefreshStart();
		// RefreshStart sets IsRefreshing synchronously and finishes in the background; wait for it before touching the order.
		if (!main.IsRefreshing) return;
		await main.WhenAnyValue(x => x.IsRefreshing).Where(r => !r).Take(1).Timeout(TimeSpan.FromMinutes(5)).ToTask(token);
	}

	/// <summary>
	/// Writes the host's load order into <paramref name="target"/> (replacing its contents), or into a new order named
	/// <paramref name="newName"/> when target is null, then selects it. No other order is touched.
	/// </summary>
	private async Task<(string Name, List<string> Missing)> ApplyHostLoadOrderAsync(SyncManifest manifest, ModOrder? target, string? newName, CancellationToken token)
	{
		var modOrder = ViewModelLocator.ModOrder;
		// The target may have been deleted or the list reloaded (sync reloads mods); find it again by path.
		if (target != null) target = modOrder.ExternalModOrders.FirstOrDefault(o => SamePath(o.FilePath, target.FilePath)) ?? target;
		var path = target?.FilePath ?? modOrder.GetOrderFilePath(newName!);
		var name = target?.Name ?? IOPath.GetFileNameWithoutExtension(path);

		var order = new ModOrder { Name = name, FilePath = path };
		List<string> missing = [];
		foreach (var entry in manifest.LoadOrder)
		{
			if (_manager.TryGetMod(entry.Uuid, out var mod)) order.Add(mod);
			else missing.Add(entry.Name);
		}

		IODirectory.CreateDirectory(modOrder.GetOrdersDirectory());
		if (!await ModDataLoader.ExportLoadOrderToFileAsync(path, order, token))
		{
			throw new InvalidOperationException($"Couldn't save '{name}'.");
		}

		var existing = modOrder.ExternalModOrders.FirstOrDefault(x => SamePath(x.FilePath, path));
		if (existing != null)
		{
			existing.CopyOrder(order);
		}
		else
		{
			modOrder.AddNewModOrder(order);
			existing = order;
		}
		modOrder.SelectOrder(existing);

		// Next sync goes to the same order by default.
		_savedSyncTarget = path;
		RxApp.MainThreadScheduler.Schedule(() =>
		{
			NewOrderName = null;
			RefreshSyncTargets();
			SaveClientState();
		});
		return (name, missing);
	}
	#endregion
}
