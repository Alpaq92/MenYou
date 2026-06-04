using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Win32;

namespace MenYou.Services;

/// GitHub-Releases-backed <see cref="IUpdateService"/>. MenYou ships via
/// an Inno Setup installer (see <c>installer/inno/menyou.iss</c>): this
/// queries the GitHub REST API for the repo's latest release, compares its
/// tag to the installed version, and when a newer one exists downloads the
/// <c>MenYou-Setup-*.exe</c> asset and launches it. Inno keys the install
/// off a fixed AppId, so running the new Setup upgrades the existing
/// install in place; its Restart-Manager integration
/// (<c>CloseApplications</c> / <c>RestartApplications</c>) closes and
/// relaunches MenYou around the file swap, so the running process doesn't
/// have to coordinate the exit itself.
///
/// A failed check is non-fatal: the call surfaces an error string but
/// MenYou keeps running on the current build, and the user can retry from
/// the same Settings button.
[SupportedOSPlatform("windows")]
public sealed class GitHubUpdateService : IUpdateService
{
    // The release pipeline publishes to https://github.com/Alpaq92/MenYou.
    private const string Owner = "Alpaq92";
    private const string Repo = "MenYou";

    /// Public project page, shared by the "About" actions in the tray menu
    /// and the Settings window (both open it in the default browser).
    public const string RepositoryUrl = "https://github.com/" + Owner + "/" + Repo;

    // Must match the [Setup] AppId in installer/inno/menyou.iss. Inno
    // records its uninstall entry under "<AppId>_is1" in the Uninstall
    // hive; the presence of that key (and its DisplayVersion) is how we
    // tell an installed build from a dev / `dotnet run` / portable extract
    // — and it's the authoritative "currently-installed version".
    private const string InnoAppId = "{A9F2C7E4-3B6D-4F8A-9C1E-5D7B2A4F6E83}";
    private const string InnoUninstallKey =
        @"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + InnoAppId + "_is1";

    public bool IsPackaged =>
        ReadInstalledVersionString() is not null;

    public async Task<(UpdateResult Outcome, string? Message)> CheckAndApplyAsync(
        CancellationToken ct = default)
    {
        var current = ReadInstalledVersion();
        if (current is null)
        {
            // Dev / portable mode — no Inno install registered. The button
            // still works, it just reports there's nothing to update.
            return (UpdateResult.UpToDate, null);
        }

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            // GitHub's API rejects requests without a User-Agent (403).
            http.DefaultRequestHeaders.UserAgent.Add(
                new ProductInfoHeaderValue("MenYou", current.ToString()));
            http.DefaultRequestHeaders.Accept.Add(
                new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

            // "releases/latest" is the newest non-draft, non-prerelease
            // release — the stable-only channel (prerelease=false).
            var json = await http
                .GetStringAsync($"https://api.github.com/repos/{Owner}/{Repo}/releases/latest", ct)
                .ConfigureAwait(false);

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var latest = ParseVersion(root.GetProperty("tag_name").GetString());
            if (latest is null || latest <= current)
                return (UpdateResult.UpToDate, null);

            // Find the Inno installer asset (MenYou-Setup-<ver>.exe).
            string? url = null;
            if (root.TryGetProperty("assets", out var assets))
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    var name = asset.GetProperty("name").GetString() ?? string.Empty;
                    if (name.StartsWith("MenYou-Setup", StringComparison.OrdinalIgnoreCase)
                        && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    {
                        url = asset.GetProperty("browser_download_url").GetString();
                        break;
                    }
                }
            }
            if (string.IsNullOrEmpty(url))
                return (UpdateResult.Failed, "no MenYou-Setup .exe asset on the latest release");

            // Download the installer to a temp file.
            var dest = Path.Combine(Path.GetTempPath(), $"MenYou-Setup-{latest}.exe");
            await using (var src = await http.GetStreamAsync(url, ct).ConfigureAwait(false))
            await using (var fs = File.Create(dest))
                await src.CopyToAsync(fs, ct).ConfigureAwait(false);

            // Launch it silently: Inno reuses the prior install location
            // and options (keyed off the AppId), shows only a progress
            // window, and its Restart Manager closes + relaunches MenYou
            // around the file swap — no wizard friction on an update, and
            // no double-launch ([Run] is skipifsilent). The full wizard is
            // reserved for the first install (website download, run with
            // no flags). UseShellExecute lets a per-machine install raise
            // the UAC prompt it needs.
            Process.Start(new ProcessStartInfo
            {
                FileName = dest,
                Arguments = "/SILENT",
                UseShellExecute = true,
            });
            return (UpdateResult.Downloaded, null);
        }
        catch (OperationCanceledException)
        {
            return (UpdateResult.UpToDate, null);
        }
        catch (Exception ex)
        {
            return (UpdateResult.Failed, ex.Message);
        }
    }

    /// The version Inno recorded at install time — the authoritative
    /// "what's installed right now". Null in dev / portable mode.
    private static Version? ReadInstalledVersion() => ParseVersion(ReadInstalledVersionString());

    private static string? ReadInstalledVersionString() =>
        ReadInnoDisplayVersion(Registry.CurrentUser)        // per-user install
        ?? ReadInnoDisplayVersion(Registry.LocalMachine);   // per-machine install

    private static string? ReadInnoDisplayVersion(RegistryKey hive)
    {
        try
        {
            using var key = hive.OpenSubKey(InnoUninstallKey);
            return key?.GetValue("DisplayVersion") as string;
        }
        catch
        {
            return null;
        }
    }

    /// Parses "v0.2.0" / "0.2.0" / "0.2.0+build" → Version(0,2,0).
    /// Normalises to three components so a 3-part tag compares cleanly
    /// against a 4-part assembly version.
    private static Version? ParseVersion(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim().TrimStart('v', 'V');
        var cut = s.IndexOfAny(new[] { '+', '-' });
        if (cut >= 0) s = s[..cut];
        if (!Version.TryParse(s, out var v)) return null;
        return new Version(v.Major, Math.Max(v.Minor, 0), Math.Max(v.Build, 0));
    }
}
