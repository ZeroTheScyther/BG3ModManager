using System.IO;
using System.Buffers.Text;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace ModManager.Services.Sync;

/// <summary>Host-side settings, persisted next to the certificate.</summary>
public sealed class ModSyncHostSettings
{
	public const int DefaultPort = 47630;

	public int Port { get; set; } = DefaultPort;
	/// <summary>Try to open the port on the router via UPnP / NAT-PMP.</summary>
	public bool UseNatPortMapping { get; set; } = true;
	/// <summary>Extra addresses to put in invites, e.g. a dynamic DNS hostname or a manually forwarded public IP.</summary>
	public List<string> ExtraAddresses { get; set; } = [];
	/// <summary>Addresses detected on this PC (LAN, VPN, ...) to leave out of invites.</summary>
	public List<string> HiddenAddresses { get; set; } = [];
	/// <summary>Shared secret clients must present. Regenerating it invalidates all previously sent invites.</summary>
	public string Token { get; set; } = "";
}

/// <summary>
/// The host's long-lived identity: a self-signed TLS certificate (whose fingerprint is pinned by clients via the
/// invite code, so no CA is involved) and the access token. Stored in the app's Data folder.
/// </summary>
public sealed class ModSyncIdentity
{
	private const string CertFileName = "identity.pfx";
	private const string SettingsFileName = "host.json";
	private static readonly JsonSerializerOptions _json = new() { WriteIndented = true };

	public string Folder { get; }
	public X509Certificate2 Certificate { get; private set; }
	public ModSyncHostSettings Settings { get; }

	/// <summary>Lowercase hex SHA-256 of the certificate's DER encoding.</summary>
	public string Fingerprint => GetFingerprint(Certificate);

	public static string GetFingerprint(X509Certificate certificate) => Convert.ToHexStringLower(SHA256.HashData(certificate.GetRawCertData()));

	private ModSyncIdentity(string folder, X509Certificate2 cert, ModSyncHostSettings settings)
	{
		Folder = folder;
		Certificate = cert;
		Settings = settings;
	}

	public static ModSyncIdentity LoadOrCreate(string folder)
	{
		Directory.CreateDirectory(folder);
		var certPath = Path.Join(folder, CertFileName);
		X509Certificate2? cert = null;
		if (File.Exists(certPath))
		{
			try
			{
				cert = X509CertificateLoader.LoadPkcs12FromFile(certPath, null, X509KeyStorageFlags.Exportable);
				if (!cert.HasPrivateKey || cert.NotAfter < DateTime.Now.AddDays(1)) cert = null;
			}
			catch (Exception ex)
			{
				DivinityApp.Log($"Failed to load mod sync certificate, generating a new one: {ex.Message}");
				cert = null;
			}
		}
		if (cert == null)
		{
			File.WriteAllBytes(certPath, CreateCertificatePfx());
			RestrictToOwner(certPath);
			cert = X509CertificateLoader.LoadPkcs12FromFile(certPath, null, X509KeyStorageFlags.Exportable);
		}

		var settingsPath = Path.Join(folder, SettingsFileName);
		ModSyncHostSettings? settings = null;
		if (File.Exists(settingsPath))
		{
			try { settings = JsonSerializer.Deserialize<ModSyncHostSettings>(File.ReadAllText(settingsPath)); }
			catch (Exception ex) { DivinityApp.Log($"Failed to read '{settingsPath}': {ex.Message}"); }
		}
		settings ??= new ModSyncHostSettings();

		var identity = new ModSyncIdentity(folder, cert, settings);
		if (string.IsNullOrEmpty(settings.Token)) identity.RegenerateToken();
		else identity.Save();
		return identity;
	}

	public void RegenerateToken()
	{
		Settings.Token = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
		Save();
	}

	public void Save()
	{
		var path = Path.Join(Folder, SettingsFileName);
		File.WriteAllText(path, JsonSerializer.Serialize(Settings, _json));
		RestrictToOwner(path);
	}

	public bool IsValidToken(string? presented)
	{
		if (string.IsNullOrEmpty(presented) || string.IsNullOrEmpty(Settings.Token)) return false;
		// Compare fixed-length digests so the comparison is constant-time regardless of input length.
		var a = SHA256.HashData(Encoding.UTF8.GetBytes(presented));
		var b = SHA256.HashData(Encoding.UTF8.GetBytes(Settings.Token));
		return CryptographicOperations.FixedTimeEquals(a, b);
	}

	private static byte[] CreateCertificatePfx()
	{
		using var rsa = RSA.Create(3072);
		var req = new CertificateRequest("CN=BG3 Mod Manager Sync", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
		req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
		req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));
		req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false)); // serverAuth
		using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(20));
		// Round-trip through PFX: on Windows, SslStream can't use the ephemeral key from CreateSelfSigned directly.
		return cert.Export(X509ContentType.Pfx);
	}

	private static void RestrictToOwner(string path)
	{
		if (!OperatingSystem.IsWindows())
		{
			try { File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
			catch (Exception ex) { DivinityApp.Log($"Failed to restrict permissions on '{path}': {ex.Message}"); }
		}
	}
}

/// <summary>
/// Everything a friend needs to connect: candidate addresses, port, access token and the pinned certificate fingerprint.
/// Encoded as "BG3SYNC1-" + base64url(JSON). Treat it like a password.
/// </summary>
public sealed record ModSyncInvite(List<string> Addresses, int Port, string Token, string Fingerprint, string HostName)
{
	public const string Prefix = "BG3SYNC1-";

	private sealed record Wire(List<string> a, int p, string t, string f, string n);

	public string Encode()
	{
		var json = JsonSerializer.SerializeToUtf8Bytes(new Wire(Addresses, Port, Token, Fingerprint, HostName));
		return Prefix + Base64Url.EncodeToString(json);
	}

	public static bool TryDecode(string? text, out ModSyncInvite? invite, out string? error)
	{
		invite = null;
		error = null;
		text = text?.Trim();
		if (string.IsNullOrEmpty(text) || !text.StartsWith(Prefix, StringComparison.Ordinal))
		{
			error = "That doesn't look like a mod sync invite code (it should start with " + Prefix + ").";
			return false;
		}
		try
		{
			var wire = JsonSerializer.Deserialize<Wire>(Base64Url.DecodeFromChars(text.AsSpan(Prefix.Length)));
			if (wire == null || wire.a == null || wire.a.Count == 0 || wire.p is < 1 or > 65535 || string.IsNullOrEmpty(wire.t) || !ModSyncValidation.IsValidSha256(wire.f))
			{
				error = "The invite code is incomplete or corrupted.";
				return false;
			}
			var addresses = wire.a.Where(x => !string.IsNullOrWhiteSpace(x) && Uri.CheckHostName(x.Trim('[', ']')) != UriHostNameType.Unknown).Select(x => x.Trim()).Distinct().Take(16).ToList();
			if (addresses.Count == 0)
			{
				error = "The invite code contains no usable addresses.";
				return false;
			}
			invite = new ModSyncInvite(addresses, wire.p, wire.t, wire.f, wire.n ?? "");
			return true;
		}
		catch (Exception ex) when (ex is FormatException or JsonException)
		{
			error = "The invite code is corrupted (was it copied completely?).";
			return false;
		}
	}
}
