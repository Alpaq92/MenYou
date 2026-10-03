using System.Diagnostics;

namespace MenYou.Services;

/// Starts a fresh copy of MenYou one second from now, from a detached cmd.exe,
/// so the CURRENT process can exit first. Shared by the tray's Restart command
/// (App.RestartApp) and crash recovery (CrashGuard).
///
/// The one-second delay is the point: it gives the OS time to release the
/// single-instance mutex, the global Shift+Win hotkey registration, the
/// TrayIcon GUID, the start2.bin file watcher and the IPC named window before
/// the new instance grabs them. Started immediately, the new process would find
/// the mutex still held, ring the old instance's doorbell and exit — or win the
/// race and come up without a working hotkey (RegisterHotKey returns
/// ERROR_HOTKEY_ALREADY_REGISTERED).
///
/// `start "" "<exe>"` opens the exe detached from cmd, so cmd terminates
/// without holding a handle on MenYou; the empty "" is cmd's required
/// window-title argument before the command.
internal static class AppRelaunch
{
    /// Returns false when nothing was scheduled — no ProcessPath (some
    /// single-file and debugger hosts) or cmd.exe refused to start (locked-down
    /// machine) — so callers can choose not to exit.
    public static bool Schedule()
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return false;
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c timeout /t 1 /nobreak >nul & start \"\" \"{exe}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            return true;
        }
        catch
        {
            return false;
        }
    }
}
