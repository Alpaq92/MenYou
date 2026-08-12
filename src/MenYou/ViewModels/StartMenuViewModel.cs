using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MenYou.Models;
using MenYou.Services;
using MenYou.ViewModels.Items;

namespace MenYou.ViewModels;

public sealed partial class StartMenuViewModel : ViewModelBase
{
    private readonly IAppDiscoveryService _discovery;
    private readonly IRecentItemsService _recent;
    private readonly IShellLauncher _launcher;
    private readonly IIconService _icons;
    private readonly ISettingsService _settings;
    private readonly IPinService _pin;
    private readonly IUserAvatarService _avatarService;

    public ProgramsViewModel Programs { get; }
    public SearchViewModel Search { get; }
    public PowerMenuViewModel Power { get; }
    public RightPanelViewModel RightPanel { get; }

    [ObservableProperty] private string _userName = Environment.UserName;
    [ObservableProperty] private Bitmap? _avatar;
    /// True only when <see cref="Avatar"/> is the generic Windows
    /// silhouette. The dark-mode invert/brighten pipeline is intended for
    /// the silhouette (a dark figure on a light background that would
    /// otherwise look like a white blob on dark UI); real user photos
    /// should render as-is.
    [ObservableProperty] private bool _isDefaultAvatar;
    [ObservableProperty] private MenuStyle _menuStyle;
    /// Mirrors <see cref="UserSettings.UseCustomTheme"/>. When true the
    /// StartMenuWindow swaps the built-in Win7/Classic layouts for the
    /// parsed <see cref="CustomThemeXaml"/>. Re-published on each
    /// SettingsService.Changed so the menu re-renders the moment the
    /// user clicks Apply in Settings.
    [ObservableProperty] private bool _useCustomTheme;
    /// Mirrors <see cref="UserSettings.CustomThemeXaml"/>. Fed through
    /// XamlStringToControlConverter on the view side.
    [ObservableProperty] private string _customThemeXaml = "";

    /// Mirrors <see cref="UserSettings.WindowBorder"/>. Drives the menu card's
    /// drop shadow (<see cref="MenuShadow"/> / <see cref="ShadowMargin"/>),
    /// which the transparent popup renders itself since it can't get a native
    /// DWM shadow. Re-published on SettingsService.Changed so switching the
    /// option in Settings takes effect on the next open.
    [ObservableProperty] private WindowBorder _windowBorder;

    // Soft (Win 11-like) and lighter drop shadows, drawn as a BoxShadow on the
    // menu card. Kept next to their margins (ShadowMarginDip) so the two never
    // drift: the margin must exceed the shadow's reach or the window (which is
    // SizeToContent) clips the tail.
    private static readonly BoxShadows ShadowSoft   = BoxShadows.Parse("0 2 8 0 #40000000, 0 10 28 0 #73000000");
    // Zero offsets on purpose: FullShade halos the card evenly on all four
    // sides (the Windows11/Subtle pairs are offset downward). The tight first
    // layer doubles as the edge definition, since FullShade draws no hairline.
    // Deepened twice from the original "0 0 10 0 #4D000000, 0 0 28 3 #66000000":
    // as the DEFAULT border it read too close to Subtle. Alphas are now 50%
    // (inner) and 65% (outer), up from 30%/40%, with roughly half again the
    // blur. Extent is blur 46 + spread 5 = 51, which is why FullShade's
    // ShadowMarginDip below is 56 rather than the 40 the other styles use — the
    // window is SizeToContent, so a shadow reaching past its band gets clipped.
    // Headroom for going further: the widest layout is Win7 at 800 DIP and the
    // window MaxWidth is 980, so the band cannot exceed 90.
    private static readonly BoxShadows ShadowFull   = BoxShadows.Parse("0 0 16 0 #80000000, 0 0 46 5 #A6000000");
    private static readonly BoxShadows ShadowSubtle = BoxShadows.Parse("0 1 4 0 #33000000, 0 6 16 0 #52000000");

    /// The drop shadow for the current <see cref="WindowBorder"/> — empty only
    /// for Hairline / None. Bound to RootBorder.BoxShadow; drawn by Skia into
    /// the transparent margin, so it works on the transparent popup where a DWM
    /// shadow can't.
    ///
    /// Custom themes USED to be excluded here, on the same "a theme owns its
    /// edge" principle that squares their corners. That was wrong: a theme can
    /// draw its own border and background, but it cannot draw a shadow OUTSIDE
    /// the window, because the margin the shadow needs is the window's to give.
    /// Excluding them just meant custom themes had no shadow at all and sat
    /// flat against the desktop while every built-in layout floated.
    public BoxShadows MenuShadow => WindowBorder switch
    {
        WindowBorder.Windows11 => ShadowSoft,
        WindowBorder.FullShade => ShadowFull,
        WindowBorder.Subtle    => ShadowSubtle,
        _                      => default,
    };

    /// Transparent margin (DIP) around the card that the shadow renders into;
    /// must exceed the shadow's reach. PositionAtTaskbar reads this to keep the
    /// card anchored once the window grows by the band. Applies to custom
    /// themes too — see <see cref="MenuShadow"/> for why they are no longer
    /// excluded.
    public double ShadowMarginDip => WindowBorder switch
    {
        // FullShade gets a wider band than the rest: its extent is blur 46 +
        // spread 5 = 51, so 40 would clip the tail. 56 clears it with room to
        // spare and still fits the window MaxWidth of 980 for every layout
        // (widest is Win7 at 800 -> 800 + 2*56 = 912).
        WindowBorder.Windows11 => 40,
        WindowBorder.FullShade => 56,
        WindowBorder.Subtle    => 22,
        _                      => 0,
    };

    /// <see cref="ShadowMarginDip"/> as a uniform Thickness for RootBorder.Margin.
    public Thickness ShadowMargin => new(ShadowMarginDip);

    // The window's minimum size. 400x500 is sized for the BUILT-IN layouts
    // INCLUDING their shadow margin — Classic1 is the narrowest at 320 DIP and
    // 320 + 2x40 lands exactly on 400. A custom theme declares its own size and
    // gets no margin (ShadowMarginDip is 0 for them), so those same clamps
    // letterbox anything smaller: the window is held at 400 while the theme
    // draws 320, and RootBorder's MenuBackground fills the 80 DIP difference as
    // a band down both sides. Very visible on a Classic1-sized theme, where the
    // band is a quarter of the width. Custom themes therefore get no floor and
    // the window hugs whatever the theme declares.
    public double MenuMinWidth => UseCustomTheme ? 0 : 400;
    public double MenuMinHeight => UseCustomTheme ? 0 : 500;

    /// True when the menu should paint its own chrome background — i.e. always
    /// EXCEPT under a custom theme, which supplies its own. RootBorder's opaque
    /// square fill behind a theme that rounds itself showed as dark square
    /// corners around the curve, so the theme read as square when it wasn't.
    public bool PaintChromeBackground => !UseCustomTheme;

    partial void OnWindowBorderChanged(WindowBorder value) => RaiseShadow();
    partial void OnUseCustomThemeChanged(bool value) => RaiseShadow();
    private void RaiseShadow()
    {
        OnPropertyChanged(nameof(MenuShadow));
        OnPropertyChanged(nameof(ShadowMarginDip));
        OnPropertyChanged(nameof(ShadowMargin));
        // Toggling a custom theme changes the size floor too, not just the edge.
        OnPropertyChanged(nameof(MenuMinWidth));
        OnPropertyChanged(nameof(MenuMinHeight));
    }

    /// True while a subtle "Updating apps…" caption should show beside the All
    /// Programs header — the gated display of a background discovery catch-up
    /// scan (see <see cref="OnBackgroundRefreshingChanged"/>). Every layout binds
    /// it. Stays false on a fast/silent refresh so it never flashes.
    [ObservableProperty] private bool _isRefreshing;

    /// Set by the host (App) so the menu can punt to the settings dialog
    /// without taking a view dependency.
    public Action? OpenSettingsRequested { get; set; }

    public ObservableCollection<AppItemViewModel> Pinned { get; } = new();
    public ObservableCollection<AppItemViewModel> Recent { get; } = new();

    /// Curated subset of <see cref="RightPanelViewModel.Shortcuts"/> —
    /// Start Menu, Documents, Settings, Control Panel — surfaced as the
    /// "Places" group custom-theme samples use (notably MintCinnamon).
    /// Filtered + ordered here so XAML themes can bind a four-entry
    /// list without reproducing the action-string filter logic. The
    /// full Shortcuts collection (10 entries including Pictures, Music,
    /// Downloads, This PC, Network, Run...) stays available for themes
    /// that want everything.
    public IEnumerable<RightPanelViewModel.ShellShortcut> Places
    {
        get
        {
            // Walk the source in declaration order to keep Title look-up
            // O(n) but predictable; the Shortcuts list is tiny (≤ 10).
            var wanted = new[] { "startmenu", "documents", "settings", "control" };
            foreach (var action in wanted)
            {
                foreach (var s in RightPanel.Shortcuts)
                {
                    if (s.Action == action) { yield return s; break; }
                }
            }
        }
    }

    public StartMenuViewModel(
        IAppDiscoveryService discovery,
        IRecentItemsService recent,
        IShellLauncher launcher,
        IIconService icons,
        ISettingsService settings,
        IPinService pin,
        IUserAvatarService avatarService,
        ProgramsViewModel programs,
        SearchViewModel search,
        PowerMenuViewModel power,
        RightPanelViewModel rightPanel)
    {
        // Avatar lookup hits the registry and decodes a JPEG/PNG (the
        // high-res account picture can be 448–1080 px). Doing it here would
        // block VM construction — which happens on the UI thread during the
        // post-login warm-up — so it's deferred to LoadAvatarAsync and the
        // Avatar/IsDefaultAvatar properties light up a beat later. Same
        // "show first, paint later" policy the rest of the menu follows.
        _avatarService = avatarService;
        _discovery = discovery;
        _recent = recent;
        _launcher = launcher;
        _icons = icons;
        _settings = settings;
        _pin = pin;
        Programs = programs;
        Search = search;
        Power = power;
        RightPanel = rightPanel;
        MenuStyle = settings.Current.MenuStyle;
        UseCustomTheme = settings.Current.UseCustomTheme;
        CustomThemeXaml = settings.Current.CustomThemeXaml;
        WindowBorder = settings.Current.WindowBorder;
        ImmediateReveal = settings.Current.ImmediateMenuReveal;
        settings.Changed += () =>
        {
            MenuStyle = settings.Current.MenuStyle;
            UseCustomTheme = settings.Current.UseCustomTheme;
            CustomThemeXaml = settings.Current.CustomThemeXaml;
            WindowBorder = settings.Current.WindowBorder;
            ImmediateReveal = settings.Current.ImmediateMenuReveal;
            // Re-cap the live Pinned / Recent lists so a changed "Max recent
            // items" (or pin set) takes effect immediately, not only after the
            // next launch or restart. Both rebuilds are diff-aware, so an
            // unrelated settings change (theme, etc.) leaves the tiles — and
            // their already-loaded icons — untouched.
            Dispatcher.UIThread.Post(() =>
            {
                RebuildPinned();
                RebuildRecent();
                _ = LoadIconsAsync();
            });
        };
        // When the discovery cache's background backstop swaps in a fresher
        // app list, rebuild the surfaces (single-flight + diff-aware, so it's
        // cheap and only the genuinely-changed tiles move).
        discovery.Refreshed += () =>
            Dispatcher.UIThread.Post(() => _ = LoadAsync());
        // Surface a subtle "Updating apps…" caption while a stale-painted or
        // just-changed list is being revalidated in the background. Gated so it
        // never flashes on a fast refresh (see OnBackgroundRefreshingChanged).
        discovery.RefreshingChanged += refreshing =>
            Dispatcher.UIThread.Post(() => OnBackgroundRefreshingChanged(refreshing));
        // The avatar bitmap is the same instance across theme flips, but the
        // dark-mode invert converter needs the binding to re-run when the
        // active theme changes. Re-emit the Avatar property so the converter
        // is asked again.
        if (Avalonia.Application.Current is { } app)
            app.ActualThemeVariantChanged += (_, _) =>
                Avalonia.Threading.Dispatcher.UIThread.Post(() => OnPropertyChanged(nameof(Avatar)));
        pin.Changed += () => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            RebuildPinned();
            _ = LoadIconsAsync();
        });
        recent.Changed += () => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            RebuildRecent();
            _ = LoadIconsAsync();
        });
        // The Mint Cinnamon layout binds the computed Places slice, which —
        // being a plain getter over RightPanel.Shortcuts — can't raise its
        // own change notification. RightPanel streams its shell icons in
        // after construction; when it's done, re-publish Places so those
        // rows repaint with real icons. (The Win11 flyout binds Shortcuts
        // directly and refreshes off the collection's own notifications.)
        RightPanel.IconsLoaded += () =>
            Dispatcher.UIThread.Post(() => OnPropertyChanged(nameof(Places)));

        _ = LoadAvatarAsync();
    }

    // --- "Updating apps…" caption gating ------------------------------------
    // _bgRefreshing is the raw service signal; IsRefreshing is the gated display
    // state. Show only if a refresh runs past RefreshShowDelayMs (no sub-400 ms
    // flash), then hold at least RefreshMinVisibleMs (no flicker). Runs on the
    // UI thread via the posted handler + DispatcherTimer.RunOnce callbacks.
    private bool _bgRefreshing;
    private DateTime _refreshShownAtUtc;
    private const int RefreshShowDelayMs = 400;
    private const int RefreshMinVisibleMs = 500;

    private void OnBackgroundRefreshingChanged(bool refreshing)
    {
        _bgRefreshing = refreshing;
        if (refreshing)
        {
            if (IsRefreshing) return; // already shown
            DispatcherTimer.RunOnce(() =>
            {
                if (_bgRefreshing && !IsRefreshing)
                {
                    IsRefreshing = true;
                    _refreshShownAtUtc = DateTime.UtcNow;
                }
            }, TimeSpan.FromMilliseconds(RefreshShowDelayMs));
        }
        else
        {
            if (!IsRefreshing) return; // never crossed the gate — nothing to hide
            var remaining = TimeSpan.FromMilliseconds(RefreshMinVisibleMs)
                            - (DateTime.UtcNow - _refreshShownAtUtc);
            if (remaining <= TimeSpan.Zero)
                IsRefreshing = false;
            else
                DispatcherTimer.RunOnce(
                    () => { if (!_bgRefreshing) IsRefreshing = false; }, remaining);
        }
    }

    /// Loads the user's account picture off the UI thread (registry probe +
    /// image decode) and lights up the bound properties when ready, so VM
    /// construction during the post-login warm-up isn't blocked on it.
    private async Task LoadAvatarAsync()
    {
        var result = await Task.Run(_avatarService.LoadAvatar);
        Avatar = result.Bitmap;
        IsDefaultAvatar = result.IsDefault;
    }

    private bool _warmLoaded;

    /// Called by App.WarmupStartMenu once the post-login warm-up
    /// <see cref="LoadAsync"/> has completed (and right before the off-screen
    /// PreRender realizes the now-populated tree). Lets the first real open
    /// skip a redundant reload.
    public void MarkWarmLoaded() => _warmLoaded = true;

    /// Returns true exactly once — when the first menu open directly follows a
    /// completed warm-up load. <see cref="Views.StartMenuWindow.OnOpened"/>
    /// uses it to skip that single redundant <see cref="LoadAsync"/>: the data
    /// is microseconds old and PreRender has already realized the populated
    /// visual tree, so re-running it would only tear the lists down and
    /// re-lay-them-out (the ~600 ms first-open cost the trace showed). Every
    /// later open returns false and loads normally, so refresh-on-open and the
    /// newly-installed rescan are unaffected.
    public bool ConsumeWarmLoad()
    {
        if (!_warmLoaded) return false;
        _warmLoaded = false;
        return true;
    }

    private Task? _activeLoad;
    private bool _hasLoaded;

    /// True once the menu's data (pinned / recent / programs) has been built
    /// at least once this session.
    public bool HasLoaded => _hasLoaded;

    /// Mirrors <see cref="UserSettings.ImmediateMenuReveal"/>. Read by
    /// <see cref="Views.StartMenuWindow.ShowMenu"/> to decide whether to reveal
    /// the window instantly (and fill tiles as discovery resolves) or wait for
    /// the first load to finish before showing.
    public bool ImmediateReveal { get; private set; }

    /// Builds (or refreshes) the menu's pinned / recent / programs surfaces.
    /// Called from <see cref="Views.StartMenuWindow.OnOpened"/> and the
    /// warm-up. SINGLE-FLIGHT: concurrent callers — classically the post-login
    /// warm-up and a first user open that beats it on a cold start — share one
    /// in-flight load instead of racing two rebuilds over the same
    /// ObservableCollections. The field resets when the load finishes, so
    /// later opens still refresh.
    public Task LoadAsync() => _activeLoad ??= RunLoadAsync();

    /// Awaited by the show path before it reveals the window, so a cold first
    /// open never flashes an empty menu. Resolves instantly once the data
    /// exists; otherwise piggybacks the in-flight (or a fresh) load.
    public Task EnsureLoadedAsync() => _hasLoaded ? Task.CompletedTask : LoadAsync();

    private async Task RunLoadAsync()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await Programs.LoadAsync();
            var progMs = sw.ElapsedMilliseconds;
            // EnsureSeededAsync also runs at app startup (see App.SeedPinnedAsync);
            // calling here again is a cheap idempotent no-op once seeded, but
            // it covers the edge case where the user opens the menu before
            // startup seeding finishes.
            await _pin.EnsureSeededAsync(_discovery);
            await ComputeNewlyInstalledAsync();
            var scanMs = sw.ElapsedMilliseconds;
            RebuildPinned();
            RebuildRecent();
            Programs.MarkNew(_newlyInstalledIds);
            _hasLoaded = true;
            _ = LoadIconsAsync();
            Platform.Windows.HookTrace.Log(
                $"RunLoadAsync: programs={progMs}ms +scan={scanMs}ms +rebuild={sw.ElapsedMilliseconds}ms");
        }
        finally
        {
            // Clear so the next open starts a fresh refresh; in-flight awaiters
            // already hold their reference to this task.
            _activeLoad = null;
        }
    }

    /// Marks any app whose <c>.lnk</c> shortcut was created within the
    /// last <see cref="FreshInstallWindow"/> as newly installed. Using the
    /// shortcut's creation time rather than diffing against a persisted
    /// SeenAppIds set is robust to repeat menu opens (each open would
    /// otherwise consume the diff and clear the flag) and naturally
    /// fades after a few days without persistent bookkeeping. Apps
    /// without a <c>SourceLnkPath</c> (UWP, ad-hoc paths) are skipped.
    private static readonly TimeSpan FreshInstallWindow = TimeSpan.FromDays(3);

    private async Task ComputeNewlyInstalledAsync()
    {
        var apps = await _discovery.GetAllAppsAsync();
        var cutoff = DateTime.UtcNow - FreshInstallWindow;
        var fresh = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var app in apps)
        {
            if (string.IsNullOrEmpty(app.SourceLnkPath)) continue;
            try
            {
                var when = System.IO.File.GetCreationTimeUtc(app.SourceLnkPath);
                if (when > cutoff) fresh.Add(app.Id);
            }
            catch { }
        }
        _newlyInstalledIds = fresh;
    }

    [RelayCommand]
    private void OpenSettings() => OpenSettingsRequested?.Invoke();

    /// Opens the Windows "Your info" account settings page — the same
    /// destination the system Start menu navigates to when the user
    /// clicks their avatar / username. ms-settings: URIs are resolved
    /// by Explorer's URI handler, so a plain Process.Start with
    /// UseShellExecute=true (the launcher's default) is enough. The
    /// shared Launched event then hides MenYou the moment the URI is
    /// dispatched — same path every other launch goes through.
    [RelayCommand]
    private void OpenAccountSettings() => _launcher.Launch("ms-settings:yourinfo");

    /// Phone-strip "Phone Link" action. Launches Microsoft's Phone Link /
    /// "Your Phone" companion app (AUMID
    /// Microsoft.YourPhone_8wekyb3d8bbwe!App) when it's installed,
    /// routing through Explorer's shell:AppsFolder handler the same way
    /// packaged apps launch elsewhere. On machines where Phone Link has
    /// been removed (it's not present on every SKU / Windows build), it
    /// falls back to the Settings → Mobile devices pairing page so the
    /// button always lands somewhere useful.
    [RelayCommand]
    private void OpenPhoneLink()
    {
        const string aumid = "Microsoft.YourPhone_8wekyb3d8bbwe!App";
        if (Platform.Windows.IconExtractor.AppExists(aumid))
            _launcher.Launch("explorer.exe", $"shell:AppsFolder\\{aumid}");
        else
            _launcher.Launch("ms-settings:mobile-devices");
    }

    /// AppIds that weren't in <see cref="UserSettings.SeenAppIds"/> at the
    /// start of this session — i.e. apps that the user installed (or that
    /// Windows surfaced) since the last MenYou launch. Drives the "new
    /// install" accent flash anywhere these items appear (Pinned, Recent,
    /// All Programs tree). Empty on first run so existing apps don't all
    /// flash as new.
    private HashSet<string> _newlyInstalledIds = new(StringComparer.OrdinalIgnoreCase);

    private void RebuildPinned()
    {
        var entries = _settings.Current.Pinned
            .OrderBy(p => p.Order)
            .Select(p => _discovery.FindById(p.AppId))
            .Where(e => e is not null)
            .Select(e => e!)
            .ToList();
        RebuildList(Pinned, entries);
    }

    private void RebuildRecent()
    {
        // Join-then-cap: resolve ids against discovery FIRST, then take the
        // cap. Capping first (the service used to Take before we joined)
        // meant a few unresolvable ids at the top — uninstalled apps, or a
        // degraded scan missing its packaged entries — rendered the whole
        // section blank despite resolvable launches sitting right below
        // the cap.
        var entries = _recent.Recent
            .Select(r => _discovery.FindById(r.AppId))
            .Where(e => e is not null)
            .Select(e => e!)
            .Take(_settings.Current.MaxRecentItems)
            .ToList();
        RebuildList(Recent, entries);
    }

    /// Reconciles an item collection against the desired entry list, but
    /// ONLY rebuilds when the contents actually changed. Tearing the
    /// collection down and recreating fresh AppItemViewModels on every
    /// open (the old behaviour) reset each tile's Icon to null, so the
    /// faster reveal path now exposed a cog → real-icon flash on every
    /// open. When the app set + order is unchanged we keep the existing
    /// view-models — they already hold their loaded bitmaps — so repeat
    /// opens paint icons immediately. A genuine change (pin added,
    /// reordered, recent updated) still rebuilds; only the changed tiles
    /// stream an icon.
    private void RebuildList(ObservableCollection<AppItemViewModel> target, IReadOnlyList<AppEntry> entries)
    {
        if (SameEntries(target, entries))
        {
            // Contents match — just refresh the newly-installed accent in
            // place (cheap, no icon reset) in case the 3-day window moved.
            for (var i = 0; i < target.Count; i++)
                target[i].IsNew = _newlyInstalledIds.Contains(target[i].Entry.Id);
            return;
        }

        target.Clear();
        foreach (var entry in entries)
        {
            var vm = new AppItemViewModel(entry, _launcher, _pin);
            if (_newlyInstalledIds.Contains(entry.Id)) vm.IsNew = true;
            target.Add(vm);
        }
    }

    private static bool SameEntries(ObservableCollection<AppItemViewModel> current, IReadOnlyList<AppEntry> desired)
    {
        if (current.Count != desired.Count) return false;
        for (var i = 0; i < current.Count; i++)
            if (!string.Equals(current[i].Entry.Id, desired[i].Id, StringComparison.OrdinalIgnoreCase))
                return false;
        return true;
    }

    /// Snapshots the pinned/recent tiles ON the UI thread, then fans icon
    /// extraction across cores; each icon lands via its own posted UI
    /// update. The old loop awaited one GetIconAsync + one UI invoke per
    /// tile, so icons filled strictly serially — and Task.Run(LoadIconsAsync)
    /// enumerated the live observable collections off-thread, racing
    /// rebuilds. Must be called on the UI thread (all call sites are).
    private Task LoadIconsAsync()
    {
        // Loud, not comment-only (see ProgramsViewModel.LoadIconsAsync).
        Dispatcher.UIThread.VerifyAccess();
        var items = Pinned.Concat(Recent).ToList();
        return _icons.LoadIconsAsync(items, i => i.Entry,
            (item, bmp) => Dispatcher.UIThread.Post(() => item.Icon = bmp));
    }
}
