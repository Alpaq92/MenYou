using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace MenYou.Platform.Windows;

/// Hidden Win32 message-only window that owns hotkey registrations and
/// fires .Pressed when WM_HOTKEY arrives. Lives on its own STA thread so we
/// don't depend on Avalonia's loop being a Win32 message loop.
[SupportedOSPlatform("windows")]
internal sealed class HotkeyWindow : IDisposable
{
    private const int HWND_MESSAGE = -3;

    public event Action<int>? Pressed;

    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new(false);
    private readonly Dictionary<int, (uint mods, uint vk)> _pending = new();
    private IntPtr _hwnd;
    private uint _threadId;
    private WndProcDelegate? _wndProcDelegate;
    private bool _disposed;

    public HotkeyWindow()
    {
        _thread = new Thread(MessageLoop)
        {
            IsBackground = true,
            Name = "MenYou.HotkeyWindow"
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _ready.Wait();
    }

    public bool Register(int id, uint modifiers, uint virtualKey)
    {
        if (_disposed) return false;
        lock (_pending)
        {
            _pending[id] = (modifiers, virtualKey);
        }
        return PostThreadMessage(_threadId, WM_USER_REGISTER, id, 0);
    }

    public void Unregister(int id)
    {
        if (_disposed) return;
        PostThreadMessage(_threadId, WM_USER_UNREGISTER, id, 0);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        PostThreadMessage(_threadId, WM_QUIT, 0, 0);
        if (Thread.CurrentThread != _thread)
            _thread.Join(500);
    }

    private const int WM_USER_REGISTER = 0x0400 + 1;
    private const int WM_USER_UNREGISTER = 0x0400 + 2;
    private const int WM_QUIT = 0x0012;

    private void MessageLoop()
    {
        _threadId = GetCurrentThreadId();

        _wndProcDelegate = WndProc;
        var wndClass = new WNDCLASS
        {
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProcDelegate),
            hInstance = GetModuleHandle(null),
            lpszClassName = "MenYou.HotkeyWindow." + Guid.NewGuid().ToString("N")
        };
        var atom = RegisterClassW(ref wndClass);
        if (atom == 0)
        {
            _ready.Set();
            return;
        }

        _hwnd = CreateWindowExW(0, wndClass.lpszClassName, "MenYou.Hotkey",
            0, 0, 0, 0, 0, new IntPtr(HWND_MESSAGE), IntPtr.Zero, wndClass.hInstance, IntPtr.Zero);
        _ready.Set();
        if (_hwnd == IntPtr.Zero) return;

        while (GetMessage(out var msg, IntPtr.Zero, 0, 0))
        {
            if (msg.message == NativeMethods.WM_HOTKEY)
            {
                var id = (int)msg.wParam;
                try { Pressed?.Invoke(id); } catch { /* swallow */ }
            }
            else if (msg.message == WM_USER_REGISTER)
            {
                (uint mods, uint vk) entry;
                lock (_pending)
                {
                    if (!_pending.TryGetValue((int)msg.wParam, out entry)) continue;
                }
                NativeMethods.RegisterHotKey(_hwnd, (int)msg.wParam, entry.mods, entry.vk);
            }
            else if (msg.message == WM_USER_UNREGISTER)
            {
                NativeMethods.UnregisterHotKey(_hwnd, (int)msg.wParam);
            }
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }

        DestroyWindow(_hwnd);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASS
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

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassW(ref WNDCLASS wndClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(uint dwExStyle, string lpClassName, string lpWindowName,
        uint dwStyle, int x, int y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu,
        IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DispatchMessage(ref MSG lpMsg);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostThreadMessage(uint idThread, int Msg, int wParam, int lParam);

    private IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
        => DefWindowProcW(hwnd, msg, wParam, lParam);
}
