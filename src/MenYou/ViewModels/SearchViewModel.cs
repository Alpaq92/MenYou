using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MenYou.Models;
using MenYou.Services;
using MenYou.ViewModels.Items;

namespace MenYou.ViewModels;

public sealed partial class SearchViewModel : ViewModelBase
{
    private readonly ISearchService _search;
    private readonly IShellLauncher _launcher;
    private readonly IIconService _icons;
    private readonly IRecentItemsService _recent;
    private CancellationTokenSource? _cts;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasQuery))]
    private string _query = "";

    public bool HasQuery => !string.IsNullOrWhiteSpace(Query);

    /// True while a search is in flight — drives the indeterminate
    /// progress bar in the search results overlay so the user gets
    /// feedback instead of a blank panel during the 80 ms debounce +
    /// thread-pool work.
    [ObservableProperty] private bool _isSearching;

    public ObservableCollection<SearchResultViewModel> Results { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRunAsAdmin))]
    [NotifyPropertyChangedFor(nameof(CanOpenFileLocation))]
    private SearchResultViewModel? _selected;

    /// Two-way bound to the per-app Recent files ListBox in Win7Layout.
    /// Drives the Enter keybinding and lets DoubleTapped resolve which
    /// destination to open without scraping the visual tree.
    [ObservableProperty] private SearchResultViewModel.RecentDestination? _selectedRecent;

    partial void OnSelectedChanged(SearchResultViewModel? value)
    {
        if (value is null) return;
        // Lazily load the prominent context-panel icon. Use HasLargeIcon
        // (checks the backing field) rather than LargeIcon — the
        // property's getter falls back to the list-row Icon.
        if (!value.HasLargeIcon)
        {
            var capturedIcon = value;
            _ = Task.Run(async () =>
            {
                try
                {
                    var bmp = await _icons.GetLargeIconForSearchResultAsync(capturedIcon.Data);
                    if (bmp is null) return;
                    await Dispatcher.UIThread.InvokeAsync(() => capturedIcon.LargeIcon = bmp);
                }
                catch { }
            });
        }

        // Lazily load the per-app Recent destination list (the JumpList
        // strip — what Excel shows for last spreadsheets etc.).
        if (!value.HasRecentLoaded)
        {
            value.HasRecentLoaded = true;
            var capturedRecent = value;
            var pathKey = ResolveJumpListKey(capturedRecent.Data);
            if (string.IsNullOrEmpty(pathKey)) return;
            _ = Task.Run(async () =>
            {
                try
                {
                    // App-published Tasks first (Firefox-style "Open new
                    // tab" / "New private window" / etc.). Skip separator
                    // markers when projecting into the VM.
                    var tasks = MenYou.Platform.Windows.JumpListReader.ReadTasks(pathKey);
                    var taskVms = tasks
                        .Where(t => !t.IsSeparator && !string.IsNullOrEmpty(t.Title))
                        .Select(t => new SearchResultViewModel.JumpTask(t.Title, t.Target, t.Arguments))
                        .ToList();
                    if (taskVms.Count > 0)
                    {
                        await Dispatcher.UIThread.InvokeAsync(() =>
                        {
                            foreach (var t in taskVms) capturedRecent.Tasks.Add(t);
                        });
                    }

                    var items = MenYou.Platform.Windows.JumpListReader.ReadRecent(pathKey, 8);
                    if (items.Count == 0) return;
                    var vms = items
                        .Select(d => new SearchResultViewModel.RecentDestination(d.Path, d.DisplayName))
                        .ToList();
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        foreach (var vm in vms) capturedRecent.Recent.Add(vm);
                    });
                    // Load per-file shell icons in the background — keeps
                    // the dropdown responsive while the list pops into
                    // view immediately with the names.
                    foreach (var vm in vms)
                    {
                        if (string.IsNullOrEmpty(vm.Path)) continue;
                        try
                        {
                            var bmp = await _icons.GetIconForPathAsync(vm.Path);
                            if (bmp is null) continue;
                            await Dispatcher.UIThread.InvokeAsync(() => vm.Icon = bmp);
                        }
                        catch { }
                    }
                }
                catch { }
            });
        }
    }

    /// Picks the right identifier for a JumpList lookup. The Recent
    /// destination files Windows writes are keyed by the *implicit* AUMID
    /// the shell derives — not the .exe path, not what Get-StartApps
    /// publishes. For .lnk shortcuts we ask IApplicationResolver (the
    /// undocumented shell COM Open-Shell uses) to compute the same AUMID
    /// Explorer used when writing recent docs. Falls through to a UWP's
    /// own AUMID or the raw target path otherwise.
    private static string ResolveJumpListKey(MenYou.Models.SearchResult r)
    {
        if (!string.IsNullOrEmpty(r.Aumid)) return r.Aumid!;
        var path = r.TargetPath;
        if (string.IsNullOrEmpty(path)) return string.Empty;
        if (path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
        {
            var resolved = MenYou.Platform.Windows.JumpListReader.GetAppIdForShortcut(path);
            if (!string.IsNullOrEmpty(resolved)) return resolved!;
        }
        return path;
    }

    [RelayCommand]
    public void OpenRecent(SearchResultViewModel.RecentDestination dest)
    {
        if (dest is null || string.IsNullOrWhiteSpace(dest.Path)) return;
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = dest.Path,
                UseShellExecute = true,
            });
            _launcher.NotifyLaunched();
        }
        catch { }
    }

    [RelayCommand]
    public void OpenTask(SearchResultViewModel.JumpTask task)
    {
        if (task is null || string.IsNullOrWhiteSpace(task.Target)) return;
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = task.Target,
                Arguments = task.Arguments ?? string.Empty,
                UseShellExecute = true,
            });
            _launcher.NotifyLaunched();
        }
        catch { }
    }

    /// True only when the selected result points at something the shell
    /// can run via the `runas` verb. UWP / Control Panel tasks aren't
    /// elevatable by us. `.lnk` is included because SearchService stores
    /// `SourceLnkPath ?? TargetPath`, so most discovered apps surface as
    /// their shortcut path (Brave → Brave.lnk) — the shell still elevates
    /// the resolved target when you ShellExecute the .lnk with runas.
    public bool CanRunAsAdmin
    {
        get
        {
            var r = Selected?.Data;
            if (r is null) return false;
            if (r.Kind is SearchResultKind.PackagedApp or SearchResultKind.ControlPanelTask) return false;
            var path = r.TargetPath;
            if (string.IsNullOrWhiteSpace(path)) return false;
            var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
            return ext is ".exe" or ".bat" or ".cmd" or ".msc" or ".lnk";
        }
    }

    /// True when the selected result has a real filesystem target we can
    /// reveal in Explorer. URI-scheme targets (ms-settings:, Control Panel
    /// tasks) don't qualify.
    public bool CanOpenFileLocation
    {
        get
        {
            var path = Selected?.Data.TargetPath;
            return !string.IsNullOrWhiteSpace(path) && System.IO.File.Exists(path);
        }
    }

    public SearchViewModel(ISearchService search, IShellLauncher launcher, IIconService icons, IRecentItemsService recent)
    {
        _search = search;
        _launcher = launcher;
        _icons = icons;
        _recent = recent;
    }

    partial void OnQueryChanged(string value) => _ = RunSearchAsync(value);

    private async Task RunSearchAsync(string q)
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        try
        {
            if (string.IsNullOrWhiteSpace(q))
            {
                IsSearching = false;
                Results.Clear();
                return;
            }
            IsSearching = true;
            await Task.Delay(80, token);
            // SearchService loops over every discovered .lnk + UWP +
            // every Settings deep-link + every Control Panel task and
            // ranks each — pure CPU work without internal awaits, so an
            // `await` here would synchronously eat the UI thread for
            // hundreds of milliseconds on common queries. Hop to the
            // thread pool explicitly, then merge results back on UI.
            var results = await Task.Run(() => _search.SearchAsync(q, token), token);
            if (token.IsCancellationRequested) return;
            // Cap the rendered result list — the search returns score-
            // sorted entries, and beyond ~60 the user is never going to
            // scroll anyway. Each extra item costs an
            // ObservableCollection notification + a list-container
            // generation cycle.
            const int Cap = 60;
            var capped = results.Count > Cap ? results.Take(Cap).ToList() : results;
            Results.Clear();
            foreach (var r in capped) Results.Add(new SearchResultViewModel(r, this));
            Selected = Results.FirstOrDefault();
            IsSearching = false;
            _ = Task.Run(() => LoadIconsAsync(token), token);
        }
        catch (OperationCanceledException) { /* superseded — leave IsSearching to the newer call */ }
    }

    private async Task LoadIconsAsync(CancellationToken token)
    {
        // Snapshot the list since Results can be mutated mid-iteration if
        // the user keeps typing. Each tile gets its icon independently;
        // we deliberately don't fail the whole pass on one extraction
        // error.
        var snapshot = Results.ToList();
        foreach (var vm in snapshot)
        {
            if (token.IsCancellationRequested) return;
            try
            {
                var bmp = await _icons.GetIconForSearchResultAsync(vm.Data);
                if (bmp is null || token.IsCancellationRequested) continue;
                await Dispatcher.UIThread.InvokeAsync(() => vm.Icon = bmp);
            }
            catch
            {
                // Skip this entry — letter avatar stays as fallback.
            }
        }
    }

    [RelayCommand]
    public void LaunchSelected()
    {
        var vm = Selected ?? Results.FirstOrDefault();
        if (vm is null) return;
        Launch(vm);
    }

    [RelayCommand]
    public void Launch(SearchResultViewModel vm)
    {
        var r = vm.Data;
        // UWP results route through the same explorer.exe shell:AppsFolder
        // path the rest of MenYou uses for packaged apps — ShellExecute
        // on a bare AUMID doesn't work for unpackaged callers.
        if (r.Kind == SearchResultKind.PackagedApp && !string.IsNullOrEmpty(r.Aumid))
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"shell:AppsFolder\\{r.Aumid}",
                    UseShellExecute = true,
                });
                _launcher.NotifyLaunched();
                RecordRecentApp(r);
            }
            catch { }
            return;
        }
        if (r.Kind == SearchResultKind.ControlPanelTask)
        {
            // Re-navigate the All Tasks namespace and invoke the verb —
            // passing the shell-IDL Path to explorer.exe routes to
            // Documents instead of the task itself.
            MenYou.Platform.Windows.ControlPanelEnumerator.LaunchTask(r.Title);
            _launcher.NotifyLaunched();
            return;
        }
        if (!string.IsNullOrWhiteSpace(r.TargetPath))
        {
            _launcher.Launch(r.TargetPath!, r.Arguments);
            RecordRecentApp(r);
        }
    }

    /// Records App / PackagedApp launches that came through search into the
    /// recent-apps list. The tile paths launch via ShellLauncher.Launch(AppEntry),
    /// which records; search used the path-based overload (and a direct
    /// Process.Start for UWP), which doesn't — so apps opened by searching never
    /// appeared in Recent. Keyed on the discovery AppId so RebuildRecent's
    /// FindById resolves it back to a tile. File / Folder / Command /
    /// Control-Panel results carry no AppId and are intentionally skipped.
    private void RecordRecentApp(SearchResult r)
    {
        if (r.Kind is SearchResultKind.App or SearchResultKind.PackagedApp
            && !string.IsNullOrEmpty(r.AppId))
            _recent.RecordLaunch(r.AppId);
    }

    // The Selected-based commands (driven by the Win7 / Classic2 search
    // context panel) and the per-item context-menu commands (added on
    // SearchResultViewModel for the Win11 / Cinnamon result lists) share
    // one implementation each — the *Selected variants just forward the
    // current selection into the parameterized methods below.
    [RelayCommand]
    public void OpenSelectedFileLocation() => OpenFileLocation(Selected);

    [RelayCommand]
    public void OpenSelectedAsAdmin() => LaunchAsAdmin(Selected);

    /// Reveals <paramref name="vm"/>'s target highlighted in Explorer.
    /// No-ops on results without a real on-disk file. Called both by
    /// OpenSelectedFileLocation and by the result row's context menu.
    public void OpenFileLocation(SearchResultViewModel? vm)
    {
        var path = vm?.Data.TargetPath;
        if (string.IsNullOrWhiteSpace(path) || !System.IO.File.Exists(path)) return;
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{path}\"",
                UseShellExecute = true,
            });
            _launcher.NotifyLaunched();
        }
        catch { }
    }

    /// Launches <paramref name="vm"/>'s target with the `runas` verb so
    /// Windows shows the UAC prompt. No-ops on results without a runnable
    /// path. Called both by OpenSelectedAsAdmin and by the result row's
    /// context menu.
    public void LaunchAsAdmin(SearchResultViewModel? vm)
    {
        var r = vm?.Data;
        if (r is null || string.IsNullOrWhiteSpace(r.TargetPath)) return;
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = r.TargetPath,
                Arguments = r.Arguments ?? string.Empty,
                UseShellExecute = true,
                Verb = "runas",
            });
            _launcher.NotifyLaunched();
            RecordRecentApp(r);
        }
        catch { }
    }

    public void Clear()
    {
        Query = "";
        Results.Clear();
        Selected = null;
    }

    /// Walks the Results list by <paramref name="delta"/> steps from the
    /// current Selected and clamps to [0, count-1]. Drives Up/Down arrow
    /// navigation in the menu: the user is typing in the search box, so
    /// the arrow keys never reach the ListBox naturally — StartMenuWindow
    /// intercepts them and forwards here. Returns true when selection
    /// actually changed (so the caller knows to mark the key handled).
    public bool MoveSelection(int delta)
    {
        if (Results.Count == 0) return false;
        var current = Selected is null ? -1 : Results.IndexOf(Selected);
        var next = current < 0
            ? (delta > 0 ? 0 : Results.Count - 1)
            : Math.Clamp(current + delta, 0, Results.Count - 1);
        if (next == current) return false;
        Selected = Results[next];
        return true;
    }
}
