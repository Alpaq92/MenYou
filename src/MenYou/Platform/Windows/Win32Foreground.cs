using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace MenYou.Platform.Windows;

/// Forces a top-level window to the foreground on Windows 11, working
/// around the focus-stealing prevention that leaves a freshly-shown window
/// behind the active app when our process doesn't currently own foreground.
///
/// This is the same AttachThreadInput dance StartMenuWindow uses for its
/// own activation, lifted into a shared helper so the Settings window can
/// reuse it. The Settings window is shown right after the start menu hides
/// — a focus bounce that otherwise drops it behind whatever app had
/// foreground before the menu, making the cog click look like it did
/// nothing (a second click then Activate()s it forward).
[SupportedOSPlatform("windows")]
internal static class Win32Foreground
{
    public static void Bring(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;

        var fg = GetForegroundWindow();
        if (fg == hwnd) return;

        // Briefly attach our input thread to the current foreground
        // thread; that grants SetForegroundWindow permission Win 11
        // otherwise denies to a background process.
        var fgThread = GetWindowThreadProcessId(fg, out _);
        var ourThread = GetCurrentThreadId();
        var attached = false;
        if (fgThread != 0 && fgThread != ourThread)
            attached = AttachThreadInput(fgThread, ourThread, true);
        try
        {
            SetForegroundWindow(hwnd);
            BringWindowToTop(hwnd);
            SetFocus(hwnd);
        }
        finally
        {
            if (attached) AttachThreadInput(fgThread, ourThread, false);
        }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr SetFocus(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo,
        [MarshalAs(UnmanagedType.Bool)] bool fAttach);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
}
