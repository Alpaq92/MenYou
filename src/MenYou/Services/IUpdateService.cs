using System.Runtime.Versioning;

namespace MenYou.Services;

/// Outcome of a single <see cref="IUpdateService.CheckAndApplyAsync"/> call.
/// The Settings dialog turns this into a status line under the
/// "Sprawdź aktualizacje" button — see SettingsViewModel.UpdateStatus.
public enum UpdateResult
{
    /// No newer release available, or the build isn't an installed one
    /// (e.g. F5 from the IDE / portable extract — no Inno install key).
    /// The button reports "Aktualna wersja" without raising an error.
    UpToDate,

    /// A newer release was found and its installer downloaded + launched.
    /// Inno upgrades the existing install in place and restarts MenYou
    /// around the swap. Settings shows "Aktualizacja gotowa".
    Downloaded,

    /// The GitHub Releases feed couldn't be reached, or the release had
    /// no installer asset. Settings shows the error message.
    Failed,

    /// The installer downloaded but was gone (or truncated) before it could
    /// be launched — in practice, quarantined by antivirus. MenYou's
    /// installers draw a recurring `Trojan:Win32/Wacatac.B!ml` false positive
    /// from Defender's cloud (see CLAUDE.md), and because the updater writes
    /// the .exe to %TEMP% and runs it, that verdict silently breaks in-app
    /// updates too. Reported separately from <see cref="Failed"/> so Settings
    /// can say what actually happened and point at the manual download,
    /// instead of surfacing "the system cannot find the file specified".
    Blocked,
}

/// Abstraction over the update mechanism so the SettingsViewModel stays
/// decoupled from the concrete source. The shipped implementation
/// (<c>GitHubUpdateService</c>) checks the GitHub Releases feed and runs
/// the Inno Setup installer; dev / portable builds no-op.
[SupportedOSPlatform("windows")]
public interface IUpdateService
{
    /// True when this process is running from an installed build (the
    /// Inno uninstall key exists). Dev builds, `dotnet run`, and portable
    /// extracts return false — the Settings button greys itself but still
    /// surfaces a "nothing to update" status on click so it doesn't look
    /// broken.
    bool IsPackaged { get; }

    /// Check the GitHub Releases feed, and if a newer version exists,
    /// download its installer and launch it. Returns the outcome plus, on
    /// <see cref="UpdateResult.Failed"/>, a free-form human-readable
    /// message for display. The message is intentionally unlocalized —
    /// the caller wraps it in Strings.UpdateCheckFailed.
    Task<(UpdateResult Outcome, string? Message)> CheckAndApplyAsync(CancellationToken ct = default);
}
