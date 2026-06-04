using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace MenYou.Platform.Windows;

/// Global low-level mouse hook that intercepts left-button clicks on the
/// Start button and fires <see cref="StartClicked"/>. Unlike an overlay
/// window, the LL hook runs BEFORE Explorer's DirectComposition input
/// routing, so swallowing the click here actually prevents the system Start
/// menu from opening — no flash.
///
/// Lives on its own STA thread with a message pump (required for
/// out-of-context hooks). Re-queries the Start button rect every time it
/// sees an event so taskbar moves don't desync us; the query is cheap
/// (Shell_TrayWnd GetWindowRect + a registry read).
[SupportedOSPlatform("windows")]
internal sealed class StartClickHook : IDisposable
{
    private const int WH_MOUSE_LL = 14;
    private const int WM_LBUTTONDOWN = 0x0201;
    private const int WM_LBUTTONUP = 0x0202;
    private const uint LLMHF_INJECTED = 0x01;
    private const int WM_QUIT = 0x0012;

    public event Action? StartClicked;

    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new();
    private LowLevelMouseProc? _proc;
    private IntPtr _hHook;
    private uint _threadId;
    private bool _disposed;
    private StartButtonLocator.RECT _startRect;
    private long _lastRectRefreshTicks;
    private bool _consumeUp;

    public StartClickHook()
    {
        _thread = new Thread(MessageLoop) { IsBackground = true, Name = "MenYou.StartClickHook" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _ready.Wait();
        HookTrace.Log("StartClickHook: installed");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        HookTrace.Log("StartClickHook: disposing");
        if (_threadId != 0) PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        if (Thread.CurrentThread != _thread) _thread.Join(500);
    }

    private void MessageLoop()
    {
        _threadId = GetCurrentThreadId();
        _proc = HookProc;
        var hMod = GetModuleHandleW(null);
        _hHook = SetWindowsHookExW(WH_MOUSE_LL, _proc, hMod, 0);
        _ready.Set();
        if (_hHook == IntPtr.Zero)
        {
            HookTrace.Log($"StartClickHook: SetWindowsHookEx failed err={Marshal.GetLastWin32Error()}");
            return;
        }
        _startRect = StartButtonLocator.Get();
        HookTrace.Log($"StartClickHook: initial Start rect {_startRect}");

        while (GetMessage(out var msg, IntPtr.Zero, 0, 0))
        {
            TranslateMessage(ref msg);
            DispatchMessageW(ref msg);
        }
        UnhookWindowsHookEx(_hHook);
        _hHook = IntPtr.Zero;
    }

    private void EnsureRectFresh()
    {
        // Refresh at most every 250 ms; the user can't move the taskbar that fast.
        var now = Environment.TickCount64;
        if (now - _lastRectRefreshTicks < 250) return;
        _lastRectRefreshTicks = now;
        var fresh = StartButtonLocator.Get();
        if (!fresh.IsEmpty) _startRect = fresh;
    }

    private IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code < 0) return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);

        var msg = (int)wParam;
        if (msg != WM_LBUTTONDOWN && msg != WM_LBUTTONUP)
            return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);

        var data = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
        // Ignore our own synthetic input so any test or self-trigger doesn't
        // recursively fire the hook.
        if ((data.flags & LLMHF_INJECTED) != 0)
            return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
        HookTrace.Log($"StartClickHook: saw msg=0x{msg:X} at ({data.pt.X},{data.pt.Y}) flags=0x{data.flags:X}");

        EnsureRectFresh();
        if (_startRect.IsEmpty) return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);

        var inside = _startRect.Contains(data.pt.X, data.pt.Y);

        if (msg == WM_LBUTTONDOWN && inside)
        {
            // If the system Start menu is already open, let the click flow
            // through so Windows' own toggle behavior closes it — otherwise
            // we'd pop MenYou behind the still-open system menu.
            if (SystemStartMenu.IsOpen())
            {
                HookTrace.Log($"StartClickHook: LBUTTONDOWN at ({data.pt.X},{data.pt.Y}) — system Start is open, passing through");
                return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
            }
            // Swallow the down. Also swallow the matching up so the focused
            // app doesn't get a stray "click released" event later.
            HookTrace.Log($"StartClickHook: intercepted LBUTTONDOWN at ({data.pt.X},{data.pt.Y})");
            _consumeUp = true;
            try { StartClicked?.Invoke(); } catch { }
            return new IntPtr(1);
        }

        if (msg == WM_LBUTTONUP && _consumeUp)
        {
            _consumeUp = false;
            return new IntPtr(1);
        }

        return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X, Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public UIntPtr dwExtraInfo;
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

    private delegate IntPtr LowLevelMouseProc(int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetWindowsHookExW(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int code, IntPtr wParam, IntPtr lParam);

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

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? name);
}
