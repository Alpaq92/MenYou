using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace MenYou.Platform.Windows;

/// Checks whether the system's own Start menu is currently the foreground
/// window. Used by the keyboard and mouse hooks to step aside while the
/// system menu is showing — otherwise hitting the Start button or pressing
/// Win again would pop MenYou behind the still-open system menu instead
/// of closing the system menu the way the user expects.
[SupportedOSPlatform("windows")]
internal static class SystemStartMenu
{
    // The Win 11 system Start menu is hosted solely by StartMenuExperienceHost.
    // Earlier this set also matched SearchHost / SearchApp / ShellExperienceHost,
    // but those host taskbar Search, Action Center and Quick Settings — so a
    // Start click made right after using one of those surfaces saw IsOpen()==true,
    // passed the click through, and opened the *native* Start menu instead of
    // MenYou. Keying strictly on StartMenuExperienceHost removes that false
    // positive while still detecting a genuinely-open system Start, so the hooks
    // can step aside and let Windows toggle it closed.
    private const string StartHostProcess = "StartMenuExperienceHost";

    /// Cheap to call — GetForegroundWindow + IsWindowVisible + one
    /// Process.GetProcessById lookup. No caching: process ids can recycle, and
    /// the hook paths that call this fire only on click / Win-down, not per-event.
    public static bool IsOpen()
    {
        var fg = GetForegroundWindow();
        if (fg == IntPtr.Zero || !IsWindowVisible(fg)) return false;
        GetWindowThreadProcessId(fg, out var pid);
        if (pid == 0) return false;
        try
        {
            using var p = Process.GetProcessById((int)pid);
            return string.Equals(p.ProcessName, StartHostProcess, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
}
