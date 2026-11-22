using Avalonia;

namespace ModManager.Launcher;

internal static class Program
{
	private const string NoUpdateArg = "--no-update";

	[STAThread]
	public static int Main(string[] args)
	{
		var updater = new Updater();
		Log.Open(Path.Join(updater.Root, "launcher.log"));
		var appArgs = args.Where(a => a != NoUpdateArg).ToArray();

		try
		{
			updater.Prepare();
		}
		catch (Exception ex)
		{
			Log.Write($"Prepare failed: {ex}");
		}

		ReleaseInfo? release = null;
		if (!args.Contains(NoUpdateArg) && !(updater.AppInstalled && updater.IsAppRunning()))
		{
			release = CheckForUpdate(updater);
		}

		if (release == null)
		{
			if (updater.AppInstalled)
			{
				updater.Launch(appArgs)?.Dispose();
				return 0;
			}
			Log.Write("Mod manager not installed and no release available");
		}

		// Updating (or installing, or reporting that we can't): show the splash.
		var splash = new SplashModel(updater, release);
		var code = AppBuilder.Configure(() => new LauncherApp(splash))
			.UsePlatformDetect()
			.StartWithClassicDesktopLifetime(appArgs);

		if (splash.StartApp && updater.AppInstalled)
		{
			var process = updater.Launch(appArgs);
			if (splash.Updated) updater.WatchAfterUpdate(process, appArgs);
			else process?.Dispose();
		}
		return code;
	}

	private static ReleaseInfo? CheckForUpdate(Updater updater)
	{
		try
		{
			using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(updater.AppInstalled ? 4 : 30));
			var release = updater.GetLatestReleaseAsync(cts.Token).GetAwaiter().GetResult();
			if (release == null) return null;

			var installed = updater.GetInstalledVersion();
			Log.Write($"Installed {installed?.ToString() ?? "none"}, latest {release.Tag}");
			if (installed != null && Updater.Normalize(installed) >= release.Version) return null;
			if (installed != null && updater.IsSkipped(release))
			{
				Log.Write($"Skipping {release.Tag} (failed to start before)");
				return null;
			}
			return release;
		}
		catch (Exception ex)
		{
			// Offline, GitHub down, rate limited: just start what we have.
			Log.Write($"Update check failed: {ex.Message}");
			return null;
		}
	}
}
