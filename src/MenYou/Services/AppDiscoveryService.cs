using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using MenYou.Models;
using MenYou.Platform.Windows;

namespace MenYou.Services;

[SupportedOSPlatform("windows")]
public sealed class AppDiscoveryService : IAppDiscoveryService
{
    private readonly Dictionary<string, AppEntry> _byId = new(StringComparer.OrdinalIgnoreCase);
    private MenuFolder? _root;
    private List<AppEntry>? _flat;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly List<FileSystemWatcher> _watchers = new();
    private Timer? _invalidateDebounce;
    private readonly ISettingsService _settings;

    public event Action? Refreshed;

    private static readonly string[] StartMenuRoots =
    {
        Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
        Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
    };

    public AppDiscoveryService(ISettingsService settings)
    {
        _settings = settings;

        // Watch both Start Menu roots so newly installed apps (Windows
        // installers drop a .lnk into one of these) appear without a
        // MenYou restart. Changes are debounced because installers
        // often write several files in rapid succession.
        foreach (var root in StartMenuRoots.Where(Directory.Exists))
        {
            try
            {
                var w = new FileSystemWatcher(root)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName
                                 | NotifyFilters.DirectoryName
                                 | NotifyFilters.LastWrite,
                    EnableRaisingEvents = true,
                };
                w.Created += OnStartMenuChanged;
                w.Deleted += OnStartMenuChanged;
                w.Renamed += OnStartMenuChanged;
                w.Changed += OnStartMenuChanged;
                _watchers.Add(w);
            }
            catch { /* best-effort; if the watcher won't attach we just fall back to startup-only discovery */ }
        }
    }

    private void OnStartMenuChanged(object sender, FileSystemEventArgs e)
    {
        // Debounce — installers commonly produce multiple events in a
        // short window. A 750 ms window covers Windows' standard MSI
        // shortcut placement without making single-file edits laggy.
        _invalidateDebounce?.Dispose();
        _invalidateDebounce = new Timer(_ =>
        {
            _lock.Wait();
            try
            {
                _root = null;
                _flat = null;
                _byId.Clear();
            }
            finally { _lock.Release(); }
        }, null, TimeSpan.FromMilliseconds(750), Timeout.InfiniteTimeSpan);
    }

    public async Task<MenuFolder> BuildProgramsTreeAsync(CancellationToken ct = default)
    {
        await EnsureLoadedAsync(ct);
        return _root!;
    }

    public async Task<IReadOnlyList<AppEntry>> GetAllAppsAsync(CancellationToken ct = default)
    {
        await EnsureLoadedAsync(ct);
        return _flat!;
    }

    public AppEntry? FindById(string id) => _byId.GetValueOrDefault(id);

    public async Task PreloadFromCacheAsync()
    {
        if (!_settings.Current.UseDiscoveryCache) return;
        if (_root is not null && _flat is not null) return;

        // Fingerprint walk + JSON load run on the thread pool. CRITICAL:
        // ConfigureAwait(false) keeps the continuation OFF the UI thread —
        // otherwise it queues behind the ~1.5 s of UI-thread startup work and
        // doesn't resume until ApplicationIdle, by which point the warm-up has
        // already loaded the data and this early-returns (i.e. the preload
        // gives no benefit at all). None of this touches UI state, so off-thread
        // is correct: Apply mutates plain data under the lock.
        var fingerprint = await Task.Run(ComputeFingerprint).ConfigureAwait(false);
        var cached = await Task.Run(DiscoveryCache.TryLoad).ConfigureAwait(false);
        if (cached is null || cached.Fingerprint != fingerprint) return;

        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_root is not null && _flat is not null) return;
            Apply(cached.Root, cached.Flat);
            HookTrace.Log($"Discovery: eager cache preload ({cached.Flat.Count} apps, {cached.Root.Folders.Count} folders)");
            // Run the live backstop, but after a settle delay so its COM work
            // doesn't fight the rest of startup (the menu already has data).
            _ = RefreshLiveInBackgroundAsync(settleDelayMs: 2500);
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task EnsureLoadedAsync(CancellationToken ct)
    {
        if (_root is not null && _flat is not null) return;
        await _lock.WaitAsync(ct);
        try
        {
            if (_root is not null && _flat is not null) return;

            // Cache path: if enabled and the on-disk snapshot's fingerprint
            // still matches the live Start Menu (a fast, COM-free filesystem
            // signature), serve it for an instant cold paint — then run a
            // background live scan as a backstop for changes the .lnk
            // fingerprint can't see (e.g. a Store app with no shortcut).
            if (_settings.Current.UseDiscoveryCache)
            {
                var fingerprint = await Task.Run(ComputeFingerprint, ct);
                var cached = DiscoveryCache.TryLoad();
                if (cached is not null && cached.Fingerprint == fingerprint)
                {
                    Apply(cached.Root, cached.Flat);
                    HookTrace.Log($"Discovery: cache hit ({cached.Flat.Count} apps, {cached.Root.Folders.Count} folders)");
                    _ = RefreshLiveInBackgroundAsync();
                    return;
                }
            }

            var (root, flat) = await DiscoverLiveAsync(ct);
            Apply(root, flat);
            if (_settings.Current.UseDiscoveryCache)
                await Task.Run(() => DiscoveryCache.Save(root, flat, ComputeFingerprint()), ct);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// Runs the full live scan: the filesystem .lnk walk (per-item shell
    /// localization) and the shell:AppsFolder UWP enumeration in parallel —
    /// both COM-heavy, so overlapping them roughly halves the wall time.
    /// Builds entirely local state, so it's safe to call WITHOUT the lock;
    /// the caller commits the result via <see cref="Apply"/>.
    private async Task<(MenuFolder Root, List<AppEntry> Flat)> DiscoverLiveAsync(CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var root = new MenuFolder { Name = "Programs", Path = "<merged>" };
        var flat = new List<AppEntry>(256);

        var uwpTask = UwpAppEnumerator.EnumerateAsync(ct);
        foreach (var dir in StartMenuRoots.Where(Directory.Exists).Select(p => Path.Combine(p, "Programs")))
        {
            if (!Directory.Exists(dir)) continue;
            await Task.Run(() => Merge(root, dir, flat), ct);
        }
        var lnkMs = sw.ElapsedMilliseconds;
        var uwp = await uwpTask;
        var uwpMs = sw.ElapsedMilliseconds;

        // Deduplicate by id, keep first
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        flat = flat.Where(a => seen.Add(a.Id)).ToList();

        MergeUwp(root, flat, uwp);
        SortRecursive(root);

        HookTrace.Log($"Discovery: lnk={lnkMs}ms uwp-join={uwpMs}ms total={sw.ElapsedMilliseconds}ms " +
                      $"({flat.Count} apps, {uwp.Count} uwp)");
        return (root, flat);
    }

    /// Commits a discovered tree as the active data set. Caller must hold
    /// <see cref="_lock"/>.
    private void Apply(MenuFolder root, List<AppEntry> flat)
    {
        _byId.Clear();
        foreach (var a in flat) _byId[a.Id] = a;
        _root = root;
        _flat = flat;
    }

    /// Backstop after a cache hit: run the live scan off-thread, and if it
    /// differs from what we painted, swap it in, rewrite the cache, and raise
    /// <see cref="Refreshed"/> so the menu rebuilds. The .lnk fingerprint
    /// already catches almost everything up front; this covers the rest
    /// (UWP-only installs/removals) within ~half a second.
    private async Task RefreshLiveInBackgroundAsync(int settleDelayMs = 0)
    {
        try
        {
            // When kicked off by the eager preload, wait for the startup storm
            // to pass so the COM-heavy live scan runs fast and doesn't contend.
            if (settleDelayMs > 0) await Task.Delay(settleDelayMs);
            var (root, flat) = await DiscoverLiveAsync(CancellationToken.None);
            bool changed;
            await _lock.WaitAsync();
            try
            {
                changed = _flat is null || !SameApps(_flat, flat);
                Apply(root, flat);
            }
            finally
            {
                _lock.Release();
            }
            // Only rewrite the cache when the app list actually changed. A
            // matching backstop — the common case — leaves the existing file
            // untouched, so the cache is written just on first run and on real
            // Start-Menu changes, not on every launch. Recompute the
            // fingerprint so it reflects the state that produced this scan.
            if (changed)
            {
                DiscoveryCache.Save(root, flat, ComputeFingerprint());
                HookTrace.Log("Discovery: background refresh updated the app list + cache");
                Refreshed?.Invoke();
            }
        }
        catch
        {
            // Background best-effort; the cached data already painted.
        }
    }

    private static bool SameApps(List<AppEntry> a, List<AppEntry> b)
    {
        if (a.Count != b.Count) return false;
        var ids = new HashSet<string>(b.Select(e => e.Id), StringComparer.OrdinalIgnoreCase);
        return a.All(e => ids.Contains(e.Id));
    }

    /// Cheap, COM-free signature of the app inventory — every Start-Menu entry's
    /// path + last-write time + size, PLUS the top-level UWP package folders. If
    /// this matches the fingerprint stored with the cache, the expensive
    /// shell-resolved data behind it is still valid, so we can reuse it without
    /// re-running any COM.
    private static string ComputeFingerprint()
    {
        var sb = new StringBuilder(8192);
        foreach (var root in StartMenuRoots.Where(Directory.Exists))
        {
            var programs = Path.Combine(root, "Programs");
            if (!Directory.Exists(programs)) continue;
            try
            {
                foreach (var path in Directory
                             .EnumerateFileSystemEntries(programs, "*", SearchOption.AllDirectories)
                             .OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
                {
                    sb.Append(path).Append('|');
                    try
                    {
                        var info = new FileInfo(path);
                        sb.Append(info.LastWriteTimeUtc.Ticks);
                        if ((info.Attributes & FileAttributes.Directory) == 0)
                            sb.Append('|').Append(info.Length);
                    }
                    catch { /* entry vanished mid-walk; skip its metadata */ }
                    sb.Append('\n');
                }
            }
            catch { /* unreadable root; skip */ }
        }

        // UWP signal — the top-level package folders under %LOCALAPPDATA%\Packages
        // (one per packaged app). Pure filesystem, NO shell COM (that's the slow
        // AppsFolder walk we cache to avoid): just the folder names + mtimes. A
        // Store-app install/uninstall adds/removes a folder, flipping the
        // fingerprint so the cache invalidates — without this, an uninstalled
        // UWP app could survive in the cache (no .lnk change to catch it) and
        // ghost on the next launch. Day-to-day app usage doesn't touch these
        // top-level folder mtimes (it writes a level deeper, e.g. LocalState),
        // so this only invalidates on real install/uninstall/first-run.
        var packages = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages");
        if (Directory.Exists(packages))
        {
            try
            {
                foreach (var dir in Directory.EnumerateDirectories(packages)
                             .OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
                {
                    sb.Append(dir).Append('|');
                    try { sb.Append(Directory.GetLastWriteTimeUtc(dir).Ticks); }
                    catch { /* folder vanished mid-walk */ }
                    sb.Append('\n');
                }
            }
            catch { /* unreadable; skip */ }
        }

        return Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    /// Merges UWP / packaged apps from Get-StartApps into the discovered
    /// .lnk-based tree. Strategy:
    ///   * Win32 .lnk that we already discovered AND that Get-StartApps
    ///     reports an AUMID for: enrich the AppEntry with that AUMID so
    ///     the Win 11 Start mirror can match it via packagedAppId.
    ///   * Pure UWP entry (no matching .lnk by name): create a fresh
    ///     AppEntry with Kind=PackagedApp and add it to the root so it
    ///     shows up in All Programs and can be pinned.
    private void MergeUwp(MenuFolder root, List<AppEntry> flat, IReadOnlyList<UwpAppEnumerator.UwpApp> uwp)
    {
        // Build a name index once. We match case-insensitively against both
        // DisplayName and AlternativeName so a Polish-localized .lnk
        // ("Wiersz polecenia") still matches Get-StartApps' equivalent.
        var byName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < flat.Count; i++)
        {
            byName.TryAdd(flat[i].DisplayName, i);
            if (!string.IsNullOrEmpty(flat[i].AlternativeName))
                byName.TryAdd(flat[i].AlternativeName!, i);
        }

        foreach (var (name, aumid) in uwp)
        {
            if (byName.TryGetValue(name, out var idx))
            {
                var existing = flat[idx];
                if (existing.Aumid is null)
                    flat[idx] = existing with { Aumid = aumid };
                continue;
            }

            // UWP entries have no on-disk filename or executable basename
            // to fall back on, so KnownAppAliases is the only path to a
            // cross-language search hit. Lookup by the localized name
            // pulls the full group (incl. the canonical English label).
            var group = KnownAppAliases.GetAliases(name);
            IReadOnlyList<string>? aliases = group.Count > 0
                ? group.Where(a => !a.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList()
                : null;
            if (aliases is { Count: 0 }) aliases = null;

            var entry = new AppEntry(
                Id: HashId($"uwp:{aumid}"),
                DisplayName: name,
                TargetPath: aumid,
                Arguments: null,
                WorkingDirectory: null,
                IconPath: null,
                IconIndex: 0,
                SourceLnkPath: null,
                Kind: AppEntryKind.PackagedApp,
                AlternativeName: null,
                Aumid: aumid,
                SearchAliases: aliases);

            flat.Add(entry);
            root.Apps.Add(entry);
        }
    }

    private static string HashId(string source)
    {
        var bytes = SHA1.HashData(Encoding.UTF8.GetBytes(source.ToLowerInvariant()));
        return Convert.ToHexString(bytes)[..16];
    }

    private static void SortRecursive(MenuFolder f)
    {
        f.Folders.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
        f.Apps.Sort((a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.CurrentCultureIgnoreCase));
        foreach (var sub in f.Folders) SortRecursive(sub);
    }

    private void Merge(MenuFolder target, string diskPath, List<AppEntry> flat)
    {
        // Phase 1 — walk folders serially (the dedup below is order-sensitive,
        // and folders are far fewer than files), collecting the per-file parse
        // work. Phase 2 then parses the shortcuts in PARALLEL: each one is an
        // independent ShellLink COM read + SHGetFileInfo localization, and that
        // per-file work is the bulk of cold discovery time (≈640 ms for ~128
        // shortcuts, measured). The helpers hold no shared mutable state, so
        // fanning them across cores is safe. Phase 3 attaches results serially
        // (cheap; SortRecursive orders everything afterwards anyway).
        var work = new List<(MenuFolder Parent, string File)>();
        WalkFolders(target, diskPath, work);

        var parsed = new AppEntry?[work.Count];
        Parallel.For(0, work.Count, i => parsed[i] = ParseAppFile(work[i].File));

        for (var i = 0; i < work.Count; i++)
        {
            if (parsed[i] is { } entry)
            {
                work[i].Parent.Apps.Add(entry);
                flat.Add(entry);
            }
        }
    }

    private static AppEntry? ParseAppFile(string file)
    {
        var ext = Path.GetExtension(file).ToLowerInvariant();
        return ext switch
        {
            ".lnk" => FromShortcut(file),
            ".url" => FromUrlFile(file),
            ".exe" => FromExe(file),
            _ => null,
        };
    }

    private void WalkFolders(MenuFolder target, string diskPath, List<(MenuFolder Parent, string File)> work)
    {
        try
        {
            foreach (var subDir in Directory.EnumerateDirectories(diskPath))
            {
                // Use the shell's localized display name when available so
                // "Accessories" shows as "Akcesoria systemu", "StartUp" as
                // "Autostart", etc. (same source Explorer uses via the
                // folder's desktop.ini LocalizedResourceName).
                var rawName = Path.GetFileName(subDir);
                var displayName = ShellLocalization.GetLocalizedDisplayName(subDir);
                var localized = !string.IsNullOrWhiteSpace(displayName)
                    && !string.Equals(displayName, rawName, StringComparison.OrdinalIgnoreCase);
                var resolvedName = localized ? displayName! : rawName;

                // Dedup aggressively. Two folders are "the same" if any of:
                //   - Same raw on-disk basename (same folder name across the
                //     per-user and common Start Menu roots).
                //   - Same final display name (different raw names that the
                //     shell localizes to the same string — e.g. an older
                //     "Administrative Tools" and a renamed "Windows Tools"
                //     both surfacing as "Narzędzia systemu Windows").
                //   - This folder's localized name matches an existing
                //     folder's raw name, or vice versa (one variant
                //     localized, the other didn't, with different on-disk
                //     basenames).
                var existing = target.Folders.FirstOrDefault(f =>
                       string.Equals(f.RawName, rawName,      StringComparison.OrdinalIgnoreCase)
                    || string.Equals(f.Name,    resolvedName, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(f.RawName, resolvedName, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(f.Name,    rawName,      StringComparison.OrdinalIgnoreCase));

                if (existing is null)
                {
                    existing = new MenuFolder
                    {
                        Name = resolvedName,
                        Path = subDir,
                        RawName = rawName,
                    };
                    target.Folders.Add(existing);
                }
                else if (localized && string.Equals(existing.Name, existing.RawName,
                    StringComparison.OrdinalIgnoreCase))
                {
                    // The other variant localized; promote raw → localized.
                    existing.Name = displayName!;
                }
                WalkFolders(existing, subDir, work);
            }

            foreach (var file in Directory.EnumerateFiles(diskPath))
            {
                var ext = Path.GetExtension(file).ToLowerInvariant();
                if (ext is ".lnk" or ".url" or ".exe")
                    work.Add((target, file));
            }
        }
        catch (UnauthorizedAccessException) { }
        catch (DirectoryNotFoundException) { }
    }

    private static AppEntry? FromShortcut(string lnkPath)
    {
        var info = ShellLinkReader.Read(lnkPath);
        var filename = Path.GetFileNameWithoutExtension(lnkPath);

        // System .lnk files embed a LocalizedString resource pointer (e.g.
        // "Command Prompt.lnk" surfaces as "Wiersz polecenia" on a Polish
        // Windows). Resolve it so the menu shows the same labels Explorer
        // does, and so search matches what the user actually types.
        var localized = ShellLocalization.GetLocalizedDisplayName(lnkPath);
        var display = string.IsNullOrWhiteSpace(localized) ? filename : localized!;
        // Keep the on-disk filename as a secondary search token only when
        // it differs — that way a user who types "Command Prompt" on a
        // Polish system still finds it.
        var alternative = string.Equals(display, filename, StringComparison.OrdinalIgnoreCase)
            ? null
            : filename;

        // Pick up cross-language + abbreviation aliases for well-known
        // Windows apps. Snipping Tool, Calculator, etc. don't store their
        // English label anywhere we can read on a Polish system — the
        // alias map injects it as a search token.
        var aliases = BuildSearchAliases(display, alternative, info?.TargetPath);

        if (info is null)
        {
            return new AppEntry(Id(lnkPath), display, null, null, null, null, 0,
                lnkPath, AppEntryKind.Shortcut, alternative, Aumid: null,
                SearchAliases: aliases);
        }
        // Skip uninstall-style entries
        if (info.Arguments?.Contains("uninstall", StringComparison.OrdinalIgnoreCase) == true)
            return null;
        if (filename.Contains("uninstall", StringComparison.OrdinalIgnoreCase) ||
            display.Contains("uninstall", StringComparison.OrdinalIgnoreCase))
            return null;

        return new AppEntry(
            Id: Id(lnkPath),
            DisplayName: display,
            TargetPath: info.TargetPath,
            Arguments: info.Arguments,
            WorkingDirectory: info.WorkingDirectory,
            IconPath: info.IconPath,
            IconIndex: info.IconIndex,
            SourceLnkPath: lnkPath,
            Kind: AppEntryKind.Shortcut,
            AlternativeName: alternative,
            Aumid: null,
            SearchAliases: aliases);
    }

    /// Collects every distinct token the user might type to find this
    /// app, excluding the ones already covered by DisplayName +
    /// AlternativeName. Adds: the executable basename (so "cmd" finds
    /// Command Prompt without needing a manual alias entry), then merges
    /// in the KnownAppAliases group for cross-language matching. Returns
    /// null when nothing extra is contributed.
    private static IReadOnlyList<string>? BuildSearchAliases(
        string display, string? alternative, string? targetPath)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { display };
        if (!string.IsNullOrEmpty(alternative)) seen.Add(alternative);
        var aliases = new List<string>();

        // Executable basename — covers Command Prompt → "cmd",
        // Task Manager → "taskmgr", Paint → "mspaint", etc. for any
        // discovered shortcut whose target is an .exe.
        if (!string.IsNullOrEmpty(targetPath))
        {
            var baseName = Path.GetFileNameWithoutExtension(targetPath);
            if (!string.IsNullOrEmpty(baseName) && seen.Add(baseName))
                aliases.Add(baseName);
        }

        foreach (var alias in KnownAppAliases.GetAliases(display))
        {
            if (seen.Add(alias)) aliases.Add(alias);
        }
        if (!string.IsNullOrEmpty(alternative))
        {
            foreach (var alias in KnownAppAliases.GetAliases(alternative))
            {
                if (seen.Add(alias)) aliases.Add(alias);
            }
        }

        return aliases.Count > 0 ? aliases : null;
    }

    private static AppEntry FromUrlFile(string urlPath)
    {
        var name = Path.GetFileNameWithoutExtension(urlPath);
        string? target = null;
        try
        {
            foreach (var line in File.ReadLines(urlPath))
            {
                if (line.StartsWith("URL=", StringComparison.OrdinalIgnoreCase))
                {
                    target = line[4..].Trim();
                    break;
                }
            }
        }
        catch { }
        return new AppEntry(Id(urlPath), name, target, null, null, null, 0, urlPath, AppEntryKind.Shortcut);
    }

    private static AppEntry FromExe(string exePath)
    {
        var name = Path.GetFileNameWithoutExtension(exePath);
        return new AppEntry(Id(exePath), name, exePath, null,
            Path.GetDirectoryName(exePath), exePath, 0, null, AppEntryKind.Executable);
    }

    private static string Id(string source)
    {
        var bytes = SHA1.HashData(Encoding.UTF8.GetBytes(source.ToLowerInvariant()));
        return Convert.ToHexString(bytes)[..16];
    }
}
