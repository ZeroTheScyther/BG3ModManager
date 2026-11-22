using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;

namespace ModManager.Launcher;

internal sealed class SplashModel(Updater updater, ReleaseInfo? release)
{
	public Updater Updater { get; } = updater;
	public ReleaseInfo? Release { get; } = release;

	/// <summary>Start the mod manager once the splash closes.</summary>
	public bool StartApp { get; set; }

	/// <summary>A new version was installed this run.</summary>
	public bool Updated { get; set; }
}

internal sealed class LauncherApp(SplashModel model) : Application
{
	public override void Initialize()
	{
		Styles.Add(new FluentTheme());
		RequestedThemeVariant = ThemeVariant.Dark;
	}

	public override void OnFrameworkInitializationCompleted()
	{
		if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
		{
			desktop.MainWindow = new SplashWindow(model);
		}
		base.OnFrameworkInitializationCompleted();
	}
}

internal sealed class SplashWindow : Window
{
	private readonly SplashModel _model;
	private readonly TextBlock _status = new() { FontSize = 15 };
	private readonly TextBlock _detail = new() { FontSize = 12, Opacity = 0.7 };
	private readonly ProgressBar _progress = new() { Minimum = 0, Maximum = 1, IsIndeterminate = true, Height = 6 };
	private readonly StackPanel _buttons = new() { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, IsVisible = false };
	private readonly CancellationTokenSource _cts = new();
	private bool _installing;

	public SplashWindow(SplashModel model)
	{
		_model = model;
		Title = "BG3 Mod Manager";
		Width = 380;
		SizeToContent = SizeToContent.Height;
		CanResize = false;
		WindowStartupLocation = WindowStartupLocation.CenterScreen;
		Background = new SolidColorBrush(Color.FromRgb(0x1e, 0x1e, 0x22));

		using (var icon = typeof(SplashWindow).Assembly.GetManifestResourceStream("icon.png"))
		{
			if (icon != null)
			{
				var bitmap = new Bitmap(icon);
				Icon = new WindowIcon(bitmap);
				_image.Source = bitmap;
			}
		}

		Content = new DockPanel
		{
			Margin = new Thickness(20),
			Children =
			{
				_image,
				new StackPanel
				{
					Spacing = 10,
					VerticalAlignment = VerticalAlignment.Center,
					Children = { _status, _progress, _detail, _buttons },
				},
			},
		};
		DockPanel.SetDock(_image, Dock.Left);

		Opened += (_, _) => _ = RunAsync();
		Closing += (_, e) =>
		{
			// Swapping folders can't be interrupted; downloading can.
			if (_installing) e.Cancel = true;
			else _cts.Cancel();
		};
	}

	private readonly Image _image = new() { Width = 48, Height = 48, Margin = new Thickness(0, 0, 16, 0), VerticalAlignment = VerticalAlignment.Top };

	private async Task RunAsync()
	{
		var release = _model.Release;
		if (release == null)
		{
			ShowError("Couldn't download the mod manager.", canStart: false);
			return;
		}

		_status.Text = $"Updating to {release.Tag}";
		try
		{
			await _model.Updater.InstallAsync(release, OnProgress, OnStatus, _cts.Token);
			_installing = false;
			_model.Updated = true;
			_model.StartApp = true;
			Close();
		}
		catch (OperationCanceledException)
		{
			Log.Write("Update cancelled");
		}
		catch (Exception ex)
		{
			Log.Write($"Update failed: {ex}");
			ShowError("Update failed.", canStart: _model.Updater.AppInstalled);
		}
	}

	private void OnStatus(string text) => Dispatcher.UIThread.Post(() =>
	{
		_installing = text != "Downloading…";
		_detail.Text = _installing ? text : _detail.Text;
		_progress.IsIndeterminate = _installing;
	});

	private long _lastReport;

	private void OnProgress(long received, long total)
	{
		var now = Environment.TickCount64;
		if (now - _lastReport < 50 && received < total) return;
		_lastReport = now;
		Dispatcher.UIThread.Post(() =>
		{
			_progress.IsIndeterminate = total <= 0;
			if (total > 0) _progress.Value = (double)received / total;
			_detail.Text = total > 0 ? $"{received / 1048576.0:0.0} / {total / 1048576.0:0.0} MB" : $"{received / 1048576.0:0.0} MB";
		});
	}

	private void ShowError(string message, bool canStart)
	{
		_installing = false;
		_status.Text = message;
		_detail.Text = "See launcher.log for details.";
		_progress.IsVisible = false;
		_buttons.Children.Clear();
		if (canStart)
		{
			var start = new Button { Content = "Start anyway" };
			start.Click += (_, _) =>
			{
				_model.StartApp = true;
				Close();
			};
			_buttons.Children.Add(start);
		}
		var close = new Button { Content = "Close" };
		close.Click += (_, _) => Close();
		_buttons.Children.Add(close);
		_buttons.IsVisible = true;
	}
}
