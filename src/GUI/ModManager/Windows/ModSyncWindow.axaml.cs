using Avalonia.Input.Platform;

using ModManager.Controls;
using ModManager.ViewModels;

using SukiUI.Toasts;

namespace ModManager;

public partial class ModSyncWindow : HideWindowBase<ModSyncWindowViewModel>
{
	private readonly ISukiToastManager _toastManager = new SukiToastManager();

	public ModSyncWindow()
	{
		InitializeComponent();

		ToastHost.Manager = _toastManager;

		this.WhenActivated(d =>
		{
			ViewModel ??= AppServices.Get<ModSyncWindowViewModel>();
			if (ViewModel == null) return;

			this.GetObservable(IsVisibleProperty).BindTo(ViewModel, x => x.IsVisible).DisposeWith(d);

			ViewModel.CopyToClipboard.RegisterHandler(async ctx =>
			{
				var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
				if (clipboard != null) await clipboard.SetTextAsync(ctx.Input);
				ctx.SetOutput(Unit.Default);
			}).DisposeWith(d);

			ViewModel.ShowAlertCommand.Subscribe(data =>
			{
				var title = data.Title.IsValid() ? data.Title : data.AlertType switch
				{
					AlertType.Danger => "Error",
					AlertType.Warning => "Warning",
					AlertType.Success => "Success",
					_ => "Info",
				};
				var duration = data.Timeout <= 0 ? TimeSpan.FromSeconds(10) : TimeSpan.FromSeconds(data.Timeout);
				RxApp.MainThreadScheduler.Schedule(() =>
				{
					var toast = _toastManager.CreateToast().WithTitle(title).WithContent(data.Message);
					toast.SetCanDismissByClicking(true);
					toast.SetDismissAfter(duration);
					toast.SetType(data.AlertType.ToNotificationType());
					toast.Queue();
				});
			}).DisposeWith(d);
		});
	}
}
