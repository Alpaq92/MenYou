using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
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

    // ASSET NAMING CONTRACT (canonical home — menyou.iss and release.yml
    // point here). x64 keeps the historic "MenYou-Setup-<ver>.exe" name:
    // updaters that predate the arch-aware matcher (<= 0.9.2) download the
    // FIRST release asset whose name starts with "MenYou-Setup", so that
    // name must never change — and the Windows-on-ARM installer
    // ("MenYou-arm64-Setup-<ver>.exe") deliberately does NOT share the
    // prefix, so those old x64 clients can never pick it up. release.yml
    // lists both exact filenames in its release step, so a rename fails
    // the pipeline loudly instead of silently orphaning field updaters.
    private const string SetupAssetPrefix = "MenYou-Setup";
    private const string Arm64SetupAssetPrefix = "MenYou-arm64-Setup";

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
            if (latest is null || latest < current)
                return (UpdateResult.UpToDate, null);

            // Pick the installer for THIS machine's architecture (see the
            // naming-contract note on the prefix constants). An ARM machine
            // falls back to the x64 installer (runs under emulation) when a
            // release lacks the native asset; x64 never takes the arm64
            // asset (the prefixes are disjoint, so its matcher can't even
            // see it). OSArchitecture reports the TRUE OS arch even from an
            // emulated x64 process, which powers the migration below.
            var wantArm64 = RuntimeInformation.OSArchitecture == Architecture.Arm64;
            string? FindInstaller(string prefix)
            {
                if (!root.TryGetProperty("assets", out var assets)) return null;
                foreach (var asset in assets.EnumerateArray())
                {
                    var name = asset.GetProperty("name").GetString() ?? string.Empty;
                    if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                        && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                        return asset.GetProperty("browser_download_url").GetString();
                }
                return null;
            }
            // Same-version pass: normally up-to-date — with one exception. An
            // ARM machine RUNNING THE X64 BUILD under emulation (a Scoop /
            // Chocolatey install, or an earlier fallback download) migrates
            // to the native arm64 asset of the SAME version when one exists.
            // Loop-safe: once native, ProcessArchitecture is Arm64 and this
            // never fires again; and when the release has no native asset,
            // it stays UpToDate rather than pointlessly reinstalling x64.
            var emulatedOnArm = wantArm64
                && RuntimeInformation.ProcessArchitecture == Architecture.X64;
            string? url;
            if (latest == current)
            {
                url = emulatedOnArm ? FindInstaller(Arm64SetupAssetPrefix) : null;
                if (string.IsNullOrEmpty(url))
                    return (UpdateResult.UpToDate, null);
            }
            else
            {
                url = (wantArm64 ? FindInstaller(Arm64SetupAssetPrefix) : null)
                      ?? FindInstaller(SetupAssetPrefix);
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
