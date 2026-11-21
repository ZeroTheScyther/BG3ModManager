using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace ModManager.Services.Sync;

public enum HostAddressKind
{
	Vpn,
	Lan,
	Internet,
	Custom,
}

public sealed record HostAddress(string Address, HostAddressKind Kind, string Source)
{
	public string KindLabel => Kind switch
	{
		HostAddressKind.Vpn => "VPN",
		HostAddressKind.Lan => "LAN",
		HostAddressKind.Internet => "Internet",
		_ => "Custom",
	};
}

public static class NetworkAddresses
{
	// Adapter names/descriptions for VPNs: OpenVPN (tun/tap, TAP-Windows, "OpenVPN Data Channel Offload", Wintun),
	// WireGuard, Tailscale, ZeroTier, Hamachi, Radmin.
	private static readonly string[] _vpnHints = ["tun", "tap", "openvpn", "wintun", "wireguard", "wg", "tailscale", "zerotier", "zt", "hamachi", "radmin", "ppp"];
	// Virtual bridges that friends can never reach.
	private static readonly string[] _ignoreHints = ["docker", "veth", "virbr", "br-", "vmnet", "vboxnet", "lxc", "lxd", "podman", "cni", "flannel", "hyper-v", "vethernet (wsl", "vethernet (default switch"];

	private static bool NameHas(NetworkInterface nic, string[] hints)
	{
		var name = nic.Name.ToLowerInvariant();
		var desc = nic.Description.ToLowerInvariant();
		// Short hints (tun, wg, zt) only match as a name prefix; longer ones may also appear anywhere in the description.
		return hints.Any(h => name.StartsWith(h) || (h.Length >= 5 && desc.Contains(h)));
	}

	public static bool IsPrivate(IPAddress ip)
	{
		if (ip.AddressFamily != AddressFamily.InterNetwork) return ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6UniqueLocal;
		var b = ip.GetAddressBytes();
		return b[0] == 10
			|| (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
			|| (b[0] == 192 && b[1] == 168)
			|| (b[0] == 100 && b[1] >= 64 && b[1] <= 127) // CGNAT, also Tailscale
			|| (b[0] == 169 && b[1] == 254);
	}

	/// <summary>IPv4 addresses on this machine that a friend could plausibly reach, VPN adapters first.</summary>
	public static List<HostAddress> GetLocalAddresses()
	{
		List<HostAddress> result = [];
		NetworkInterface[] nics;
		try { nics = NetworkInterface.GetAllNetworkInterfaces(); }
		catch (Exception ex)
		{
			DivinityApp.Log($"Failed to enumerate network interfaces: {ex.Message}");
			return result;
		}

		foreach (var nic in nics)
		{
			if (nic.OperationalStatus != OperationalStatus.Up && nic.OperationalStatus != OperationalStatus.Unknown) continue;
			if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
			if (NameHas(nic, _ignoreHints)) continue;

			var isVpn = nic.NetworkInterfaceType is NetworkInterfaceType.Tunnel or NetworkInterfaceType.Ppp || NameHas(nic, _vpnHints);
			IPInterfaceProperties props;
			try { props = nic.GetIPProperties(); } catch { continue; }

			foreach (var ua in props.UnicastAddresses)
			{
				var ip = ua.Address;
				if (ip.AddressFamily != AddressFamily.InterNetwork) continue;
				var b = ip.GetAddressBytes();
				if (b[0] == 169 && b[1] == 254) continue; // link-local autoconfig, never useful

				var kind = isVpn ? HostAddressKind.Vpn : IsPrivate(ip) ? HostAddressKind.Lan : HostAddressKind.Internet;
				result.Add(new HostAddress(ip.ToString(), kind, nic.Name));
			}
		}
		return [.. result.DistinctBy(x => x.Address).OrderBy(x => x.Kind)];
	}

	/// <summary>Formats a host for use in a URL, bracketing IPv6 literals.</summary>
	public static string ToUriHost(string address)
	{
		var a = address.Trim().Trim('[', ']');
		return IPAddress.TryParse(a, out var ip) && ip.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{a}]" : a;
	}
}
