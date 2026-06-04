using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using MenYou.Models;
using MenYou.Services;

namespace MenYou.Platform.Windows;

/// Reads the user's Win 11 Start menu pin list by shelling out to
/// <c>powershell -NoProfile -Command "Export-StartLayout -Path tmp.json"</c>
/// and parsing the JSON's <c>pinnedList</c>. Each entry is one of:
///
///   <c>{ "desktopAppLink": "%ALLUSERSPROFILE%\\...\\App.lnk" }</c>
///   <c>{ "desktopAppId":   "{1AC14E77-...}\\notepad.exe" }</c>  (AUMID)
///   <c>{ "packagedAppId":  "Microsoft.WindowsTerminal_8wek...!App" }</c>
///
/// We resolve each entry to a discovered AppEntry by matching
/// SourceLnkPath / TargetPath / DisplayName / AlternativeName, so the
/// mirrored pin list lines up with MenYou's own discovery.
///
/// On Win 11 24H2 the cmdlet has been flaky — some SKUs report
/// "method not implemented." When that happens we return Failure so the
/// caller can fall back to the last good snapshot and show a one-time
/// warning.
[SupportedOSPlatform("windows")]
internal static class Win11StartLayoutReader
{
    public sealed record Result(IReadOnlyList<string> AppIds, string? Error)
    {
        public bool Success => Error is null;
        public static Result Failed(string msg) => new(Array.Empty<string>(), msg);
    }

    public static async Task<Result> ReadAsync(IAppDiscoveryService discovery, CancellationToken ct = default)
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"menyou_pins_{Guid.NewGuid():N}.json");
        try
        {
            var (exit, stderr) = await RunExportAsync(tmp, ct);
            if (exit != 0 || !File.Exists(tmp))
                return Result.Failed(string.IsNullOrWhiteSpace(stderr)
                    ? $"Export-StartLayout exited with code {exit}"
                    : stderr.Trim());

            var json = await File.ReadAllTextAsync(tmp, ct);
            var entries = ParsePinnedList(json);
            if (entries.Count == 0)
                return new Result(Array.Empty<string>(), null);

            var apps = await discovery.GetAllAppsAsync(ct);
            var ordered = ResolveToAppIds(entries, apps);
            return new Result(ordered, null);
        }
        catch (Exception ex)
        {
            return Result.Failed(ex.Message);
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
        }
    }

    private static async Task<(int exitCode, string stderr)> RunExportAsync(string outPath, CancellationToken ct)
    {
        // -NoProfile keeps startup cost predictable. The Export-StartLayout
        // cmdlet ships in the StartLayout module which is part of every
        // Win 10/11 install — no PSGallery dependency.
        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass " +
                        $"-Command \"Export-StartLayout -Path '{outPath.Replace("'", "''")}'\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var proc = Process.Start(psi);
        if (proc is null) return (-1, "failed to start powershell.exe");

        // Cap the wait — Export-StartLayout normally completes in 200–600 ms.
        // 8 s gives a generous margin without hanging the menu open forever.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(8));
        try
        {
            await proc.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(true); } catch { }
            return (-1, "Export-StartLayout timed out");
        }

        var stderr = await proc.StandardError.ReadToEndAsync(ct);
        return (proc.ExitCode, stderr);
    }

    /// Parses just the pinnedList array — we don't care about taskbar
    /// or quickAccessList sections.
    private static List<PinnedEntry> ParsePinnedList(string json)
    {
        var results = new List<PinnedEntry>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("pinnedList", out var list) ||
                list.ValueKind != JsonValueKind.Array)
                return results;

            foreach (var elem in list.EnumerateArray())
            {
                string? lnk = null, aumid = null, packaged = null;
                if (elem.TryGetProperty("desktopAppLink", out var l) && l.ValueKind == JsonValueKind.String)
                    lnk = ExpandEnv(l.GetString());
                if (elem.TryGetProperty("desktopAppId", out var d) && d.ValueKind == JsonValueKind.String)
                    aumid = d.GetString();
                if (elem.TryGetProperty("packagedAppId", out var p) && p.ValueKind == JsonValueKind.String)
                    packaged = p.GetString();

                if (lnk is null && aumid is null && packaged is null) continue;
                results.Add(new PinnedEntry(lnk, aumid, packaged));
            }
        }
        catch (JsonException)
        {
            // Malformed JSON — treat as empty so the caller's fallback kicks in.
        }
        return results;
    }

    private static List<string> ResolveToAppIds(
        List<PinnedEntry> entries, IReadOnlyList<AppEntry> apps)
    {
        var ids = new List<string>(entries.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var e in entries)
        {
            var match = MatchEntry(e, apps);
            if (match is null) continue;
            if (!seen.Add(match.Id)) continue;
            ids.Add(match.Id);
        }
        return ids;
    }

    private static AppEntry? MatchEntry(PinnedEntry e, IReadOnlyList<AppEntry> apps)
    {
        // .lnk path is the strongest signal — match SourceLnkPath directly,
        // then fall back to matching the .lnk's resolved target executable.
        if (!string.IsNullOrEmpty(e.DesktopAppLink))
        {
            var byLnk = apps.FirstOrDefault(a =>
                string.Equals(a.SourceLnkPath, e.DesktopAppLink, StringComparison.OrdinalIgnoreCase));
            if (byLnk is not null) return byLnk;

            var resolved = ShellLinkReader.Read(e.DesktopAppLink)?.TargetPath;
            if (!string.IsNullOrEmpty(resolved))
            {
                var byTarget = apps.FirstOrDefault(a =>
                    string.Equals(a.TargetPath, resolved, StringComparison.OrdinalIgnoreCase));
                if (byTarget is not null) return byTarget;
            }

            // Last-chance filename match for cases where the .lnk lives in a
            // location our discovery doesn't scan (e.g. a roaming-only path).
            var fname = Path.GetFileNameWithoutExtension(e.DesktopAppLink);
            if (!string.IsNullOrEmpty(fname))
            {
                var byName = apps.FirstOrDefault(a =>
                       string.Equals(a.DisplayName,     fname, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(a.AlternativeName, fname, StringComparison.OrdinalIgnoreCase));
                if (byName is not null) return byName;
            }
        }

        // AUMID (desktopAppId) — Windows surfaces these in the form
        // "{CLSID}\\foo.exe" or a plain AUMID like "Microsoft.Notepad". We
        // don't have a generic AUMID resolver, so we match by the trailing
        // executable name against TargetPath's filename.
        if (!string.IsNullOrEmpty(e.DesktopAppId))
        {
            var exe = ExeFromAumid(e.DesktopAppId);
            if (!string.IsNullOrEmpty(exe))
            {
                var byExe = apps.FirstOrDefault(a =>
                    !string.IsNullOrEmpty(a.TargetPath) &&
                    string.Equals(Path.GetFileName(a.TargetPath), exe, StringComparison.OrdinalIgnoreCase));
                if (byExe is not null) return byExe;
            }
        }

        // packagedAppId — UWP family name like "Microsoft.WindowsTerminal_…!App"
        // or the new "windows.immersivecontrolpanel_..." form used for
        // Settings. AppDiscoveryService.MergeUwp populates AppEntry.Aumid
        // from Get-StartApps so this match is exact and locale-independent.
        if (!string.IsNullOrEmpty(e.PackagedAppId))
        {
            var byAumid = apps.FirstOrDefault(a =>
                !string.IsNullOrEmpty(a.Aumid) &&
                string.Equals(a.Aumid, e.PackagedAppId, StringComparison.OrdinalIgnoreCase));
            if (byAumid is not null) return byAumid;
        }

        return null;
    }

    private static string? ExeFromAumid(string aumid)
    {
        // Form 1: "{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}\\notepad.exe"
        var slash = aumid.LastIndexOf('\\');
        if (slash >= 0 && slash + 1 < aumid.Length)
            return aumid[(slash + 1)..];
        // Form 2: a bare AUMID — no exe hint, return null.
        return null;
    }

    private static string? ExpandEnv(string? path) =>
        string.IsNullOrEmpty(path) ? path : Environment.ExpandEnvironmentVariables(path);

    private sealed record PinnedEntry(string? DesktopAppLink, string? DesktopAppId, string? PackagedAppId);
}
