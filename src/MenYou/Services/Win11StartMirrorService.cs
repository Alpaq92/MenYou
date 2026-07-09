using System.Runtime.Versioning;
using MenYou.Platform.Windows;

namespace MenYou.Services;

/// Live one-way mirror of the Windows 11 Start menu pin list into
/// MenYou's Pinned section.
///
/// Architecture:
///   1. FileSystemWatcher on
///      %LocalAppData%\Packages\Microsoft.Windows.StartMenuExperienceHost_cw5n1h2txyewy\LocalState\start2.bin
///      catches every pin/unpin/reorder in the system Start menu.
///   2. A 500 ms debounce coalesces the multiple writes Windows fires per
///      change and also absorbs unrelated rewrites (theme changes,
///      housekeeping).
///   3. Each tick calls Win11StartLayoutReader to shell out to
///      Export-StartLayout, then hands the resolved AppEntry IDs to
///      IPinService.ReplaceAll.
///   4. PinService.ReplaceAll skips the write if nothing changed, so the
///      common "rewrite for a non-pin reason" case is free.
///
/// Why not parse start2.bin directly: it's an undocumented proprietary
/// blob (E2 7A E1 4B ... header). No public parser exists. Every Win 11
/// Start replacement on the market sidesteps it the same way we do.
[SupportedOSPlatform("windows")]
public sealed class Win11StartMirrorService : IWin11StartMirror
{
    private static readonly string PinFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Packages",
        "Microsoft.Windows.StartMenuExperienceHost_cw5n1h2txyewy",
        "LocalState",
        "start2.bin");

    private static readonly TimeSpan DebounceWindow = TimeSpan.FromMilliseconds(500);

    private readonly ISettingsService _settings;
    private readonly IPinService _pins;
    private readonly IAppDiscoveryService _discovery;

    private FileSystemWatcher? _watcher;
    private Timer? _debounce;
    private CancellationTokenSource? _refreshCts;
    private bool _disposed;
    private readonly object _lock = new();

    public bool IsRunning => _watcher is not null;
    public bool LastReadSucceeded { get; private set; } = true;
    public string? LastError { get; private set; }

    public event Action? StatusChanged;

    public Win11StartMirrorService(
        ISettingsService settings,
        IPinService pins,
        IAppDiscoveryService discovery)
    {
        _settings = settings;
        _pins = pins;
        _discovery = discovery;
    }

    public async Task StartAsync()
    {
        if (!_settings.Current.MirrorWindowsStart) return;
        if (_watcher is not null) return;
        if (_disposed) return;

        var dir = Path.GetDirectoryName(PinFilePath);
        var file = Path.GetFileName(PinFilePath);
        if (dir is null || !Directory.Exists(dir))
        {
            LastReadSucceeded = false;
            LastError = "StartMenuExperienceHost package directory not found.";
            StatusChanged?.Invoke();
            return;
        }

        _watcher = new FileSystemWatcher(dir, file)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime,
            EnableRaisingEvents = true,
        };
        _watcher.Changed += OnWatcherFired;
        _watcher.Created += OnWatcherFired;
        _watcher.Renamed += OnWatcherFired;

        // Kick off an initial read so the user sees the mirror result on
        // first menu open without waiting for the next system pin change.
        await RefreshInternalAsync();
    }

    public void Stop()
    {
        lock (_lock)
        {
            if (_watcher is not null)
            {
                _watcher.EnableRaisingEvents = false;
                _watcher.Changed -= OnWatcherFired;
                _watcher.Created -= OnWatcherFired;
                _watcher.Renamed -= OnWatcherFired;
                _watcher.Dispose();
                _watcher = null;
            }
            _debounce?.Dispose();
            _debounce = null;
            _refreshCts?.Cancel();
        }
    }

    public Task RefreshNowAsync() => RefreshInternalAsync();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }

    private List<string> ResolveStrandedUwpPins(List<string> exported, HashSet<string> excluded)
    {
        var inExport = new HashSet<string>(exported, StringComparer.OrdinalIgnoreCase);
        var apps = _discovery.GetAllAppsAsync().GetAwaiter().GetResult();
        var byId = new Dictionary<string, MenYou.Models.AppEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in apps) byId[a.Id] = a;

        var preserved = new List<string>();
        foreach (var pin in _settings.Current.Pinned.OrderBy(p => p.Order))
        {
            if (inExport.Contains(pin.AppId)) continue;
            if (excluded.Contains(pin.AppId)) continue;
            if (!byId.TryGetValue(pin.AppId, out var entry)) continue;
            // Only UWP/packaged entries are preserved. .lnk pins absent
            // from export are real unpins (Export-StartLayout is reliable
            // for Win32 .lnk entries on every SKU we've seen).
            if (string.IsNullOrEmpty(entry.Aumid) && entry.Kind != MenYou.Models.AppEntryKind.PackagedApp)
                continue;
            preserved.Add(pin.AppId);
        }
        return preserved;
    }

    private void OnWatcherFired(object sender, FileSystemEventArgs e)
    {
        // Debounce: cancel the pending timer and restart it. Windows often
        // writes start2.bin two or three times in rapid succession per
        // change, and we don't want to spawn powershell once per write.
        lock (_lock)
        {
            _debounce?.Dispose();
            _debounce = new Timer(_ => _ = RefreshInternalAsync(), null, DebounceWindow, Timeout.InfiniteTimeSpan);
        }
    }

    private async Task RefreshInternalAsync()
    {
        // Cancel any in-flight refresh before starting a new one — if the
        // user pin-spammed in the system Start menu we only care about
        // the latest snapshot.
        CancellationTokenSource cts;
        lock (_lock)
        {
            _refreshCts?.Cancel();
            _refreshCts = new CancellationTokenSource();
            cts = _refreshCts;
        }

        try
        {
            var result = await Win11StartLayoutReader.ReadAsync(_discovery, cts.Token);
            if (cts.IsCancellationRequested) return;

            LastReadSucceeded = result.Success;
            LastError = result.Error;

            // An empty-but-"successful" export is indistinguishable from the
            // known Export-StartLayout flake (or from parsing a half-written
            // start2.bin): ParsePinnedList yields zero entries and Success
            // stays true. While we still hold pins, trusting it would wipe
            // the entire Pinned section AND prune every MirrorExclusion in
            // one sync. Treat it as a failed read — keep the last good pin
            // set; the next watcher tick re-syncs. (A genuinely empty pin
            // list on a machine where MenYou also has no pins still flows
            // through: there is nothing to lose then.)
            if (result.Success && result.AppIds.Count == 0 && _settings.Current.Pinned.Count > 0)
            {
                LastReadSucceeded = false;
                LastError = "Export-StartLayout returned an empty pin list; keeping the last good pin set.";
                StatusChanged?.Invoke();
                return;
            }

            if (result.Success)
            {
                // Apply the user's local exclusion list — items the user
                // unpinned from MenYou's context menu without touching the
                // Windows pin list. Also drop stale exclusions for items
                // that aren't in Windows' pin set anymore (re-pinning in
                // Windows should restore visibility, not leave the entry
                // permanently hidden).
                var exclusions = _settings.Current.MirrorExclusions;
                var pruned = exclusions.Where(id => result.AppIds.Contains(id, StringComparer.OrdinalIgnoreCase)).ToList();
                if (pruned.Count != exclusions.Count)
                {
                    _settings.Current.MirrorExclusions = pruned;
                    _settings.Save();
                }

                var excludedSet = new HashSet<string>(pruned, StringComparer.OrdinalIgnoreCase);
                var filtered = result.AppIds.Where(id => !excludedSet.Contains(id)).ToList();

                // Win 11 24H2's Export-StartLayout has been silently regressing
                // for UWP/packaged pins — it returns the .lnk entries but
                // omits things like Settings, even when those are pinned in
                // the system Start menu. Naively calling ReplaceAll would
                // wipe those UWP pins on every start2.bin write (which
                // Windows triggers for unrelated reasons too: theme tweaks,
                // housekeeping). Preserve previously-pinned UWP entries that
                // are still installed but missing from this export — treat
                // their absence as the cmdlet bug, not a user unpin. The
                // user still has MirrorExclusions to genuinely remove one.
                var preserved = ResolveStrandedUwpPins(filtered, excludedSet);
                var final = filtered.Concat(preserved).ToList();
                _pins.ReplaceAll(final);
            }

            StatusChanged?.Invoke();
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer refresh — drop this result silently.
        }
        catch (Exception ex)
        {
            LastReadSucceeded = false;
            LastError = ex.Message;
            StatusChanged?.Invoke();
        }
    }
}
