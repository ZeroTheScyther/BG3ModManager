using Avalonia;

using ModManager.Utils;

using ReactiveUI.Avalonia;
using ReactiveUI.Builder;

using System;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text;

namespace ModManager;

internal class Program
{
	private static bool EnsureSingleInstance(string[] args)
	{
		if (IsAnotherInstanceRunning())
		{
			if (args.Length > 0)
			{
				var argsMessage = string.Join(" ", args);
				try
				{
					using var pipe = new NamedPipeClientStream(".", DivinityApp.PIPE_ID,
					PipeDirection.Out, PipeOptions.WriteThrough, System.Security.Principal.TokenImpersonationLevel.Impersonation);
					pipe.Connect(500);
					using var sw = new System.IO.StreamWriter(pipe, Encoding.UTF8);
					sw.Write(argsMessage);
					sw.Flush();
				}
				catch (Exception ex)
				{
					Console.WriteLine($"Error sending args to server:\n{ex}");
				}
#if DEBUG
				return true;
#endif
			}
#if !DEBUG
			return true;
#endif
		}
		return false;
	}

	/// <summary>
	/// Same-named processes only count if they run this exe: the launcher (updater) is also called BG3ModManager and
	/// is still alive while it starts us.
	/// </summary>
	private static bool IsAnotherInstanceRunning()
	{
		using var self = Process.GetCurrentProcess();
		var selfPath = Environment.ProcessPath;
		var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
		foreach (var p in Process.GetProcessesByName(self.ProcessName))
		{
			using (p)
			{
				if (p.Id == self.Id) continue;
				try
				{
					if (selfPath == null || string.Equals(p.MainModule?.FileName, selfPath, comparison)) return true;
				}
				catch
				{
					// Can't inspect it (other user, exiting); not ours.
				}
			}
		}
		return false;
	}

	[STAThread]
	public static void Main(string[] args)
	{
		//Only close if args are passed in and we're a debug build,
		//otherwise always close if another instance exists in release
		if (EnsureSingleInstance(args))
		{
			Environment.Exit(0);
			return;
		}
		AppDomain.CurrentDomain.UnhandledException += (_, e) =>
		{
			if (e.ExceptionObject is Exception ex) WriteCrashLog(ex);
		};
		try
		{
			BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
		}
		catch (Exception ex)
		{
			WriteCrashLog(ex);
			throw;
		}
	}

	private static bool _crashLogged;

	/// <summary>
	/// Always writes _Logs/crash_*.log, independent of Debug Mode, so hard crashes leave a trace.
	/// </summary>
	private static void WriteCrashLog(Exception ex)
	{
		if (_crashLogged) return;
		_crashLogged = true;
		try
		{
			var dir = System.IO.Path.Join(AppContext.BaseDirectory, "_Logs");
			System.IO.Directory.CreateDirectory(dir);
			var file = System.IO.Path.Join(dir, $"crash_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.log");
			var version = typeof(Program).Assembly.GetName().Version;
			System.IO.File.WriteAllText(file,
				$"Version: {version}\nOS: {Environment.OSVersion} ({System.Runtime.InteropServices.RuntimeInformation.OSDescription})\n" +
				$"Runtime: {Environment.Version}\nTime: {DateTime.Now:O}\n\n{ex}\n");
			Console.Error.WriteLine($"Crash log written to {file}");
		}
		catch
		{
			// Nothing else we can do while crashing.
		}
	}

	public static AppBuilder BuildAvaloniaApp()
	{
		return AppBuilder.Configure<App>()
			.UsePlatformDetect()
			.WithInterFont()
			.LogToTrace()
			.UseReactiveUI(config =>
			{
				config
				.WithPlatformServices()
				.WithExceptionHandler(new ExceptionSuppressionHandler());
			});
	}
}
