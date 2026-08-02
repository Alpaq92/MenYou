using System.Runtime.Versioning;
using MenYou.Models;
using MenYou.Platform.Windows;

namespace MenYou.Services;

[SupportedOSPlatform("windows")]
public sealed class Win32HotkeyService : IHotkeyService
{
    private const int FallbackHotkeyId = 0xBEE1;
    private const uint VK_F12 = 0x7B;

    /// How long a press captured before the UI existed stays worth honouring.
    /// Comfortably covers a slow cold boot (measured ~7 s from process start to
    /// the first line of our code) while still being short enough that a press
    /// the user has long since given up on can't pop a menu out of nowhere.
    private static readonly TimeSpan QueuedPressTtl = TimeSpan.FromSeconds(12);

    private readonly HotkeyWindow _hotkey = new();
    private StartClickHook? _startClick;
    private WinKeyHook? _winKey;
    private BridgeInjector? _bridge;
    private volatile Action? _callback;
    private bool _fallbackRegistered;

    /// UTC ticks of a press that arrived with no callback wired yet, or 0 for
    /// none. Written from the hook threads and read from the UI thread, hence
    /// Interlocked rather than a plain DateTime.
    private long _queuedPressTicks;

    public Win32HotkeyService()
    {
        _hotkey.Pressed += id =>
        {
            if (id == FallbackHotkeyId) OnPressed();
        };
    }

    /// Every hook funnels through here.
    ///
    /// EarlyStartup installs the hooks before Avalonia loads, so presses can
    /// arrive seconds before there is any UI to show. Those are QUEUED, not
    /// passed through to Windows: MenYou is a Start-menu REPLACEMENT, so
    /// letting the system Start menu open during the boot window would surface
    /// the wrong menu (and then possibly MenYou on top of it), while dropping
    /// the press would surface nothing at all. Queueing keeps the gesture and
    /// its result matched up — the menu is just late.
    ///
    /// Coalesced to a single pending press, keeping the LATEST one: mashing
    /// Start while the machine boots should open the menu once, and dating the
    /// queue from the last press is what keeps the TTL check meaningful.
    private void OnPressed()
    {
        var callback = _callback;
        if (callback is not null)
        {
            callback();
            return;
        }

        if (Interlocked.Exchange(ref _queuedPressTicks, DateTime.UtcNow.Ticks) == 0)
            HookTrace.Log("Hotkey: press arrived before the UI was ready -> queued");
    }

    /// Outside trigger — the injected bridge's WM_COPYDATA notifications
    /// (CopyDataListener) come in this way so they share the hooks' callback
    /// and pre-UI queue instead of needing a live UI thread of their own.
    public void Trigger() => OnPressed();

    public void Initialize(Action onPressed)
    {
        // Wire the callback FIRST, then drain: a press landing in between is
        // then serviced directly instead of falling into a queue nobody reads.
        _callback = onPressed;

        var ticks = Interlocked.Exchange(ref _queuedPressTicks, 0);
        if (ticks == 0) return;

        var age = DateTime.UtcNow - new DateTime(ticks, DateTimeKind.Utc);
        if (age > QueuedPressTtl)
        {
            HookTrace.Log($"Hotkey: queued press dropped ({age.TotalSeconds:F1}s stale)");
            return;
        }

        HookTrace.Log($"Hotkey: replaying queued press ({age.TotalSeconds:F1}s old)");
        onPressed();
    }

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
            _startClick.StartClicked += OnPressed;
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
            _winKey.LoneWinTap += OnPressed;
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
