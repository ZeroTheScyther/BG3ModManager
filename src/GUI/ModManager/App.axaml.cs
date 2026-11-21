using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;

using ModManager.Services;
using ModManager.Utils;
using ModManager.Windows;

using SukiUI;

using System.Reflection;

namespace ModManager;
public partial class App : Application
{
	private System.Runtime.InteropServices.PosixSignalRegistration? _sigterm;

	public override void Initialize() => AvaloniaXamlLoader.Load(this);

	public IClassicDesktopStyleApplicationLifetime? DesktopLifetime => ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;

	public void Shutdown(int errorCode = 0)
	{
		DesktopLifetime?.Shutdown(errorCode);
	}

	public static new App Current => (App)Application.Current!;

	public override void OnFrameworkInitializationCompleted()
	{
		//Locale.Resources.Culture = new CultureInfo("en-US");
#if DEBUG
		if (Design.IsDesignMode)
		{
			base.OnFrameworkInitializationCompleted();
			return;
		}
#endif
		if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
		{
			// Note: Avalonia handles ReactiveUI init via .UseReactiveUI(rxuiBuilder => {/* ... */})
			//AppLocator.CurrentMutable.RegisterViewsForViewModels(Assembly.GetExecutingAssembly());

			ToolTip.ShowDelayProperty.OverrideDefaultValue<Panel>(500);
			ToolTip.BetweenShowDelayProperty.OverrideDefaultValue<Panel>(500);
			ToolTip.PlacementProperty.OverrideDefaultValue<Panel>(PlacementMode.RightEdgeAlignedTop);

			var viewLocator = new ViewLocator();
			AppLocator.CurrentMutable.RegisterConstant<IViewLocator>(viewLocator);
			DataTemplates.Add(viewLocator);

			var mainWindow = new MainWindow();
			desktop.MainWindow = mainWindow;
			// Tool windows (Mod Sync, Settings, ...) hide instead of closing, so with the default OnLastWindowClose the
			// process kept running invisibly (still hosting mod sync) after the user closed the main window.
			desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
			// Avalonia ignores SIGTERM (logout, `kill`); shut down properly so Exit handlers run (e.g. removing the
			// router port mapping when hosting).
			if (!OperatingSystem.IsWindows())
			{
				_sigterm = System.Runtime.InteropServices.PosixSignalRegistration.Create(System.Runtime.InteropServices.PosixSignal.SIGTERM, ctx =>
				{
					ctx.Cancel = true;
					Avalonia.Threading.Dispatcher.UIThread.Post(() => desktop.Shutdown());
				});
			}
			AppLocator.CurrentMutable.RegisterConstant(mainWindow);
			mainWindow.DataContext = ViewModelLocator.Main;

			AppLocator.CurrentMutable.InitializeSplat();
			//AppLocator.CurrentMutable.InitializeReactiveUI();

			AppLocator.CurrentMutable.RegisterConstant(new WindowManagerService(mainWindow, AppServices.Interactions));

			RxApp.MainThreadScheduler = AvaloniaScheduler.Instance;

			SukiTheme.GetInstance().ChangeBaseTheme(ThemeVariant.Light);
			SukiTheme.GetInstance().ChangeBaseTheme(ThemeVariant.Dark);

			AppServices.Initialize();
			SplatRegistrations.RegisterConstant(new ColorThemeService(AppServices.Settings, AppServices.Locale));
		}

		base.OnFrameworkInitializationCompleted();
	}
}