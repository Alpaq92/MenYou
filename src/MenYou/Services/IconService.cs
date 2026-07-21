using System.Runtime.Versioning;
using Avalonia.Media.Imaging;
using MenYou.Models;
using MenYou.Platform.Windows;

namespace MenYou.Services;

[SupportedOSPlatform("windows")]
public sealed class IconService : IIconService
{
    private readonly Dictionary<string, Bitmap?> _cache = new(StringComparer.OrdinalIgnoreCase);
    // Entry-icon extractions currently in flight, keyed by AppEntry.Id.
    // Guarantees exactly-once extraction under the parallel batch loader:
    // the Pinned/Recent batch and the Programs-tree batch run concurrently
    // on every cold start and overlap on most pinned/recent ids (verified
    // in review), so without this the same icon was extracted twice.
    // Losers block on the winner's Lazy instead of re-running COM.
    private readonly Dictionary<string, Lazy<Bitmap?>> _inflight = new(StringComparer.OrdinalIgnoreCase);

    public Bitmap? PlaceholderIcon => null;

    public Task<Bitmap?> GetIconAsync(AppEntry entry) => Task.Run(() => Extract(entry));

    /// <summary>
    /// Fans the batch across cores with Parallel.ForEachAsync calling the
    /// synchronous Extract directly — no per-item Task.Run hop.
    /// </summary>
    /// <remarks>
    /// One outer Task.Run keeps every worker off the caller's (UI) thread:
    /// ForEachAsync may run a fully-synchronous body inline on the calling
    /// thread, and a shell-COM extraction must never run there. Concurrent
    /// same-id requests (the Pinned/Recent batch overlaps the Programs-tree
    /// batch on every cold start) are collapsed to one extraction by the
    /// _inflight map inside Extract.
    /// </remarks>
    public Task LoadIconsAsync<T>(IReadOnlyList<T> items, Func<T, AppEntry> entryOf, Action<T, Bitmap> apply) =>
        Task.Run(async () =>
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var loaded = 0;
            // DOP capped below core count: extraction fans into third-party
            // shell extensions (icon handlers, AV overlays) that were never
            // hit concurrently before this batch existed, and the cold-boot
            // login storm is core-starved already. Half the cores (2..8)
            // keeps nearly all of the wall-clock win.
            var options = new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 2, 2, 8),
            };
            await Parallel.ForEachAsync(items, options, (item, _) =>
            {
                // Per-item isolation: one corrupt .ico or throwing shell
                // handler must not fault the whole batch — ForEachAsync
                // stops scheduling after an unhandled throw, and callers
                // discard this Task, so the rest of the menu would silently
                // stay on cog placeholders. Exception (not a narrower type)
                // is deliberate: the failure surface spans COM, GDI+ and
                // third-party shell extensions; the tile keeps its cog.
                var entry = entryOf(item);
                try
                {
                    var bmp = Extract(entry);
                    if (bmp is not null)
                    {
                        Interlocked.Increment(ref loaded);
                        apply(item, bmp);
                    }
                }
                catch (Exception ex)
                {
                    HookTrace.Log($"Icons: apply failed for '{entry.DisplayName}' — {ex.Message}");
                }
                return ValueTask.CompletedTask;
            });
            // The icon fill is the visible tail of a cold start (the data
            // paints instantly from cache; tiles show cogs until this
            // completes), so its duration is a first-class startup metric —
            // OPTIMIZATION.md's methodology reads it from this line.
            HookTrace.Log($"Icons: filled {loaded}/{items.Count} tiles in {sw.ElapsedMilliseconds} ms");
        });

    public Task<Bitmap?> GetIconForPathAsync(string path) => Task.Run<Bitmap?>(() =>
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        // Locked read: the batch loader hammers _cache from parallel workers,
        // so the old lock-free TryGetValue here became a read-during-write.
        lock (_cache)
        {
            if (_cache.TryGetValue(path, out var cached)) return cached;
        }
        var bmp = IconExtractor.ExtractForFile(path);
        lock (_cache) _cache[path] = bmp;
        return bmp;
    });

    public Task<Bitmap?> GetLargeIconForSearchResultAsync(SearchResult r) => Task.Run<Bitmap?>(() =>
    {
        // For the context-panel preview only — skip caching since the
        // selected item changes rarely. UWP / URI / Control-Panel-task
        // results still go through the regular extractor (no large
        // variant exists for those; the existing 32×32 result is fine
        // because the cog fallback or the AUMID-resolved bitmap already
        // carries enough detail).
        if (r.Kind == SearchResultKind.PackagedApp && !string.IsNullOrEmpty(r.Aumid))
            return IconExtractor.ExtractForAumid(r.Aumid);
        if (string.IsNullOrWhiteSpace(r.TargetPath) || IsUriScheme(r.TargetPath))
            return null;
        if (r.Kind == SearchResultKind.ControlPanelTask) return null;
        // Pull SHIL_JUMBO (256×256) and let the Image element downscale
        // to 48 — supersampling reads crisp, unlike SHIL_EXTRALARGE's
        // 48 that has to be upscaled from a small embedded variant
        // when the .exe doesn't ship a native 48 size.
        return IconExtractor.ExtractLargeForFile(r.TargetPath, jumbo: true);
    });

    public Task<Bitmap?> GetIconForSearchResultAsync(SearchResult r) => Task.Run<Bitmap?>(() =>
    {
        // Cache key per result kind+target so two queries returning the
        // same app don't re-extract from shell32.
        var cacheKey = r.Kind switch
        {
            SearchResultKind.PackagedApp => $"uwp:{r.Aumid}",
            _                            => $"path:{r.IconPath}|{r.IconIndex}|{r.TargetPath}",
        };
        lock (_cache)
        {
            if (_cache.TryGetValue(cacheKey, out var cached)) return cached;
        }

        Bitmap? bmp = null;
        if (r.Kind == SearchResultKind.PackagedApp && !string.IsNullOrEmpty(r.Aumid))
        {
            bmp = IconExtractor.ExtractForAumid(r.Aumid);
        }
        else
        {
            // Same fallback chain as AppEntry icons: explicit IconPath
            // (env-var expanded) + IconIndex, then SHGetFileInfo on the
            // launch target. Skip SHGetFileInfo when the target is a URI
            // scheme (ms-settings:, shell:, etc.) — SHGetFileInfo returns
            // the generic "blank page" icon for those, which is uglier
            // than letting SearchResultViewModel show its cog fallback.
            var iconPath = ExpandEnv(r.IconPath);
            if (!string.IsNullOrWhiteSpace(iconPath) && File.Exists(iconPath))
                bmp = IconExtractor.ExtractAvaloniaBitmap(iconPath, r.IconIndex);
            if (bmp is null
                && !string.IsNullOrWhiteSpace(r.TargetPath)
                && !IsUriScheme(r.TargetPath)
                && r.Kind != SearchResultKind.ControlPanelTask)
                bmp = IconExtractor.ExtractForFile(r.TargetPath);
        }

        lock (_cache) _cache[cacheKey] = bmp;
        return bmp;
    });

    /// True when the path is a Windows URI scheme rather than a filesystem
    /// path — SHGetFileInfo can't return a real icon for these and falls
    /// back to a generic page icon we'd rather replace with our cog.
    private static bool IsUriScheme(string path)
    {
        var colon = path.IndexOf(':');
        if (colon <= 0) return false;
        var prefix = path[..colon];
        return prefix.Equals("ms-settings", StringComparison.OrdinalIgnoreCase)
            || prefix.Equals("shell", StringComparison.OrdinalIgnoreCase)
            || prefix.Equals("ms-availablenetworks", StringComparison.OrdinalIgnoreCase)
            || prefix.Equals("microsoft-edge", StringComparison.OrdinalIgnoreCase)
            || prefix.Equals("http", StringComparison.OrdinalIgnoreCase)
            || prefix.Equals("https", StringComparison.OrdinalIgnoreCase);
    }

    private Bitmap? Extract(AppEntry entry)
    {
        Lazy<Bitmap?> lazy;
        lock (_cache)
        {
            if (_cache.TryGetValue(entry.Id, out var cached)) return cached;
            if (!_inflight.TryGetValue(entry.Id, out lazy!))
            {
                lazy = new Lazy<Bitmap?>(() => ExtractCore(entry),
                    LazyThreadSafetyMode.ExecutionAndPublication);
                _inflight[entry.Id] = lazy;
            }
        }

        // Outside the lock: the winner runs the COM extraction; losers block
        // here until it finishes instead of extracting the same icon again.
        // A throwing extraction is treated as icon-less and cached as null so
        // it isn't retried on every open (the tile keeps its cog fallback).
        // Exception (not narrower) is deliberate — COM/GDI+/shell-extension
        // failures surface as many types and all mean the same "no icon".
        Bitmap? bmp;
        try { bmp = lazy.Value; }
        catch (Exception ex)
        {
            HookTrace.Log($"Icons: extraction failed for '{entry.DisplayName}' — {ex.Message}");
            bmp = null;
        }
        lock (_cache)
        {
            _cache[entry.Id] = bmp;
            _inflight.Remove(entry.Id);
        }
        return bmp;
    }

    private static Bitmap? ExtractCore(AppEntry entry)
    {
        Bitmap? bmp = null;
        // UWP / packaged apps: ask the shell for the tile icon associated
        // with shell:AppsFolder\<AUMID>. Don't fall through to TargetPath
        // because for PackagedApp entries TargetPath IS the AUMID and
        // SHGetFileInfo on a raw AUMID returns the generic .exe icon.
        if (entry.Kind == AppEntryKind.PackagedApp && !string.IsNullOrEmpty(entry.Aumid))
        {
            bmp = IconExtractor.ExtractForAumid(entry.Aumid);
        }

        // .lnk IconLocation is stored unexpanded (e.g. "%windir%\explorer.exe"
        // for the File Explorer pin). Expand before checking File.Exists or
        // SHGetFileInfo silently returns the generic file icon.
        //
        // Extract the NATIVE 32 px icon (the standard large shell-icon size)
        // and render it 1:1 in the 32 px tile. Pulling a larger variant and
        // downscaling (GetImage 48/256) reads softer at 100 % scaling — and
        // for apps that only ship a small icon it bakes in an upscale — so
        // native 32 is the crisp default here.
        if (bmp is null)
        {
            var iconPath = ExpandEnv(entry.IconPath);
            if (!string.IsNullOrWhiteSpace(iconPath) && File.Exists(iconPath))
                bmp = IconExtractor.ExtractAvaloniaBitmap(iconPath, entry.IconIndex);
        }
        if (bmp is null && !string.IsNullOrWhiteSpace(entry.TargetPath) &&
            entry.Kind != AppEntryKind.PackagedApp)
            bmp = IconExtractor.ExtractForFile(entry.TargetPath);
        if (bmp is null && !string.IsNullOrWhiteSpace(entry.SourceLnkPath))
            bmp = IconExtractor.ExtractForFile(entry.SourceLnkPath);

        return bmp;
    }

    private static string? ExpandEnv(string? path) =>
        string.IsNullOrEmpty(path) || !path.Contains('%')
            ? path
            : Environment.ExpandEnvironmentVariables(path);
}
