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
/// matching <c>MenYou-*-Setup-*.exe</c> asset and launches it. The asset is
/// picked to match BOTH this machine's architecture (x64 / arm64) and this
/// install's build VARIANT — self-contained (<c>MenYou-Setup</c>) or
/// framework-dependent (<c>MenYou-fd-Setup</c>) — so an FD install updates
/// to FD and an SC install to SC, never silently swapping the runtime model
/// out from under the user (see <see cref="IsFrameworkDependent"/>). Inno
/// keys the install off a fixed AppId, so running the new Setup upgrades the
/// existing install in place; its Restart-Manager integration
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

    /// CHANGELOG.md as it stood at <paramref name="tag"/> ("vX.Y.Z"), where that
    /// version's entry is the newest — i.e. at the top of the file. An anchor
    /// deep-link on main is not derivable: release-please renders headings as
    /// "0.9.13 (2026-07-30)", so the anchor embeds a release date the app can't
    /// know. Unversioned builds fall back to main. Lives here because this class
    /// owns the repo's identity and URL shapes (see the naming contract below).
    public static string ChangelogUrl(string? tag) =>
        $"{RepositoryUrl}/blob/{(string.IsNullOrEmpty(tag) ? "main" : tag)}/CHANGELOG.md";

    // Must match the [Setup] AppId in installer/inno/menyou.iss. Inno
    // records its uninstall entry under "<AppId>_is1" in the Uninstall
    // hive; the PRESENCE of that key is how we tell an installed build from a
    // dev / `dotnet run` / portable extract (see IsPackaged). Its DisplayVersion
    // is NOT used to decide the current version — in-place upgrades leave it
    // stale — so the actual "currently-installed version" comes from the running
    // assembly version instead (see ReadInstalledVersion).
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
    // lists every exact filename in its release step, so a rename fails
    // the pipeline loudly instead of silently orphaning field updaters.
    private const string SetupAssetPrefix = "MenYou-Setup";
    private const string Arm64SetupAssetPrefix = "MenYou-arm64-Setup";
    // Framework-dependent installer (needs the .NET 10 Desktop Runtime; ~half
    // the payload). Its prefix is DISJOINT from "MenYou-Setup" for the same
    // reason the arm64 one is: a pre-variant-aware "first MenYou-Setup*"
    // matcher must never grab it. Direct-download only (not in
    // winget/choco). Only an FD install (detected via coreclr.dll's
    // absence) ever selects this asset — see IsFrameworkDependent().
    private const string FdSetupAssetPrefix = "MenYou-fd-Setup";

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

            // Pick the installer matching THIS install's VARIANT and this
            // machine's architecture (see the naming-contract note on the
            // prefix constants). Variant first: a framework-dependent install
            // must stay FD and a self-contained one SC — the two ship
            // different payloads against the same AppId, so crossing them on
            // an update would leave a mismatched install. FD is x64-only
            // (single MenYou-fd-Setup asset) and takes no part in the arm64
            // migration below. For SC, an ARM machine falls back to the x64
            // installer (runs under emulation) when a release lacks the native
            // asset; x64 never takes the arm64 asset (disjoint prefixes).
            // OSArchitecture reports the TRUE OS arch even from an emulated x64
            // process, which powers the migration below.
            var isFrameworkDependent = IsFrameworkDependent();
            var wantArm64 = RuntimeInformation.OSArchitecture == Architecture.Arm64;
            string? FindInstaller(string prefix)
            {
                if (!root.TryGetProperty("assets", out var assets)) return null;
                foreach (var asset in assets.EnumerateArray())
                {
                    var name = asset.GetProperty("name").GetString() ?? string.Empty;
                    // Prefixes are pairwise disjoint ("MenYou-fd-Setup" and
                    // "MenYou-arm64-Setup" don't start with "MenYou-Setup"), so
                    // a StartsWith match is unambiguous across variants/arches.
                    if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                        && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                        return asset.GetProperty("browser_download_url").GetString();
                }
                return null;
            }
            // Same-version pass: normally up-to-date — with one exception (SC
            // only). An ARM machine RUNNING THE X64 BUILD under emulation (a
            // Chocolatey install, or an earlier fallback download)
            // migrates to the native arm64 asset of the SAME version when one
            // exists. Loop-safe: once native, ProcessArchitecture is Arm64 and
            // this never fires again; and when the release has no native
            // asset, it stays UpToDate rather than pointlessly reinstalling
            // x64. FD has no cross-arch asset, so it's simply UpToDate here.
            var emulatedOnArm = !isFrameworkDependent && wantArm64
                && RuntimeInformation.ProcessArchitecture == Architecture.X64;
            string? url;
            if (latest == current)
            {
                url = emulatedOnArm ? FindInstaller(Arm64SetupAssetPrefix) : null;
                if (string.IsNullOrEmpty(url))
                    return (UpdateResult.UpToDate, null);
            }
            else if (isFrameworkDependent)
            {
                url = FindInstaller(FdSetupAssetPrefix);
            }
            else
            {
                url = (wantArm64 ? FindInstaller(Arm64SetupAssetPrefix) : null)
                      ?? FindInstaller(SetupAssetPrefix);
            }
            if (string.IsNullOrEmpty(url))
                return (UpdateResult.Failed, isFrameworkDependent
                    ? "no MenYou-fd-Setup .exe asset on the latest release"
                    : "no MenYou-Setup .exe asset on the latest release");

            // Download the installer to a temp file (name reflects the variant
            // for tidiness in %TEMP%; Inno keys the upgrade off the AppId, not
            // the filename).
            var destPrefix = isFrameworkDependent ? FdSetupAssetPrefix : SetupAssetPrefix;
            var dest = Path.Combine(Path.GetTempPath(), $"{destPrefix}-{latest}.exe");
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

    /// Whether THIS install is the framework-dependent build, which decides
    /// the release-asset variant to update to (FD → FD, SC → SC — see
    /// <see cref="CheckAndApplyAsync"/>). Self-contained builds ship the
    /// CoreCLR runtime beside the apphost; framework-dependent builds bind to
    /// the machine-wide .NET runtime and don't, so <c>coreclr.dll</c>'s
    /// presence next to <c>MenYou.exe</c> is the cleanest single
    /// discriminator: the SC publish always includes it, and the FD
    /// installer's runtime precheck keeps a broken FD install from ever
    /// existing without a runtime. If the probe throws (odd layout, access
    /// error) we assume SC — its asset always exists on every release, so a
    /// wrong guess degrades to "offered the SC installer", never "stranded on
    /// a missing FD asset".
    private static bool IsFrameworkDependent()
    {
        try
        {
            return !File.Exists(Path.Combine(AppContext.BaseDirectory, "coreclr.dll"));
        }
        catch
        {
            return false;
        }
    }

    /// The version this build actually IS — the running assembly version, which
    /// release.yml stamps via <c>-p:Version=&lt;tag&gt;</c>. This is authoritative
    /// and always correct, unlike the Inno registry DisplayVersion, which
    /// in-place upgrades have left stale (observed "0.8.5" on a 0.9.9 install) —
    /// reading that made the updater think it was perpetually out of date and
    /// re-offer the same update. Null in dev / portable mode (no Inno install
    /// registered, see <see cref="IsPackaged"/>), so those never self-update.
    private Version? ReadInstalledVersion()
    {
        if (!IsPackaged) return null;
        var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        return v is null ? null : new Version(v.Major, Math.Max(v.Minor, 0), Math.Max(v.Build, 0));
    }

    /// Registry presence only — used to tell an installed build (Inno recorded
    /// an uninstall entry) from a dev / <c>dotnet run</c> / portable extract.
    /// The DisplayVersion value itself is NOT used for the version check (it
    /// goes stale on upgrade); see <see cref="ReadInstalledVersion"/>.
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
