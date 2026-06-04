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
        var avatarResult = avatarService.LoadAvatar();
        _avatar = avatarResult.Bitmap;
        _isDefaultAvatar = avatarResult.IsDefault;
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
        settings.Changed += () =>
        {
            MenuStyle = settings.Current.MenuStyle;
            UseCustomTheme = settings.Current.UseCustomTheme;
            CustomThemeXaml = settings.Current.CustomThemeXaml;
        };
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
    }

    /// Builds the menu's pinned / recent / programs surfaces. Called
    /// directly from <see cref="Views.StartMenuWindow.OnOpened"/> — no
    /// XAML Command binding, so no [RelayCommand] attribute is needed.
    public async Task LoadAsync()
    {
        await Programs.LoadAsync();
        // EnsureSeededAsync also runs at app startup (see App.SeedPinnedAsync);
        // calling here again is a cheap idempotent no-op once seeded, but
        // it covers the edge case where the user opens the menu before
        // startup seeding finishes.
        await _pin.EnsureSeededAsync(_discovery);
        await ComputeNewlyInstalledAsync();
        RebuildPinned();
        RebuildRecent();
        Programs.MarkNew(_newlyInstalledIds);
        _ = Task.Run(LoadIconsAsync);
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
        Pinned.Clear();
        foreach (var p in _settings.Current.Pinned.OrderBy(p => p.Order))
        {
            var entry = _discovery.FindById(p.AppId);
            if (entry is null) continue;
            var vm = new AppItemViewModel(entry, _launcher, _pin);
            if (_newlyInstalledIds.Contains(p.AppId)) vm.IsNew = true;
            Pinned.Add(vm);
        }
    }

    private void RebuildRecent()
    {
        Recent.Clear();
        foreach (var r in _recent.Recent)
        {
            var entry = _discovery.FindById(r.AppId);
            if (entry is null) continue;
            var vm = new AppItemViewModel(entry, _launcher, _pin);
            if (_newlyInstalledIds.Contains(r.AppId)) vm.IsNew = true;
            Recent.Add(vm);
        }
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
