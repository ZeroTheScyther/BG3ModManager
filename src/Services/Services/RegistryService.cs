using Gameloop.Vdf;
using Gameloop.Vdf.Linq;

using Microsoft.Win32;

using ModManager.Services.Reg;
using ModManager.Util;

using System.Runtime.InteropServices;

namespace ModManager.Services;

/// <inheritdoc />
public partial class RegistryService : IRegistryService
{
	const string PATH_Steam_WorkshopFolder = @"steamapps/workshop";
	const string PATH_Steam_LibraryFile = @"steamapps/libraryfolders.vdf";

	private readonly IFileSystemService _fs;

	private readonly IRegHelper? _regHelper;

	public string? GetSteamInstallPath() => _regHelper?.GetSteamInstallPath();

	public string GetTruePath(string path)
	{
		try
		{
			var driveType = FileUtils.GetPathDriveType(path);
			if (driveType == System.IO.DriveType.Fixed)
			{
				if (JunctionPoint.Exists(path))
				{
					var realPath = JunctionPoint.GetTarget(path);
					if (realPath.IsValid())
					{
						return realPath;
					}
				}
			}
			else
			{
				DivinityApp.Log($"Skipping junction check for path '{path}'. Drive type is '{driveType}'.");
			}
		}
		catch (Exception ex)
		{
			DivinityApp.Log($"Error checking junction point '{path}': {ex}");
		}
		return path;
	}

	public string? GetGoGInstallPath() => _regHelper?.GetGOGInstallPath();

	/// <summary>
	/// Returns the Steam root plus every library listed in libraryfolders.vdf (deduplicated, existing folders only).
	/// </summary>
	public List<string> GetSteamLibraryFolders()
	{
		List<string> libraryFolders = [];
		var steamInstallPath = GetSteamInstallPath();
		if (!steamInstallPath.IsExistingDirectory()) return libraryFolders;

		void Add(string? path)
		{
			if (!path.IsExistingDirectory()) return;
			var full = _fs.Path.TrimEndingDirectorySeparator(_fs.Path.GetFullPath(path!));
			if (!libraryFolders.Any(x => string.Equals(x, full, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)))
			{
				libraryFolders.Add(full);
			}
		}

		Add(steamInstallPath);

		var libraryFile = _fs.Path.Join(steamInstallPath, PATH_Steam_LibraryFile);
		if (_fs.File.Exists(libraryFile))
		{
			try
			{
				var libraryData = VdfConvert.Deserialize(_fs.File.ReadAllText(libraryFile));
				foreach (var token in libraryData.Value.Children().OfType<VProperty>())
				{
					if (token.Key == "TimeNextStatsReport" || token.Key == "ContentStatsID") continue;
					var path = token.Value.Children().OfType<VProperty>().FirstOrDefault(x => x.Key == "path");
					if (path?.Value is VValue innerValue)
					{
						var p = innerValue.Value<string>();
						if (p.IsExistingDirectory())
						{
							DivinityApp.Log($"Found steam library folder at '{p}'.");
							Add(p);
						}
					}
				}
			}
			catch (Exception ex)
			{
				DivinityApp.Log($"Error parsing steam library file at '{libraryFile}': {ex}");
			}
		}
		else
		{
			DivinityApp.Log($"Steam library file not found at '{libraryFile}'");
		}
		return libraryFolders;
	}

	/// <summary>
	/// Finds the Steam library that has the given app installed (the one containing appmanifest_[appid].acf).
	/// Proton prefixes (compatdata) live in that same library, so this is used for both the game and its data.
	/// </summary>
	private string? GetSteamLibraryForApp(string steamAppId, out string? installDir)
	{
		installDir = null;
		foreach (var library in GetSteamLibraryFolders())
		{
			var appManifest = _fs.Path.Join(library, "steamapps", $"appmanifest_{steamAppId}.acf");
			if (!appManifest.IsExistingFile()) continue;
			try
			{
				var manifestData = VdfConvert.Deserialize(_fs.File.ReadAllText(appManifest));
				var prop = manifestData?.Value.Children().OfType<VProperty>().FirstOrDefault(x => x.Key == "installdir");
				var dir = prop?.Value?.Value<string>();
				if (dir.IsValid())
				{
					installDir = dir;
					DivinityApp.Log($"Using appmanifest installDir '{dir}' from library '{library}'");
				}
			}
			catch (Exception ex)
			{
				DivinityApp.Log($"Error parsing app manifest '{appManifest}': {ex}");
			}
			return library;
		}
		return null;
	}

	public string? GetProtonDataPath(string steamAppId)
	{
		//Proton prefix: <library>/steamapps/compatdata/<appid>/pfx/drive_c/users/steamuser/AppData/Local
		const string protonLocal = "pfx/drive_c/users/steamuser/AppData/Local";

		var gameLibrary = GetSteamLibraryForApp(steamAppId, out _);
		IEnumerable<string> libraries = GetSteamLibraryFolders();
		if (gameLibrary != null) libraries = libraries.Prepend(gameLibrary);

		foreach (var library in libraries)
		{
			var protonLocalFolder = _fs.Path.Join(library, "steamapps", "compatdata", steamAppId, protonLocal);
			if (protonLocalFolder.IsExistingDirectory())
			{
				DivinityApp.Log($"Found Proton app data folder at '{protonLocalFolder}'");
				return protonLocalFolder;
			}
		}
		return null;
	}

	public string? GetSteamGameInstallPath(string gameFolder, string steamAppId)
	{
		try
		{
			var gameLibrary = GetSteamLibraryForApp(steamAppId, out var installDir);
			if (installDir.IsValid()) gameFolder = installDir!;

			IEnumerable<string> libraries = GetSteamLibraryFolders();
			if (gameLibrary != null) libraries = libraries.Prepend(gameLibrary);

			foreach (var library in libraries.Distinct())
			{
				var folder = _fs.Path.Join(library, "steamapps", "common", gameFolder);
				DivinityApp.Log($"Looking for game at '{folder}'.");
				if (folder.IsExistingDirectory())
				{
					DivinityApp.Log($"Found game at '{folder}'.");
					return folder;
				}
			}
		}
		catch (Exception ex)
		{
			DivinityApp.Log($"[*ERROR*] Error finding game path: {ex}");
		}
		return null;
	}

	public string? GetAppDataPath(string? steamAppId = null)
	{
		if (steamAppId.IsValid())
		{
			var protonPath = GetProtonDataPath(steamAppId);
			if (protonPath.IsExistingDirectory())
			{
				return protonPath;
			}
		}
		return _regHelper?.GetAppDataPath();
	}
	/// <inheritdoc />
	public string? GetApplicationInstallPath(string displayName)
	{
		return _regHelper?.GetApplicationInstallPath(displayName);
	}

	/// <inheritdoc />
	public bool IsAssociatedWithNXMProtocol(string exePath)
	{
		return _regHelper?.IsAssociatedWithNXMProtocol(exePath) == true;
	}

	/// <inheritdoc />
	public bool SetNXMProtocol(string exePath)
	{
		return _regHelper?.SetNXMProtocol(exePath) == true;
	}

	public RegistryService(IFileSystemService fs)
	{
		_fs = fs;

		if (OperatingSystem.IsWindows())
		{
			_regHelper = new WindowsRegistryHelper(fs);
		}
		else if (OperatingSystem.IsLinux())
		{
			_regHelper = new LinuxRegistryHelper(fs);
			//TODO register protocol with x-scheme-handler / mime handler
		}
		else
		{
			_regHelper = null;
		}
	}
}
