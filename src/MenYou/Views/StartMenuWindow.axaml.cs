using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MenYou.Models;
using MenYou.Platform.Windows;
using MenYou.Services;
using MenYou.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace MenYou.Views;

[SupportedOSPlatform("windows")]
public partial class StartMenuWindow : Window
{
    public StartMenuWindow()
    {
        InitializeComponent();
        KeyDown += OnKeyDown;
        Activated += OnActivated;
        Deactivated += OnDeactivated;
        Opened += OnOpened;
    }

    private void OnActivated(object? sender, EventArgs e)
    {
        HookTrace.Log("StartMenuWindow: Activated");
        // Focused → show the slim scrollbar. See the Window.window-unfocused
        // rules in Styles/Scrollbar.axaml: the bar is a constant slim 5 px and
        // visible whenever the menu has focus, and fades out when it doesn't.
        Classes.Set("window-unfocused", false);
    }

    /// Wall-clock instant up to which auto-hide signals
    /// (Deactivated + the App-level ForegroundWatcher) should be
    /// suppressed. Set on every ShowMenu so the deferred
    /// ForceForeground tick has time to land before any spurious
    /// "we lost foreground" event hides us.
    ///
    /// The trigger is the tray-menu Open path: dismissing the native
    /// context menu returns foreground to whatever owned it before
    /// the tray popup (Explorer, browser, …), our window appears for
    /// one frame, loses foreground, OnDeactivated fires, HideMenu
    /// runs — the menu "blinks" and disappears. ForceForeground is
    /// posted at Background priority and doesn't get a chance to
    /// take foreground before the hide path runs.
    private DateTime _settlingUntilUtc = DateTime.MinValue;
    private static readonly TimeSpan SettleWindow = TimeSpan.FromMilliseconds(750);

    /// Cap on how long the reveal waits for cold-start data before showing
    /// anyway — so a hung/slow discovery can never leave the window stuck
    /// invisible. Warm opens resolve EnsureLoadedAsync instantly and never
    /// approach this.
    private static readonly TimeSpan RevealDataTimeout = TimeSpan.FromMilliseconds(2500);

    /// True from ShowMenu until the deferred reveal sets Opacity back to 1.
    /// On a cold start that span can be a second or more (waiting on the
    /// first data load); it must suppress auto-hide for the whole time, or a
    /// stray foreground event hides the menu before it has even appeared.
    private bool _revealing;

    /// True while the auto-hide suppression window is active. Read by
    /// the App's ForegroundWatcher handler so it skips its own
    /// HideMenu call during the same window. Covers both the post-show
    /// settle window and the (possibly long) cold-start reveal wait.
    public bool IsSettling => _revealing || DateTime.UtcNow < _settlingUntilUtc;

    public void ShowMenu()
    {
        HookTrace.Log($"StartMenuWindow: ShowMenu (wasVisible={IsVisible})");
        _revealing = true;
        _settlingUntilUtc = DateTime.UtcNow + SettleWindow;

        // PositionAtTaskbar depends on Bounds.Height, which is only
        // populated after Show() realises the SizeToContent dance — so
        // we have to Show first, then position. The catch: the user
        // would see a one-frame flash at the previous Position before
        // the deferred reposition lands. Workaround: paint the window
        // fully transparent for that frame, do the math, then snap
        // Opacity back up.
        //
        // Priority: these run at DispatcherPriority.Loaded, NOT Background.
        // Background is the lowest non-idle band, so under load (icon
        // streaming, layout churn) the reveal was starved for 115–155 ms
        // — the window sat mapped-but-transparent that whole time and the
        // reposition/resize could leak a visible frame (the "flicker").
        // Loaded still runs *after* the render/layout pass — so Bounds is
        // valid for PositionAtTaskbar, same guarantee Background gave —
        // but it's far higher in the queue, so the reveal lands in ~1
        // frame instead. (Anything above Render would run before layout
        // and reposition against a stale Bounds, re-introducing the
        // off-position bug, so Loaded is the ceiling here.)
        Opacity = 0;
        Show();
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                // Cold start: the post-login warm-up may still be loading when
                // the user clicks. Wait for real content before revealing so
                // the menu never flashes empty — and never reveals-then-
                // auto-hides while the data trickles in. _revealing keeps
                // auto-hide suppressed for this whole wait; the timeout reveals
                // anyway if discovery is unusually slow so the window can't get
                // stuck invisible. Warm opens resolve instantly (data already
                // built), so this adds no delay.
                // Immediate mode (default): reveal now and let the tiles fill
                // in as discovery resolves. Off: wait for the first load so the
                // window never shows an empty frame. Either way a warm cache
                // makes HasLoaded already true, so there's nothing to wait for.
                if (DataContext is StartMenuViewModel vm && !vm.HasLoaded && !vm.ImmediateReveal)
                    await Task.WhenAny(vm.EnsureLoadedAsync(), Task.Delay(RevealDataTimeout));

                // Re-run SizeToContent now that the layout IsVisible bindings
                // have resolved AND the lists are populated. At the first Show
                // the stacked built-in layouts are all briefly visible (their
                // MenuStyle bindings haven't produced a value yet), so the
                // window measures to the WIDEST one — and the width-less
                // Classic layouts blow out to MaxWidth, latching the window at
                // 900 px. Toggling SizeToContent forces a fresh measure against
                // the now-correct visibility (only the active layout), so the
                // window shrinks to fit it.
                SizeToContent = SizeToContent.Manual;
                SizeToContent = SizeToContent.WidthAndHeight;
                Dispatcher.UIThread.Post(() =>
                {
                    // A HideMenu (e.g. toggle-close) during the cold wait above
                    // hides the window; don't resurrect it.
                    if (!IsVisible) { _revealing = false; return; }
                    PositionAtTaskbar();
                    ApplyDwmWindowChrome();
                    ForceForeground();
                    FindFirstSearchBox()?.Focus();
                    Opacity = 1;
                    _revealing = false;
                    // Start the auto-hide settle window from when the menu
                    // ACTUALLY appears, not from ShowMenu entry — on a cold
                    // start the wait above can outlast a fixed-from-entry
                    // window and let a stray foreground event hide the
                    // just-shown menu.
                    _settlingUntilUtc = DateTime.UtcNow + SettleWindow;
                    HookTrace.Log("StartMenuWindow: shown + re-measured + force-foregrounded");
                }, DispatcherPriority.Loaded);
            }
            catch
            {
                // Never leave the window stuck invisible if anything above
                // throws — reveal as a fallback.
                Opacity = 1;
                _revealing = false;
                _settlingUntilUtc = DateTime.UtcNow + SettleWindow;
            }
        }, DispatcherPriority.Loaded);
    }

    /// Give the menu the Win 11 window treatment via DWM (22H2+): rounded
    /// corners and the border color, rounding the actual window rect to match
    /// the inner Border's CornerRadius. The floating drop shadow is NOT done
    /// here — a borderless transparent popup can't receive a native DWM shadow,
    /// so it's an Avalonia BoxShadow on the card (see MenuShadow / the AXAML).
    /// Custom themes opt out (they own their edge, and many are square) — same
    /// contract as the corner / border-thickness converters.
    private void ApplyDwmWindowChrome()
    {
        var hwnd = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (hwnd == IntPtr.Zero) return;

        var vm = DataContext as StartMenuViewModel;
        var custom = vm?.UseCustomTheme == true;

        // Corners: round for every built-in edge style; square for custom.
        int pref = custom ? DWMWCP_DONOTROUND : DWMWCP_ROUND;
        _ = DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int));

        // Leave the DWM system border OFF: DWMWA_BORDER_COLOR=DEFAULT proved too
        // faint to read on the dark menu ("I don't see the border"). The outlined
        // styles draw their own visible MenuBorderBrush hairline (RootBorder,
        // below) instead.
        int border = unchecked((int)DWMWA_COLOR_NONE);
        _ = DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref border, sizeof(int));

        // The drop shadow is NOT a DWM shadow: this window is transparent
        // (WS_EX_NOREDIRECTIONBITMAP) and DWM won't cast a shadow on such a
        // popup — the earlier DwmExtendFrameIntoClientArea margin-trick never
        // actually rendered ("frame, but no shade"). The floating shadow is now
        // an Avalonia BoxShadow on RootBorder (bound to MenuShadow), drawn into
        // the transparent ShadowMargin band. See StartMenuWindow.axaml.

        // Visible 1 px theme hairline only for the OUTLINED styles — Windows11
        // (outline + shadow) and Hairline (outline only). Subtle and None have
        // no outline (outline and shadow are decoupled as of 0.9.12), and
        // custom themes own their edge. Read from the VM's WindowBorder mirror,
        // which SettingsService.Changed refreshes, so a Settings change takes
        // effect on the next open (chrome is re-applied every ShowMenu).
        var style = vm?.WindowBorder ?? WindowBorder.FullShade;
        var hairline = !custom && style is WindowBorder.Windows11 or WindowBorder.Hairline;
        RootBorder.BorderThickness = new Thickness(hairline ? 1 : 0);
    }

    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWA_BORDER_COLOR = 34;
    private const int DWMWCP_ROUND = 2;
    private const int DWMWCP_DONOTROUND = 1;
    private const uint DWMWA_COLOR_DEFAULT = 0xFFFFFFFF;
    private const uint DWMWA_COLOR_NONE = 0xFFFFFFFE;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    public void HideMenu()
    {
        HookTrace.Log("StartMenuWindow: HideMenu");
        // Cancel any in-flight cold-start reveal: clearing _revealing lifts the
        // auto-hide suppression, and the deferred reveal post bails when it
        // sees the window is no longer visible.
        _revealing = false;
        if (DataContext is StartMenuViewModel vm) vm.Search.Clear();
        Hide();
    }

    private async void OnOpened(object? sender, EventArgs e)
    {
        // Skip the data load for the off-screen warm-up Show (see PreRender):
        // App.WarmupStartMenu already kicked off LoadAsync explicitly, and
        // letting this fire it again would run a second, concurrent
        // pinned/recent rebuild. Real opens leave the flag false and load
        // normally, so refresh-on-open is unaffected.
        if (SuppressOpenLoad) return;
        if (DataContext is not StartMenuViewModel vm) return;
        // First open right after warm-up: the data was just loaded and the
        // off-screen PreRender already realized the populated tree, so skip
        // the redundant reload (it would only Clear()+rebuild and re-lay-out).
        // Returns true only once; every later open loads normally.
        if (vm.ConsumeWarmLoad()) return;
        await vm.LoadAsync();
    }

    private bool _preRendered;

    /// True only for the duration of the warm-up <see cref="PreRender"/>
    /// Show/Hide cycle, so <see cref="OnOpened"/> skips its data load.
    public bool SuppressOpenLoad { get; private set; }

    /// One-shot, off-screen render warm-up. The first real <see cref="ShowMenu"/>
    /// otherwise pays the full first-paint cost — visual-tree realization,
    /// render/GPU setup, font load — which the hook trace measured at ~1.1 s
    /// after a cold login. Realizing the window once here (parked far off
    /// every monitor, fully transparent, never activated) moves that cost
    /// into the post-login idle window, so the first Shift+Win / Start-button
    /// open is instant.
    ///
    /// Flash-safe: Opacity stays 0 throughout, ShowActivated is off (it never
    /// steals focus from the user's foreground app), and the window sits at
    /// (-32000,-32000). The deferred Hide runs at Loaded priority — i.e.
    /// after one real render/layout pass — so the realization actually
    /// happens before we hide it again.
    public void PreRender()
    {
        if (_preRendered) return;
        _preRendered = true;

        SuppressOpenLoad = true;
        Opacity = 0;
        ShowActivated = false;
        Position = new PixelPoint(-32000, -32000);
        Show();
        Dispatcher.UIThread.Post(() =>
        {
            HookTrace.Log("PreRender: window realized off-screen (first paint paid; first real open is now instant)");
            Hide();
            // Restore normal activation for real opens (ShowMenu relies on
            // the Show()+ForceForeground path taking foreground).
            ShowActivated = true;
            SuppressOpenLoad = false;
        }, DispatcherPriority.Loaded);
    }

    private void OnDeactivated(object? sender, EventArgs e)
    {
        var settings = App.Services.GetRequiredService<ISettingsService>();
        // During the post-show settle window (see ShowMenu), foreground
        // is still bouncing between the dying tray context menu, our
        // window, and the previous foreground app. Treat Deactivated
        // events as noise during this period; the real auto-hide kicks
        // in as soon as the settle window expires AND the user
        // genuinely clicks away.
        if (IsSettling)
        {
            HookTrace.Log("StartMenuWindow: Deactivated suppressed (settling)");
            return;
        }
        HookTrace.Log($"StartMenuWindow: Deactivated (HideOnFocusLost={settings.Current.HideOnFocusLost})");
        // Not focused → fade the slim scrollbar out (Scrollbar.axaml
        // Window.window-unfocused rules). Gated by the settling early-return
        // above, so a spurious Deactivated during the show settle can't blink
        // the bar. When HideOnFocusLost is on the whole window hides anyway;
        // this is what matters when it's off — the menu stays up but unfocused.
        Classes.Set("window-unfocused", true);
        if (settings.Current.HideOnFocusLost) HideMenu();
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            HideMenu();
            e.Handled = true;
            return;
        }

        if (DataContext is not StartMenuViewModel vm) return;

        // Arrow / page nav while the search panel is up — the user is
        // typing in the TextBox so neither the Results ListBox nor the
        // Recent-files ListBox ever sees these keys naturally. We forward
        // them to SearchViewModel.MoveSelection, which walks Results and
        // updates Selected. Down on an empty selection picks Results[0]
        // so the first arrow press already highlights something.
        if (vm.Search.HasQuery)
        {
            var delta = e.Key switch
            {
                Key.Down       => 1,
                Key.Up         => -1,
                Key.PageDown   => 5,
                Key.PageUp     => -5,
                Key.End        => int.MaxValue / 2,
                Key.Home       => int.MinValue / 2,
                _              => 0
            };
            if (delta != 0)
            {
                if (vm.Search.MoveSelection(delta)) e.Handled = true;
                return;
            }
        }

        if (e.Key == Key.Enter && vm.Search.HasQuery)
        {
            vm.Search.LaunchSelectedCommand.Execute(null);
            HideMenu();
            e.Handled = true;
        }
    }

    private TextBox? FindFirstSearchBox()
    {
        foreach (var box in this.GetVisualDescendants().OfType<TextBox>())
        {
            if (box.Classes.Contains("search") && box.IsVisible) return box;
        }
        return null;
    }

    private void PositionAtTaskbar()
    {
        var screen = Screens.ScreenFromVisual(this) ?? Screens.Primary;
        if (screen is null) return;
        var work = screen.WorkingArea;
        var scale = DesktopScaling;
        var pixelHeight = (int)(Bounds.Height * scale);
        var vm = DataContext as StartMenuViewModel;
        // Offset the CARD from the taskbar/screen corner so the desktop shows
        // around the menu — matches how Win 11's Start floats rather than
        // sitting flush. Custom themes get no gap: they own their own (often
        // square) chrome — e.g. Windows7Square — and read as "anchored into the
        // corner" like the classic Start menu.
        int gap = vm?.UseCustomTheme == true ? 0 : 16;
        // Bounds now includes the transparent ShadowMargin band the drop shadow
        // renders into, so the window is larger than the visible card by
        // shadowPx per edge. Shift the window out by the band so the CARD keeps
        // its corner gap and the band just spills past the corner (transparent —
        // only the blur shows). Hairline / None / custom have shadowPx = 0 and
        // reduce to the plain corner-gap placement, unchanged from before.
        int shadowPx = (int)((vm?.ShadowMarginDip ?? 0) * scale);
        Position = new PixelPoint(
            work.X + gap - shadowPx,
            work.Y + work.Height - pixelHeight - gap + shadowPx);
    }

    /// Win 11 focus-stealing prevention means a plain Show/Activate may leave
    /// our window visible but inactive — and then Deactivated never fires
    /// because the window never had foreground in the first place. Delegates
    /// to the shared <see cref="Win32Foreground"/> helper (the AttachThreadInput
    /// dance), which the Settings window reuses too.
    private void ForceForeground()
    {
        var ourHwnd = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (ourHwnd == IntPtr.Zero) return;
        Win32Foreground.Bring(ourHwnd);
        HookTrace.Log($"ForceForeground: requested foreground for 0x{ourHwnd.ToInt64():X}");
    }
}
