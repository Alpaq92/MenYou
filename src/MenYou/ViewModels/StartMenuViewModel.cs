using System.Collections.ObjectModel;
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
        ImmediateReveal = settings.Current.ImmediateMenuReveal;
        settings.Changed += () =>
        {
            MenuStyle = settings.Current.MenuStyle;
            UseCustomTheme = settings.Current.UseCustomTheme;
            CustomThemeXaml = settings.Current.CustomThemeXaml;
            ImmediateReveal = settings.Current.ImmediateMenuReveal;
        };
        // When the discovery cache's background backstop swaps in a fresher
        // app list, rebuild the surfaces (single-flight + diff-aware, so it's
        // cheap and only the genuinely-changed tiles move).
        discovery.Refreshed += () =>
            Dispatcher.UIThread.Post(() => _ = LoadAsync());
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
            _ = Task.Run(LoadIconsAsync);
        });
        recent.Changed += () => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            RebuildRecent();
            _ = Task.Run(LoadIconsAsync);
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
            _ = Task.Run(LoadIconsAsync);
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
        var entries = _recent.Recent
            .Select(r => _discovery.FindById(r.AppId))
            .Where(e => e is not null)
            .Select(e => e!)
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

    private async Task LoadIconsAsync()
    {
        foreach (var item in Pinned.Concat(Recent))
        {
            var bmp = await _icons.GetIconAsync(item.Entry);
            if (bmp is not null)
                await Dispatcher.UIThread.InvokeAsync(() => item.Icon = bmp);
        }
    }
}
