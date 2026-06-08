using System.Diagnostics;
using System.Runtime.Versioning;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MenYou.Models;
using MenYou.Platform.Windows;
using MenYou.Services;

namespace MenYou.ViewModels.Items;

[SupportedOSPlatform("windows")]
public sealed partial class AppItemViewModel : MenuItemViewModel
{
    public AppEntry Entry { get; }
    private readonly IShellLauncher _launcher;
    private readonly IPinService _pin;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PinToggleLabel))]
    private bool _isPinned;

    /// Controls whether the Pin/Unpin context menu item is visible. With
    /// hybrid pinning it's always true — the user can pin any app or unpin
    /// any pinned one in both manual and mirror modes. In mirror mode a
    /// manual pin is recorded in UserSettings.ManualPins (so it survives
    /// syncs) and an unpin of a Windows-mirrored app adds it to
    /// MirrorExclusions; the Windows Start pin list is never touched.
    [ObservableProperty]
    private bool _canModifyPin;

    public string PinToggleLabel => IsPinned ? Strings.UnpinFromStart : Strings.PinToStart;

    public override System.Windows.Input.ICommand? Command => LaunchCommand;

    public AppItemViewModel(AppEntry entry, IShellLauncher launcher, IPinService pin)
    {
        Entry = entry;
        _launcher = launcher;
        _pin = pin;
        DisplayName = entry.DisplayName;
        _isPinned = pin.IsPinned(entry.Id);
        _canModifyPin = pin.CanUnpin(entry.Id);
        pin.Changed += OnPinChanged;
    }

    private void OnPinChanged()
    {
        IsPinned = _pin.IsPinned(Entry.Id);
        // CanUnpin depends on both the mirror setting AND whether this
        // specific entry is currently pinned (in mirror mode you can only
        // unpin currently-pinned items — re-pinning is done in Windows).
        CanModifyPin = _pin.CanUnpin(Entry.Id);
    }

    [RelayCommand]
    private void Launch() => _launcher.Launch(Entry);

    /// "Run as administrator" context-menu verb. Routes through
    /// <see cref="IShellLauncher.LaunchAsAdmin"/> which sets
    /// <c>ProcessStartInfo.Verb = "runas"</c> so Windows shows the UAC
    /// elevation prompt.
    [RelayCommand]
    private void RunAsAdministrator() => _launcher.LaunchAsAdmin(Entry);

    [RelayCommand]
    private void TogglePin() => _pin.Toggle(Entry.Id);

    [RelayCommand]
    private void OpenFileLocation()
    {
        var target = Entry.SourceLnkPath ?? Entry.TargetPath;
        if (string.IsNullOrWhiteSpace(target)) return;
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{target}\"",
                UseShellExecute = true
            });
        }
        catch { }
    }

    /// Launches a JumpList entry from the context menu: a published Task
    /// (target + CLI args, e.g. Brave's "New window") or a Recent destination
    /// (a file path, args null). Signals the menu to hide afterward. Mirrors
    /// SearchViewModel.OpenTask / OpenRecent for the app context menu.
    public void OpenJumpListTarget(string target, string? arguments)
    {
        if (string.IsNullOrWhiteSpace(target)) return;
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = target,
                Arguments = arguments ?? string.Empty,
                UseShellExecute = true,
            });
            _launcher.NotifyLaunched();
        }
        catch { }
    }

}
