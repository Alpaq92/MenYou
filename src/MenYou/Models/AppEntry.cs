namespace MenYou.Models;

/// A launchable installed application discovered from the Start Menu, App Paths,
/// or shipped as a built-in command.
public sealed record AppEntry(
    string Id,
    string DisplayName,
    string? TargetPath,
    string? Arguments,
    string? WorkingDirectory,
    string? IconPath,
    int IconIndex,
    string? SourceLnkPath,
    AppEntryKind Kind,
    // Secondary searchable name. For Windows-localized .lnk files this is
    // the original English filename (e.g. DisplayName="Wiersz polecenia",
    // AlternativeName="Command Prompt") so search matches on either side.
    string? AlternativeName = null,
    // Application User Model ID for UWP / packaged apps. Used to match
    // packagedAppId entries from Export-StartLayout and to launch via
    // shell:AppsFolder\<Aumid>. Always set when Kind=PackagedApp; may
    // also be set on Win32 entries that registered an AUMID (rare).
    string? Aumid = null,
    // Extra searchable tokens beyond DisplayName + AlternativeName:
    // known cross-language localizations + common abbreviations from
    // KnownAppAliases (e.g. "Snipping Tool" alongside the Polish
    // "Narzędzie Wycinanie" display name, or "cmd" alongside "Command
    // Prompt"). SearchService ranks each entry independently and picks
    // the highest score. Null when no aliases apply.
    IReadOnlyList<string>? SearchAliases = null);

public enum AppEntryKind
{
    Shortcut,
    Executable,
    BuiltInCommand,
    SpecialFolder,
    /// UWP / packaged app. TargetPath holds the AUMID for legacy callers;
    /// Aumid holds the same value. Launched via explorer.exe
    /// shell:AppsFolder\<Aumid>; icon via PIDL.
    PackagedApp,
}
