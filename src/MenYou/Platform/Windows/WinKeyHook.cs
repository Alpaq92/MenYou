using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace MenYou.Platform.Windows;

/// Global low-level keyboard hook that intercepts the lone Win-key tap and
/// fires <see cref="LoneWinTap"/>. Modeled on Open-Shell's mechanism — they
/// install the same hook from <c>StartMenu.exe</c> on Win 11 24H2.
///
/// Strategy: buffer-and-replay. On Win 11 24H2 the system shell's lone-tap
/// detector triggers off the *real* Win-down before Win-up arrives, so
/// passing Win-down through and only swallowing Win-up is too late — Start
/// has already started opening.
///
/// Instead we swallow BOTH the real Win-down and the real Win-up. While Win
/// is held, the OS doesn't know it. If the user then presses another key
/// during the hold (combo), we lazily synthesize a Win-down via SendInput,
/// pass the combo key through, and synthesize a matching Win-up on the real
/// Win-up. If no other key arrives (lone tap), we fire MenYou and the OS
/// never sees any Win event at all — no system Start menu can fire.
///
/// Auto-repeat Win-down events are swallowed without re-injection (we only
/// inject one synthetic down per held session). Injected events carry
/// <c>LLKHF_INJECTED</c> so the hook skips its own synthesized events and
/// doesn't recurse.
[SupportedOSPlatform("windows")]
internal sealed class WinKeyHook : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_SYSKEYUP = 0x0105;
    private const uint VK_LWIN = 0x5B;
    private const uint VK_RWIN = 0x5C;
    private const uint VK_CONTROL = 0x11;
    private const uint LLKHF_INJECTED = 0x10;
    private const uint KEYEVENTF_KEYUP = 0x02;
    private const int WM_QUIT = 0x0012;

    public event Action? LoneWinTap;

    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new();
    private LowLevelKeyboardProc? _proc;
    private IntPtr _hHook;
    private uint _threadId;
    private bool _winHeld;
    private bool _winCompound;
    private bool _winReplayedDown;
    private bool _passingThrough;
    private uint _winHeldVk;
    private bool _disposed;

    public WinKeyHook()
    {
        _thread = new Thread(MessageLoop) { IsBackground = true, Name = "MenYou.WinKeyHook" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _ready.Wait();
        HookTrace.Log("WinKeyHook: installed");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        HookTrace.Log("WinKeyHook: disposing");
        if (_threadId != 0) PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        if (Thread.CurrentThread != _thread) _thread.Join(500);
    }

    private void MessageLoop()
    {
        _threadId = GetCurrentThreadId();
        _proc = HookProc;
        var hMod = GetModuleHandleW(null);
        _hHook = SetWindowsHookExW(WH_KEYBOARD_LL, _proc, hMod, 0);
        _ready.Set();
        if (_hHook == IntPtr.Zero)
        {
            HookTrace.Log($"WinKeyHook: SetWindowsHookEx failed err={Marshal.GetLastWin32Error()}");
            return;
        }

        while (GetMessage(out var msg, IntPtr.Zero, 0, 0))
        {
            TranslateMessage(ref msg);
            DispatchMessageW(ref msg);
        }
        UnhookWindowsHookEx(_hHook);
        _hHook = IntPtr.Zero;
    }

    private IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code < 0) return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);

        var data = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
        var injected = (data.flags & LLKHF_INJECTED) != 0;
        if (injected) return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);

        var msg = (int)wParam;
        var isWin = data.vkCode == VK_LWIN || data.vkCode == VK_RWIN;
        var isDown = msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN;
        var isUp = msg == WM_KEYUP || msg == WM_SYSKEYUP;

        if (isWin && isDown)
        {
            if (!_winHeld)
            {
                // If the system Start menu is already open, the user's Win
                // press is meant to close it — let the OS handle it normally
                // and remember to also let the up event through.
                if (SystemStartMenu.IsOpen())
                {
                    _passingThrough = true;
                    HookTrace.Log($"WinKeyHook: Win-down vk=0x{data.vkCode:X} — system Start is open, passing through");
                    return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
                }

                _winHeld = true;
                _winCompound = false;
                _winReplayedDown = false;
                _winHeldVk = data.vkCode;
                HookTrace.Log($"WinKeyHook: Win-down vk=0x{data.vkCode:X} (swallowed)");
            }
            // Auto-repeat or normal arm: in either case, swallow.
            return new IntPtr(1);
        }

        if (isWin && isUp)
        {
            if (_passingThrough)
            {
                _passingThrough = false;
                HookTrace.Log($"WinKeyHook: Win-up vk=0x{data.vkCode:X} — passing through (matched Win-down)");
                return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
            }

            var lone = _winHeld && !_winCompound;
            var wasHeld = _winHeld;
            var replayed = _winReplayedDown;
            _winHeld = false;
            _winCompound = false;
            _winReplayedDown = false;

            if (lone)
            {
                // OS never saw Win — perfect. Just fire MenYou.
                HookTrace.Log($"WinKeyHook: lone Win-tap vk=0x{data.vkCode:X} → opening MenYou");
                try { LoneWinTap?.Invoke(); } catch { }
                return new IntPtr(1);
            }

            // Compound: we (lazily) replayed Win-down at the first combo
            // keystroke, so the OS thinks Win is held. Inject the matching
            // Win-up so the modifier releases cleanly.
            if (wasHeld && replayed)
            {
                HookTrace.Log($"WinKeyHook: compound Win-up vk=0x{data.vkCode:X} → injecting Win-up");
                InjectWinKey(_winHeldVk, true);
            }
            // Swallow the real Win-up either way (we may have already injected
            // ours; we don't want a double-up).
            return new IntPtr(1);
        }

        if (_winHeld && (isDown || isUp))
        {
            if (!_winCompound)
            {
                _winCompound = true;
                HookTrace.Log($"WinKeyHook: combo detected vk=0x{data.vkCode:X} → replaying Win-down");
                // Lazy replay: synthesize Win-down so the OS sees Win held
                // when this combo key (and any that follow) is dispatched.
                InjectWinKey(_winHeldVk, false);
                _winReplayedDown = true;
            }
        }

        return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    private static void InjectWinKey(uint vk, bool up)
    {
        var inputs = new INPUT[1];
        inputs[0] = new INPUT { type = 1 };
        inputs[0].U.ki = new KEYBDINPUT
        {
            wVk = (ushort)vk,
            dwFlags = up ? KEYEVENTF_KEYUP : 0u
        };
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
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

    private delegate IntPtr LowLevelKeyboardProc(int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetWindowsHookExW(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

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

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);
}
