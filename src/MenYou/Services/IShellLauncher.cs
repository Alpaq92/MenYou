using MenYou.Models;

namespace MenYou.Services;

public interface IShellLauncher
{
    void Launch(AppEntry entry);
    void Launch(string path, string? args = null, string? workingDirectory = null);
    /// Same launch path as <see cref="Launch(AppEntry)"/> but with UAC
    /// elevation requested via <c>ProcessStartInfo.Verb = "runas"</c>.
    /// Used by the context-menu "Run as administrator" item.
    void LaunchAsAdmin(AppEntry entry);
    void OpenSpecialFolder(Environment.SpecialFolder folder);

    /// Fired after any launch path completes — the standard Launch
    /// overloads above, plus external Process.Start callers that route
    /// their own launches via <see cref="NotifyLaunched"/> (e.g.
    /// SearchViewModel's UWP/Control-Panel/JumpList paths). The host
    /// hides the StartMenu in response so the user doesn't see the menu
    /// linger while ShellExecute is still warming up the new window.
    event Action? Launched;

    /// External hook for launch sites that intentionally bypass the
    /// Launch overloads (raw Process.Start, ms-settings: URIs, runas
    /// elevation). Fires <see cref="Launched"/> without performing any
    /// actual launch — call this immediately after the bypass succeeds.
    void NotifyLaunched();
}
