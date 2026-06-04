using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.Input;
using MenYou.Platform.Windows;
using MenYou.Services;

namespace MenYou.ViewModels;

[SupportedOSPlatform("windows")]
public sealed partial class RightPanelViewModel : ViewModelBase
{
    /// <param name="Title">Display string, localised from the shell.</param>
    /// <param name="Action">Routing key for <see cref="Open"/>.</param>
    /// <param name="IconGlyph">
    /// Optional Segoe Fluent Icons / MDL2 Assets codepoint (single
    /// character). Acts as the fallback when <see cref="Icon"/> is null
    /// — themes that want a glyph-only look can still bind it.
    /// </param>
    /// <param name="Icon">
    /// Optional bitmap extracted from a Windows shell DLL (typically
    /// shell32.dll or imageres.dll). When present, custom themes
    /// (notably MintCinnamon) render this directly via an Image control
    /// instead of the monochrome glyph — gives Places rows real
    /// colored Windows icons matching what the user sees in Explorer.
    /// The production layouts ignore both Icon and IconGlyph and only
    /// render Title, so nullable defaults keep existing behaviour
    /// unchanged.
    /// </param>
    public sealed record ShellShortcut(
        string Title,
        string Action,
        string IconGlyph = "",
        Bitmap? Icon = null);

    public ObservableCollection<ShellShortcut> Shortcuts { get; } = new();

    /// Raised on the UI thread once <see cref="LoadShellIconsAsync"/> has
    /// finished streaming the deferred shell icons into <see cref="Shortcuts"/>.
    /// Layouts that bind the collection directly (the Win11 flyout) re-render
    /// automatically off ObservableCollection's Replace notifications; the
    /// Mint Cinnamon layout binds the curated
    /// <see cref="StartMenuViewModel.Places"/> slice instead, so
    /// StartMenuViewModel hooks this to re-publish that property.
    public event Action? IconsLoaded;

    private readonly IShellLauncher _launcher;

    public RightPanelViewModel(IShellLauncher launcher)
    {
        _launcher = launcher;

        // Titles come from the system shell so they match whatever locale
        // Windows is in (e.g. "Dokumenty", "Obrazy", "Ten komputer" on a
        // Polish Windows) — same path Open-Shell uses to populate its
        // right-hand column.
        // "Start menu" entry — Open-Shell exposes the same escape hatch. We
        // re-use the system's own phrase (shell32.dll,-30464 = "Menu Start"
        // on a Polish Windows). Activating it synthesizes a Win-key tap;
        // our LL keyboard hook skips injected events, so the system shell
        // sees a real lone-tap and opens its own Start menu.
        // The real per-row shell ICONS are deferred (see
        // LoadShellIconsAsync): each ResolveShellIcon hop is a shell-COM
        // call (SHGetFileInfo / IShellItemImageFactory / AppX-manifest
        // AUMID resolution) and four of the ten entries take the slowest
        // AUMID path. Running all ten synchronously stalled the UI thread
        // while the menu warmed up right after login, so the rows are
        // built icon-less here and the bitmaps streamed in afterwards —
        // MenYou's "show first, paint icons later" policy. Only the
        // Win11 + Mint Cinnamon layouts render these icons at all.
        Shortcuts.Add(new ShellShortcut(Strings.StartMenu, "startmenu"));
        Shortcuts.Add(FromKnownFolder("documents", ShellLocalization.KnownFolders.Documents, "Documents"));
        Shortcuts.Add(FromKnownFolder("pictures", ShellLocalization.KnownFolders.Pictures, "Pictures"));
        Shortcuts.Add(FromKnownFolder("music", ShellLocalization.KnownFolders.Music, "Music"));
        Shortcuts.Add(FromKnownFolder("downloads", ShellLocalization.KnownFolders.Downloads, "Downloads"));
        Shortcuts.Add(FromKnownFolder("computer", ShellLocalization.KnownFolders.ComputerFolder, "This PC"));
        Shortcuts.Add(FromKnownFolder("network", ShellLocalization.KnownFolders.NetworkFolder, "Network"));

        // KNOWNFOLDERID for Control Panel returns the verbose "All Control
        // Panel Items" name. The short string ("Control Panel" / "Panel
        // sterowania" / etc.) lives at shell32.dll,-4161.
        Shortcuts.Add(FromIndirect("control", @"@%SystemRoot%\System32\shell32.dll,-4161", "Control Panel"));
        // Win 11 Settings name is in a UWP package, not in shell32 —
        // Strings.Settings already wraps the SHLoadIndirectString
        // attempt + culture-dictionary fallback (same approach the rest
        // of MenYou uses), so reuse it here instead of maintaining a
        // second translation table.
        Shortcuts.Add(new ShellShortcut(Strings.Settings, "settings"));
        // "Run..." is shell32.dll,-12710 on every modern Windows build.
        Shortcuts.Add(FromIndirect("run", @"@%SystemRoot%\System32\shell32.dll,-12710", "Run..."));

        _ = LoadShellIconsAsync();
    }

    /// Streams the real Windows shell icons into the Places rows after
    /// construction. ResolveShellIcon is all shell-COM, so each hop runs on
    /// the thread pool; the finished bitmap is swapped into the matching row
    /// back on the UI thread (the await resumes on Avalonia's UI
    /// synchronization context, so the ObservableCollection mutation and the
    /// IconsLoaded callback are both raised there). Replacing the record via
    /// `with { Icon }` raises a Replace on the collection, so the Win11
    /// flyout re-renders that row; IconsLoaded lets StartMenuViewModel
    /// re-publish its curated Places slice for the Mint Cinnamon layout.
    private async Task LoadShellIconsAsync()
    {
        for (var i = 0; i < Shortcuts.Count; i++)
        {
            var sc = Shortcuts[i];
            var icon = await Task.Run(() => ResolveShellIcon(sc.Action));
            if (icon is not null)
                Shortcuts[i] = sc with { Icon = icon };
        }
        IconsLoaded?.Invoke();
    }

    private static ShellShortcut FromKnownFolder(string action, Guid knownFolderId, string fallback)
    {
        var title = ShellLocalization.GetKnownFolderDisplayName(knownFolderId);
        return new ShellShortcut(
            string.IsNullOrWhiteSpace(title) ? fallback : title!,
            action);
    }

    private static ShellShortcut FromIndirect(string action, string indirect, string fallback)
    {
        var title = ShellLocalization.LoadIndirectString(indirect);
        return new ShellShortcut(
            string.IsNullOrWhiteSpace(title) ? fallback : title!,
            action);
    }

    /// Resolves a Windows shell icon for a given action key. Earlier
    /// attempts hard-coded shell32.dll / imageres.dll resource indices
    /// — that approach produced wrong / missing icons because the
    /// indices drift between Windows builds. The current path uses
    /// what Explorer itself uses:
    ///
    ///   • Filesystem folders (Documents, Pictures, Music, Downloads,
    ///     Start Menu) — SHGetFileInfo on the real path. Picks up the
    ///     localized desktop.ini icon when the user customised it.
    ///   • Shell-namespace items (This PC, Network, Control Panel) —
    ///     SHParseDisplayName on the canonical CLSID parse-name +
    ///     SHGetFileInfo with SHGFI_PIDL. Same path Explorer uses.
    ///   • Settings — SHGetStockIconInfo with SIID_SETTINGS = 106.
    ///     The OS owns the "canonical Settings icon" mapping; no need
    ///     to guess which DLL houses it on this build.
    ///   • Run — no canonical icon source, return null and let the
    ///     theme fall back to the glyph.
    ///
    /// All paths return null on failure; the consumer
    /// (MintCinnamon.axaml) falls back to the IconGlyph TextBlock.
    private static Bitmap? ResolveShellIcon(string action)
    {
        try
        {
            return action switch
            {
                "documents" => IconExtractor.ExtractForFile(
                    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)),
                "pictures"  => IconExtractor.ExtractForFile(
                    Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)),
                "music"     => IconExtractor.ExtractForFile(
                    Environment.GetFolderPath(Environment.SpecialFolder.MyMusic)),
                // Downloads Place. User prefers the "Kopia zapasowa
                // systemu Windows" / "Windows Backup" tile (the teal
                // cloud-with-up-arrow glyph) over the plain Downloads
                // folder icon — it reads as "files coming down / in
                // from the cloud." Ships in the modern Win 11 CBS
                // package; fall back to the Downloads folder icon if
                // the package is absent.
                "downloads" =>
                    IconExtractor.ExtractForAumid("MicrosoftWindows.Client.CBS_cw5n1h2txyewy!WindowsBackup")
                    ?? IconExtractor.ExtractForFile(
                        Path.Combine(
                            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                            "Downloads")),
                // Run Place. No folder/binary maps cleanly to "Run..."
                // (rundll32 shell32,#61 is a verb, not a file). User
                // prefers the "Kliknij, aby wykonać" / "Click to Do"
                // tile (the sparkly AI pictogram) as a stand-in — it
                // reads as "kick off an action." Ships in the Win 11
                // CoreAI package; null (no icon) if absent, which is
                // the pre-existing behaviour for this Place anyway.
                "run" =>
                    IconExtractor.ExtractForAumid("MicrosoftWindows.Client.CoreAI_cw5n1h2txyewy!ClickToDoApp"),
                // Start Menu Place. Default Start-Menu *folder* icon is a
                // plain folder, which reads as ambiguous next to the
                // other Places. Use the "Get Started" / "Wprowadzenie"
                // app's tile icon instead — Windows ships it on every
                // Win 10 / Win 11 SKU, but the package family name has
                // shifted across Windows generations:
                //   • Modern Win 11 (2024+): rolled into the
                //     MicrosoftWindows.Client.CBS WebExperienceHost
                //     package; the icon is the teal/cyan stylized
                //     pictogram in the screenshot the user pinned.
                //   • Older Win 10 / Win 11: still shipped as the
                //     standalone Microsoft.Getstarted package.
                // Try both AUMIDs in newest-first order; if neither
                // package is present (Server Core / heavily-stripped
                // LTSC), fall back to the bare Start Menu folder icon.
                "startmenu" =>
                    IconExtractor.ExtractForAumid("MicrosoftWindows.Client.CBS_cw5n1h2txyewy!WebExperienceHost")
                    ?? IconExtractor.ExtractForAumid("Microsoft.Getstarted_8wekyb3d8bbwe!App")
                    ?? IconExtractor.ExtractForFile(
                        Environment.GetFolderPath(Environment.SpecialFolder.StartMenu)),

                // Shell-namespace CLSIDs (documented + stable since Win 7).
                "computer"  => IconExtractor.ExtractForShellNamespace(
                    "::{20D04FE0-3AEA-1069-A2D8-08002B30309D}"),
                "network"   => IconExtractor.ExtractForShellNamespace(
                    "::{208D2C60-3AEA-1069-A2D7-08002B30309D}"),
                "control"   => IconExtractor.ExtractForShellNamespace(
                    "::{26EE0668-A00A-44D7-9371-BEB064C98683}"),

                // Settings is a UWP/AppX app on modern Windows. The
                // canonical icon for the Settings tile is whatever the
                // immersive ControlPanel package's manifest declares,
                // which Explorer resolves via the AUMID. SHGetStockIconInfo
                // (SIID_SETTINGS = 106) is supposed to give the same icon
                // but returns null on some Win 11 builds for reasons that
                // aren't documented — the AUMID path is what Explorer
                // actually uses to paint the Settings tile, so it's more
                // reliable. Fall back to the SystemSettings.exe binary
                // icon if AppX resolution somehow fails (e.g. Windows
                // build with the immersive ControlPanel disabled).
                "settings"  =>
                    IconExtractor.ExtractForAumid(
                        "windows.immersivecontrolpanel_cw5n1h2txyewy!microsoft.windows.immersivecontrolpanel")
                    ?? IconExtractor.ExtractStockIcon(106)
                    ?? IconExtractor.ExtractForFile(Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                        "ImmersiveControlPanel", "SystemSettings.exe")),

                _           => null,
            };
        }
        catch
        {
            return null;
        }
    }

    [RelayCommand]
    private void Open(ShellShortcut? s)
    {
        if (s is null) return;
        switch (s.Action)
        {
            case "documents": _launcher.OpenSpecialFolder(Environment.SpecialFolder.MyDocuments); break;
            case "pictures":  _launcher.OpenSpecialFolder(Environment.SpecialFolder.MyPictures); break;
            case "music":     _launcher.OpenSpecialFolder(Environment.SpecialFolder.MyMusic); break;
            case "downloads": _launcher.Launch(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads")); break;
            case "computer":  _launcher.Launch("explorer.exe", "shell:MyComputerFolder"); break;
            case "network":   _launcher.Launch("explorer.exe", "shell:NetworkPlacesFolder"); break;
            case "control":   _launcher.Launch("control.exe"); break;
            case "settings":  _launcher.Launch("ms-settings:"); break;
            case "run":       _launcher.Launch("rundll32.exe", "shell32.dll,#61"); break;
            case "startmenu": SynthesizeWinKey(); break;
        }
    }

    /// Sends a synthetic LWin down/up via SendInput. The events carry
    /// LLKHF_INJECTED, which our WinKeyHook explicitly skips — so the
    /// system shell receives an apparently-real lone Win-tap and opens its
    /// own Start menu. MenYou hides on its own because the
    /// ForegroundWatcher sees focus leave our process.
    private static void SynthesizeWinKey()
    {
        const ushort VK_LWIN = 0x5B;
        const uint KEYEVENTF_KEYUP = 0x02;
        var inputs = new INPUT[2];
        inputs[0] = new INPUT { type = 1 };
        inputs[0].U.ki = new KEYBDINPUT { wVk = VK_LWIN };
        inputs[1] = new INPUT { type = 1 };
        inputs[1].U.ki = new KEYBDINPUT { wVk = VK_LWIN, dwFlags = KEYEVENTF_KEYUP };
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public INPUTUNION U;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct INPUTUNION
    {
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public UIntPtr dwExtraInfo;
        public uint padding1;
        public uint padding2;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);
}
