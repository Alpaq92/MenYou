using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace MenYou.Platform.Windows;

/// Detects the system Start menu opening (mouse click on the taskbar Start
/// button, Ctrl+Esc, etc.), dismisses it, and fires <see cref="Intercepted"/>
/// so a caller can show MenYou instead.
///
/// On Win 10/11 the Start menu is hosted out-of-process by
/// <c>StartMenuExperienceHost.exe</c>. Its windows are pre-created and merely
/// hidden/shown across opens, so <c>EVENT_OBJECT_SHOW</c> alone is unreliable
/// (e.g. <c>SetWindowPos</c>-based showing won't always raise it). We install
/// two SetWinEventHook ranges:
///   - <c>EVENT_SYSTEM_FOREGROUND</c> — fires whenever the Start menu window
///     gains focus, which is the most reliable single signal across versions.
///   - <c>EVENT_OBJECT_SHOW</c> — covers the case where a host process raises
///     SHOW but the window doesn't immediately steal foreground.
///
/// Both run on a dedicated STA thread with a message loop, as required by
/// <c>WINEVENT_OUTOFCONTEXT</c>. Process-name lookups are cached by PID.
[SupportedOSPlatform("windows")]
internal sealed class StartMenuMonitor : IDisposable
{
    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    private const uint EVENT_OBJECT_SHOW = 0x8002;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    private const uint WINEVENT_SKIPOWNPROCESS = 0x0002;
    private const int OBJID_WINDOW = 0;
    private const int SW_HIDE = 0;
    private const uint VK_ESCAPE = 0x1B;
    private const uint KEYEVENTF_KEYUP = 0x02;
    private const int WM_QUIT = 0x0012;
    private const int ThrottleMillis = 300;

    private static readonly HashSet<string> HostProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "StartMenuExperienceHost",
        "SearchHost",
        "SearchApp",
        "ShellExperienceHost",
    };

    public event Action? Intercepted;

    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new();
    private readonly Dictionary<uint, bool> _pidIsHost = new();
    private WinEventDelegate? _proc;
    private IntPtr _hookForeground;
    private IntPtr _hookShow;
    private uint _threadId;
    private long _lastFireTicks;
    private bool _disposed;

    public StartMenuMonitor()
    {
        _thread = new Thread(MessageLoop) { IsBackground = true, Name = "MenYou.StartMenuMonitor" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _ready.Wait();
        HookTrace.Log("StartMenuMonitor: installed");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        HookTrace.Log("StartMenuMonitor: disposing");
        if (_threadId != 0) PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        if (Thread.CurrentThread != _thread) _thread.Join(500);
    }

    private void MessageLoop()
    {
        _threadId = GetCurrentThreadId();
        _proc = OnEvent;

        _hookForeground = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero, _proc, 0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
        _hookShow = SetWinEventHook(EVENT_OBJECT_SHOW, EVENT_OBJECT_SHOW,
            IntPtr.Zero, _proc, 0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);

        _ready.Set();
        if (_hookForeground == IntPtr.Zero && _hookShow == IntPtr.Zero)
        {
            HookTrace.Log("StartMenuMonitor: SetWinEventHook failed for both events");
            return;
        }

        while (GetMessage(out var msg, IntPtr.Zero, 0, 0))
        {
            TranslateMessage(ref msg);
            DispatchMessageW(ref msg);
        }

        if (_hookForeground != IntPtr.Zero) UnhookWinEvent(_hookForeground);
        if (_hookShow != IntPtr.Zero) UnhookWinEvent(_hookShow);
        _hookForeground = _hookShow = IntPtr.Zero;
    }

    private void OnEvent(IntPtr hWinEventHook, uint @event, IntPtr hwnd,
        int idObject, int idChild, uint thread, uint time)
    {
        // Only react to top-level window events. Foreground events always have
        // idObject == OBJID_WINDOW so this filter is cheap.
        if (idObject != OBJID_WINDOW || idChild != 0 || hwnd == IntPtr.Zero) return;
        if (!IsWindowVisible(hwnd)) return;

        if (!IsStartHostWindow(hwnd, out var processName))
        {
            // In trace mode we still want to see which windows we're seeing,
            // to diagnose mis-detection. Only foreground events, to keep noise
            // down — SHOW is much chattier.
            if (HookTrace.Enabled && @event == EVENT_SYSTEM_FOREGROUND)
                HookTrace.Log($"StartMenuMonitor: ignored foreground hwnd=0x{hwnd:X} proc={processName ?? "?"} cls={GetClass(hwnd)}");
            return;
        }

        var now = Environment.TickCount64;
        if (now - _lastFireTicks < ThrottleMillis) return;
        _lastFireTicks = now;

        HookTrace.Log($"StartMenuMonitor: intercept evt=0x{@event:X4} hwnd=0x{hwnd:X} proc={processName} cls={GetClass(hwnd)}");
        DismissStart(hwnd);
        try { Intercepted?.Invoke(); } catch { /* swallow */ }
    }

    private bool IsStartHostWindow(IntPtr hwnd, out string? processName)
    {
        processName = null;
        try
        {
            GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == 0) return false;
            if (_pidIsHost.TryGetValue(pid, out var cached))
            {
                if (cached) processName = ProcessNameSafe(pid);
                return cached;
            }

            var name = ProcessNameSafe(pid);
            processName = name;
            var match = name is not null && HostProcessNames.Contains(name);
            _pidIsHost[pid] = match;
            return match;
        }
        catch
        {
            return false;
        }
    }

    private static string? ProcessNameSafe(uint pid)
    {
        try
        {
            using var p = Process.GetProcessById((int)pid);
            return p.ProcessName;
        }
        catch
        {
            return null;
        }
    }

    private static string GetClass(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        return GetClassNameW(hwnd, sb, sb.Capacity) > 0 ? sb.ToString() : "?";
    }

    private static void DismissStart(IntPtr hwnd)
    {
        SendEscape();
        ShowWindow(hwnd, SW_HIDE);
    }

    private static void SendEscape()
    {
        var inputs = new INPUT[2];
        inputs[0] = new INPUT { type = 1 };
        inputs[0].U.ki = new KEYBDINPUT { wVk = (ushort)VK_ESCAPE };
        inputs[1] = new INPUT { type = 1 };
        inputs[1].U.ki = new KEYBDINPUT { wVk = (ushort)VK_ESCAPE, dwFlags = KEYEVENTF_KEYUP };
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
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
    private struct INPUT
    {
        public uint type;
        public INPUTUNION U;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct INPUTUNION
    {
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public UIntPtr dwExtraInfo;
        public uint padding1;
        public uint padding2;
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

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

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

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);
}
