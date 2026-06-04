using System.Collections.ObjectModel;
using Avalonia.Threading;
using MenYou.Models;
using MenYou.Services;
using MenYou.ViewModels.Items;

namespace MenYou.ViewModels;

public sealed partial class ProgramsViewModel : ViewModelBase
{
    private readonly IAppDiscoveryService _discovery;
    private readonly IIconService _icons;
    private readonly IShellLauncher _launcher;
    private readonly IPinService _pin;

    public ObservableCollection<MenuItemViewModel> Items { get; } = new();

    public ProgramsViewModel(IAppDiscoveryService discovery, IIconService icons, IShellLauncher launcher, IPinService pin)
    {
        _discovery = discovery;
        _icons = icons;
        _launcher = launcher;
        _pin = pin;
    }

    /// Builds the programs tree and kicks off async icon loading.
    /// Called directly (no XAML Command binding) from
    /// <see cref="StartMenuViewModel.LoadAsync"/>.
    public async Task LoadAsync()
    {
        var root = await _discovery.BuildProgramsTreeAsync();
        Items.Clear();
        foreach (var f in root.Folders) Items.Add(BuildFolder(f));
        foreach (var a in root.Apps) Items.Add(BuildApp(a));
        _ = Task.Run(() => LoadIconsAsync(Items));
    }

    private FolderItemViewModel BuildFolder(MenuFolder folder)
    {
        var vm = new FolderItemViewModel(folder);
        foreach (var sub in folder.Folders) vm.ChildItems.Add(BuildFolder(sub));
        foreach (var app in folder.Apps) vm.ChildItems.Add(BuildApp(app));
        return vm;
    }

    private AppItemViewModel BuildApp(AppEntry e) => new(e, _launcher, _pin);

    /// Sets <see cref="MenuItemViewModel.IsNew"/> on every app in the
    /// tree whose ID appears in <paramref name="newlyInstalledIds"/>,
    /// and bubbles the flag up to any folder containing such an app so
    /// the highlight is visible at the collapsed folder level (the
    /// common case after a fresh install — the new entry lives inside
    /// the program's freshly-created Start Menu sub-folder).
    public void MarkNew(IReadOnlySet<string> newlyInstalledIds)
    {
        if (newlyInstalledIds.Count == 0) return;
        MarkNewRecursive(Items, newlyInstalledIds);
    }

    private static bool MarkNewRecursive(IEnumerable<MenuItemViewModel> items, IReadOnlySet<string> newIds)
    {
        bool anyNew = false;
        foreach (var item in items)
        {
            switch (item)
            {
                case AppItemViewModel app when newIds.Contains(app.Entry.Id):
                    app.IsNew = true;
                    anyNew = true;
                    break;
                case FolderItemViewModel folder:
                    if (MarkNewRecursive(folder.ChildItems, newIds))
                    {
                        folder.IsNew = true;
                        anyNew = true;
                    }
                    break;
            }
        }
        return anyNew;
    }

    private async Task LoadIconsAsync(IEnumerable<MenuItemViewModel> items)
    {
        foreach (var item in items)
        {
            switch (item)
            {
                case AppItemViewModel app:
                    var bmp = await _icons.GetIconAsync(app.Entry);
                    if (bmp is not null)
                        await Dispatcher.UIThread.InvokeAsync(() => app.Icon = bmp);
                    break;
                case FolderItemViewModel folder:
                    await LoadIconsAsync(folder.Children);
                    break;
            }
        }
    }
}
