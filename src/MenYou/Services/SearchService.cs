using System.Runtime.Versioning;
using MenYou.Models;
using MenYou.Platform.Windows;

namespace MenYou.Services;

/// Fast, in-memory fuzzy ranker over discovered apps and a handful of well-known
/// paths. Each match scores higher when the query matches the start of a word
/// or whole token, with a bonus for prefix matches on the display name.
[SupportedOSPlatform("windows")]
public sealed class SearchService : ISearchService
{
    private readonly IAppDiscoveryService _apps;

    public SearchService(IAppDiscoveryService apps) => _apps = apps;

    public async Task<IReadOnlyList<SearchResult>> SearchAsync(string query, CancellationToken ct = default)
    {
        var q = query?.Trim() ?? "";
        if (q.Length == 0) return Array.Empty<SearchResult>();

        var apps = await _apps.GetAllAppsAsync(ct);
        var results = new List<SearchResult>();

        // Tracks the searchable identifiers each discovered app contributes
        // (DisplayName + AlternativeName). Built-in commands consult this
        // to suppress themselves when a localized .lnk already represents
        // them — "Control Panel" built-in vs "Panel sterowania" .lnk on
        // a Polish system, etc.
        var discoveredNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var a in apps)
        {
            ct.ThrowIfCancellationRequested();
            // Score against every available label for this app so the
            // user can type any of: the (possibly localized) DisplayName,
            // the original on-disk filename ("Command Prompt" on a Polish
            // system → "Wiersz polecenia" .lnk), the executable basename
            // ("cmd" → cmd.exe), or any KnownAppAliases entry
            // ("Snipping Tool" → Polish "Narzędzie Wycinanie" UWP).
            var score = Rank(a.DisplayName, q);
            if (!string.IsNullOrEmpty(a.AlternativeName))
                score = Math.Max(score, Rank(a.AlternativeName!, q));
            if (a.SearchAliases is not null)
            {
                foreach (var alias in a.SearchAliases)
                    score = Math.Max(score, Rank(alias, q));
            }
            if (score <= 0) continue;
            // UWP entries get the AUMID + PackagedApp kind so the result
            // can be launched via shell:AppsFolder and the icon resolved
            // through SHParseDisplayName + SHGetFileInfo.
            var isUwp = a.Kind == AppEntryKind.PackagedApp && !string.IsNullOrEmpty(a.Aumid);
            results.Add(new SearchResult(
                Title: a.DisplayName,
                // Subtitle: cleaner UX — for UWP the AUMID is noise, show
                // the localized "App" hint instead via Recent's empty sub.
                Subtitle: isUwp ? null : a.TargetPath,
                TargetPath: a.SourceLnkPath ?? a.TargetPath,
                Arguments: a.Arguments,
                IconPath: a.IconPath,
                IconIndex: a.IconIndex,
                Kind: isUwp ? SearchResultKind.PackagedApp : SearchResultKind.App,
                Score: score,
                Aumid: a.Aumid,
                AppId: a.Id));
            discoveredNames.Add(a.DisplayName);
            if (!string.IsNullOrEmpty(a.AlternativeName))
                discoveredNames.Add(a.AlternativeName!);
            if (a.SearchAliases is not null)
            {
                foreach (var alias in a.SearchAliases)
                    discoveredNames.Add(alias);
            }
        }

        foreach (var c in BuiltInCommands)
        {
            // Rank against the Title AND the executable basename so
            // "cmd" matches "Command Prompt" (TargetPath="cmd.exe"),
            // "regedit" matches "Registry Editor", etc. without needing
            // dedicated alias entries for every built-in.
            var score = Rank(c.Title, q);
            if (!string.IsNullOrEmpty(c.TargetPath))
            {
                var baseName = Path.GetFileNameWithoutExtension(c.TargetPath);
                if (!string.IsNullOrEmpty(baseName))
                    score = Math.Max(score, Rank(baseName, q));
            }
            if (score <= 0) continue;
            // Skip the built-in when a discovered .lnk already covers the
            // same English label — either as its localized DisplayName
            // (rare) or, more commonly, as the AlternativeName the shell
            // localization layer stamped on it. Without this guard the
            // Polish "Panel sterowania" .lnk and the English "Control
            // Panel" built-in both appear in results.
            if (discoveredNames.Contains(c.Title)) continue;
            results.Add(c with { Score = score });
        }

        // Control Panel "All Tasks" entries (the GodMode namespace) — ~200
        // localized task-level actions, same source Open-Shell uses. Each
        // entry's ShellPath is launched by Explorer with that path as its
        // single argument.
        foreach (var cp in ControlPanelEnumerator.Enumerate())
        {
            var score = Rank(cp.Name, q);
            if (score <= 0) continue;
            results.Add(new SearchResult(
                Title: cp.Name,
                Subtitle: Strings.ControlPanel,
                TargetPath: cp.ShellPath,
                Arguments: null,
                IconPath: null,
                IconIndex: 0,
                Kind: SearchResultKind.ControlPanelTask,
                Score: score));
        }

        // Settings deep-links — parsed from AllSystemSettings_*.xml and
        // resolved through SHLoadIndirectString against the Settings UWP
        // package. Rank against both the display name AND the
        // semicolon-separated HighKeywords so users can find a setting by
        // a related word that doesn't appear in the title.
        foreach (var ds in SettingsDeepLinkEnumerator.Enumerate())
        {
            var primary = Rank(ds.Name, q);
            var kw = string.IsNullOrEmpty(ds.Keywords) ? 0 : Rank(ds.Keywords, q);
            var score = Math.Max(primary, kw);
            if (score <= 0) continue;
            results.Add(new SearchResult(
                Title: ds.Name,
                Subtitle: Strings.Settings,
                TargetPath: ds.Uri,
                Arguments: null,
                IconPath: null,
                IconIndex: 0,
                Kind: SearchResultKind.Command,
                Score: score));
        }

        return results
            .OrderByDescending(r => r.Score)
            .ThenBy(r => r.Title)
            .Take(40)
            .ToList();
    }

    private static int Rank(string source, string query)
    {
        if (string.IsNullOrEmpty(source)) return 0;
        var s = source;
        var qLower = query.ToLowerInvariant();
        var sLower = s.ToLowerInvariant();

        if (sLower == qLower) return 1000;
        if (sLower.StartsWith(qLower)) return 700 - (s.Length - qLower.Length);

        // word-start match
        var words = sLower.Split(new[] { ' ', '-', '_', '.', '(', ')', '[', ']' }, StringSplitOptions.RemoveEmptyEntries);
        if (words.Any(w => w.StartsWith(qLower))) return 500;

        // contains
        var idx = sLower.IndexOf(qLower, StringComparison.Ordinal);
        if (idx >= 0) return 200 - idx;

        // subsequence (each query char found in order)
        var si = 0;
        foreach (var ch in qLower)
        {
            si = sLower.IndexOf(ch, si);
            if (si < 0) return 0;
            si++;
        }
        return 50;
    }

    private static readonly SearchResult[] BuiltInCommands =
    {
        new("Control Panel", "Windows shell", "control", null, null, 0, SearchResultKind.Command, 0),
        new("Settings", "ms-settings:", "ms-settings:", null, null, 0, SearchResultKind.Command, 0),
        new("File Explorer", "explorer.exe", "explorer.exe", null, null, 0, SearchResultKind.Command, 0),
        new("Task Manager", "taskmgr.exe", "taskmgr.exe", null, null, 0, SearchResultKind.Command, 0),
        new("Command Prompt", "cmd.exe", "cmd.exe", null, null, 0, SearchResultKind.Command, 0),
        new("PowerShell", "powershell.exe", "powershell.exe", null, null, 0, SearchResultKind.Command, 0),
        new("Registry Editor", "regedit.exe", "regedit.exe", null, null, 0, SearchResultKind.Command, 0),
        new("Run", "Run dialog", "rundll32.exe", "shell32.dll,#61", null, 0, SearchResultKind.Command, 0),
    };
}
