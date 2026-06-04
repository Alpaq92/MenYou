using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
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
                if (DataContext is StartMenuViewModel vm && !vm.HasLoaded)
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
                    ApplyDwmRoundedCorners();
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

    /// Ask DWM to round the window itself (Win 11 22H2+). The inner Border's
    /// CornerRadius still draws our content rounded; this rounds the actual
    /// window rect so the transparent-window fallback doesn't leak square
    /// corners when AcrylicBlur isn't honored by the compositor.
    private void ApplyDwmRoundedCorners()
    {
        var hwnd = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (hwnd == IntPtr.Zero) return;
        int pref = DWMWCP_ROUND;
        _ = DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int));
    }

    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    public void HideMenu()
    {
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
        // Offset from the taskbar/screen edge so the desktop background
        // shows around the menu — matches how Win 11's Start menu floats
        // rather than sitting flush. Custom themes get no margin: they own
        // their own (often square) chrome — e.g. Windows7Square — and
        // should read as "anchored into the corner" like the classic
        // Start menu, not as a floating card.
        int margin = (DataContext as StartMenuViewModel)?.UseCustomTheme == true ? 0 : 16;
        Position = new PixelPoint(
            work.X + margin,
            work.Y + work.Height - pixelHeight - margin);
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
