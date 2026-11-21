using Microsoft.VisualBasic.FileIO;

using System.Runtime.InteropServices;

namespace ModManager.Util;

public static class RecycleBinHelper
{
	// Source: http://csharphelper.com/blog/2015/07/manage-the-recycle-bin-wastebasket-in-c/
	// Structure used by SHQueryRecycleBin.
	[StructLayout(LayoutKind.Sequential)]
	private struct SHQUERYRBINFO
	{
		public int cbSize;
		public long i64Size;
		public long i64NumItems;
	}

	// Get information from recycle bin.
	[DllImport("shell32.dll")]
	private static extern int SHQueryRecycleBin(string pszRootPath,
		ref SHQUERYRBINFO pSHQueryRBInfo);

	// Empty the recycle bin.
	[DllImport("shell32.dll")]
	static extern int SHEmptyRecycleBin(IntPtr hWnd,
		string pszRootPath, uint dwFlags);

	// Return the number of items in the recycle bin.

	// Note: In Windows 2000, you need to supply the root
	// directory to the call to SHQueryRecycleBin so to get
	// the total number of files in the recycle you must add
	// up the results for each disk. See:
	// http://www.pinvoke.net/default.aspx/shell32/SHQueryRecycleBin.html
	public static int NumberOfFilesInRecycleBin()
	{
		SHQUERYRBINFO sqrbi = new()
		{
			cbSize = Marshal.SizeOf(typeof(SHQUERYRBINFO))
		};
		var hresult = SHQueryRecycleBin(string.Empty, ref sqrbi);
		return (int)sqrbi.i64NumItems;
	}

	// Delete a file or move it to the recycle bin.
	public static bool DeleteFile(string filename, bool confirm, bool deletePermanently = false)
	{
		if (!OperatingSystem.IsWindows()) return DeleteFileUnix(filename, deletePermanently);

		var uiDisplayOptions = confirm ? UIOption.AllDialogs : UIOption.OnlyErrorDialogs;
		var reyclingOptions = deletePermanently ? RecycleOption.DeletePermanently : RecycleOption.SendToRecycleBin;

		try
		{
			Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(filename, uiDisplayOptions, reyclingOptions);
			return true;
		}
		catch (Exception ex)
		{
			DivinityApp.Log("Error deleting file.\n" + ex.ToString());
		}
		return false;
	}

	/// <summary>
	/// Linux/macOS: Microsoft.VisualBasic's SendToRecycleBin is Windows-only (throws PlatformNotSupportedException).
	/// Use the desktop's trash via `gio trash` (GLib; present on GNOME, KDE, XFCE, Cinnamon...) or `trash-put`
	/// (trash-cli), both of which follow the freedesktop.org trash spec, including per-mount .Trash-$UID folders
	/// for files on other drives (e.g. a Steam library on a second disk).
	/// Never falls back to a permanent delete silently.
	/// </summary>
	private static bool DeleteFileUnix(string filename, bool deletePermanently)
	{
		try
		{
			if (!System.IO.File.Exists(filename) && !System.IO.Directory.Exists(filename)) return true;
			if (deletePermanently)
			{
				if (System.IO.Directory.Exists(filename)) System.IO.Directory.Delete(filename, true);
				else System.IO.File.Delete(filename);
				return true;
			}

			foreach (var (tool, args) in new[] { ("gio", new[] { "trash", "--", filename }), ("trash-put", new[] { "--", filename }) })
			{
				try
				{
					var psi = new System.Diagnostics.ProcessStartInfo(tool) { RedirectStandardError = true, UseShellExecute = false };
					foreach (var a in args) psi.ArgumentList.Add(a);
					using var p = System.Diagnostics.Process.Start(psi);
					if (p == null) continue;
					var err = p.StandardError.ReadToEnd();
					if (!p.WaitForExit(30_000)) { try { p.Kill(); } catch { } continue; }
					if (p.ExitCode == 0) return true;
					DivinityApp.Log($"'{tool}' failed to trash '{filename}' (exit {p.ExitCode}): {err.Trim()}");
				}
				catch (System.ComponentModel.Win32Exception)
				{
					// Tool not installed; try the next one.
				}
			}
			DivinityApp.Log($"Couldn't move '{filename}' to the trash: neither 'gio' nor 'trash-put' worked. Install glib2 (gio) or trash-cli, or delete permanently.");
		}
		catch (Exception ex)
		{
			DivinityApp.Log("Error deleting file.\n" + ex.ToString());
		}
		return false;
	}

	// Empty the wastebasket.
	[Flags]
	private enum RecycleFlags : uint
	{
		SHERB_NOCONFIRMATION = 0x1,
		SHERB_NOPROGRESSUI = 0x2,
		SHERB_NOSOUND = 0x4
	}
	public static void EmptyWastebasket(bool show_progress,
		bool play_sound, bool confirm)
	{
		RecycleFlags options = 0;
		if (!show_progress) options =
			options | RecycleFlags.SHERB_NOPROGRESSUI;
		if (!play_sound) options =
		 options | RecycleFlags.SHERB_NOSOUND;
		if (!confirm) options =
	  options | RecycleFlags.SHERB_NOCONFIRMATION;

		try
		{
			SHEmptyRecycleBin(IntPtr.Zero, null, (uint)options);
		}
		catch (Exception ex)
		{
			DivinityApp.Log("Error emptying wastebasket.\n" + ex.ToString());
		}
	}
}
