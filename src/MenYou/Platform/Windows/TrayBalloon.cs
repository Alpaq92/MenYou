using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace MenYou.Platform.Windows;

/// One-shot Shell_NotifyIcon balloon-tip helper. Avalonia's TrayIcon
/// abstraction doesn't surface NIF_INFO balloons, so when we need to
/// nudge the user (e.g. "Win 11 Start mirror is unavailable on this
/// SKU") we drop down to Win32 directly with a transient tray icon.
///
/// The icon registers, fires the balloon, and unregisters as soon as the
/// balloon dismisses — we don't keep a second tray icon around. This
/// avoids the "two MenYou tray icons" weirdness that would result from
/// trying to bolt NIF_INFO onto Avalonia's tray icon by HWND lookup.
[SupportedOSPlatform("windows")]
internal static class TrayBalloon
{
    public static void Show(string title, string body)
    {
        // Run on a background STA so the hidden message window pumps
        // independently of Avalonia's dispatcher (Win32 balloons need a
        // window to receive the NIN_BALLOONUSERCLICK / TIMEOUT messages,
        // but for our fire-and-forget case we can keep the pump short and
        // just wait for the timeout).
        var thread = new Thread(() => Pump(title, body))
        {
            IsBackground = true,
            Name = "MenYou.TrayBalloon",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    private static void Pump(string title, string body)
    {
        var hInstance = GetModuleHandleW(null);

        var wndProc = (WndProcDelegate)((h, m, w, l) => DefWindowProcW(h, m, w, l));
        var wndProcPtr = Marshal.GetFunctionPointerForDelegate(wndProc);

        var wc = new WNDCLASSW
        {
            lpfnWndProc = wndProcPtr,
            hInstance = hInstance,
            lpszClassName = "MenYouTrayBalloon",
        };
        if (RegisterClassW(ref wc) == 0)
            return;

        var hwnd = CreateWindowExW(0, "MenYouTrayBalloon", "MenYouTrayBalloon",
            0, 0, 0, 0, 0, new IntPtr(-3), IntPtr.Zero, hInstance, IntPtr.Zero);
        if (hwnd == IntPtr.Zero)
        {
            UnregisterClassW("MenYouTrayBalloon", hInstance);
            return;
        }

        try
        {
            var data = new NOTIFYICONDATAW
            {
                cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
                hWnd = hwnd,
                uID = 1,
                uFlags = NIF_INFO | NIF_ICON,
                hIcon = LoadIconW(IntPtr.Zero, new IntPtr(IDI_INFORMATION)),
                szInfoTitle = title,
                szInfo = body,
                dwInfoFlags = NIIF_INFO,
            };

            // Add + Modify pattern — Add registers the icon (we leave it
            // invisible by not setting NIF_TIP/state), Modify triggers the
            // balloon. Then a short pump so Windows actually displays it
            // before we tear down.
            if (Shell_NotifyIconW(NIM_ADD, ref data))
            {
                Shell_NotifyIconW(NIM_MODIFY, ref data);

                // Pump for ~6 s so the balloon has time to appear and
                // dismiss naturally. (System default balloon timeout is
                // 5–7 s on Win 11.)
                var end = Environment.TickCount64 + 6500;
                while (Environment.TickCount64 < end)
                {
                    if (PeekMessageW(out var msg, IntPtr.Zero, 0, 0, PM_REMOVE))
                    {
                        TranslateMessage(ref msg);
                        DispatchMessageW(ref msg);
                    }
                    else
                    {
                        Thread.Sleep(50);
                    }
                }

                Shell_NotifyIconW(NIM_DELETE, ref data);
            }
        }
        finally
        {
            DestroyWindow(hwnd);
            UnregisterClassW("MenYouTrayBalloon", hInstance);
            GC.KeepAlive(wndProc);
        }
    }

    private const uint NIF_ICON = 0x00000002;
    private const uint NIF_INFO = 0x00000010;
    private const uint NIM_ADD = 0x00000000;
    private const uint NIM_MODIFY = 0x00000001;
    private const uint NIM_DELETE = 0x00000002;
    private const uint NIIF_INFO = 0x00000001;
    private const int  IDI_INFORMATION = 32516;
    private const uint PM_REMOVE = 0x0001;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATAW
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WNDCLASSW
    {
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int pt_x;
        public int pt_y;
    }

    private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Shell_NotifyIconW(uint dwMessage, ref NOTIFYICONDATAW lpData);

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

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DefWindowProcW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool PeekMessageW(out MSG msg, IntPtr hwnd, uint min, uint max, uint remove);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool TranslateMessage(ref MSG msg);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DispatchMessageW(ref MSG msg);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadIconW(IntPtr hInstance, IntPtr lpIconName);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? name);
}
