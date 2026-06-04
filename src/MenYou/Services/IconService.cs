using System.Runtime.Versioning;
using Avalonia.Media.Imaging;
using MenYou.Models;
using MenYou.Platform.Windows;

namespace MenYou.Services;

[SupportedOSPlatform("windows")]
public sealed class IconService : IIconService
{
    private readonly Dictionary<string, Bitmap?> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _lock = new(1, 1);

    public Bitmap? PlaceholderIcon => null;

    public Task<Bitmap?> GetIconAsync(AppEntry entry) => Task.Run(() => Extract(entry));

    public Task<Bitmap?> GetIconForPathAsync(string path) => Task.Run<Bitmap?>(() =>
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        if (_cache.TryGetValue(path, out var cached)) return cached;
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
        var cacheKey = entry.Id;
        lock (_cache)
        {
            if (_cache.TryGetValue(cacheKey, out var cached)) return cached;
        }

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

        lock (_cache) _cache[cacheKey] = bmp;
        return bmp;
    }

    private static string? ExpandEnv(string? path) =>
        string.IsNullOrEmpty(path) || !path.Contains('%')
            ? path
            : Environment.ExpandEnvironmentVariables(path);
}
