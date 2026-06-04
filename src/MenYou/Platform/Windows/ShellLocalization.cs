using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace MenYou.Platform.Windows;

/// Wraps the Win32 shell APIs that return localized display names for
/// well-known folders and shell verbs. Lets the menu show "Dokumenty",
/// "Obrazy" etc. on a Polish Windows (and the equivalent on every other
/// locale) instead of our hard-coded English labels — same path Open-Shell
/// uses to populate its right-hand column.
///
/// All lookups are best-effort; on failure we return null and the caller
/// keeps its English fallback.
[SupportedOSPlatform("windows")]
internal static class ShellLocalization
{
    /// Well-known KNOWNFOLDERIDs we care about. See
    /// https://learn.microsoft.com/en-us/windows/win32/shell/knownfolderid.
    public static class KnownFolders
    {
        public static readonly Guid Documents       = new("FDD39AD0-238F-46AF-ADB4-6C85480369C7");
        public static readonly Guid Pictures        = new("33E28130-4E1E-4676-835A-98395C3BC3BB");
        public static readonly Guid Music           = new("4BD8D571-6D19-48D3-BE97-422220080E43");
        public static readonly Guid Videos          = new("18989B1D-99B5-455B-841C-AB7C74E4DDFC");
        public static readonly Guid Downloads       = new("374DE290-123F-4565-9164-39C4925E467B");
        public static readonly Guid Desktop         = new("B4BFCC3A-DB2C-424C-B029-7FE99A87C641");
        public static readonly Guid ComputerFolder  = new("0AC0837C-BBF8-452A-850D-79D08E667CA7"); // "This PC"
        public static readonly Guid NetworkFolder   = new("D20BEEC4-5CA8-4905-AE3B-BF251EA09B53");
        public static readonly Guid ControlPanel    = new("82A74AEB-AEB4-465C-A014-D097EE346D63");
        public static readonly Guid RecycleBin      = new("B7534046-3ECB-4C18-BE4E-64CD4CB7D6AC");
        public static readonly Guid SettingsFolder  = new("48E7CAAB-B918-4E58-A94D-505519C795DC"); // not present on every build
    }

    /// Returns the system's localized display name for a known folder
    /// (e.g. "Dokumenty" instead of "Documents" on a Polish Windows), or
    /// null if the folder isn't present on this build.
    public static string? GetKnownFolderDisplayName(Guid knownFolderId)
    {
        var pidl = IntPtr.Zero;
        try
        {
            var hr = SHGetKnownFolderIDList(knownFolderId, 0, IntPtr.Zero, out pidl);
            if (hr != 0 || pidl == IntPtr.Zero) return null;

            var info = new SHFILEINFOW();
            var result = SHGetFileInfoW(pidl, 0, ref info,
                (uint)Marshal.SizeOf<SHFILEINFOW>(),
                SHGFI_PIDL | SHGFI_DISPLAYNAME);
            if (result == IntPtr.Zero) return null;
            return Nullify(info.szDisplayName);
        }
        catch
        {
            return null;
        }
        finally
        {
            if (pidl != IntPtr.Zero) Marshal.FreeCoTaskMem(pidl);
        }
    }

    /// Returns the shell's localized display name for a file or folder.
    ///
    /// Strategy (folders only — files skip straight to step 4):
    ///   1. Parse <c>desktop.ini</c> for
    ///      <c>[.ShellClassInfo]/LocalizedResourceName=</c> and resolve
    ///      via <c>SHLoadIndirectString</c>. This is what Explorer does
    ///      internally and works deterministically; SHGetFileInfo's
    ///      <c>SHGFI_DISPLAYNAME</c> intermittently skips desktop.ini
    ///      for Start-Menu subfolders depending on ownership.
    ///   2. If the folder has no desktop.ini (or no
    ///      LocalizedResourceName entry), look up the raw folder name in
    ///      a small well-known-name → shell32.dll resource ID table.
    ///      Covers folders like "Maintenance" and "System Tools" that
    ///      Microsoft has translations for in shell32.dll but never
    ///      wired up via desktop.ini on every install.
    ///   3. Fall back to SHGetFileInfo.
    ///
    /// .lnk files go through step 4 directly because their
    /// <c>LocalizedString</c> extra-data block is the canonical source
    /// and SHGFI_DISPLAYNAME reads it reliably.
    public static string? GetLocalizedDisplayName(string path)
    {
        if (string.IsNullOrEmpty(path)) return null;

        if (Directory.Exists(path))
        {
            var fromIni = ReadLocalizedResourceFromDesktopIni(path);
            if (!string.IsNullOrEmpty(fromIni)) return fromIni;

            var folderName = Path.GetFileName(path);
            if (WellKnownFolderShell32Ids.TryGetValue(folderName, out var resId))
            {
                var resolved = LoadIndirectString(
                    $@"@%SystemRoot%\System32\shell32.dll,-{resId}");
                if (!string.IsNullOrEmpty(resolved)) return resolved;
            }
        }

        try
        {
            var info = new SHFILEINFOW();
            var hr = SHGetFileInfoW_Path(path, 0, ref info,
                (uint)Marshal.SizeOf<SHFILEINFOW>(),
                SHGFI_DISPLAYNAME);
            if (hr == IntPtr.Zero) return null;
            var name = info.szDisplayName;
            if (string.IsNullOrWhiteSpace(name)) return null;
            // SHGFI_DISPLAYNAME strips the .lnk extension already; guard anyway
            // in case the user has "show extensions" set for known types.
            if (name.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
                name = name[..^4];
            return name;
        }
        catch
        {
            return null;
        }
    }

    /// Raw English folder name → shell32.dll resource ID. These are the
    /// well-known Start Menu folder names that ship without a
    /// LocalizedResourceName entry in their desktop.ini despite a
    /// translation existing in shell32.dll. Verified on Polish Win 11
    /// 24H2 via direct SHLoadIndirectString; resource IDs are stable
    /// going back to Win 7.
    private static readonly Dictionary<string, int> WellKnownFolderShell32Ids =
        new(StringComparer.OrdinalIgnoreCase)
        {
            { "Accessories",          21761 }, // "Akcesoria systemu"
            { "Accessibility",        21840 }, // "Ułatwienia dostępu"
            { "Administrative Tools", 21762 }, // "Narzędzia administracyjne"
            { "Windows Tools",        21762 }, // Win 11 renamed the above
            { "Maintenance",          21811 }, // "Konserwacja"
            { "Startup",              21787 }, // "Autostart" — also matches
                                               // the older "StartUp" casing
                                               // since the comparer is
                                               // OrdinalIgnoreCase.
            { "System Tools",         21788 }, // "System" on Win 11 24H2
            { "Games",                21786 },
        };

    /// Reads <folder>\desktop.ini and returns the localized name from
    /// <c>[.ShellClassInfo]/LocalizedResourceName=</c>. Handles UTF-16 LE
    /// BOM, UTF-8 BOM, and ANSI files (desktop.ini in the wild uses all
    /// three). Returns null when the file is missing, unreadable, or
    /// doesn't have a folder-level localization entry — that's a normal
    /// case (e.g. when desktop.ini only contains LocalizedFileNames for
    /// child .lnks).
    private static string? ReadLocalizedResourceFromDesktopIni(string folder)
    {
        // Try both casings — most installs use "desktop.ini" but
        // ProgramData\...\Maintenance has "Desktop.ini". NTFS is
        // case-insensitive so File.Exists matches either, but being
        // explicit makes the intent clear.
        var ini = Path.Combine(folder, "desktop.ini");
        if (!File.Exists(ini)) return null;

        try
        {
            var bytes = File.ReadAllBytes(ini);
            string text;
            if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
                text = Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
            else if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
                text = Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
            else
                text = Encoding.UTF8.GetString(bytes);

            foreach (var rawLine in text.Split('\n'))
            {
                var line = rawLine.Trim();
                if (!line.StartsWith("LocalizedResourceName=", StringComparison.OrdinalIgnoreCase))
                    continue;
                var value = line["LocalizedResourceName=".Length..].Trim();
                if (string.IsNullOrEmpty(value)) return null;
                return value.StartsWith('@')
                    ? LoadIndirectString(value)
                    : value;
            }
        }
        catch
        {
            // Ignore — desktop.ini may be locked / unreadable on locked-down
            // installs; we'll fall through to the well-known table or
            // SHGetFileInfo.
        }
        return null;
    }

    /// Resolves an "@module.dll,-id"-style indirect resource string (the
    /// format used by the shell for things like "Run..." or "Settings").
    /// Returns null if the resource can't be loaded — e.g. the DLL ID we
    /// guessed isn't present on the user's build.
    public static string? LoadIndirectString(string source)
    {
        try
        {
            var sb = new StringBuilder(512);
            var hr = SHLoadIndirectString(source, sb, (uint)sb.Capacity, IntPtr.Zero);
            return hr == 0 ? Nullify(sb.ToString()) : null;
        }
        catch
        {
            return null;
        }
    }

    private static string? Nullify(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    // ---- P/Invoke -------------------------------------------------------

    private const uint SHGFI_DISPLAYNAME = 0x000000200;
    private const uint SHGFI_PIDL        = 0x000000008;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFOW
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]  public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfoW(IntPtr pidl, uint dwFileAttributes,
        ref SHFILEINFOW psfi, uint cbFileInfo, uint uFlags);

    // Separate overload that takes a string path instead of a PIDL. Shell32
    // exports a single SHGetFileInfoW that dispatches on the SHGFI_PIDL flag.
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHGetFileInfoW")]
    private static extern IntPtr SHGetFileInfoW_Path(string pszPath, uint dwFileAttributes,
        ref SHFILEINFOW psfi, uint cbFileInfo, uint uFlags);

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderIDList(
        [MarshalAs(UnmanagedType.LPStruct)] Guid rfid, uint dwFlags, IntPtr hToken, out IntPtr ppidl);

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int SHLoadIndirectString(string pszSource, StringBuilder pszOutBuf,
        uint cchOutBuf, IntPtr ppvReserved);
}
