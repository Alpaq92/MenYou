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
    private readonly IPinService _pin;
    private CancellationTokenSource? _cts;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasQuery))]
    private string _query = "";

    public bool HasQuery => !string.IsNullOrWhiteSpace(Query);

    /// True while a search is in flight — drives the indeterminate
    /// progress bar in the search results overlay so the user gets
    /// feedback instead of a blank panel during the 80 ms debounce +
    /// thread-pool work. Also flips the overlay header between the
    /// "Loading" (searching) and "Search results" (settled) states.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowResultsHeader))]
    private bool _isSearching;

    /// Drives the "Search results" (Znalezione wyniki) overlay header,
    /// shown ONLY once a search has settled (not in flight) with at least
    /// one hit. While searching, the "Loading" header shows instead (bound
    /// to IsSearching); when the query is empty or nothing matched, neither
    /// header shows. Re-raised from ReconcileResults whenever the result
    /// count changes.
    public bool ShowResultsHeader => !IsSearching && Results.Count > 0;

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

        // Lazily load the per-app JumpList (published Tasks + Recent
        // destinations) — shared with the right-click context menu, which
        // awaits the same cached load Task to fill its app-specific items.
        _ = EnsureJumpListLoadedAsync(value);
    }

    /// Loads a result's per-app JumpList (app-published Tasks + Recent
    /// destinations) exactly once, caching the in-flight Task on the result
    /// so the selection detail panel and the right-click context menu share a
    /// single load. Returns the cached Task on repeat calls — await it to know
    /// when <see cref="SearchResultViewModel.Tasks"/> /
    /// <see cref="SearchResultViewModel.Recent"/> are populated. Recent-file
    /// icons stream in afterward.
    public Task EnsureJumpListLoadedAsync(SearchResultViewModel vm)
    {
        if (vm.JumpListLoad is not null) return vm.JumpListLoad;
        var pathKey = ResolveJumpListKey(vm.Data);
        if (string.IsNullOrEmpty(pathKey)) return vm.JumpListLoad = Task.CompletedTask;
        return vm.JumpListLoad = LoadJumpListAsync(vm, pathKey);
    }

    private async Task LoadJumpListAsync(SearchResultViewModel vm, string pathKey)
    {
        try
        {
            // App-published Tasks (Firefox "Open new tab", Brave "New window" /
            // "New private window", …) + per-app Recent destinations — read off
            // the UI thread. Separator markers are dropped from Tasks.
            var (taskVms, recentVms) = await Task.Run(() =>
            {
                var tasks = MenYou.Platform.Windows.JumpListReader.ReadTasks(pathKey)
                    .Where(t => !t.IsSeparator && !string.IsNullOrEmpty(t.Title))
                    .Select(t => new SearchResultViewModel.JumpTask(t.Title, t.Target, t.Arguments))
                    .ToList();
                // Load the full recent list (NOT the context-menu cap): the
                // Win 7 / Classic side panel binds to vm.Recent and shows all of
                // it. The right-click menu caps separately (ContextMenuRecentCount).
                var recent = MenYou.Platform.Windows.JumpListReader.ReadRecent(pathKey, 50)
                    .Select(d => new SearchResultViewModel.RecentDestination(d.Path, d.DisplayName))
                    .ToList();
                return (taskVms: tasks, recentVms: recent);
            });

            // Publish both lists in one UI hop so anything awaiting this Task
            // (the context menu) sees the fully-populated collections at once.
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                foreach (var t in taskVms) vm.Tasks.Add(t);
                foreach (var r in recentVms) vm.Recent.Add(r);
            });

            // Stream per-file shell icons in afterward — keeps the list snappy
            // while the names are already on screen.
            foreach (var r in recentVms)
            {
                if (string.IsNullOrEmpty(r.Path)) continue;
                try
                {
                    var bmp = await _icons.GetIconForPathAsync(r.Path);
                    if (bmp is null) continue;
                    await Dispatcher.UIThread.InvokeAsync(() => r.Icon = bmp);
                }
                catch { }
            }
        }
        catch { }
    }

    /// Picks the right identifier for a JumpList lookup. The Recent
    /// destination files Windows writes are keyed by the *implicit* AUMID
    /// the shell derives — not the .exe path, not what Get-StartApps
    /// publishes. For .lnk shortcuts we ask IApplicationResolver (the
    /// undocumented shell COM Open-Shell uses) to compute the same AUMID
    /// Explorer used when writing recent docs. Falls through to a UWP's
    /// own AUMID or the raw target path otherwise.
    private static string ResolveJumpListKey(MenYou.Models.SearchResult r) =>
        // Search stores SourceLnkPath ?? TargetPath in TargetPath, so the .lnk
        // (when there is one) is already in TargetPath — pass it as both the
        // lnk and the fallback path. Shared with the app context menus.
        MenYou.Platform.Windows.JumpListReader.ResolveKey(r.Aumid, r.TargetPath, r.TargetPath);

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

    public SearchViewModel(ISearchService search, IShellLauncher launcher, IIconService icons, IRecentItemsService recent, IPinService pin)
    {
        _search = search;
        _launcher = launcher;
        _icons = icons;
        _recent = recent;
        _pin = pin;
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
                // Clear BEFORE flipping IsSearching: the IsSearching setter
                // re-raises ShowResultsHeader, which reads Results.Count — so
                // the list must already be empty or the header would linger.
                // The explicit raise covers the case where IsSearching was
                // already false (clearing an already-settled query), where the
                // setter wouldn't fire a change notification.
                Results.Clear();
                IsSearching = false;
                OnPropertyChanged(nameof(ShowResultsHeader));
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
            ReconcileResults(capped);
            Selected = Results.FirstOrDefault();
            IsSearching = false;
            _ = Task.Run(() => LoadIconsAsync(token), token);
        }
        catch (OperationCanceledException) { /* superseded — leave IsSearching to the newer call */ }
    }

    /// Diff-aware update of <see cref="Results"/>. Clearing and re-adding the
    /// whole list on every keystroke meant a click landing while a
    /// still-settling search (the 80 ms debounce + thread-pool work) committed
    /// its results hit a row being torn down and rebuilt — the pointer press
    /// was dropped, so the result "needed 2-3 clicks to launch". Reuse the
    /// existing view-model (and its already-loaded icon) for any result
    /// unchanged at its position; only genuinely changed rows are swapped, so a
    /// stable result stays a stable click target across keystrokes.
    private void ReconcileResults(IReadOnlyList<SearchResult> desired)
    {
        while (Results.Count > desired.Count)
            Results.RemoveAt(Results.Count - 1);
        for (var i = 0; i < desired.Count; i++)
        {
            if (i < Results.Count)
            {
                if (!SameResult(Results[i].Data, desired[i]))
                    Results[i] = new SearchResultViewModel(desired[i], this, _pin);
            }
            else
            {
                Results.Add(new SearchResultViewModel(desired[i], this, _pin));
            }
        }
        // Result count may have changed — refresh the "Search results" header
        // gate. (The main search path flips IsSearching=false right after this,
        // which also re-raises it, but a same-count reconcile that only swaps
        // rows wouldn't — so raise it explicitly here too.)
        OnPropertyChanged(nameof(ShowResultsHeader));
    }

    private static bool SameResult(SearchResult a, SearchResult b) =>
        a.Kind == b.Kind
        && string.Equals(a.Title, b.Title, StringComparison.Ordinal)
        && string.Equals(a.AppId ?? a.TargetPath, b.AppId ?? b.TargetPath, StringComparison.OrdinalIgnoreCase)
        // Launch-critical fields: if these differ, the row must NOT be reused —
        // a stale view-model would launch the old target.
        && string.Equals(a.Arguments, b.Arguments, StringComparison.Ordinal)
        && string.Equals(a.Aumid, b.Aumid, StringComparison.OrdinalIgnoreCase);

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
