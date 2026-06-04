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
    private static readonly HashSet<string> HostNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "StartMenuExperienceHost",
        "SearchHost",
        "SearchApp",
        "ShellExperienceHost",
    };

    /// Cheap to call — single GetForegroundWindow + one Process.GetProcessById
    /// lookup. No caching: process ids can recycle, and the hook paths that
    /// call this fire only on click / Win-down, not per-event.
    public static bool IsOpen()
    {
        var fg = GetForegroundWindow();
        if (fg == IntPtr.Zero) return false;
        GetWindowThreadProcessId(fg, out var pid);
        if (pid == 0) return false;
        try
        {
            using var p = Process.GetProcessById((int)pid);
            return HostNames.Contains(p.ProcessName);
        }
        catch
        {
            return false;
        }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
}
