using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace MenYou.Platform.Windows;

/// Watches global foreground-window changes via SetWinEventHook and fires
/// <see cref="ForegroundLeftOurProcess"/> whenever the new foreground belongs
/// to another process.
///
/// We need this because Avalonia's <c>Window.Deactivated</c> event doesn't
/// fire reliably for our popup style on Win 11 24H2 (the combination of
/// <c>WindowDecorations="None"</c> + <c>Topmost="True"</c> +
/// <c>ShowInTaskbar="False"</c> apparently keeps the window outside the
/// standard activation flow). This Win32 watcher is the reliable substitute.
[SupportedOSPlatform("windows")]
internal sealed class ForegroundWatcher : IDisposable
{
    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    private const int OBJID_WINDOW = 0;
    private const int WM_QUIT = 0x0012;

    public event Action? ForegroundLeftOurProcess;

    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new();
    private readonly uint _ourPid;
    private WinEventDelegate? _proc;
    private IntPtr _hook;
    private uint _threadId;
    private bool _disposed;

    public ForegroundWatcher()
    {
        _ourPid = (uint)Environment.ProcessId;
        _thread = new Thread(MessageLoop) { IsBackground = true, Name = "MenYou.ForegroundWatcher" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _ready.Wait();
        HookTrace.Log("ForegroundWatcher: installed");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_threadId != 0) PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        if (Thread.CurrentThread != _thread) _thread.Join(500);
    }

    private void MessageLoop()
    {
        _threadId = GetCurrentThreadId();
        _proc = OnEvent;
        // No SKIPOWNPROCESS — we explicitly want to see foreground events for
        // both our own window (so we ignore them) and others (to fire the
        // event). Filtering happens in OnEvent.
        _hook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero, _proc, 0, 0, WINEVENT_OUTOFCONTEXT);
        _ready.Set();
        if (_hook == IntPtr.Zero)
        {
            HookTrace.Log($"ForegroundWatcher: SetWinEventHook failed err={Marshal.GetLastWin32Error()}");
            return;
        }

        while (GetMessage(out var msg, IntPtr.Zero, 0, 0))
        {
            TranslateMessage(ref msg);
            DispatchMessageW(ref msg);
        }
        UnhookWinEvent(_hook);
        _hook = IntPtr.Zero;
    }

    private void OnEvent(IntPtr hHook, uint @event, IntPtr hwnd,
        int idObject, int idChild, uint thread, uint time)
    {
        if (idObject != OBJID_WINDOW || idChild != 0 || hwnd == IntPtr.Zero) return;
        GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == _ourPid) return;
        try { ForegroundLeftOurProcess?.Invoke(); } catch { }
    }

    private delegate void WinEventDelegate(IntPtr hWinEventHook, uint @event, IntPtr hwnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc,
        WinEventDelegate pfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DispatchMessageW(ref MSG lpMsg);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostThreadMessage(uint idThread, int Msg, IntPtr wParam, IntPtr lParam);

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
}
