using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32;

namespace MenYou.Services;

/// GitHub-Releases-backed <see cref="IUpdateService"/>. MenYou ships via
/// an Inno Setup installer (see <c>installer/inno/menyou.iss</c>): this
/// queries the GitHub REST API for the repo's latest release, compares its
/// tag to the installed version, and when a newer one exists downloads the
/// matching <c>MenYou-*-Setup-*.exe</c> asset and launches it. The asset is
/// picked to match this machine's architecture (x64 / arm64).
///
/// Releases used to also carry a framework-dependent installer
/// (<c>MenYou-fd-Setup</c>) and this picked the asset matching the installed
/// variant. That variant is RETIRED, so the only job left for the FD probe is
/// MIGRATION: an existing FD install is offered the self-contained installer
/// instead (see <see cref="IsFrameworkDependent"/>), which upgrades it in
/// place onto the bundled runtime. Without that, every FD install in the field
/// would fail its update check forever looking for an asset that no longer
/// exists. Inno
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
    // "MenYou-fd-Setup" is RETIRED and intentionally has no constant here.
    // Releases no longer build it, so nothing may select it — but the name
    // stays burned into this contract as reserved: it must never be reused for
    // a different payload, because FD installs still in the field would happily
    // download it. They are migrated to the self-contained installer instead;
    // see IsFrameworkDependent().

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

            // Pick the installer matching this machine's architecture (see the
            // naming-contract note on the prefix constants). An ARM machine
            // falls back to the x64 installer (runs under emulation) when a
            // release lacks the native asset; x64 never takes the arm64 asset
            // (disjoint prefixes). OSArchitecture reports the TRUE OS arch even
            // from an emulated x64 process, which powers the migrations below.
            //
            // A framework-dependent install gets the SAME self-contained asset
            // as everyone else — that IS the migration. Both variants share the
            // Inno AppId, so the SC installer upgrades the FD install in place
            // and the bundled runtime lands beside the apphost; from then on
            // coreclr.dll exists and the probe reports SC.
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
                    // This still matters after the FD retirement: releases up
                    // to 0.9.19 carry an fd asset, and an FD install checking
                    // against one of those must land on the SC installer that
                    // migrates it — not walk back onto MenYou-fd-Setup.
                    if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                        && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                        return asset.GetProperty("browser_download_url").GetString();
                }
                return null;
            }
            // Same-version pass: normally up-to-date, with two migrations that
            // fire even when the tag matches, because both leave the user on a
            // build that is no longer the right one for their machine.
            //   * An ARM machine RUNNING THE X64 BUILD under emulation (a
            //     Chocolatey install, or an earlier fallback download) takes
            //     the native arm64 asset of the same version when one exists.
            //   * A framework-dependent install takes the self-contained asset,
            //     retiring a variant that is no longer built.
            // Both are loop-safe: once native, ProcessArchitecture is Arm64;
            // once self-contained, coreclr.dll exists — so neither fires twice.
            // With no matching asset each stays UpToDate rather than
            // pointlessly reinstalling what is already there.
            var emulatedOnArm = wantArm64
                && RuntimeInformation.ProcessArchitecture == Architecture.X64;
            string? url;
            if (latest == current)
            {
                url = emulatedOnArm ? FindInstaller(Arm64SetupAssetPrefix) : null;
                if (string.IsNullOrEmpty(url) && isFrameworkDependent)
                    url = FindInstaller(SetupAssetPrefix);
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

            // Download the installer to a temp file (Inno keys the upgrade off
            // the AppId, not the filename).
            const string destPrefix = SetupAssetPrefix;
            var dest = Path.Combine(Path.GetTempPath(), $"{destPrefix}-{latest}.exe");
            try
            {
                await using var src = await http.GetStreamAsync(url, ct).ConfigureAwait(false);
                await using var fs = File.Create(dest);
                await src.CopyToAsync(fs, ct).ConfigureAwait(false);
            }
            catch (UnauthorizedAccessException)
            {
                // Real-time protection can yank the handle mid-write.
                return (UpdateResult.Blocked, dest);
            }

            // Verify what actually landed before running it. Two distinct
            // failures used to arrive here indistinguishable, both surfacing as
            // Process.Start's "the system cannot find the file specified":
            //   * antivirus quarantined the download (MenYou's installers draw
            //     a recurring Wacatac.B!ml false positive — see CLAUDE.md), or
            //   * the transfer truncated and the installer is corrupt.
            // Running an unverified installer is the worse half of that: Inno
            // would start against a partial payload.
            var info = new FileInfo(dest);
            if (!info.Exists || info.Length == 0)
                return (UpdateResult.Blocked, dest);

            // The release body publishes each asset's SHA-256; when it parses,
            // hold the download to it. A mismatch is corruption, not AV — the
            // file is still there.
            var expected = ExpectedSha256(root, Path.GetFileName(new Uri(url).LocalPath));
            if (expected is not null)
            {
                var actual = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(dest, ct).ConfigureAwait(false)));
                if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                {
                    TryDelete(dest);
                    return (UpdateResult.Failed, $"downloaded installer failed its SHA-256 check (expected {expected[..16]}…, got {actual[..16]}…)");
                }
            }

            // Launch it silently: Inno reuses the prior install location
            // and options (keyed off the AppId), shows only a progress
            // window, and its Restart Manager closes + relaunches MenYou
            // around the file swap — no wizard friction on an update, and
            // no double-launch ([Run] is skipifsilent). The full wizard is
            // reserved for the first install (website download, run with
            // no flags). UseShellExecute lets a per-machine install raise
            // the UAC prompt it needs.
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = dest,
                    Arguments = "/SILENT",
                    UseShellExecute = true,
                });
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException)
            {
                // Verified a moment ago, so it was almost certainly quarantined
                // in the gap — the same AV path, just losing a slower race.
                return (UpdateResult.Blocked, dest);
            }
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

    /// Whether THIS install is a leftover framework-dependent build. The FD
    /// variant is retired, so this no longer selects an asset — it triggers a
    /// one-time MIGRATION onto the self-contained installer, including on the
    /// same-version pass (see <see cref="CheckAndApplyAsync"/>), so FD installs
    /// don't sit there failing every check against an asset that stopped being
    /// published.
    ///
    /// Self-contained builds ship the CoreCLR runtime beside the apphost;
    /// framework-dependent builds bind to the machine-wide .NET runtime and
    /// don't, so <c>coreclr.dll</c>'s presence next to <c>MenYou.exe</c> is the
    /// cleanest single discriminator. If the probe throws (odd layout, access
    /// error) we assume SC, which is now the harmless answer in both
    /// directions: every release carries the SC asset, and the only cost of a
    /// wrong guess is that a stale FD install migrates one release later.
    /// The SHA-256 the release body publishes for <paramref name="assetName"/>,
    /// or null when the body is absent or doesn't list it. release.yml writes
    /// one "&lt;64 hex&gt;  &lt;filename&gt;" line per asset inside a fenced block; parsing
    /// is deliberately forgiving, because a body-format change must degrade to
    /// "skip the check", never to "refuse to update".
    private static string? ExpectedSha256(JsonElement root, string assetName)
    {
        if (!root.TryGetProperty("body", out var bodyEl)) return null;
        var body = bodyEl.GetString();
        if (string.IsNullOrEmpty(body)) return null;

        using var reader = new StringReader(body);
        for (string? raw; (raw = reader.ReadLine()) is not null;)
        {
            var line = raw.Trim();
            if (line.Length < 66) continue;
            var sep = line.IndexOf(' ');
            if (sep != 64) continue;
            if (!line.AsSpan(sep).Trim().Equals(assetName, StringComparison.OrdinalIgnoreCase)) continue;
            var hash = line[..64];
            foreach (var c in hash)
                if (!Uri.IsHexDigit(c)) return null;
            return hash;
        }
        return null;
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* best-effort */ }
    }

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
