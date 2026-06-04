using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace MenYou.Platform.Windows;

/// Hidden message-only window with the well-known class name
/// "MenYouBridgeListener". The native bridge DLL inside Explorer finds it via
/// FindWindowEx(HWND_MESSAGE, ...) and posts WM_COPYDATA with a small
/// payload ID indicating which gesture (Start click, Win key) was
/// intercepted.
///
/// Runs on its own STA thread with a standard message loop so receiving
/// these messages doesn't depend on Avalonia's dispatcher being a Win32
/// pump (it isn't, on Windows).
[SupportedOSPlatform("windows")]
internal sealed class CopyDataListener : IDisposable
{
    public const string ClassName = "MenYouBridgeListener";

    public enum NotifyId
    {
        StartClicked = 1,
        WinKey       = 2,
    }

    public sealed record Message(NotifyId Id, string? Payload);

    public event Action<Message>? Received;

    private const int WM_COPYDATA = 0x004A;
    private const int WM_QUIT = 0x0012;
    private const int HWND_MESSAGE = -3;

    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new();
    private WndProcDelegate? _wndProc;
    private IntPtr _hwnd;
    private uint _threadId;
    private bool _disposed;

    public CopyDataListener()
    {
        _thread = new Thread(Pump) { IsBackground = true, Name = "MenYou.CopyDataListener" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _ready.Wait();
        HookTrace.Log($"CopyDataListener: hwnd=0x{_hwnd.ToInt64():X}");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_threadId != 0) PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        if (Thread.CurrentThread != _thread) _thread.Join(500);
    }

    /// Sends a notification with an optional UTF-16 string payload to a
    /// running MenYou instance. Returns false if no listener is reachable.
    public static bool SendTo(NotifyId id, string? payload = null)
    {
        var hwnd = FindWindowExW(new IntPtr(HWND_MESSAGE), IntPtr.Zero, ClassName, null);
        if (hwnd == IntPtr.Zero) return false;

        var bytes = string.IsNullOrEmpty(payload)
            ? Array.Empty<byte>()
            : System.Text.Encoding.Unicode.GetBytes(payload + "\0");
        var ptr = bytes.Length > 0 ? Marshal.AllocHGlobal(bytes.Length) : IntPtr.Zero;
        try
        {
            if (ptr != IntPtr.Zero) Marshal.Copy(bytes, 0, ptr, bytes.Length);
            var cds = new COPYDATASTRUCT
            {
                dwData = new IntPtr((long)id),
                cbData = (uint)bytes.Length,
                lpData = ptr,
            };
            SendMessageTimeoutW(hwnd, WM_COPYDATA, IntPtr.Zero, ref cds,
                SMTO_ABORTIFHUNG | SMTO_BLOCK, 1000, out _);
            return true;
        }
        finally
        {
            if (ptr != IntPtr.Zero) Marshal.FreeHGlobal(ptr);
        }
    }

    private void Pump()
    {
        _threadId = GetCurrentThreadId();
        _wndProc = WndProc;

        var wc = new WNDCLASSW
        {
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = GetModuleHandleW(null),
            lpszClassName = ClassName,
        };
        var atom = RegisterClassW(ref wc);
        if (atom == 0)
        {
            HookTrace.Log($"CopyDataListener: RegisterClass failed err={Marshal.GetLastWin32Error()}");
            _ready.Set();
            return;
        }

        _hwnd = CreateWindowExW(0, ClassName, "MenYouBridgeListener",
            0, 0, 0, 0, 0, new IntPtr(HWND_MESSAGE), IntPtr.Zero, wc.hInstance, IntPtr.Zero);
        _ready.Set();
        if (_hwnd == IntPtr.Zero)
        {
            HookTrace.Log($"CopyDataListener: CreateWindowEx failed err={Marshal.GetLastWin32Error()}");
            return;
        }

        while (GetMessage(out var msg, IntPtr.Zero, 0, 0))
        {
            TranslateMessage(ref msg);
            DispatchMessageW(ref msg);
        }

        DestroyWindow(_hwnd);
        UnregisterClassW(ClassName, wc.hInstance);
    }

    private IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_COPYDATA)
        {
            var cds = Marshal.PtrToStructure<COPYDATASTRUCT>(lParam);
            var id = (NotifyId)cds.dwData.ToInt64();
            string? payload = null;
            if (cds.cbData > 0 && cds.lpData != IntPtr.Zero)
            {
                // Trim trailing null terminator if present.
                payload = Marshal.PtrToStringUni(cds.lpData, (int)(cds.cbData / 2))?.TrimEnd('\0');
            }
            HookTrace.Log($"CopyDataListener: received id={id} payload='{payload ?? "(none)"}'");
            try { Received?.Invoke(new Message(id, payload)); } catch { }
            return new IntPtr(1);
        }
        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
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

    [StructLayout(LayoutKind.Sequential)]
    private struct COPYDATASTRUCT
    {
        public IntPtr dwData;
        public uint cbData;
        public IntPtr lpData;
    }

    private const uint SMTO_ABORTIFHUNG = 0x0002;
    private const uint SMTO_BLOCK       = 0x0001;

    private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassW(ref WNDCLASSW wc);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool UnregisterClassW(string className, IntPtr hInstance);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(uint dwExStyle, string lpClassName, string lpWindowName,
        uint dwStyle, int x, int y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu,
        IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindWindowExW(IntPtr parent, IntPtr after, string className, string? title);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageTimeoutW(IntPtr hWnd, uint msg, IntPtr wParam, ref COPYDATASTRUCT lParam,
        uint flags, uint timeout, out IntPtr result);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DefWindowProcW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMessage(out MSG msg, IntPtr hwnd, uint min, uint max);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool TranslateMessage(ref MSG msg);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DispatchMessageW(ref MSG msg);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostThreadMessage(uint idThread, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? name);
}
