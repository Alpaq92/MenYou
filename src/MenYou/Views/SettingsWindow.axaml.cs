using System.IO;
using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Fluid.Avalonia;
using MenYou.Platform.Windows;
using MenYou.Services;
using MenYou.ViewModels;

namespace MenYou.Views;

[SupportedOSPlatform("windows")]
public partial class SettingsWindow : Window
{
    private ISettingsService? _settings;
    private Action? _settingsHandler;

    public SettingsWindow()
    {
        InitializeComponent();
        Opened += OnOpenedHandler;
        Closed += OnClosedHandler;
    }

    private void OnOpenedHandler(object? sender, EventArgs e)
    {
        // Reveal only after the first layout pass has positioned (CenterScreen)
        // and painted the window. The Window starts Opacity=0 — baked into the
        // XAML so the very first composited frame is already invisible — so the
        // user never sees the unpositioned/unpainted first frame, which showed
        // up as a transparent window blinking in the top-left corner before it
        // snapped to center. Posted FIRST, before the early-returns below, so a
        // missing service can never leave the window stuck invisible. Loaded
        // priority runs after layout/positioning (same reveal the StartMenu
        // uses), so by the time Opacity goes to 1 the window is centered.
        Dispatcher.UIThread.Post(() => Opacity = 1, DispatcherPriority.Loaded);

        if (App.Services is null) return;
        _settings = App.Services.GetService(typeof(ISettingsService)) as ISettingsService;
        if (_settings is null) return;

        // Wire the file-picker hook the SettingsViewModel needs to
        // route its upload command through Avalonia's StorageProvider.
        // The ViewModel can't construct a picker without a TopLevel
        // reference, so we hand it a lambda that does.
        if (DataContext is SettingsViewModel vm)
        {
            vm.PickXamlFileAsync = PickXamlFileAsync;
            vm.PickSaveXamlFileAsync = PickSaveXamlFileAsync;
            vm.ConfirmEmptyCustomThemeAsync = ConfirmEmptyCustomThemeAsync;
        }

        ApplyAccentOverride();

        // Repaint on Apply — the user might have toggled "Follow Windows"
        // off and typed a hex, or vice versa. AccentService already
        // subscribes to the OS ColorValuesChanged event (in Apply), so the
        // live-OS-accent path stays in sync on its own; we only need to
        // react to MenYou's own settings changes here.
        _settingsHandler = () =>
            Dispatcher.UIThread.Post(ApplyAccentOverride);
        _settings.Changed += _settingsHandler;
    }

    private void OnClosedHandler(object? sender, EventArgs e)
    {
        if (_settings is not null && _settingsHandler is not null)
            _settings.Changed -= _settingsHandler;
        _settings = null;
        _settingsHandler = null;
    }

    private void ApplyAccentOverride()
    {
        if (_settings is null) return;

        // AccentService publishes the SystemAccentColor* ramp that the
        // Fluid theme's accent brushes resolve through. UseSystemAccent
        // clears any override and reverts to the live OS accent — the
        // "Follow Windows" path. SetAccent(<parsed hex>) overrides it and
        // is the manual path. Parse failure falls back to system so a typo
        // doesn't blank the accent. The service is static/app-wide;
        // FluidTheme's constructor has already called Apply (which stores
        // the Application instance), so these calls are safe by the time
        // the Opened handler runs.
        if (_settings.Current.UseSystemAccent
            || !TryParseHexColor(_settings.Current.Accent, out var color))
        {
            AccentService.UseSystemAccent();
            return;
        }
        AccentService.SetAccent(color);
    }

    private static bool TryParseHexColor(string? input, out Color color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(input)) return false;
        var s = input.Trim();
        if (!s.StartsWith('#')) s = "#" + s;
        return Color.TryParse(s, out color);
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();

    /// File picker for the "Upload custom theme" button. Filters to
    /// .axaml / .xaml extensions. Returns the chosen file's local path
    /// or null on cancel.
    private async Task<string?> PickXamlFileAsync()
    {
        var top = TopLevel.GetTopLevel(this);
        if (top is null) return null;
        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select an Avalonia XAML file",
            AllowMultiple = false,
            SuggestedStartLocation = await ResolveThemeStartFolderAsync(top),
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Avalonia XAML")
                {
                    Patterns = new[] { "*.axaml", "*.xaml" }
                }
            }
        });
        if (files.Count == 0) return null;
        var path = files[0].TryGetLocalPath();
        RememberThemeFolder(path);
        return path;
    }

    /// Save-picker counterpart to <see cref="PickXamlFileAsync"/>.
    /// Defaults the extension to .axaml since that's what MenYou's
    /// custom-theme loader expects on round-trip.
    private async Task<string?> PickSaveXamlFileAsync()
    {
        var top = TopLevel.GetTopLevel(this);
        if (top is null) return null;
        var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save custom theme as",
            DefaultExtension = "axaml",
            ShowOverwritePrompt = true,
            SuggestedStartLocation = await ResolveThemeStartFolderAsync(top),
            FileTypeChoices = new[]
            {
                new FilePickerFileType("Avalonia XAML")
                {
                    Patterns = new[] { "*.axaml", "*.xaml" }
                }
            }
        });
        var path = file?.TryGetLocalPath();
        RememberThemeFolder(path);
        return path;
    }

    /// Resolves the folder the custom-theme dialogs should open at: the last
    /// folder the user picked a theme from (remembered across launches), or
    /// MenYou's bundled samples folder ({app}\samples\custom-themes) when there
    /// is no remembered folder yet or it no longer exists. Returns null when
    /// neither exists (e.g. a dev build with no samples dir) so the OS uses its
    /// own default — which is the per-app "remember last folder" behaviour.
    private async Task<IStorageFolder?> ResolveThemeStartFolderAsync(TopLevel top)
    {
        var last = _settings?.Current.LastThemeFolder;
        if (!string.IsNullOrEmpty(last) && Directory.Exists(last))
            return await top.StorageProvider.TryGetFolderFromPathAsync(last);

        var samples = Path.Combine(AppContext.BaseDirectory, "samples", "custom-themes");
        return Directory.Exists(samples)
            ? await top.StorageProvider.TryGetFolderFromPathAsync(samples)
            : null;
    }

    /// Remembers the folder a just-picked theme file lives in, so the next
    /// Load/Save dialog reopens there. No-op when nothing was picked.
    private void RememberThemeFolder(string? pickedPath)
    {
        if (_settings is null || string.IsNullOrEmpty(pickedPath)) return;
        var dir = Path.GetDirectoryName(pickedPath);
        if (string.IsNullOrEmpty(dir) || dir == _settings.Current.LastThemeFolder) return;
        _settings.Current.LastThemeFolder = dir;
        _settings.Save();
    }

    /// Confirmation the SettingsViewModel calls before applying an
    /// enabled-but-empty custom theme (which would leave a blank menu).
    /// Returns true when the user chooses to apply anyway. A small modal
    /// Window keeps this dependency-light and self-contained; it inherits
    /// the app-wide Fluid.Avalonia theme automatically.
    private async Task<bool> ConfirmEmptyCustomThemeAsync()
    {
        var body = new TextBlock
        {
            Text = Strings.ConfirmEmptyThemeBody,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 360,
        };

        var yesButton = new Button { Content = Strings.Yes, MinWidth = 90 };
        var noButton = new Button { Content = Strings.No, MinWidth = 90, IsCancel = true };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        buttons.Children.Add(yesButton);
        buttons.Children.Add(noButton);

        var root = new StackPanel { Margin = new Thickness(20), Spacing = 16 };
        root.Children.Add(body);
        root.Children.Add(buttons);

        var dialog = new Window
        {
            Title = Strings.ConfirmEmptyThemeTitle,
            Content = root,
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            ShowInTaskbar = false,
        };
        // A child dialog is a separate top-level, but Application.Styles
        // (where Fluid.Avalonia now lives) cascade to every window, so the
        // prompt picks up the Fluid chrome automatically — no per-dialog
        // theme needed.
        yesButton.Click += (_, _) => dialog.Close(true);
        noButton.Click += (_, _) => dialog.Close(false);

        // ShowDialog<bool> returns the value passed to Close(); closing via
        // the title-bar X yields default(bool) = false (treated as cancel).
        return await dialog.ShowDialog<bool>(this);
    }
}
