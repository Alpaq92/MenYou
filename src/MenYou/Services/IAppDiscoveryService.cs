using MenYou.Models;

namespace MenYou.Services;

public interface IAppDiscoveryService
{
    /// Returns the root of the All Programs tree, merged from the per-user and
    /// common Start Menu directories.
    Task<MenuFolder> BuildProgramsTreeAsync(CancellationToken ct = default);

    /// Flat enumeration of every discovered app for search and quick lookup.
    Task<IReadOnlyList<AppEntry>> GetAllAppsAsync(CancellationToken ct = default);

    AppEntry? FindById(string id);

    /// Eagerly populates the app list FROM THE ON-DISK CACHE only (no live
    /// scan), if the cache is enabled and its fingerprint still matches.
    /// Pure file I/O — no shell COM — so it can run at the very start of
    /// startup without the COM contention a live scan would hit, making the
    /// menu's data ready almost immediately. A cache miss is a no-op; the
    /// regular warm-up then does the live scan at idle.
    Task PreloadFromCacheAsync();

    /// Raised (on a thread-pool thread) when a background live scan replaces
    /// data that was initially served from the on-disk cache and the result
    /// actually differs — so the menu can rebuild its surfaces against the
    /// fresh app list. Not raised when the cache was already up to date.
    event Action? Refreshed;

    /// Raised at the start (true) and end (false) of a background catch-up scan
    /// — one revalidating a stale-painted or just-changed app list — so the UI
    /// can show a subtle "updating apps" hint. NOT raised for a routine
    /// confirming backstop after a fresh cache hit.
    event Action<bool>? RefreshingChanged;
}
