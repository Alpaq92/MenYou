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

    private static readonly string[] StartMenuRoots =
    {
        Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
        Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
    };

    public AppDiscoveryService()
    {
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

    private async Task EnsureLoadedAsync(CancellationToken ct)
    {
        if (_root is not null && _flat is not null) return;
        await _lock.WaitAsync(ct);
        try
        {
            if (_root is not null && _flat is not null) return;
            var root = new MenuFolder { Name = "Programs", Path = "<merged>" };
            var flat = new List<AppEntry>(256);

            // .lnk discovery and Get-StartApps run in parallel — the UWP
            // enumeration shells out to powershell.exe (~400 ms) which we
            // can overlap with the filesystem walk.
            var uwpTask = UwpAppEnumerator.EnumerateAsync(ct);
            foreach (var dir in StartMenuRoots.Where(Directory.Exists).Select(p => Path.Combine(p, "Programs")))
            {
                if (!Directory.Exists(dir)) continue;
                await Task.Run(() => Merge(root, dir, flat), ct);
            }
            var uwp = await uwpTask;

            // Deduplicate by id, keep first
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            flat = flat.Where(a => seen.Add(a.Id)).ToList();

            MergeUwp(root, flat, uwp);

            SortRecursive(root);
            _byId.Clear();
            foreach (var a in flat) _byId[a.Id] = a;
            _root = root;
            _flat = flat;
        }
        finally
        {
            _lock.Release();
        }
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
                Merge(existing, subDir, flat);
            }

            foreach (var file in Directory.EnumerateFiles(diskPath))
            {
                var ext = Path.GetExtension(file).ToLowerInvariant();
                AppEntry? entry = ext switch
                {
                    ".lnk" => FromShortcut(file),
                    ".url" => FromUrlFile(file),
                    ".exe" => FromExe(file),
                    _ => null
                };
                if (entry is null) continue;
                target.Apps.Add(entry);
                flat.Add(entry);
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
