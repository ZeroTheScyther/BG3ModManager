using System;
using System.Collections.Generic;
using System.Text;

namespace ModManager.Services.Reg;

internal class LinuxRegistryHelper(IFileSystemService fs) : IRegHelper
{
	public string? GetApplicationInstallPath(string displayName)
	{
		throw new NotImplementedException();
	}

	public string? GetGOGInstallPath()
	{
		return null;
	}

	public string? GetAppDataPath()
	{
		var homeDataFolder = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
		var homeFolder = Environment.GetEnvironmentVariable("HOME");
		DivinityApp.Log($"Checking for 'XDG_DATA_HOME' environment variable: '{homeDataFolder}'");
		if (!homeDataFolder.IsExistingDirectory())
		{
			if (homeFolder.IsExistingDirectory())
			{
				homeDataFolder = fs.Path.Join(homeFolder, ".local", "share");
			}
			else
			{
				DivinityApp.Log("'XDG_DATA_HOME' not set. Trying '~/.local/share'");
				homeDataFolder = "~/.local/share";
			}
		}
		return homeDataFolder;
	}

	/// <summary>
	/// Candidate Steam roots, in priority order. ~/.steam/steam (and ~/.steam/root) are symlinks Steam itself
	/// maintains to the active install, so they cover native, Debian/Ubuntu (~/.steam/debian-installation) and
	/// most distro packages. Flatpak and Snap installs live in their own sandboxed home folders.
	/// </summary>
	private IEnumerable<string> GetSteamCandidates()
	{
		var home = Environment.GetEnvironmentVariable("HOME");
		if (home.IsExistingDirectory())
		{
			yield return fs.Path.Join(home, ".steam", "steam");
			yield return fs.Path.Join(home, ".steam", "root");
		}
		var dataHome = GetAppDataPath();
		if (dataHome.IsExistingDirectory()) yield return fs.Path.Join(dataHome, "Steam");
		if (home.IsExistingDirectory())
		{
			yield return fs.Path.Join(home, ".steam", "debian-installation");
			yield return fs.Path.Join(home, ".var", "app", "com.valvesoftware.Steam", ".local", "share", "Steam");
			yield return fs.Path.Join(home, ".var", "app", "com.valvesoftware.Steam", ".steam", "steam");
			yield return fs.Path.Join(home, "snap", "steam", "common", ".local", "share", "Steam");
		}
	}

	public string? GetSteamInstallPath()
	{
		foreach (var candidate in GetSteamCandidates())
		{
			// Require steamapps: e.g. ~/.local/share/Steam can exist holding only compatibilitytools.d/steamcmd.
			if (fs.Path.Join(candidate, "steamapps").IsExistingDirectory())
			{
				var resolved = candidate;
				try
				{
					var target = new System.IO.DirectoryInfo(candidate).ResolveLinkTarget(true);
					if (target != null && target.Exists) resolved = target.FullName;
				}
				catch (Exception ex)
				{
					DivinityApp.Log($"Failed to resolve symlink '{candidate}': {ex.Message}");
				}
				DivinityApp.Log($"Found steam at '{resolved}'");
				return resolved;
			}
		}
		DivinityApp.Log("Failed to find a Steam install (checked ~/.steam/steam, ~/.local/share/Steam, Flatpak, Snap).");
		return null;
	}

	public bool IsAssociatedWithNXMProtocol(string appExePath)
	{
		return false;
	}

	public bool SetNXMProtocol(string appExePath)
	{
		return false;
	}
}
