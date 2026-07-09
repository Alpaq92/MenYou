using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace MenYou.Platform.Windows;

/// Best-effort estimate of where the visible Start button lives on screen.
///
/// On Win 11 the Start button is rendered inside the taskbar's
/// <c>Windows.UI.Composition.DesktopWindowContentBridge</c> XAML island, so
/// there's no Win32 HWND to query directly. We fall back to a layout-based
/// guess driven by the user's <c>TaskbarAl</c> setting:
///   - 0 (left-aligned)  → first ~48 px of the taskbar from the left
///   - 1 (centered)      → 48 px centered on the taskbar's midpoint, then
///     shifted left to land roughly on Start (Start is leftmost of the
///     centered icon group; without UI Automation we can't be precise, but
///     for the click hook a slightly oversized rect is fine — false
///     positives are rare in this region).
///
/// Refresh the rect whenever the taskbar moves or the alignment toggles.
[SupportedOSPlatform("windows")]
internal static class StartButtonLocator
{
    private const int StartButtonWidth = 48;
    // The left-aligned Win 11 Start button is wider than StartButtonWidth: UI
    // Automation reports its bounding rect at 55 px wide at 100% DPI ([0,55)),
    // with the first pinned app (File Explorer) flush at x=55. Swallowing only
    // the leftmost 48 px left a [48,55) dead sliver (~13% of the button) where a
    // click fell through to Windows and opened the native Start menu — the
    // "sometimes the normal Start menu shows up" bug. RECT.Contains is half-open
    // (x < Right), so 55 covers the whole button yet still excludes File
    // Explorer's first pixel at x=55: no false positives. (Do NOT widen to 64 —
    // that swallows ~9 px of File Explorer.) The button scales with DPI, so a
    // future UI-Automation lookup of the real StartButton rect would be more
    // durable than this constant; see EnsureRectFresh.
    private const int LeftAlignedStartWidth = 55;
    private const int CenteredSlop = 40; // a bit wider than the visible button so we don't miss

    /// Returns the screen-coordinate rectangle covering the Start button.
    /// Returns an empty rect if Shell_TrayWnd isn't found (Explorer not up).
    public static RECT Get()
    {
        var tray = FindWindowW("Shell_TrayWnd", null);
        if (tray == IntPtr.Zero) return default;
        if (!GetWindowRect(tray, out var trayRect)) return default;

        var align = GetTaskbarAlignment();
        var h = trayRect.Bottom - trayRect.Top;
        if (align == 0)
        {
            // Left-aligned: Start is the leftmost icon.
            return new RECT
            {
                Left = trayRect.Left,
                Top = trayRect.Top,
                Right = trayRect.Left + LeftAlignedStartWidth,
                Bottom = trayRect.Bottom
            };
        }

        // Centered: best-effort. Take a wider band centered on the
        // centerpoint, shifted slightly left.
        var midX = (trayRect.Left + trayRect.Right) / 2;
        // Visible Start icon is usually 5-7 icons to the left of dead center.
        var startX = midX - 7 * StartButtonWidth;
        return new RECT
        {
            Left = startX,
            Top = trayRect.Top,
            Right = startX + StartButtonWidth + CenteredSlop,
            Bottom = trayRect.Bottom
        };
    }

    private static int GetTaskbarAlignment()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced");
            return (int)(key?.GetValue("TaskbarAl") ?? 0);
        }
        catch
        {
            return 0;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left, Top, Right, Bottom;
        public bool IsEmpty => Right - Left <= 0 || Bottom - Top <= 0;
        public bool Contains(int x, int y) => x >= Left && x < Right && y >= Top && y < Bottom;
        public override string ToString() => $"({Left},{Top})-({Right},{Bottom})";
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindWindowW(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
}
