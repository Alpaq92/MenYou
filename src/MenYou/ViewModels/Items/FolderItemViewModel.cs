using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MenYou.Models;

namespace MenYou.ViewModels.Items;

public sealed partial class FolderItemViewModel : MenuItemViewModel
{
    public MenuFolder Folder { get; }
    public ObservableCollection<MenuItemViewModel> ChildItems { get; } = new();

    public override IReadOnlyList<MenuItemViewModel> Children => ChildItems;

    /// Folder items prefer the shell's generic folder icon to the cog
    /// when their per-folder icon is unset, falling back to the cog only
    /// if the folder icon failed to load at startup.
    protected override Bitmap? FallbackForType
        => FolderFallbackIcon ?? base.FallbackForType;

    [ObservableProperty] private bool _isExpanded;

    public FolderItemViewModel(MenuFolder folder)
    {
        Folder = folder;
        DisplayName = folder.Name;
    }

    /// Folder-level "Open file location" context-menu verb. Opens the
    /// on-disk Start Menu directory the folder mirrors in Explorer.
    /// Folders in the tree have no Launch / RunAsAdmin / Pin verbs —
    /// they're navigational containers, not launchable targets.
    [RelayCommand]
    private void OpenFolderLocation()
    {
        if (string.IsNullOrWhiteSpace(Folder.Path)) return;
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{Folder.Path}\"",
                UseShellExecute = true,
            });
        }
        catch { }
    }
}
