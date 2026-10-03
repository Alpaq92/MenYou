using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace MenYou.Platform.Windows;

/// Raises <see cref="ExplorerRestarted"/> whenever Explorer relaunches.
///
/// Why this exists: MenYou's Win-key interception lives in a thread-targeted
/// <c>WH_GETMESSAGE</c> hook on Explorer's UI thread (see
/// <see cref="BridgeInjector"/>). When Explorer dies and restarts — a crash, a
/// "Restart Explorer" from Task Manager, an in-place Windows update — that
/// thread is gone and the hook with it. Nothing errors and MenYou keeps
/// running, so the failure is SILENT: the Start button and the Win key quietly
/// revert to the system menu until MenYou itself is restarted.
///
/// Open-Shell solved exactly this and is the model here: <c>CStartHookWindow</c>
/// in Src/StartMenu/StartMenu.cpp is, in its own words, "a hidden window that
/// waits for the TaskbarCreated message and rehooks the explorer process".
/// Windows broadcasts the registered <c>TaskbarCreated</c> message to every
/// top-level window once the new Explorer's taskbar is up — which is precisely
/// the moment a new hook can be installed.
///
/// Two details this depends on:
///   • The window must be TOP-LEVEL, not message-only. Broadcasts reach
///     top-level windows; an HWND_MESSAGE window never sees them. (That is why
///     this cannot simply live in <see cref="HotkeyWindow"/>, which is
///     deliberately message-only.) It is still invisible — created without
///     WS_VISIBLE and sized 0x0.
///   • <c>ChangeWindowMessageFilterEx</c> must allow the message through, or
///     UIPI drops the broadcast when Explorer runs at a different integrity
///     level than MenYou. Open-Shell makes the same call for the same reason.
///
/// Owns a dedicated thread and message loop, mirroring
/// <see cref="HotkeyWindow"/>, so it neither depends on Avalonia's pump nor
/// blocks it.
[SupportedOSPlatform("windows")]
internal sealed class ExplorerRestartWatcher : IDisposable
{
    /// Fired on the watcher's own thread once Explorer's taskbar is back.
    /// Handlers must marshal to whatever thread they need themselves.
    public event Action? ExplorerRestarted;

    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new(false);
    private WndProcDelegate? _wndProcDelegate;
    private IntPtr _hwnd;
    private uint _taskbarCreatedMsg;
    private uint _threadId;
    private bool _disposed;

    public ExplorerRestartWatcher()
    {
        _thread = new Thread(MessageLoop)
        {
            IsBackground = true,
            Name = "MenYou.ExplorerRestartWatcher",
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        // No wait here. This is constructed on the cold-start path (EnsureBridge,
        // before Avalonia), and nothing needs the window to exist yet — its only
        // input is a broadcast from some future Explorer restart. The one thing
        // that does depend on setup having finished is Dispose, which needs
        // _threadId to post WM_QUIT, so the handshake is awaited there instead.
    }

    private void MessageLoop()
    {
        _threadId = GetCurrentThreadId();
        _wndProcDelegate = WndProc;

        var wndClass = new WNDCLASS
        {
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProcDelegate),
            hInstance = GetModuleHandle(null),
            lpszClassName = "MenYou.ExplorerRestartWatcher." + Guid.NewGuid().ToString("N"),
        };
        if (RegisterClassW(ref wndClass) == 0)
        {
            HookTrace.Log($"ExplorerRestartWatcher: RegisterClass failed err={Marshal.GetLastWin32Error()}");
            _ready.Set();
            return;
        }

        // Top-level (parent IntPtr.Zero), WS_POPUP, never shown. A broadcast
        // only reaches top-level windows, so HWND_MESSAGE is not an option.
        _hwnd = CreateWindowExW(0, wndClass.lpszClassName, "MenYou.ExplorerRestartWatcher",
            WS_POPUP, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, wndClass.hInstance, IntPtr.Zero);
        if (_hwnd == IntPtr.Zero)
        {
            HookTrace.Log($"ExplorerRestartWatcher: CreateWindow failed err={Marshal.GetLastWin32Error()}");
            _ready.Set();
            return;
        }

        _taskbarCreatedMsg = RegisterWindowMessageW("TaskbarCreated");
        if (_taskbarCreatedMsg == 0)
            HookTrace.Log("ExplorerRestartWatcher: RegisterWindowMessage(TaskbarCreated) failed");
        else if (!ChangeWindowMessageFilterEx(_hwnd, _taskbarCreatedMsg, MSGFLT_ALLOW, IntPtr.Zero))
            // Non-fatal: the filter is only needed when Explorer and MenYou run
            // at different integrity levels. Log and keep listening.
            HookTrace.Log($"ExplorerRestartWatcher: message filter not applied err={Marshal.GetLastWin32Error()}");

        HookTrace.Log($"ExplorerRestartWatcher: listening (msg=0x{_taskbarCreatedMsg:X})");
        _ready.Set();

        while (GetMessage(out var msg, IntPtr.Zero, 0, 0))
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }

        DestroyWindow(_hwnd);
        _hwnd = IntPtr.Zero;
    }

    private IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (_taskbarCreatedMsg != 0 && msg == _taskbarCreatedMsg)
        {
            HookTrace.Log("ExplorerRestartWatcher: TaskbarCreated — Explorer restarted");
            // Never let a handler fault take down the watcher thread: this
            // window is the only thing that notices Explorer coming back, so if
            // it dies the hook stays broken for the rest of the session.
            try { ExplorerRestarted?.Invoke(); }
            catch (Exception ex) { HookTrace.Log($"ExplorerRestartWatcher: handler threw ({ex.GetType().Name})"); }
        }
        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _ready.Wait(500);   // see the constructor: setup must finish before _threadId is valid
        if (_threadId != 0)
            PostThreadMessageW(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        if (Thread.CurrentThread != _thread)
            _thread.Join(500);
        _ready.Dispose();
        HookTrace.Log("ExplorerRestartWatcher: disposed");
    }

    // ---- interop ---------------------------------------------------------

    private const uint WS_POPUP = 0x80000000;
    private const uint WM_QUIT = 0x0012;
    private const uint MSGFLT_ALLOW = 1;

    private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

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
        public int ptX;
        public int ptY;
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassW(ref WNDCLASS lpWndClass);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowExW(uint dwExStyle, string lpClassName, string lpWindowName,
        uint dwStyle, int x, int y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu,
        IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessageW(string lpString);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ChangeWindowMessageFilterEx(IntPtr hwnd, uint message, uint action, IntPtr pChangeFilterStruct);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DispatchMessage(ref MSG lpMsg);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostThreadMessageW(uint idThread, uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);
}
