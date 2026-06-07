using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace MenYou.Platform.Windows;

/// First-run loading splash drawn with raw Win32/GDI on its OWN thread.
///
/// Why not an Avalonia window: the long first start is the app's UI thread
/// blocking while the cold, unsigned, self-contained payload is loaded and
/// Defender-scanned. A splash on that same thread can't paint during the block
/// — it shows the busy cursor and ghosts/blinks. This window lives on a
/// separate thread with its own message pump (the TrayBalloon pattern), so it
/// stays painted — and animates a marquee — right through the block. Shown from
/// Program.Main (before Avalonia loads) and torn down by App.AnnounceReady once
/// the tray + hotkey are up (a safety timer closes it otherwise).
///
/// Layout: a large circular app icon on the left (the icon is itself a glossy
/// circular button, so no extra ring), with the title, subtitle and an
/// indeterminate marquee progress bar stacked to its right. Double-buffered.
/// Theme-aware (light/dark from the Windows "apps" theme). The icon is the
/// 256 px frame pre-scaled ONCE to its display size with GDI+ high-quality
/// bicubic — DrawIconEx / per-frame scaling looked muddy. No Avalonia / theme-
/// brush dependency (it can't touch the runtime that's still cold-loading).
/// Best-effort throughout: nothing here may break startup.
[SupportedOSPlatform("windows")]
internal static class NativeSplash
{
    private const string ClassName = "MenYouSplashWindow";
    private const uint SAFETY_TIMER = 1;
    private const uint ANIM_TIMER = 2;

    private static volatile IntPtr _hwnd;
    private static volatile bool _closeRequested;
    private static Thread? _thread;
    private static WndProcDelegate? _wndProc;   // rooted so the GC can't collect it mid-life
    private static string _title = "MenYou";
    private static string _subtitle = "";

    private static IntPtr _titleFont, _subFont;
    private static Bitmap? _iconBmp;            // pre-scaled to display size
    private static int _frame;
    private static double _scale = 1.0;

    // Theme-aware palette (filled by SetPalette from the Windows apps theme).
    private static int _bg, _border, _titleColor, _subColor, _trackColor, _accentColor;

    public static void Show(string title, string subtitle)
    {
        if (_thread is not null) return;
        _title = title;
        _subtitle = subtitle;
        _closeRequested = false;
        _hwnd = IntPtr.Zero;
        _frame = 0;
        var t = new Thread(Run) { IsBackground = true, Name = "MenYou.Splash" };
        t.SetApartmentState(ApartmentState.STA);
        _thread = t;
        t.Start();
    }

    public static void Close()
    {
        _closeRequested = true;
        var h = _hwnd;
        if (h != IntPtr.Zero) PostMessageW(h, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        _thread = null;
    }

    private static void Run()
    {
        try { Pump(); } catch { /* splash is best-effort */ }
        finally { _hwnd = IntPtr.Zero; }
    }

    private static void Pump()
    {
        var hInstance = GetModuleHandleW(null);
        _wndProc = WndProc;

        var wc = new WNDCLASSW
        {
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = hInstance,
            lpszClassName = ClassName,
            hCursor = LoadCursorW(IntPtr.Zero, (IntPtr)IDC_ARROW),
        };
        RegisterClassW(ref wc);

        _scale = GetScale();
        SetPalette();
        int w = S(460), h = S(160);
        GetWorkAreaCenter(w, h, out int x, out int y);

        var hwnd = CreateWindowExW(
            WS_EX_TOOLWINDOW | WS_EX_TOPMOST | WS_EX_NOACTIVATE,
            ClassName, "MenYou", WS_POPUP,
            x, y, w, h, IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);
        if (hwnd == IntPtr.Zero) { UnregisterClassW(ClassName, hInstance); return; }
        _hwnd = hwnd;

        // Icon: load the 256 px frame, then pre-scale ONCE to the display size
        // with high-quality bicubic so each frame is just a 1:1 alpha blit.
        var src = LoadIconBitmap(256);
        if (src is not null)
        {
            try { _iconBmp = ScaleHighQuality(src, S(120)); } catch { /* keep null */ }
            src.Dispose();
        }
        _titleFont = CreateFontW(-S(22), 0, 0, 0, FW_SEMIBOLD, 0u, 0u, 0u,
            DEFAULT_CHARSET, 0u, 0u, CLEARTYPE_QUALITY, 0u, "Segoe UI Variable Display");
        _subFont = CreateFontW(-S(13), 0, 0, 0, FW_NORMAL, 0u, 0u, 0u,
            DEFAULT_CHARSET, 0u, 0u, CLEARTYPE_QUALITY, 0u, "Segoe UI Variable Text");

        TryRoundCorners(hwnd);
        ShowWindow(hwnd, SW_SHOWNOACTIVATE);
        UpdateWindow(hwnd);

        SetTimer(hwnd, (UIntPtr)ANIM_TIMER, 33, IntPtr.Zero);      // ~30 fps marquee
        SetTimer(hwnd, (UIntPtr)SAFETY_TIMER, 25000, IntPtr.Zero); // never get stuck

        if (_closeRequested) { DestroyWindow(hwnd); }

        while (GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessageW(ref msg);
        }

        _iconBmp?.Dispose(); _iconBmp = null;
        if (_titleFont != IntPtr.Zero) { DeleteObject(_titleFont); _titleFont = IntPtr.Zero; }
        if (_subFont != IntPtr.Zero) { DeleteObject(_subFont); _subFont = IntPtr.Zero; }
        UnregisterClassW(ClassName, hInstance);
        GC.KeepAlive(_wndProc);
    }

    private static IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_PAINT:
                Paint(hwnd);
                return IntPtr.Zero;
            case WM_ERASEBKGND:
                return (IntPtr)1;   // double-buffered in WM_PAINT; skip default erase
            case WM_TIMER:
                if ((uint)wParam == ANIM_TIMER) { _frame++; InvalidateRect(hwnd, IntPtr.Zero, false); }
                else { KillTimer(hwnd, (UIntPtr)SAFETY_TIMER); DestroyWindow(hwnd); }
                return IntPtr.Zero;
            case WM_CLOSE:
                DestroyWindow(hwnd);
                return IntPtr.Zero;
            case WM_DESTROY:
                KillTimer(hwnd, (UIntPtr)ANIM_TIMER);
                KillTimer(hwnd, (UIntPtr)SAFETY_TIMER);
                PostQuitMessage(0);
                return IntPtr.Zero;
            default:
                return DefWindowProcW(hwnd, msg, wParam, lParam);
        }
    }

    private static void Paint(IntPtr hwnd)
    {
        var hdc = BeginPaint(hwnd, out var ps);
        if (hdc == IntPtr.Zero) return;

        GetClientRect(hwnd, out var rc);
        var mem = CreateCompatibleDC(hdc);
        var bmp = CreateCompatibleBitmap(hdc, rc.right, rc.bottom);
        var oldBmp = SelectObject(mem, bmp);
        try
        {
            // Card + border (theme-aware).
            FillSolid(mem, ref rc, _bg);
            var borderBrush = CreateSolidBrush(_border);
            FrameRect(mem, ref rc, borderBrush);
            DeleteObject(borderBrush);

            int cx = S(94), cy = rc.bottom / 2;
            int tx = S(176);

            // Right: title, subtitle, marquee (GDI).
            SetBkMode(mem, TRANSPARENT);
            DrawLeft(mem, _titleFont, _title, tx, S(42), S(78), _titleColor);
            DrawLeft(mem, _subFont, _subtitle, tx, S(80), S(104), _subColor);
            DrawMarquee(mem, tx, S(118), S(252), S(10), S(10));

            // Left: the icon, drawn LAST via GDI+ (so GDI and GDI+ don't
            // interleave). Pre-scaled, so this is a crisp 1:1 alpha blit. It's a
            // glossy circular button already — no extra ring.
            if (_iconBmp is not null)
            {
                try
                {
                    using var g = Graphics.FromHdc(mem);
                    g.DrawImageUnscaled(_iconBmp, cx - _iconBmp.Width / 2, cy - _iconBmp.Height / 2);
                }
                catch { /* icon is best-effort */ }
            }

            BitBlt(hdc, 0, 0, rc.right, rc.bottom, mem, 0, 0, SRCCOPY);
        }
        finally
        {
            SelectObject(mem, oldBmp);
            DeleteObject(bmp);
            DeleteDC(mem);
            EndPaint(hwnd, ref ps);
        }
    }

    private static void DrawMarquee(IntPtr hdc, int x, int y, int w, int h, int rad)
    {
        var track = CreateSolidBrush(_trackColor);
        var oldB = SelectObject(hdc, track);
        var oldP = SelectObject(hdc, GetStockObject(NULL_PEN));
        RoundRect(hdc, x, y, x + w, y + h, rad, rad);

        int segW = (int)(w * 0.35);
        int cycle = w + segW;
        int pos = (int)((_frame * (5 * _scale)) % cycle);
        int segLeft = x - segW + pos;
        SaveDC(hdc);
        IntersectClipRect(hdc, x, y, x + w, y + h);
        var accent = CreateSolidBrush(_accentColor);
        SelectObject(hdc, accent);
        RoundRect(hdc, segLeft, y, segLeft + segW, y + h, rad, rad);
        RestoreDC(hdc, -1);

        SelectObject(hdc, oldP);
        SelectObject(hdc, oldB);
        DeleteObject(track);
        DeleteObject(accent);
    }

    private static void DrawLeft(IntPtr hdc, IntPtr font, string text, int x, int top, int bottom, int color)
    {
        var old = SelectObject(hdc, font);
        SetTextColor(hdc, color);
        var rc = new RECT { left = x, top = top, right = x + S(270), bottom = bottom };
        DrawTextW(hdc, text, -1, ref rc, DT_LEFT | DT_SINGLELINE | DT_VCENTER | DT_NOPREFIX | DT_END_ELLIPSIS);
        SelectObject(hdc, old);
    }

    private static void FillSolid(IntPtr hdc, ref RECT rc, int color)
    {
        var b = CreateSolidBrush(color);
        FillRect(hdc, ref rc, b);
        DeleteObject(b);
    }

    private static void SetPalette()
    {
        if (IsLightTheme())
        {
            _bg = RGB(0xF3, 0xF3, 0xF3); _border = RGB(0xCF, 0xCF, 0xCF);
            _titleColor = RGB(0x1A, 0x1A, 0x1A); _subColor = RGB(0x5A, 0x5A, 0x5A);
            _trackColor = RGB(0xDC, 0xDC, 0xDC); _accentColor = RGB(0x00, 0x67, 0xC0);
        }
        else
        {
            _bg = RGB(0x20, 0x20, 0x20); _border = RGB(0x3A, 0x3A, 0x3A);
            _titleColor = RGB(0xF2, 0xF2, 0xF2); _subColor = RGB(0x9A, 0x9A, 0x9A);
            _trackColor = RGB(0x33, 0x33, 0x33); _accentColor = RGB(0x4C, 0x8B, 0xF5);
        }
    }

    private static bool IsLightTheme()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return k?.GetValue("AppsUseLightTheme") is int v && v != 0;
        }
        catch { return false; }
    }

    private static Bitmap ScaleHighQuality(Bitmap src, int size)
    {
        var dst = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(dst);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.CompositingQuality = CompositingQuality.HighQuality;
        g.DrawImage(src, new Rectangle(0, 0, size, size));
        return dst;
    }

    /// Load the app icon as a high-res managed Bitmap (the 256 px frame from the
    /// exe). PrivateExtractIcons honours the requested size and pulls the real
    /// 256 frame; we then scale it down ourselves with GDI+ for a crisp render.
    private static Bitmap? LoadIconBitmap(int size)
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return null;
            var icons = new IntPtr[1];
            var ids = new IntPtr[1];
            if (PrivateExtractIconsW(exe, 0, size, size, icons, ids, 1, 0) == 0 || icons[0] == IntPtr.Zero)
                return null;
            try
            {
                using var ico = Icon.FromHandle(icons[0]);
                return ico.ToBitmap();
            }
            finally { DestroyIcon(icons[0]); }
        }
        catch { return null; }
    }

    private static int S(int logical) => (int)(logical * _scale);

    private static double GetScale()
    {
        try { var dpi = GetDpiForSystem(); return dpi == 0 ? 1.0 : dpi / 96.0; }
        catch { return 1.0; }
    }

    private static void GetWorkAreaCenter(int w, int h, out int x, out int y)
    {
        var rc = new RECT();
        if (SystemParametersInfoW(SPI_GETWORKAREA, 0, ref rc, 0))
        {
            x = rc.left + ((rc.right - rc.left) - w) / 2;
            y = rc.top + ((rc.bottom - rc.top) - h) / 2;
        }
        else { x = 300; y = 300; }
    }

    private static void TryRoundCorners(IntPtr hwnd)
    {
        try { int pref = DWMWCP_ROUND; DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int)); }
        catch { /* pre-Win11: square corners are fine */ }
    }

    private static int RGB(int r, int g, int b) => r | (g << 8) | (b << 16);

    // ---- constants -------------------------------------------------------
    private const uint WS_POPUP = 0x80000000;
    private const uint WS_EX_TOPMOST = 0x00000008;
    private const uint WS_EX_TOOLWINDOW = 0x00000080;
    private const uint WS_EX_NOACTIVATE = 0x08000000;
    private const int SW_SHOWNOACTIVATE = 4;
    private const uint WM_PAINT = 0x000F;
    private const uint WM_ERASEBKGND = 0x0014;
    private const uint WM_TIMER = 0x0113;
    private const uint WM_CLOSE = 0x0010;
    private const uint WM_DESTROY = 0x0002;
    private const int IDC_ARROW = 32512;
    private const int TRANSPARENT = 1;
    private const int FW_NORMAL = 400, FW_SEMIBOLD = 600;
    private const uint DEFAULT_CHARSET = 1;
    private const uint CLEARTYPE_QUALITY = 5;
    private const uint DT_LEFT = 0x0, DT_VCENTER = 0x4, DT_SINGLELINE = 0x20, DT_NOPREFIX = 0x800, DT_END_ELLIPSIS = 0x8000;
    private const uint SPI_GETWORKAREA = 0x0030;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;
    private const int NULL_PEN = 8;
    private const uint SRCCOPY = 0x00CC0020;

    // ---- structs ---------------------------------------------------------
    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int left, top, right, bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd; public uint message; public IntPtr wParam; public IntPtr lParam;
        public uint time; public int pt_x; public int pt_y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PAINTSTRUCT
    {
        public IntPtr hdc; public int fErase; public RECT rcPaint; public int fRestore; public int fIncUpdate;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] rgbReserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WNDCLASSW
    {
        public uint style; public IntPtr lpfnWndProc; public int cbClsExtra; public int cbWndExtra;
        public IntPtr hInstance; public IntPtr hIcon; public IntPtr hCursor; public IntPtr hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
    }

    private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    // ---- imports ---------------------------------------------------------
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? name);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassW(ref WNDCLASSW wc);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool UnregisterClassW(string className, IntPtr hInstance);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(uint dwExStyle, string lpClassName, string lpWindowName,
        uint dwStyle, int x, int y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu,
        IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int nCmdShow);
    [DllImport("user32.dll")] private static extern bool UpdateWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool InvalidateRect(IntPtr hwnd, IntPtr rect, bool erase);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DefWindowProcW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetMessageW(out MSG msg, IntPtr hwnd, uint min, uint max);

    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG msg);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DispatchMessageW(ref MSG msg);

    [DllImport("user32.dll")] private static extern void PostQuitMessage(int code);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool PostMessageW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")] private static extern UIntPtr SetTimer(IntPtr hwnd, UIntPtr id, uint elapse, IntPtr proc);
    [DllImport("user32.dll")] private static extern bool KillTimer(IntPtr hwnd, UIntPtr id);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadCursorW(IntPtr hInstance, IntPtr lpCursorName);

    [DllImport("user32.dll")] private static extern IntPtr BeginPaint(IntPtr hwnd, out PAINTSTRUCT ps);
    [DllImport("user32.dll")] private static extern bool EndPaint(IntPtr hwnd, ref PAINTSTRUCT ps);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hwnd, out RECT rc);
    [DllImport("user32.dll")] private static extern int FillRect(IntPtr hdc, ref RECT rc, IntPtr hbr);
    [DllImport("user32.dll")] private static extern int FrameRect(IntPtr hdc, ref RECT rc, IntPtr hbr);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int DrawTextW(IntPtr hdc, string text, int count, ref RECT rc, uint format);

    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr hIcon);
    [DllImport("user32.dll")] private static extern uint GetDpiForSystem();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfoW(uint action, uint param, ref RECT rc, uint winIni);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint PrivateExtractIconsW(string szFileName, int nIconIndex, int cxIcon, int cyIcon,
        [Out] IntPtr[] phicon, [Out] IntPtr[] piconid, uint nIcons, uint flags);

    [DllImport("gdi32.dll")] private static extern IntPtr CreateSolidBrush(int color);
    [DllImport("gdi32.dll")] private static extern IntPtr GetStockObject(int obj);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern int SetBkMode(IntPtr hdc, int mode);
    [DllImport("gdi32.dll")] private static extern int SetTextColor(IntPtr hdc, int color);
    [DllImport("gdi32.dll")] private static extern bool RoundRect(IntPtr hdc, int l, int t, int r, int b, int ew, int eh);
    [DllImport("gdi32.dll")] private static extern int IntersectClipRect(IntPtr hdc, int l, int t, int r, int b);
    [DllImport("gdi32.dll")] private static extern int SaveDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern bool RestoreDC(IntPtr hdc, int saved);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int w, int h);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, uint rop);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFontW(int cHeight, int cWidth, int cEscapement, int cOrientation,
        int cWeight, uint bItalic, uint bUnderline, uint bStrikeOut, uint iCharSet, uint iOutPrecision,
        uint iClipPrecision, uint iQuality, uint iPitchAndFamily, string pszFaceName);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
}
