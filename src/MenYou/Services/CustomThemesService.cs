using System.Collections.ObjectModel;
using System.Runtime.Versioning;

namespace MenYou.Services;

/// Manages user-uploaded XAML theme files. Themes live as plain text
/// files under <c>%AppData%\MenYou\CustomThemes\*.axaml</c> — outside
/// the main settings.json so they round-trip cleanly through git/sync
/// without bloating the settings file. The service handles listing,
/// loading, and copying uploaded files into the managed folder.
///
/// Files are referenced by their filename (without extension) in the
/// Settings UI — that name shows up in the ComboBox and round-trips
/// through <see cref="MenYou.Models.UserSettings.CustomThemeXaml"/>
/// when the user picks one.
[SupportedOSPlatform("windows")]
public sealed class CustomThemesService
{
    private static readonly string ThemesDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "MenYou", "CustomThemes");

    /// Names of the currently-known theme files (without extension).
    /// Re-built on <see cref="Refresh"/>.
    public ObservableCollection<string> ThemeNames { get; } = new();

    public CustomThemesService()
    {
        EnsureDirectory();
        Refresh();
    }

    /// Re-scan the themes directory and republish <see cref="ThemeNames"/>.
    /// Called on construction + after a successful upload.
    public void Refresh()
    {
        ThemeNames.Clear();
        if (!Directory.Exists(ThemesDirectory)) return;
        try
        {
            foreach (var file in Directory.EnumerateFiles(ThemesDirectory, "*.axaml")
                .Concat(Directory.EnumerateFiles(ThemesDirectory, "*.xaml"))
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                ThemeNames.Add(Path.GetFileNameWithoutExtension(file));
            }
        }
        catch
        {
            // Best-effort enumeration — broken folder permissions
            // shouldn't crash Settings. The UI just shows an empty list.
        }
    }

    /// Reads the raw XAML text for the given theme name. Returns null
    /// when the file is missing or unreadable — caller surfaces an
    /// empty editor / preview in that case.
    public string? Load(string themeName)
    {
        if (string.IsNullOrEmpty(themeName)) return null;
        var path = ResolvePath(themeName);
        if (path is null || !File.Exists(path)) return null;
        try
        {
            return File.ReadAllText(path);
        }
        catch
        {
            return null;
        }
    }

    /// Copies an external .axaml / .xaml file into the managed themes
    /// directory under its original filename. Overwrites silently if a
    /// theme with the same name already exists (the user is expected to
    /// realize they re-uploaded). Returns the theme name (without
    /// extension) on success, null on any IO error.
    public string? Import(string sourcePath)
    {
        if (string.IsNullOrEmpty(sourcePath) || !File.Exists(sourcePath))
            return null;
        try
        {
            EnsureDirectory();
            var fileName = Path.GetFileName(sourcePath);
            var ext = Path.GetExtension(fileName).ToLowerInvariant();
            if (ext != ".axaml" && ext != ".xaml")
            {
                // Force the .axaml extension so Refresh() sees it.
                fileName = Path.GetFileNameWithoutExtension(fileName) + ".axaml";
            }
            var dest = Path.Combine(ThemesDirectory, fileName);
            File.Copy(sourcePath, dest, overwrite: true);
            Refresh();
            return Path.GetFileNameWithoutExtension(fileName);
        }
        catch
        {
            return null;
        }
    }

    /// Removes a theme file from disk and republishes
    /// <see cref="ThemeNames"/>. Returns true when the file existed and
    /// was deleted; false when it wasn't there or the delete failed
    /// (best-effort — file in use, permissions, etc.).
    public bool Delete(string themeName)
    {
        if (string.IsNullOrEmpty(themeName)) return false;
        var path = ResolvePath(themeName);
        if (path is null) return false;
        try
        {
            File.Delete(path);
            Refresh();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void EnsureDirectory()
    {
        try { Directory.CreateDirectory(ThemesDirectory); }
        catch { /* best-effort */ }
    }

    private static string? ResolvePath(string themeName)
    {
        foreach (var ext in new[] { ".axaml", ".xaml" })
        {
            var path = Path.Combine(ThemesDirectory, themeName + ext);
            if (File.Exists(path)) return path;
        }
        return null;
    }
}
