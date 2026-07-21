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
    private readonly ISettingsService _settings;
    private ProgramsOrder _builtOrder;

    public ObservableCollection<MenuItemViewModel> Items { get; } = new();

    public ProgramsViewModel(IAppDiscoveryService discovery, IIconService icons, IShellLauncher launcher, IPinService pin, ISettingsService settings)
    {
        _discovery = discovery;
        _icons = icons;
        _launcher = launcher;
        _pin = pin;
        _settings = settings;
        // Re-order the tree when the "All" ordering setting changes. Changed
        // fires on every Save (and can fire off the UI thread), so hop onto
        // the dispatcher and gate on an actual difference from what's built —
        // unrelated settings churn must not tear the tree down (that resets
        // scroll/expand state and re-cogs the icons until the cache refills).
        settings.Changed += () => Dispatcher.UIThread.Post(() =>
        {
            if (Items.Count > 0 && _settings.Current.ProgramsOrder != _builtOrder)
                _ = LoadAsync();
        });
    }

    private int _loadVersion;

    /// <summary>Builds the programs tree and kicks off async icon loading.</summary>
    /// <remarks>
    /// Called directly (no XAML Command binding) from
    /// <see cref="StartMenuViewModel.LoadAsync"/>. Latest-wins: callers all
    /// run on the UI thread, but the discovery await is an interleave point —
    /// a settings-change reload can start while a Refreshed reload is still
    /// awaiting, and the OLDER continuation must not clear/refill Items over
    /// the newer tree. The version check after the await lets only the most
    /// recent call commit.
    /// </remarks>
    public async Task LoadAsync()
    {
        Dispatcher.UIThread.VerifyAccess();
        var version = ++_loadVersion;
        var root = await _discovery.BuildProgramsTreeAsync();
        if (version != _loadVersion) return; // superseded while awaiting
        var order = _settings.Current.ProgramsOrder;
        Items.Clear();
        foreach (var item in BuildOrdered(root, order)) Items.Add(item);
        _builtOrder = order;
        _ = LoadIconsAsync();
    }

    /// <summary>
    /// Orders one level of the tree per Settings → "All" order, applied
    /// recursively — a folder's children follow the same rule.
    /// </summary>
    /// <remarks>
    /// The actual semantics live in <see cref="ProgramsOrdering"/>, shared
    /// with the theme-facing ProgramsOrderConverter so the two can't diverge.
    /// </remarks>
    private IEnumerable<MenuItemViewModel> BuildOrdered(MenuFolder folder, ProgramsOrder order)
    {
        var folders = folder.Folders.Select(f => (MenuItemViewModel)BuildFolder(f, order));
        var apps = folder.Apps.Select(a => (MenuItemViewModel)BuildApp(a));
        return ProgramsOrdering.Apply(folders, apps, order);
    }

    private FolderItemViewModel BuildFolder(MenuFolder folder, ProgramsOrder order)
    {
        var vm = new FolderItemViewModel(folder);
        foreach (var child in BuildOrdered(folder, order)) vm.ChildItems.Add(child);
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

    /// Flattens the whole tree ON the UI thread, then fans icon extraction
    /// across cores; each icon lands via its own posted UI update. The old
    /// recursive walk awaited one GetIconAsync + one UI invoke per app —
    /// ~N serial shell-COM round-trips on every cold start — and ran inside
    /// Task.Run, enumerating live observable collections off-thread. Must
    /// be called on the UI thread (LoadAsync's continuation is).
    private int _iconGeneration;

    private Task LoadIconsAsync()
    {
        // Loud, not comment-only: the flatten below walks live observable
        // collections, which is only safe on the UI thread (review flagged
        // that settings/pin Changed events fire from background threads and
        // safety depended on every handler remembering to Post).
        Dispatcher.UIThread.VerifyAccess();
        // Superseded-batch guard: LoadAsync tears Items down and re-fires;
        // a previous batch's icons must not flood posts onto the rebuilt
        // tree (they'd land on detached view-models).
        var gen = ++_iconGeneration;
        var apps = new List<AppItemViewModel>();
        CollectApps(Items, apps);
        return _icons.LoadIconsAsync(apps, a => a.Entry,
            (app, bmp) => Dispatcher.UIThread.Post(() =>
            {
                if (gen == _iconGeneration) app.Icon = bmp;
            }));
    }

    private static void CollectApps(IEnumerable<MenuItemViewModel> items, List<AppItemViewModel> into)
    {
        foreach (var item in items)
        {
            switch (item)
            {
                case AppItemViewModel app: into.Add(app); break;
                case FolderItemViewModel folder: CollectApps(folder.ChildItems, into); break;
            }
        }
    }
}
