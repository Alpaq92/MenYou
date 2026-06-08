using System.Runtime.Versioning;
using MenYou.Models;
using MenYou.Platform.Windows;

namespace MenYou.Services;

[SupportedOSPlatform("windows")]
public sealed class Win32HotkeyService : IHotkeyService
{
    private const int FallbackHotkeyId = 0xBEE1;
    private const uint VK_F12 = 0x7B;

    private readonly HotkeyWindow _hotkey = new();
    private StartClickHook? _startClick;
    private WinKeyHook? _winKey;
    private BridgeInjector? _bridge;
    private Action? _callback;
    private bool _fallbackRegistered;

    public Win32HotkeyService()
    {
        _hotkey.Pressed += id =>
        {
            if (id == FallbackHotkeyId) _callback?.Invoke();
        };
    }

    public void Initialize(Action onPressed) => _callback = onPressed;

    public void ApplyBindings(UserSettings settings)
    {
        // Behavior parity with Open-Shell on Win 11 24H2:
        //   - StartClickHook (WH_MOUSE_LL) catches the taskbar Start-button
        //     click and opens MenYou.
        //   - WinKeyHook (WH_KEYBOARD_LL) catches the lone Win-key tap. We
        //     inject a Ctrl-down/up immediately after Win-down so the system
        //     shell's lone-tap detector sees a compound press and never
        //     opens the system Start menu; on the real Win-up our hook
        //     fires MenYou.
        //   - BridgeInjector maps MenYou.Bridge.dll into Explorer's UI thread
        //     and installs a WH_GETMESSAGE hook there. It catches the lone
        //     Win-tap as it surfaces on Win 11 24H2 — a plain WM_KEYUP on
        //     Explorer's input window (mirroring Open-Shell) — neuters it so
        //     the system Start menu never opens, and posts WM_COPYDATA back to
        //     our CopyDataListener to open MenYou: an in-process counterpart to
        //     the out-of-process WinKeyHook above. (The DLL is shadow-copied out
        //     of the install dir first, so an in-place upgrade can't get stuck
        //     on Explorer holding it — see BridgeInjector.ResolveBridgePath.)
        //   - Win+F12 stays as a deterministic fallback when ReplaceWinKey
        //     is off (or when the LL hook can't be installed, e.g. on
        //     locked-down machines).
        if (settings.ReplaceWinKey)
        {
            EnsureFallback(false);
            EnsureStartClick(true);
            EnsureWinKey(true);
            EnsureBridge(true);
        }
        else
        {
            EnsureStartClick(false);
            EnsureWinKey(false);
            EnsureBridge(false);
            EnsureFallback(true);
        }
    }

    public void Unregister()
    {
        EnsureFallback(false);
        EnsureStartClick(false);
        EnsureWinKey(false);
        EnsureBridge(false);
    }

    public void Dispose()
    {
        Unregister();
        _hotkey.Dispose();
    }

    private void EnsureFallback(bool wanted)
    {
        if (wanted == _fallbackRegistered) return;
        if (wanted)
            _hotkey.Register(FallbackHotkeyId,
                NativeMethods.MOD_WIN | NativeMethods.MOD_NOREPEAT, VK_F12);
        else
            _hotkey.Unregister(FallbackHotkeyId);
        _fallbackRegistered = wanted;
    }

    private void EnsureStartClick(bool wanted)
    {
        if (wanted == (_startClick != null)) return;
        if (wanted)
        {
            _startClick = new StartClickHook();
            _startClick.StartClicked += () => _callback?.Invoke();
        }
        else
        {
            _startClick?.Dispose();
            _startClick = null;
        }
    }

    private void EnsureWinKey(bool wanted)
    {
        if (wanted == (_winKey != null)) return;
        if (wanted)
        {
            _winKey = new WinKeyHook();
            _winKey.LoneWinTap += () => _callback?.Invoke();
        }
        else
        {
            _winKey?.Dispose();
            _winKey = null;
        }
    }

    private void EnsureBridge(bool wanted)
    {
        if (wanted == (_bridge != null)) return;
        if (wanted)
        {
            _bridge = new BridgeInjector();
            if (!_bridge.Inject())
            {
                _bridge.Dispose();
                _bridge = null;
            }
        }
        else
        {
            _bridge?.Dispose();
            _bridge = null;
        }
    }
}
