using Xunit;

namespace ModManager.Tests;

public abstract class BaseTest : IDisposable
{
	private readonly ITestOutputHelper _output;

	public ITestOutputHelper Output => _output;

	private static readonly Lock _rxInitLock = new();
	private static bool _rxInitialized;

	public BaseTest(ITestOutputHelper output)
	{
		// ReactiveUI 23 refuses to create ReactiveObjects (e.g. ModOrder) until it has been initialized; the app does this
		// via Avalonia's UseReactiveUI, so tests need to do it themselves.
		lock (_rxInitLock)
		{
			if (!_rxInitialized)
			{
				((ReactiveUI.Builder.ReactiveUIBuilder)ReactiveUI.Builder.RxAppBuilder.CreateReactiveUIBuilder().WithCoreServices()).BuildApp();
				_rxInitialized = true;
			}
		}
		_output = output;
		DivinityApp.LogMethod = _output.WriteLine;
	}

	public virtual void Dispose()
	{
		DivinityApp.LogMethod = null;
	}
}
