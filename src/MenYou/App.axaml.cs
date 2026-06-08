using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Avalonia.Threading;
using MenYou.Models;
using MenYou.Platform.Windows;
using MenYou.Services;
using MenYou.ViewModels;
using MenYou.Views;
using Microsoft.Extensions.DependencyInjection;

namespace MenYou;

[SupportedOSPlatform("windows")]
public partial class App : Application
{
    public static IServiceProvider Services { get; private set; } = null!;

    private TrayIcon? _trayIcon;
    private StartMenuWindow? _startMenu;
    private bool _isFirstRun;
    private SettingsWindow? _settingsWindow;
    private IHotkeyService? _hotkey;
    private ForegroundWatcher? _foregroundWatcher;
    private CopyDataListener? _ipcListener;
    private IWin11StartMirror? _startMirror;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        // Initialize the localizer BEFORE anything touches Strings.X.
        // Strings.cs evaluates its culture-dict properties lazily through
        // Localizer.Get, and the tray-icon setup, SetupHotkey, etc. below
        // pull those properties — so the localizer has to exist first.
        Jeek.Avalonia.Localization.Localizer.SetLocalizer(
            new MenYou.Services.AvaloniaResourceLocalizer());
        // Capture the first-run signal (no settings.json yet) to gate
        // AnnounceReady, which surfaces the one-time "ready" tray balloon once
        // the tray + hotkey are up.
        _isFirstRun = !File.Exists(SettingsFilePath);
        Services = BuildServices();
        // Eagerly warm app discovery FROM THE CACHE (file I/O only, no shell
        // COM, so — unlike a live scan — it doesn't suffer the startup COM
        // contention). This makes the menu's data ready almost immediately,
        // well before the ApplicationIdle warm-up, so an early first open
        // isn't left waiting. A cache miss is a no-op here; the warm-up does
        // the live scan at idle.
        _ = Services.GetRequiredService<IAppDiscoveryService>().PreloadFromCacheAsync();
        EnsureAutostartDefault();
        ApplyTheme(Services.GetRequiredService<ISettingsService>().Current.Theme);
        Services.GetRequiredService<ISettingsService>().Changed += () =>
            Dispatcher.UIThread.Post(() => ApplyTheme(Services.GetRequiredService<ISettingsService>().Current.Theme));

        // Diagnostic logging: off by default, driven by the Developer-tab
        // toggle (the MENYOU_TRACE_HOOKS env var still force-enables it for
        // pre-settings debugging). SetEnabled just flips a flag, so no
        // dispatcher hop is needed on change. Then sweep any old/oversized log
        // from a prior session off the UI thread so it never blocks startup.
        var devSettings = Services.GetRequiredService<ISettingsService>();
        HookTrace.SetEnabled(devSettings.Current.DiagnosticLogging);
        devSettings.Changed += () => HookTrace.SetEnabled(
            Services.GetRequiredService<ISettingsService>().Current.DiagnosticLogging);
        var maxLogBytes = (long)Math.Max(1, devSettings.Current.MaxLogSizeMb) * 1024 * 1024;
        _ = Task.Run(() => HookTrace.Cleanup(maxLogBytes, TimeSpan.FromDays(7)));
        if (PlatformSettings is not null)
        {
            PlatformSettings.ColorValuesChanged += (_, _) =>
                Dispatcher.UIThread.Post(() =>
                {
                    if (Services.GetRequiredService<ISettingsService>().Current.Theme == AppTheme.System)
                        ApplyTheme(AppTheme.System);
                });
        }

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            desktop.Exit += (_, _) => Cleanup();
        }

        SetupTrayIcon();
        HookTrace.Log("Startup: tray done");
        SetupHotkey();
        HookTrace.Log("Startup: hotkey done");
        SetupForegroundWatcher();
        SetupIpcListener();
        SetupSingleInstanceShowListener();
        SetupLaunchHider();
        // One-shot cleanup of any "Pin to MenYou" shell entries an earlier
        // build wrote. No-op once the keys are gone.
        ContextMenuRegistration.UnregisterCurrentUser();
        _ = SeedPinnedAsync();
        _ = SetupStartMirrorAsync();
        _ = LoadFallbackIconAsync();
        HookTrace.Log("Startup: sync init done (async tasks kicked off)");
        // First run only: tray + hotkey are live now, so surface a one-time
        // balloon confirming MenYou is up + the Shift+Win hint. One-shot — next
        // launch settings.json exists, so _isFirstRun is false and the balloon
        // doesn't reappear.
        if (_isFirstRun) AnnounceReady();
        // React to the user toggling MirrorWindowsStart in Settings.
        Services.GetRequiredService<ISettingsService>().Changed += () =>
            Dispatcher.UIThread.Post(() => _ = ApplyMirrorStateAsync());

        // Warm up the StartMenuWindow + its ViewModel AFTER the rest of app
        // startup finishes. Without this the first Shift+Win pays the cost of
        // constructing the window AND awaiting LoadAsync (programs tree, pin
        // seed, newly-installed scan, icon batch) — the user sees an empty menu
        // for hundreds of ms. With it, both costs are paid before the user does
        // anything; first show is instant.
        //
        // WHEN we warm depends on how MenYou launched (see ScheduleWarmup):
        //  • Warm launch (machine already up) — Input priority, drains ~+0.7 s
        //    after sync init; the window is realized ~1.5 s sooner than the
        //    render-timer-starved Background path would manage.
        //  • Cold boot (autostart firing into the post-login storm) — HOLD the
        //    warm-up so the heavy window build + off-screen first-paint don't
        //    pile onto the disk/CPU thrash of login (the #1 reason the first
        //    open feels slow right after a reboot). Warm once the boot settles.
        ScheduleWarmup();
        // NOTE: there is deliberately no Settings-window warm-up. It used to
        // Show() a throwaway off-screen, Opacity=0 SettingsWindow during idle
        // to pre-realize its control tree — but at a cold first launch (just
        // after login / install) the off-screen position + zero opacity
        // weren't applied before the window's first composited frame, so it
        // flashed a faint window on screen for a split second. With Fluid.
        // Avalonia now app-wide its theme loads at startup regardless, so the
        // warm-up's only remaining benefit was JIT-ing the window — not worth
        // a visible flash on every cold start. The first cog click realizes
        // the window cold (Settings is a rare, cold-path window).

        base.OnFrameworkInitializationCompleted();
    }

    /// Eagerly load the Windows Settings cog icon and stash it as the
    /// MenuItemViewModel fallback. Any pinned/recent/search-result item
    /// whose own icon extraction returns null (Settings is the recurring
    /// offender — its UWP AUMID sometimes 404s out of the shell) renders
    /// this cog instead of an empty 32×32 hole. Runs off the UI thread;
    /// the assignment hops back to UI to refresh any items already on
    /// screen.
    private async Task LoadFallbackIconAsync()
    {
        try
        {
            // Settings cog as the generic "app failed to load" placeholder.
            var cog = await Task.Run(() => IconExtractor.ExtractForAumid(
                "windows.immersivecontrolpanel_cw5n1h2txyewy!microsoft.windows.immersivecontrolpanel"));
            // Shell's generic folder icon (shell32.dll #3, native 32 px) for
            // folder entries — keeps All-Programs nodes readable as folders
            // even when our per-item icon extraction skips them. Rendered
            // 1:1 in the 32 px tile.
            var folder = await Task.Run(() =>
            {
                var shell32 = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.System), "shell32.dll");
                return IconExtractor.ExtractAvaloniaBitmap(shell32, 3);
            });

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (cog is not null)
                    ViewModels.Items.MenuItemViewModel.FallbackIcon = cog;
                if (folder is not null)
                    ViewModels.Items.MenuItemViewModel.FolderFallbackIcon = folder;
            });
            HookTrace.Log("Startup: fallback icons loaded");
        }
        catch
        {
            // Fallback loading is best-effort. If the shell refuses we
            // simply keep the existing "show nothing" behavior on null.
        }
    }

    private async Task SetupStartMirrorAsync()
    {
        try
        {
            // Defer the heavy initial sync. _startMirror.StartAsync runs
            // Export-StartLayout (PowerShell) which takes ~1.9 s and saturates
            // the UI thread — running it during the first couple seconds blocks
            // the window warm-up (ApplicationIdle) and any early open. The
            // Pinned section already shows the seeded/cached pins; the mirror
            // only keeps it live-synced, so it can start a beat later. The
            // delay runs off the UI thread so it parks nothing on it.
            await Task.Delay(2500).ConfigureAwait(false);
            _startMirror = Services.GetRequiredService<IWin11StartMirror>();
            _startMirror.StatusChanged += OnMirrorStatusChanged;
            HookTrace.Log("Startup: StartMirror.StartAsync begin");
            await _startMirror.StartAsync();
            HookTrace.Log("Startup: StartMirror.StartAsync done");
        }
        catch
        {
            // Mirror is best-effort — never block startup.
        }
    }

    private async Task ApplyMirrorStateAsync()
    {
        if (_startMirror is null) return;
        var settings = Services.GetRequiredService<ISettingsService>();
        if (settings.Current.MirrorWindowsStart && !_startMirror.IsRunning)
        {
            await _startMirror.StartAsync();
        }
        else if (!settings.Current.MirrorWindowsStart && _startMirror.IsRunning)
        {
            _startMirror.Stop();
        }
    }

    private void OnMirrorStatusChanged()
    {
        if (_startMirror is null) return;
        if (_startMirror.LastReadSucceeded) return;

        var settings = Services.GetRequiredService<ISettingsService>();
        if (settings.Current.MirrorUnavailableNotified) return;

        // Win 11 24H2 has been silently breaking Export-StartLayout on
        // some SKUs (Microsoft tech community thread linked from research).
        // Surface that once via a tray balloon so the user knows their
        // pin list won't update, then never nag again.
        settings.Current.MirrorUnavailableNotified = true;
        settings.Save();
        TrayBalloon.Show(Strings.MirrorUnavailableTitle, Strings.MirrorUnavailableBody);
    }

    private void SetupIpcListener()
    {
        // Receives WM_COPYDATA from the injected bridge DLL (StartClicked /
        // WinKey notifications, used when the LL hook strategy isn't
        // available). Lives independently of the hotkey service so it stays
        // up even when the user has disabled the Win key replacement.
        _ipcListener = new CopyDataListener();
        _ipcListener.Received += msg => Dispatcher.UIThread.Post(() => HandleIpcMessage(msg));
    }

    private void HandleIpcMessage(CopyDataListener.Message msg)
    {
        switch (msg.Id)
        {
            case CopyDataListener.NotifyId.StartClicked:
            case CopyDataListener.NotifyId.WinKey:
                ToggleStartMenu();
                break;
        }
    }

    /// Wire the single-instance "show" doorbell to the menu. Program.Main's
    /// SingleInstance guard lets a second launch signal this (already-running)
    /// instance instead of starting a duplicate; when it fires we surface the
    /// Start menu — the same idempotent reveal the tray Open verb uses. The
    /// signal arrives on a background listener thread, so hop to the UI thread.
    private void SetupSingleInstanceShowListener()
    {
        SingleInstance.StartShowListener(
            () => Dispatcher.UIThread.Post(ShowStartMenu));
    }

    private static async Task SeedPinnedAsync()
    {
        // First-run only: copy the user's taskbar pins into MenYou's Pinned
        // list. Done at startup (not first menu open) so Pinned is already
        // populated by the time the user looks at it.
        try
        {
            var pin = Services.GetRequiredService<IPinService>();
            var discovery = Services.GetRequiredService<IAppDiscoveryService>();
            await pin.EnsureSeededAsync(discovery);
            HookTrace.Log("Startup: pin seed done");
        }
        catch
        {
            // Seeding is best-effort — don't let it block app startup.
        }
    }

    /// Hide the StartMenu the moment any launch fires, instead of waiting
    /// for ForegroundWatcher to notice the new window grabbing focus.
    /// ShellExecute → window-creation → foreground-switch takes ~150-400
    /// ms; the user sees the clicked button stay pressed and the menu
    /// linger during that gap, which read as "the menu got confused".
    /// IShellLauncher fires Launched after every launch path (including
    /// SearchViewModel's direct Process.Start UWP/JumpList bypasses,
    /// which route through NotifyLaunched()), so this single handler
    /// covers every click + Enter activation.
    private void SetupLaunchHider()
    {
        var launcher = Services.GetRequiredService<IShellLauncher>();
        launcher.Launched += () =>
            Dispatcher.UIThread.Post(() => _startMenu?.HideMenu());
    }

    private void SetupForegroundWatcher()
    {
        // Avalonia's Window.Deactivated doesn't fire reliably for our popup
        // style on Win 11; this Win32 watcher fires whenever the foreground
        // window belongs to a process other than ours.
        _foregroundWatcher = new ForegroundWatcher();
        _foregroundWatcher.ForegroundLeftOurProcess += () =>
            Dispatcher.UIThread.Post(() =>
            {
                // Skip auto-hide during the post-show settle window —
                // the tray-menu Open path drops foreground briefly
                // while the native context menu dismisses; without
                // this guard the menu blinks open and immediately
                // hides. See StartMenuWindow.IsSettling for the
                // window duration.
                if (_startMenu is { IsVisible: true, IsSettling: false } &&
                    Services.GetRequiredService<ISettingsService>().Current.HideOnFocusLost)
                {
                    HookTrace.Log("App.ForegroundWatcher: foreground left our process -> HideMenu");
                    _startMenu.HideMenu();
                }
                else if (_startMenu is { IsVisible: true } menu)
                {
                    HookTrace.Log($"App.ForegroundWatcher: foreground left but skipped (settling={menu.IsSettling})");
                }
            });
    }

    private static IServiceProvider BuildServices()
    {
        var s = new ServiceCollection();
        s.AddSingleton<ISettingsService, SettingsService>();
        s.AddSingleton<IAppDiscoveryService, AppDiscoveryService>();
        s.AddSingleton<IIconService, IconService>();
        s.AddSingleton<IRecentItemsService, RecentItemsService>();
        s.AddSingleton<IShellLauncher, ShellLauncher>();
        s.AddSingleton<ISearchService, SearchService>();
        s.AddSingleton<IPowerService, Win32PowerService>();
        s.AddSingleton<IHotkeyService, Win32HotkeyService>();
        s.AddSingleton<IAutostartService, Win32AutostartService>();
        s.AddSingleton<IPinService, PinService>();
        s.AddSingleton<IWin11StartMirror, Win11StartMirrorService>();
        s.AddSingleton<IUserAvatarService, UserAvatarService>();
        s.AddSingleton<CustomThemesService>();
        // In-app update check against the GitHub Releases feed. Reads the
        // installed version from Inno's uninstall key, downloads the
        // newer MenYou-Setup .exe, and launches it (Inno upgrades in
        // place). Backs the Settings "Check for updates" button.
        s.AddSingleton<IUpdateService, GitHubUpdateService>();

        s.AddTransient<ProgramsViewModel>();
        s.AddTransient<SearchViewModel>();
        s.AddTransient<PowerMenuViewModel>();
        // Singleton (not transient): its constructor extracts ~10 shell icons
        // (several via slow AUMID/AppX resolution). Both the tray "Places"
        // submenu and the StartMenu's Places flyout resolve it — sharing one
        // instance means that work happens once, not once per consumer.
        s.AddSingleton<RightPanelViewModel>();
        s.AddSingleton<StartMenuViewModel>();
        s.AddTransient<SettingsViewModel>();

        return s.BuildServiceProvider();
    }

    private readonly Dictionary<MenuStyle, NativeMenuItem> _skinItems = new();
    private readonly Dictionary<string, NativeMenuItem> _customThemeItems = new();
    private NativeMenu? _skinMenu;
    private NativeMenuItemSeparator? _customThemeSeparator;
    private CustomThemesService? _customThemes;

    private void SetupTrayIcon()
    {
        // The tray icon's user-visible affordances (left-click toggle,
        // Open / Settings / Restart / Exit menu items) wire directly to
        // the App's methods below. There used to be a TrayIconViewModel
        // that exposed equivalent RelayCommands + Toggle/Settings/Exit
        // events — none of those commands were ever bound, none of the
        // events ever fired, so the whole VM was a dead intermediary.
        var settings = Services.GetRequiredService<ISettingsService>();

        var menu = new NativeMenu();

        // "Otwórz MenYou" / "Open MenYou" — the label promises Open, so
        // wire it to a dedicated always-show path rather than
        // ToggleStartMenu. Otherwise clicking Open while the menu is
        // already visible (e.g. after a Shift+Win press) HIDES it,
        // which reads as a bug: "I clicked Open and it closed!"
        var open = new NativeMenuItem(Strings.OpenMenYou);
        open.Click += (_, _) => ShowStartMenu();
        menu.Items.Add(open);
        menu.Items.Add(new NativeMenuItemSeparator());

        _customThemes = Services.GetRequiredService<CustomThemesService>();

        var skinParent = new NativeMenuItem(Strings.Skin) { Menu = new NativeMenu() };
        _skinMenu = skinParent.Menu!;
        foreach (var option in NamedOptions.MenuStyles)
        {
            var item = new NativeMenuItem(option.Label) { ToggleType = MenuItemToggleType.Radio };
            var captured = option.Value;
            item.Click += (_, _) =>
            {
                // Picking a built-in style also clears any active custom
                // theme — UseCustomTheme gates the layout swap, so without
                // this the menu would keep rendering the custom XAML.
                settings.Current.MenuStyle = captured;
                settings.Current.UseCustomTheme = false;
                settings.Save();
            };
            _skinMenu.Items.Add(item);
            _skinItems[option.Value] = item;
        }
        // Append the user's uploaded custom themes (from the Settings
        // window) below the built-in styles, and keep them in sync as
        // themes are uploaded/deleted so the submenu never goes stale.
        RebuildCustomThemeItems(settings);
        _customThemes.ThemeNames.CollectionChanged += (_, _) =>
            Dispatcher.UIThread.Post(() => RebuildCustomThemeItems(settings));
        menu.Items.Add(skinParent);

        // "Miejsca" / "Places" submenu — the same shell shortcuts the
        // menu's Places flyout lists (Start menu, Documents, Pictures,
        // Music, Downloads, This PC, Network, Control Panel, Settings,
        // Run…), reachable straight from the tray without opening MenYou.
        // Each item routes back through RightPanelViewModel.OpenCommand,
        // which launches via the shared IShellLauncher.
        //
        // Populate it OFF the synchronous startup path: resolving
        // RightPanelViewModel runs ~10 shell-icon extractions (slow AUMID
        // resolution included), and doing that here would delay the tray
        // icon + hotkey becoming usable right after login. The tray menu
        // only needs the titles, and the user won't open it in the few ms
        // this takes — so add the submenu now (empty) and fill it at
        // Background priority once the synchronous startup has settled.
        var placesParent = new NativeMenuItem(Strings.Places) { Menu = new NativeMenu() };
        menu.Items.Add(placesParent);
        Dispatcher.UIThread.Post(() =>
        {
            var places = Services.GetRequiredService<RightPanelViewModel>();
            foreach (var shortcut in places.Shortcuts)
            {
                var captured = shortcut;
                var placeItem = new NativeMenuItem(shortcut.Title);
                placeItem.Click += (_, _) => places.OpenCommand.Execute(captured);
                placesParent.Menu!.Items.Add(placeItem);
            }
        }, DispatcherPriority.Background);

        var settingsItem = new NativeMenuItem(Strings.Settings);
        settingsItem.Click += (_, _) => ShowSettings();
        menu.Items.Add(settingsItem);

        // "About" opens MenYou's GitHub project page in the default browser
        // (same destination as the Settings-window About button). Routed
        // through the shared RepositoryUrl constant so both stay in sync.
        var about = new NativeMenuItem(Strings.About);
        about.Click += (_, _) => OpenProjectPage();
        menu.Items.Add(about);

        menu.Items.Add(new NativeMenuItemSeparator());

        // Restart label reuses the localized shell-DLL string
        // (authui.dll,-3010 = "Restart" / "Uruchom ponownie" / locale
        // equivalent — the same imperative verb Windows prints on its
        // own power Restart button) and appends the app name. The "MenYou"
        // suffix makes it explicit that this restarts the app, not the
        // system, and it reads naturally in every language Windows ships
        // the resource for. No new localization key required.
        var restart = new NativeMenuItem($"{Strings.Restart} MenYou");
        restart.Click += (_, _) => RestartApp();
        menu.Items.Add(restart);

        var exit = new NativeMenuItem(Strings.Exit);
        exit.Click += (_, _) => ExitApp();
        menu.Items.Add(exit);

        settings.Changed += () => Dispatcher.UIThread.Post(() => UpdateSkinChecks(settings));

        _trayIcon = new TrayIcon
        {
            ToolTipText = Strings.TrayTooltip,
            IsVisible = true,
            Icon = LoadTrayIcon(),
            Menu = menu
        };
        _trayIcon.Clicked += (_, _) => ToggleStartMenu();

        TrayIcon.SetIcons(this, new TrayIcons { _trayIcon });
    }

    /// Rebuilds the custom-theme radio items in the tray's Skin submenu
    /// from CustomThemesService.ThemeNames — the uploaded themes from the
    /// Settings window — separated from the built-in styles by a divider.
    /// Called at startup and whenever a theme is uploaded or deleted, so
    /// the submenu stays current without an app restart.
    private void RebuildCustomThemeItems(ISettingsService settings)
    {
        if (_skinMenu is null || _customThemes is null) return;

        // Drop the previous custom block (separator + items) before
        // republishing, so repeated rebuilds don't accumulate entries.
        if (_customThemeSeparator is not null)
        {
            _skinMenu.Items.Remove(_customThemeSeparator);
            _customThemeSeparator = null;
        }
        foreach (var item in _customThemeItems.Values)
            _skinMenu.Items.Remove(item);
        _customThemeItems.Clear();

        if (_customThemes.ThemeNames.Count > 0)
        {
            _customThemeSeparator = new NativeMenuItemSeparator();
            _skinMenu.Items.Add(_customThemeSeparator);
            foreach (var name in _customThemes.ThemeNames)
            {
                var captured = name;
                var item = new NativeMenuItem(name) { ToggleType = MenuItemToggleType.Radio };
                item.Click += (_, _) =>
                {
                    var xaml = _customThemes.Load(captured);
                    if (xaml is null) return;
                    // Activating a custom theme mirrors the Settings flow:
                    // flip UseCustomTheme on and stash the file's XAML. The
                    // StartMenuViewModel reacts to settings.Changed and
                    // swaps the live menu to the parsed custom control.
                    settings.Current.UseCustomTheme = true;
                    settings.Current.CustomThemeXaml = xaml;
                    settings.Save();
                };
                _skinMenu.Items.Add(item);
                _customThemeItems[name] = item;
            }
        }

        UpdateSkinChecks(settings);
    }

    /// Syncs the Skin submenu's radio checkmarks to the active style: a
    /// built-in item is checked when it matches MenuStyle and no custom
    /// theme is active; a custom item is checked when UseCustomTheme is on
    /// and its file content matches the saved CustomThemeXaml (an edited-
    /// in-Settings theme won't match any file, so none shows checked).
    private void UpdateSkinChecks(ISettingsService settings)
    {
        var useCustom = settings.Current.UseCustomTheme;
        foreach (var (style, item) in _skinItems)
            item.IsChecked = !useCustom && settings.Current.MenuStyle == style;
        foreach (var (name, item) in _customThemeItems)
            item.IsChecked = useCustom
                && string.Equals(settings.Current.CustomThemeXaml, _customThemes?.Load(name), StringComparison.Ordinal);
    }

    private static WindowIcon? LoadTrayIcon()
    {
        try
        {
            var uri = new Uri("avares://MenYou/Assets/icon_v2.ico");
            using var stream = Avalonia.Platform.AssetLoader.Open(uri);
            return new WindowIcon(stream);
        }
        catch
        {
            return null;
        }
    }

    private void SetupHotkey()
    {
        _hotkey = Services.GetRequiredService<IHotkeyService>();
        _hotkey.Initialize(() => Dispatcher.UIThread.Post(ToggleStartMenu));
        _hotkey.ApplyBindings(Services.GetRequiredService<ISettingsService>().Current);
    }

    private void ToggleStartMenu()
    {
        if (_startMenu is { IsVisible: true })
        {
            HookTrace.Log("App.ToggleStartMenu: visible -> HideMenu");
            _startMenu.HideMenu();
            return;
        }
        HookTrace.Log("App.ToggleStartMenu: hidden -> ShowMenu");
        EnsureStartMenu();
        _startMenu!.ShowMenu();
    }

    /// Idempotent "show the menu" — used by the tray-menu Open item where
    /// the user-promised verb is Open, not Toggle. If the menu is already
    /// visible we re-foreground it instead of hiding it.
    private void ShowStartMenu()
    {
        EnsureStartMenu();
        if (_startMenu!.IsVisible)
        {
            // Already on screen but might be behind another window — pull
            // it back to the foreground. Activate() goes through Avalonia's
            // normal Activate path; the original ShowMenu also runs
            // ForceForeground via the bridge but Activate is usually
            // enough when the window is already realized.
            _startMenu.Activate();
            return;
        }
        _startMenu.ShowMenu();
    }

    /// Lazily realizes the StartMenuWindow + DataContext. Shared between
    /// ToggleStartMenu and ShowStartMenu so the construction logic
    /// (OpenSettingsRequested wiring, DI resolution) lives in one place.
    private void EnsureStartMenu()
    {
        if (_startMenu is not null) return;
        HookTrace.Log("EnsureStartMenu: resolving StartMenuViewModel");
        var vm = Services.GetRequiredService<StartMenuViewModel>();
        HookTrace.Log("EnsureStartMenu: VM resolved; constructing window");
        vm.OpenSettingsRequested = () =>
        {
            _startMenu?.HideMenu();
            ShowSettings();
        };
        _startMenu = new StartMenuWindow { DataContext = vm };
        HookTrace.Log("EnsureStartMenu: window object built");
    }

    // If MenYou launched within this long after the OS booted, it was almost
    // certainly started by its autostart entry into the post-login congestion
    // storm (the shell + every other auto-start app coming up at once).
    private const long ColdBootThresholdMs = 120_000;   // 2 min of OS uptime
    // How long to hold the warm-up on a cold boot so the login storm can drain
    // before we pay the expensive window build + off-screen first-paint.
    private static readonly TimeSpan ColdBootSettleDelay = TimeSpan.FromSeconds(20);

    /// Decides WHEN to run the one-shot warm-up. On a warm launch the machine
    /// is idle and the user is likely about to use MenYou, so warm immediately
    /// at Input priority. On a cold boot, autostart fires while the system is
    /// thrashing through login; doing the heavy window construction then both
    /// drags out MenYou's own first paint and adds to the storm. So we hold for
    /// a settle window, then post at Background priority (which drains fine once
    /// the render timer has calmed). If the user opens the menu during the
    /// hold, the open realizes the window on-demand and the deferred warm-up
    /// no-ops (see the guard below) — so we never do redundant work, and never
    /// warm eagerly while the user isn't even looking.
    private void ScheduleWarmup()
    {
        if (Environment.TickCount64 >= ColdBootThresholdMs)
        {
            HookTrace.Log("Warmup: warm launch -> immediate (Input)");
            Dispatcher.UIThread.Post(WarmupStartMenu, DispatcherPriority.Input);
            return;
        }
        HookTrace.Log($"Warmup: cold boot (uptime {Environment.TickCount64 / 1000}s) -> hold {ColdBootSettleDelay.TotalSeconds:F0}s");
        _ = Task.Run(async () =>
        {
            await Task.Delay(ColdBootSettleDelay).ConfigureAwait(false);
            Dispatcher.UIThread.Post(WarmupStartMenu, DispatcherPriority.Background);
        });
    }

    /// One-shot warm-up. Builds the menu window and runs the heavy LoadAsync
    /// work (programs tree, pin list seed, newly-installed scan) in the
    /// background so the first Shift+Win press has nothing to wait for. No-ops
    /// if the window is already realized — e.g. the user opened the menu during
    /// a cold-boot hold, so an on-demand realize already happened and re-warming
    /// would needlessly re-run LoadAsync and PreRender a live, visible window.
    private void WarmupStartMenu()
    {
        if (_startMenu is not null) { HookTrace.Log("WarmupStartMenu: already realized -> skip"); return; }
        HookTrace.Log("WarmupStartMenu: called (about to construct window)");
        EnsureStartMenu();
        HookTrace.Log("WarmupStartMenu: window constructed");
        if (_startMenu?.DataContext is StartMenuViewModel vm)
            _ = WarmupSequenceAsync(vm);
    }

    /// Post-login warm-up, ordered so the off-screen render warms the
    /// POPULATED tree:
    ///   1. await LoadAsync — build pinned / recent / programs + stream icons.
    ///   2. MarkWarmLoaded — so the first real open skips a redundant reload.
    ///   3. PreRender — realize the now-populated window once, off-screen and
    ///      transparent, paying the first-paint + first-populated-layout cost
    ///      here instead of on the user's first open.
    /// The earlier ordering pre-rendered the EMPTY tree (LoadAsync was
    /// fire-and-forget), so the first open still paid ~600 ms for the first
    /// populated layout; this sequence moves that into idle time.
    private async Task WarmupSequenceAsync(StartMenuViewModel vm)
    {
        HookTrace.Log("Warmup: begin (ApplicationIdle fired)");
        await vm.LoadAsync();
        vm.MarkWarmLoaded();
        HookTrace.Log("Warmup: data loaded, pre-rendering window");
        _startMenu?.PreRender();
    }

    /// Tray-menu Restart: spawns a fresh MenYou.exe and exits the
    /// current process. The cmd.exe wrapper waits ~1 second before
    /// launching so this process has fully terminated first — that
    /// gives the OS time to release the global Shift+Win hotkey
    /// registration, the TrayIcon GUID, the start2.bin file watcher,
    /// and the IPC named-window before the new instance grabs them.
    /// Without the delay the new process can race the old one and end
    /// up without a working hotkey (RegisterHotKey returns
    /// ERROR_HOTKEY_ALREADY_REGISTERED).
    private void RestartApp()
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe))
        {
            // No path means we're running from a context where
            // Environment.ProcessPath isn't populated (single-file
            // bundles before .NET 6 SP1, certain debugger hosts).
            // Skip the relaunch — exiting alone would be more
            // surprising than a no-op, so just bail.
            return;
        }

        try
        {
            // `start "" "<exe>"` opens the exe detached from cmd, so
            // the cmd process terminates without holding a handle on
            // MenYou. The empty "" is cmd's required window-title arg
            // before the actual command.
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c timeout /t 1 /nobreak >nul & start \"\" \"{exe}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
            });
        }
        catch
        {
            // Best-effort. If cmd refused (locked-down environment,
            // unusual PATH), don't kill our process — the user can
            // still close + relaunch manually.
            return;
        }
        ExitApp();
    }

    private void ShowSettings()
    {
        if (_settingsWindow is { IsVisible: true })
        {
            _settingsWindow.Activate();
            BringSettingsToFront();
            return;
        }
        _settingsWindow = new SettingsWindow
        {
            DataContext = Services.GetRequiredService<SettingsViewModel>()
        };
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
        _settingsWindow.Activate();
        // The cog handler hides the start menu just before this, which
        // bounces foreground around; on Win 11 a plain Show()/Activate()
        // then leaves the window behind the previous app ("clicked, but
        // nothing happened — works the second time"). Force it to the
        // front once the hide settles, the same way the start menu does.
        BringSettingsToFront();
    }

    /// Post-show foreground push for the Settings window, deferred so it
    /// runs after the start-menu-hide foreground bounce.
    private void BringSettingsToFront() =>
        Dispatcher.UIThread.Post(() =>
        {
            var hwnd = _settingsWindow?.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            if (hwnd != IntPtr.Zero)
            {
                _settingsWindow!.Activate();
                Win32Foreground.Bring(hwnd);
            }
        }, DispatcherPriority.Background);

    private void ExitApp()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.Shutdown();
    }

    /// Opens MenYou's GitHub project page in the default browser.
    /// UseShellExecute=true routes the https URL through the shell so it
    /// resolves to the registered browser; best-effort, so a missing
    /// handler just no-ops.
    private static void OpenProjectPage()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = GitHubUpdateService.RepositoryUrl,
                UseShellExecute = true,
            });
        }
        catch
        {
            // Best-effort — never let a browser-launch failure crash the tray.
        }
    }

    private void Cleanup()
    {
        _hotkey?.Dispose();
        _trayIcon?.Dispose();
        _foregroundWatcher?.Dispose();
        _ipcListener?.Dispose();
        _startMirror?.Dispose();
    }

    /// Path to the persisted settings file (kept in sync with
    /// <see cref="Services.SettingsService"/>). Its ABSENCE is the
    /// "fresh install / first run" signal that gates the one-time "ready"
    /// balloon.
    private static string SettingsFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "MenYou", "settings.json");

    /// First-run wrap-up: surface a one-time tray balloon confirming MenYou is
    /// up, plus the Shift+Win hint (doubles as onboarding). Best-effort — never
    /// throws into the startup path.
    private void AnnounceReady()
    {
        try { TrayBalloon.Show(Strings.ReadyTitle, Strings.ReadyBody); }
        catch { /* balloon is best-effort */ }
    }

    /// One-shot migration for profiles that predate the
    /// <see cref="UserSettings.StartWithWindows"/> default flip: force
    /// StartWithWindows on and write the Run-key registry entry, then
    /// mark the migration applied so we never overwrite the user's
    /// future choice. New installs hit the same code path harmlessly —
    /// StartWithWindows is already true at that point, so SetEnabled(true)
    /// is a no-op idempotent registry write.
    private static void EnsureAutostartDefault()
    {
        var settings = Services.GetRequiredService<ISettingsService>();

        // One-time default-on flip for profiles that predate the
        // StartWithWindows default (old settings.json carried false).
        if (!settings.Current.AutostartDefaultApplied)
        {
            settings.Current.StartWithWindows = true;
            settings.Current.AutostartDefaultApplied = true;
            settings.Save();
        }

        // One-time migration off the throttled HKCU\Run autostart and onto a
        // logon-triggered scheduled task (see Win32AutostartService — Run-key
        // apps are held ~10-16 s after sign-in by Windows). Re-applies the
        // user's current StartWithWindows choice through the new mechanism,
        // which creates the task and clears the legacy Run value.
        //
        // Off the UI thread: creating the task spawns schtasks.exe
        // (~100-300 ms), and it only affects the NEXT sign-in — there's no
        // reason to delay this session's tray/hotkey setup on it. Idempotent
        // and gated by the flag, so it runs at most once per profile.
        if (!settings.Current.AutostartTaskMigrated)
        {
            var want = settings.Current.StartWithWindows;
            _ = Task.Run(() =>
            {
                var autostart = Services.GetRequiredService<IAutostartService>();
                var established = false;
                try
                {
                    autostart.SetEnabled(want);
                    // Only treat the migration as done once autostart is ACTUALLY
                    // in place (task or Run-key) — or the user wants it off. If
                    // SetEnabled couldn't establish it (a bad task XML once slipped
                    // through and left no autostart at all), leave the flag false so
                    // the next launch retries, rather than flipping "migrated" and
                    // stranding the user with no autostart.
                    established = !want || autostart.IsEnabled;
                }
                catch
                {
                    // established stays false -> retry on the next launch.
                }
                if (established)
                    Dispatcher.UIThread.Post(() =>
                    {
                        settings.Current.AutostartTaskMigrated = true;
                        settings.Save();
                    });
            });
        }
    }

    private static void ApplyTheme(AppTheme theme)
    {
        if (Current is null) return;
        Current.RequestedThemeVariant = theme switch
        {
            AppTheme.Light => ThemeVariant.Light,
            AppTheme.Dark => ThemeVariant.Dark,
            _ => ResolveSystemTheme()
        };
    }

    private static ThemeVariant ResolveSystemTheme()
    {
        // Avalonia's ThemeVariant.Default falls back to Light on some
        // Windows configurations even when Apps are set to Dark. Read
        // the OS preference explicitly so System actually follows it.
        var platform = Current?.PlatformSettings;
        if (platform is not null)
        {
            var os = platform.GetColorValues().ThemeVariant;
            return os == Avalonia.Platform.PlatformThemeVariant.Dark
                ? ThemeVariant.Dark
                : ThemeVariant.Light;
        }
        return ThemeVariant.Default;
    }
}
