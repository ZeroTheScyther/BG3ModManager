using System.IO;
using Mono.Nat;

using System.Net;

namespace ModManager.Services.Sync;

/// <summary>
/// Best-effort automatic port forwarding through UPnP-IGD or NAT-PMP/PCP. Many routers have this disabled,
/// in which case the user forwards the port manually; failure here is reported, never fatal.
/// </summary>
public sealed class NatPortMapper : IAsyncDisposable
{
	private const string Description = "BG3 Mod Manager Sync";
	private const int LeaseSeconds = 3600;

	private INatDevice? _device;
	private Mapping? _mapping;
	private Timer? _renewTimer;

	public IPAddress? ExternalAddress { get; private set; }
	public string Status { get; private set; } = "Not attempted";
	public bool IsMapped => _mapping != null;

	public async Task<bool> MapAsync(int port, TimeSpan discoveryTimeout, CancellationToken token)
	{
		var found = new TaskCompletionSource<INatDevice>(TaskCreationOptions.RunContinuationsAsynchronously);
		void OnFound(object? sender, DeviceEventArgs e) => found.TrySetResult(e.Device);

		NatUtility.DeviceFound += OnFound;
		try
		{
			Status = "Searching for router (UPnP / NAT-PMP)...";
			NatUtility.StartDiscovery(NatProtocol.Upnp, NatProtocol.Pmp);
			var winner = await Task.WhenAny(found.Task, Task.Delay(discoveryTimeout, token));
			if (winner != found.Task)
			{
				token.ThrowIfCancellationRequested();
				Status = "No UPnP / NAT-PMP router found. Forward the port manually if friends connect over the internet.";
				return false;
			}
			_device = found.Task.Result;
		}
		finally
		{
			NatUtility.DeviceFound -= OnFound;
			NatUtility.StopDiscovery();
		}

		try
		{
			try
			{
				_mapping = await _device.CreatePortMapAsync(new Mapping(Protocol.Tcp, port, port, LeaseSeconds, Description));
			}
			catch (MappingException ex) when (_device.NatProtocol == NatProtocol.Upnp)
			{
				// Some UPnP routers only accept permanent leases (error 725). Retry with lifetime 0 (= permanent).
				DivinityApp.Log($"UPnP mapping with a lease failed ({ex.Message}); retrying as permanent.");
				_mapping = await _device.CreatePortMapAsync(new Mapping(Protocol.Tcp, port, port, 0, Description));
			}

			try { ExternalAddress = await _device.GetExternalIPAsync(); }
			catch (Exception ex) { DivinityApp.Log($"Router mapped the port but didn't report an external IP: {ex.Message}"); }

			if (_mapping.Lifetime > 0)
			{
				var renewEvery = TimeSpan.FromSeconds(Math.Max(60, _mapping.Lifetime / 2));
				_renewTimer = new Timer(_ => _ = RenewAsync(port), null, renewEvery, renewEvery);
			}

			var ext = ExternalAddress;
			var isPublic = ext != null && !NetworkAddresses.IsPrivate(ext) && !IPAddress.Any.Equals(ext);
			Status = isPublic
				? $"Port {port} opened on router via {_device.NatProtocol} (public IP {ext})."
				: $"Port {port} opened via {_device.NatProtocol}, but the router reports a private external IP ({ext?.ToString() ?? "unknown"}). You're probably behind CGNAT or a second router, so internet connections may not work. Use the VPN instead.";
			if (!isPublic) ExternalAddress = null;
			return true;
		}
		catch (Exception ex)
		{
			_mapping = null;
			Status = $"Router refused the port mapping ({ex.Message}). Forward TCP port {port} manually.";
			DivinityApp.Log(Status);
			return false;
		}
	}

	private async Task RenewAsync(int port)
	{
		if (_device == null || _mapping == null) return;
		try
		{
			_mapping = await _device.CreatePortMapAsync(new Mapping(Protocol.Tcp, port, port, LeaseSeconds, Description));
		}
		catch (Exception ex)
		{
			DivinityApp.Log($"Failed to renew router port mapping: {ex.Message}");
		}
	}

	public async ValueTask DisposeAsync()
	{
		_renewTimer?.Dispose();
		_renewTimer = null;
		if (_device != null && _mapping != null)
		{
			try
			{
				using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
				await _device.DeletePortMapAsync(_mapping).WaitAsync(cts.Token);
			}
			catch (Exception ex)
			{
				DivinityApp.Log($"Failed to remove router port mapping: {ex.Message}");
			}
		}
		_mapping = null;
		_device = null;
	}
}
