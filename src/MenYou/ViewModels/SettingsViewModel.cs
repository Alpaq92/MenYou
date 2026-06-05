using System.Runtime.Versioning;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MenYou.Models;
using MenYou.Platform.Windows;
using MenYou.Services;

namespace MenYou.ViewModels;

[SupportedOSPlatform("windows")]
public sealed partial class SettingsViewModel : ViewModelBase
{
    private readonly ISettingsService _settings;
    private readonly IAutostartService _autostart;
    private readonly IHotkeyService _hotkey;
    private readonly IAppDiscoveryService _discovery;
    private readonly CustomThemesService _customThemes;
    private readonly IUpdateService _updates;
    /// Window-level hook the view sets at OnOpened so the upload
    /// command can route through Avalonia's StorageProvider — the
    /// ViewModel itself can't construct one without a TopLevel
    /// reference. Returns the chosen file's filesystem path or null
    /// when the user cancels.
    public Func<Task<string?>>? PickXamlFileAsync { get; set; }

    /// Same shape as <see cref="PickXamlFileAsync"/> but routes
    /// through a Save dialog so the Save/export button can ask the
    /// user where to drop the current editor content. Returns the
    /// destination filesystem path or null on cancel.
    public Func<Task<string?>>? PickSaveXamlFileAsync { get; set; }

    /// View-supplied confirmation prompt. Apply calls this when the user
    /// is about to enable a custom theme whose XAML is empty (which would
    /// render a blank menu); returns true to proceed, false to abort. The
    /// view wires it to a ContentDialog so the VM stays UI-framework
    /// agnostic, same as the file-picker hooks above.
    public Func<Task<bool>>? ConfirmEmptyCustomThemeAsync { get; set; }

    [ObservableProperty] private MenuStyle _menuStyle;
    [ObservableProperty] private AppTheme _theme;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEditAccent))]
    private bool _useSystemAccent;
    [ObservableProperty] private string _accent = "";
    /// SukiUI-Playground-style XAML iteration: when checked, the
    /// CustomThemeXaml string below is parsed live and the resulting
    /// Control is mounted as a preview inside the dialog.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEditCustomTheme))]
    private bool _useCustomTheme;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSaveCustomTheme))]
    private string _customThemeXaml = "";

    /// Currently selected uploaded theme. ComboBox binds to this; the
    /// partial-method hook below pulls the matching file's text into
    /// <see cref="CustomThemeXaml"/> so the editor + live preview
    /// switch on each selection.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDeleteCustomTheme))]
    private string? _selectedCustomTheme;

    /// Theme names exposed for the ComboBox ItemsSource. Re-published
    /// when CustomThemesService picks up a freshly-uploaded file.
    public System.Collections.ObjectModel.ObservableCollection<string> CustomThemes
        => _customThemes.ThemeNames;

    /// Inverse of <see cref="UseCustomTheme"/>, used to disable the
    /// editor TextBox + preview area when the feature is off so the
    /// greyed-out chrome makes the relationship between the toggle and
    /// the inputs obvious.
    public bool CanEditCustomTheme => UseCustomTheme;

    /// Delete (Forget) button enable-state: the master toggle must be
    /// on AND a theme must currently be picked in the ComboBox. Avoids
    /// the "delete with no selection" edge case.
    public bool CanDeleteCustomTheme =>
        UseCustomTheme && !string.IsNullOrEmpty(SelectedCustomTheme);

    /// Save button enable-state: master toggle on AND the editor has
    /// something to save. The user can save fresh iterations they
    /// haven't loaded from a file as well as edits to existing themes.
    public bool CanSaveCustomTheme =>
        UseCustomTheme && !string.IsNullOrWhiteSpace(CustomThemeXaml);

    partial void OnSelectedCustomThemeChanged(string? value)
    {
        if (string.IsNullOrEmpty(value)) return;
        var content = _customThemes.Load(value);
        if (content is not null) CustomThemeXaml = content;
    }

    /// Opens the OS file picker via the view-supplied
    /// <see cref="PickXamlFileAsync"/> hook, copies the chosen file into
    /// the managed themes directory, refreshes the dropdown, and selects
    /// the new entry — which in turn populates the editor via
    /// OnSelectedCustomThemeChanged.
    [RelayCommand]
    public async Task UploadCustomThemeAsync()
    {
        if (PickXamlFileAsync is null) return;
        var path = await PickXamlFileAsync();
        if (string.IsNullOrEmpty(path)) return;
        var imported = _customThemes.Import(path);
        if (imported is not null) SelectedCustomTheme = imported;
    }

    /// Deletes the currently selected uploaded theme from disk and
    /// clears the selection. The next theme in the list (if any) doesn't
    /// auto-select — leaving the editor empty makes it obvious the
    /// delete succeeded. Command label is "Forget" in the UI because
    /// the action is "stop tracking this theme," not a destructive
    /// system-level delete.
    [RelayCommand]
    public void DeleteCustomTheme()
    {
        if (string.IsNullOrEmpty(SelectedCustomTheme)) return;
        if (_customThemes.Delete(SelectedCustomTheme))
        {
            SelectedCustomTheme = null;
            CustomThemeXaml = string.Empty;
        }
    }

    /// Save / export the current editor content to a user-picked file.
    /// Uses the view-supplied Save picker hook; on a successful write
    /// the saved file is NOT auto-imported into the managed themes
    /// directory — that's a separate Load operation. Keeps export
    /// independent of the in-app theme list.
    [RelayCommand]
    public async Task SaveCustomThemeAsync()
    {
        if (PickSaveXamlFileAsync is null) return;
        if (string.IsNullOrWhiteSpace(CustomThemeXaml)) return;
        var path = await PickSaveXamlFileAsync();
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            await File.WriteAllTextAsync(path, CustomThemeXaml);
        }
        catch
        {
            // Best-effort write — if the user picked a locked path or
            // a readonly drive we just don't write. A future iteration
            // could surface the error inline in the dialog, but a
            // silent no-op is friendlier than a thrown exception
            // crashing the Settings dialog.
        }
    }

    /// Inverse of <see cref="UseSystemAccent"/>, used by the manual hex
    /// TextBox's IsEnabled binding so it greys out while the dialog is in
    /// "follow Windows" mode.
    public bool CanEditAccent => !UseSystemAccent;

    /// Heuristic check for "is this a #RRGGBB or #AARRGGBB literal" used
    /// when surfacing the persisted Accent in the UI. Deliberately lax
    /// (just '#' + 6 or 8 hex chars) so the actual Color.Parse happens
    /// once in SettingsWindow.ApplyAccentOverride.
    private static bool LooksLikeHexColor(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return false;
        var t = s.Trim();
        if (t.Length == 0 || t[0] != '#') return false;
        if (t.Length != 7 && t.Length != 9) return false;
        for (int i = 1; i < t.Length; i++)
        {
            var c = t[i];
            var isHex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
            if (!isHex) return false;
        }
        return true;
    }
    [ObservableProperty] private bool _showRecent;
    [ObservableProperty] private bool _showPinned;
    [ObservableProperty] private bool _showSearch;
    [ObservableProperty] private bool _replaceWinKey;
    [ObservableProperty] private bool _startWithWindows;
    [ObservableProperty] private bool _hideOnFocusLost;
    [ObservableProperty] private bool _mirrorWindowsStart;
    [ObservableProperty] private int _maxRecentItems;
    [ObservableProperty] private int _menuWidth;
    [ObservableProperty] private int _menuHeight;
    [ObservableProperty] private string? _pushStatus;

    // ---- Developer tab ---------------------------------------------------
    [ObservableProperty] private bool _useDiscoveryCache;
    [ObservableProperty] private bool _immediateMenuReveal;
    [ObservableProperty] private bool _diagnosticLogging;
    [ObservableProperty] private int _maxLogSizeMb;

    /// Status line under the "Sprawdź aktualizacje" button. null = no
    /// recent check; otherwise one of the four phrases below
    /// (Checking / UpToDate / Downloaded / Failed). Cleared on dialog
    /// close in OnClosedHandler.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCheckForUpdates))]
    private string? _updateStatus;

    /// True while CheckForUpdatesAsync is in flight. Disables the
    /// button + flips its label to "Sprawdzanie...".
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCheckForUpdates))]
    private bool _isCheckingForUpdates;

    /// Button enable-state. False during the check (re-entrancy guard so
    /// a second click can't fire a second download / installer launch
    /// while the first is in flight).
    public bool CanCheckForUpdates => !IsCheckingForUpdates;

    public IReadOnlyList<NamedOption<MenuStyle>> MenuStyles => NamedOptions.MenuStyles;
    public IReadOnlyList<NamedOption<AppTheme>> Themes => NamedOptions.Themes;

    public NamedOption<MenuStyle> SelectedMenuStyle
    {
        get => MenuStyles.First(o => o.Value == MenuStyle);
        set => MenuStyle = value.Value;
    }

    public NamedOption<AppTheme> SelectedTheme
    {
        get => Themes.First(o => o.Value == Theme);
        set => Theme = value.Value;
    }

    partial void OnMenuStyleChanged(MenuStyle value) => OnPropertyChanged(nameof(SelectedMenuStyle));
    partial void OnThemeChanged(AppTheme value) => OnPropertyChanged(nameof(SelectedTheme));

    public SettingsViewModel(
        ISettingsService settings,
        IAutostartService autostart,
        IHotkeyService hotkey,
        IAppDiscoveryService discovery,
        CustomThemesService customThemes,
        IUpdateService updates)
    {
        _settings = settings;
        _autostart = autostart;
        _hotkey = hotkey;
        _discovery = discovery;
        _customThemes = customThemes;
        _updates = updates;
        var s = settings.Current;
        _menuStyle = s.MenuStyle;
        _theme = s.Theme;
        _useSystemAccent = s.UseSystemAccent;
        // Surface a clean empty field if the persisted value isn't a real
        // hex color — covers fresh installs (no value), legacy non-hex
        // sentinels left over from earlier accent-picker iterations
        // ("Auto", an enum-name string), and corruption from outside
        // editors. The PlaceholderText hint then tells the user what to
        // type if they want a manual override.
        _accent = LooksLikeHexColor(s.Accent) ? s.Accent : "";
        _useCustomTheme = s.UseCustomTheme;
        _customThemeXaml = s.CustomThemeXaml;
        _showRecent = s.ShowRecent;
        _showPinned = s.ShowPinned;
        _showSearch = s.ShowSearch;
        _replaceWinKey = s.ReplaceWinKey;
        _startWithWindows = s.StartWithWindows;
        _hideOnFocusLost = s.HideOnFocusLost;
        _mirrorWindowsStart = s.MirrorWindowsStart;
        _maxRecentItems = s.MaxRecentItems;
        _menuWidth = s.MenuWidth;
        _menuHeight = s.MenuHeight;
        _useDiscoveryCache = s.UseDiscoveryCache;
        _immediateMenuReveal = s.ImmediateMenuReveal;
        _diagnosticLogging = s.DiagnosticLogging;
        _maxLogSizeMb = s.MaxLogSizeMb;
    }

    [RelayCommand]
    public async Task Apply()
    {
        // Guard against applying an enabled-but-empty custom theme, which
        // mounts a blank menu. Ask the user to confirm; bail if they decline.
        if (UseCustomTheme
            && string.IsNullOrWhiteSpace(CustomThemeXaml)
            && ConfirmEmptyCustomThemeAsync is not null
            && !await ConfirmEmptyCustomThemeAsync())
        {
            return;
        }

        var s = _settings.Current;
        s.MenuStyle = MenuStyle;
        s.Theme = Theme;
        s.UseSystemAccent = UseSystemAccent;
        s.Accent = Accent;
        s.UseCustomTheme = UseCustomTheme;
        s.CustomThemeXaml = CustomThemeXaml;
        s.ShowRecent = ShowRecent;
        s.ShowPinned = ShowPinned;
        s.ShowSearch = ShowSearch;
        s.ReplaceWinKey = ReplaceWinKey;
        s.StartWithWindows = StartWithWindows;
        s.HideOnFocusLost = HideOnFocusLost;
        s.MirrorWindowsStart = MirrorWindowsStart;
        s.MaxRecentItems = MaxRecentItems;
        s.MenuWidth = MenuWidth;
        s.MenuHeight = MenuHeight;
        s.UseDiscoveryCache = UseDiscoveryCache;
        s.ImmediateMenuReveal = ImmediateMenuReveal;
        s.DiagnosticLogging = DiagnosticLogging;
        s.MaxLogSizeMb = MaxLogSizeMb;
        _settings.Save();
        _autostart.SetEnabled(StartWithWindows);
        _hotkey.ApplyBindings(s);
        // Apply the logging toggle immediately so it takes effect without a
        // restart (the settings.Changed subscription also covers this, but
        // doing it here keeps the behaviour obvious).
        HookTrace.SetEnabled(DiagnosticLogging);
    }

    /// Writes MenYou's pin list to the ConfigureStartPins policy and
    /// restarts Explorer. One-shot operation — Windows applies the policy
    /// on next sign-in / Explorer startup, not instantly.
    [RelayCommand]
    public async Task PushToWindowsStartAsync()
    {
        try
        {
            // Resolve the saved pin IDs to AppEntries via discovery so the
            // policy gets the right .lnk paths and target executables.
            var apps = await _discovery.GetAllAppsAsync();
            var pinnedIds = _settings.Current.Pinned
                .OrderBy(p => p.Order)
                .Select(p => p.AppId)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var pinnedEntries = _settings.Current.Pinned
                .OrderBy(p => p.Order)
                .Select(p => apps.FirstOrDefault(a => a.Id == p.AppId))
                .Where(a => a is not null)
                .Cast<AppEntry>()
                .ToList();

            var result = StartPinsPolicyWriter.Push(pinnedEntries);
            if (!result.Success)
            {
                PushStatus = result.Error ?? "Failed to write policy.";
                return;
            }

            PushStatus = $"Wrote {result.PinCount} pins. Restarting Explorer...";
            // Yield so the status text actually paints before Explorer
            // dies and the desktop flashes.
            await Task.Delay(150);
            StartPinsPolicyWriter.RestartExplorer();
            PushStatus = $"Wrote {result.PinCount} pins. Sign out and back in if Start doesn't refresh.";
        }
        catch (Exception ex)
        {
            PushStatus = ex.Message;
        }
    }

    [RelayCommand]
    public void Reset()
    {
        _settings.Reset();
        var s = _settings.Current;
        MenuStyle = s.MenuStyle;
        Theme = s.Theme;
        UseSystemAccent = s.UseSystemAccent;
        Accent = LooksLikeHexColor(s.Accent) ? s.Accent : "";
        UseCustomTheme = s.UseCustomTheme;
        CustomThemeXaml = s.CustomThemeXaml;
        ShowRecent = s.ShowRecent;
        ShowPinned = s.ShowPinned;
        ShowSearch = s.ShowSearch;
        ReplaceWinKey = s.ReplaceWinKey;
        StartWithWindows = s.StartWithWindows;
        HideOnFocusLost = s.HideOnFocusLost;
        MirrorWindowsStart = s.MirrorWindowsStart;
        MaxRecentItems = s.MaxRecentItems;
        MenuWidth = s.MenuWidth;
        MenuHeight = s.MenuHeight;
        UseDiscoveryCache = s.UseDiscoveryCache;
        ImmediateMenuReveal = s.ImmediateMenuReveal;
        DiagnosticLogging = s.DiagnosticLogging;
        MaxLogSizeMb = s.MaxLogSizeMb;
    }

    /// Hits the GitHub Releases feed and, if a newer version exists,
    /// downloads its installer and runs it (Inno upgrades in place,
    /// then relaunches MenYou). The status line under the button
    /// reflects the four possible outcomes:
    ///   - Sprawdzanie...       — in flight
    ///   - Aktualna wersja      — already on latest / dev build
    ///   - Aktualizacja gotowa  — newer version's installer launched
    ///   - <error message>      — feed unreachable / no installer asset
    /// Pinning the labels in <see cref="Strings"/> means the localizer
    /// shows the user the right phrase per culture.
    [RelayCommand]
    public async Task CheckForUpdatesAsync()
    {
        if (IsCheckingForUpdates) return;
        IsCheckingForUpdates = true;
        UpdateStatus = Strings.UpdateChecking;
        try
        {
            var (outcome, message) = await _updates.CheckAndApplyAsync();
            UpdateStatus = outcome switch
            {
                UpdateResult.Downloaded => Strings.UpdateDownloaded,
                UpdateResult.Failed     => $"{Strings.UpdateCheckFailed} ({message})",
                _                       => Strings.UpdateUpToDate,
            };
        }
        finally
        {
            IsCheckingForUpdates = false;
        }
    }

    /// Opens MenYou's GitHub project page in the user's default browser.
    /// UseShellExecute=true hands the https URL to the shell so it resolves
    /// through the registered browser; best-effort, so a missing handler
    /// just no-ops rather than throwing into the UI.
    [RelayCommand]
    public void OpenAbout()
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
            // Best-effort — never let a browser-launch failure surface.
        }
    }
}
