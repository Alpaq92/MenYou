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
///   - 0 (left-aligned)  → the leftmost <see cref="LeftAlignedStartWidth"/>
///     px of the taskbar (measured at 100% DPI, scaled by the tray's DPI)
///   - 1 (centered)      → a band aimed ~7 icon-pitches left of the taskbar
///     midpoint (Start is leftmost of the centered icon group; without UI
///     Automation we can't know the group's width, so this stays a guess —
///     for the click hook a slightly oversized band is fine, and false
///     positives are rare in this region). Same DPI scaling as above.
///
/// With "show my taskbar on all displays" on, each extra monitor gets its own
/// taskbar (<c>Shell_SecondaryTrayWnd</c>) with its own Start button, so there
/// is one rect per taskbar, each scaled by its own tray's monitor DPI.
///
/// Refresh the rects whenever the taskbar moves, the alignment toggles, or
/// the display scale changes.
[SupportedOSPlatform("windows")]
internal static class StartButtonLocator
{
    // Icon-pitch unit for the centered-taskbar guess below — the ~48 px stride
    // between taskbar icons at 100% DPI (scaled at use), NOT the Start
    // button's real width (that's LeftAlignedStartWidth).
    private const int CenteredIconStep = 48;
    // Measured width of the left-aligned Win 11 Start button at 100% DPI: UI
    // Automation reports its bounding rect as [0,55), with the first pinned app
    // (File Explorer) flush at x=55. Swallowing only the leftmost 48 px left a
    // [48,55) dead sliver (~13% of the button) where a click fell through to
    // Windows and opened the native Start menu — the "sometimes the normal
    // Start menu shows up" bug. RECT.Contains is half-open (x < Right), so 55
    // covers the whole button yet still excludes File Explorer's first pixel at
    // x=55: no false positives. (Do NOT widen to 64 — that swallows ~9 px of
    // File Explorer.) The button grows with DPI, so GetStartRect scales this by
    // the tray's monitor DPI, rounding UP (69 px at 125%, 83 at 150%): at a
    // fractional boundary the shared pixel column goes to Start — the safe
    // side, since the half-open Contains keeps the column at Right excluded
    // either way. A UI-Automation lookup of the real StartButton rect would be
    // more durable still — but it must run OFF the hook thread (a cross-process
    // UIA call can take tens of ms; overrunning the LL-hook timeout gets
    // WH_MOUSE_LL silently removed), never inside StartClickHook.HookProc /
    // EnsureRectsFresh.
    private const int LeftAlignedStartWidth = 55;
    private const int CenteredSlop = 40; // a bit wider than the visible button so we don't miss

    /// Fills <paramref name="results"/> with one screen-coordinate rect per
    /// visible Start button: the primary taskbar (Shell_TrayWnd) plus one per
    /// secondary-monitor taskbar (Shell_SecondaryTrayWnd). Clears the list
    /// first; leaves it empty if no taskbar is found (Explorer not up). A tray
    /// window that dies mid-query is skipped, not treated as fatal. Takes the
    /// caller's list so the click hook can refresh without allocating.
    public static void GetAll(List<RECT> results)
    {
        results.Clear();
        var align = GetTaskbarAlignment();

        var tray = FindWindowW("Shell_TrayWnd", null);
        if (tray != IntPtr.Zero)
        {
            var rect = GetStartRect(tray, align);
            if (!rect.IsEmpty) results.Add(rect);
        }

        // Secondary taskbars don't center their icon group on Win 11: Start is
        // the leftmost element there even when TaskbarAl says centered.
        var secondary = IntPtr.Zero;
        while ((secondary = FindWindowExW(IntPtr.Zero, secondary, "Shell_SecondaryTrayWnd", null)) != IntPtr.Zero)
        {
            var rect = GetStartRect(secondary, align: 0);
            if (!rect.IsEmpty) results.Add(rect);
        }
    }

    /// Start rect for one taskbar window; empty if the window died mid-query.
    private static RECT GetStartRect(IntPtr tray, int align)
    {
        if (!GetWindowRect(tray, out var trayRect)) return default;

        // The constants above are 96-DPI measurements while trayRect (and the
        // hook coordinates this rect is compared against) are physical pixels,
        // so both branches scale by the tray's monitor DPI (Explorer is
        // per-monitor DPI aware). 0 means the tray hwnd died since the
        // GetWindowRect above — return empty like the sibling failure paths so
        // EnsureRectsFresh keeps the last good rects instead of adopting an
        // unscaled one.
        var dpi = (int)GetDpiForWindow(tray);
        if (dpi == 0) return default;

        if (align == 0)
        {
            // Left-aligned: Start is the leftmost icon. (+95)/96 is integer
            // ceiling — round the scaled width UP so a fractional boundary
            // column (125% → 68.75) stays with Start rather than leaking the
            // button's last pixel to Windows; Contains excludes Right itself,
            // so the first pinned app is never swallowed.
            return new RECT
            {
                Left = trayRect.Left,
                Top = trayRect.Top,
                Right = trayRect.Left + (LeftAlignedStartWidth * dpi + 95) / 96,
                Bottom = trayRect.Bottom
            };
        }

        // Centered: best-effort guess. Start is the leftmost icon of the
        // centered group; without UI Automation we can't know the group's
        // width, so aim one icon-pitch-wide band (plus slop) ~7 pitches left
        // of the taskbar midpoint. Truncation is fine here — the whole branch
        // is a heuristic, not a measurement.
        var step = CenteredIconStep * dpi / 96;
        var midX = (trayRect.Left + trayRect.Right) / 2;
        var startX = midX - 7 * step;
        return new RECT
        {
            Left = startX,
            Top = trayRect.Top,
            Right = startX + step + CenteredSlop * dpi / 96,
            Bottom = trayRect.Bottom
        };
    }

    private static int GetTaskbarAlignment()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced");
            // Win 11's out-of-box taskbar is CENTERED and TaskbarAl is only
            // written once the user first toggles alignment in Settings — so
            // a missing value (or an unreadable key) means centered, not left.
            return (int)(key?.GetValue("TaskbarAl") ?? 1);
        }
        catch
        {
            return 1;
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

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindWindowExW(IntPtr hWndParent, IntPtr hWndChildAfter, string? lpszClass, string? lpszWindow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);
}
