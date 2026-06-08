using System.Runtime.Versioning;
using Jeek.Avalonia.Localization;

namespace MenYou.Platform.Windows;

/// All user-visible labels in MenYou. Resolution order is **Windows-first**:
///
///   1. Each property tries one or more Windows shell-resource indirect
///      strings via <see cref="ShellLocalization.LoadIndirectString"/>.
///      Picking up the system's own phrasing keeps MenYou's UI
///      indistinguishable from native Windows surfaces ("Wyloguj się"
///      matches what users see in the system Start menu), and the
///      labels follow Windows display-language changes for free.
///   2. On miss (resource ID doesn't exist on the running build, the DLL
///      isn't present, or the label genuinely doesn't live in any shell
///      DLL — MenYou-specific phrases, UWP-only resources), falls
///      through to <see cref="Localizer.Get"/> against the Jeek JSON
///      bundles under <c>Languages\*.json</c>. See
///      <see cref="MenYou.Services.AvaloniaResourceLocalizer"/>.
///
/// Add a new Windows source by passing extra indirect-string arguments
/// to <see cref="Resolve"/> — they're tried in order. The first non-empty
/// one wins.
///
/// AXAML refers to these via <c>{x:Static loc:Strings.X}</c>.
[SupportedOSPlatform("windows")]
public static class Strings
{
    private const string Sys = @"%SystemRoot%\System32";

    // ---- system-shell verbs (authui.dll) ---------------------------------
    public static string Shutdown  { get; } = ResolveShell($@"@{Sys}\authui.dll,-3013", "Shut down");
    public static string Restart   { get; } = ResolveShell($@"@{Sys}\authui.dll,-3010", "Restart");
    public static string Sleep     { get; } = ResolveShell($@"@{Sys}\authui.dll,-3019", "Sleep");

    // ---- Win 11 Start menu (StartTileData.dll) ---------------------------
    public static string Pinned           { get; } = ResolveShell($@"@{Sys}\StartTileData.dll,-2000", "Pinned");
    public static string Recent           { get; } = ResolveShell($@"@{Sys}\StartTileData.dll,-2001", "Recent");
    public static string Open             { get; } = ResolveShell($@"@{Sys}\StartTileData.dll,-1000", "Open");
    public static string RunAsAdmin       { get; } = ResolveShell($@"@{Sys}\StartTileData.dll,-1002", "Run as administrator");
    public static string PinToStart       { get; } = ResolveShell($@"@{Sys}\StartTileData.dll,-1007", "Pin to Start");
    public static string UnpinFromStart   { get; } = ResolveShell($@"@{Sys}\StartTileData.dll,-1008", "Unpin from Start");
    // "Open location" verbs. Earlier code used a single label for both
    // the app and folder context-menu entries and went through several
    // bad compromises: StartTileData,-2104 ("Otwórz lokalizację pliku" /
    // "Open file location") reads wrong on folders, so it was swapped to
    // ,-1001 ("Otwórz nowe okno" / "Open new window") — which is wrong
    // for *both*, since neither command opens a new app window. Both
    // commands actually reveal the target in Explorer:
    //   • OpenFileLocation  → explorer /select,"file"  (highlights it)
    //   • OpenFolderLocation → explorer "folder"        (opens into it)
    // So we now carry two accurate, separately-sourced verbs. Both are
    // the system's own Explorer/Start-menu phrasing (localized for free
    // in every Windows display language); mnemonics are stripped by the
    // resolver, so the shell32 "&folderu" ampersand never reaches the UI.
    public static string OpenFileLocation   { get; } = ResolveShell($@"@{Sys}\StartTileData.dll,-2104", "Open file location");
    public static string OpenFolderLocation { get; } = ResolveShell($@"@{Sys}\shell32.dll,-1040",       "Open folder location");

    // ---- shell32 / twinui ------------------------------------------------
    public static string Search       { get; } = ResolveShell($@"@{Sys}\shell32.dll,-12708", "Search");
    public static string Settings     => Resolve("Settings", $@"@{Sys}\twinui.dll,-10206");
    public static string StartMenu    { get; } = ResolveShell($@"@{Sys}\shell32.dll,-30464", "Start menu");
    public static string ControlPanel { get; } = ResolveShell($@"@{Sys}\shell32.dll,-4161",  "Control Panel");

    // ---- Windows-first with Jeek fallback --------------------------------
    // Each `Resolve("Key", indirect, ...)` call tries SHLoadIndirectString
    // on each indirect string in order; if all miss it falls through to
    // Localizer.Get("Key"). The current candidate indirects below cover
    // the well-documented shell resources; for labels we haven't found a
    // system source for, only the Jeek key is passed. Lazy `=>` so the
    // active culture's translation flows through if the system display
    // language changes between accesses.

    // AllPrograms: aclui.dll,-56 = "Wszystko" / "All". A deep-dive over
    // SHLoadIndirectString found no clean shell string for the Win 11
    // "All apps" wording ("Wszystkie aplikacje" only lives buried in
    // longer sentences / non-RT_STRING resources, and the real Start term
    // is an ms-resource in StartMenuExperienceHost). The generic "All" at
    // aclui.dll,-56 is the one terse, addressable, auto-localizing option
    // — so the header uses it, system-sourced, no JSON entry needed
    // (aclui.dll ships on every SKU).
    public static string AllPrograms             => Resolve("AllPrograms", $@"@{Sys}\aclui.dll,-56");
    // Places: deep-dive of shell32 / ExplorerFrame / twinui revealed no
    // standalone single-noun "Places" / "Miejsca" resource — only phrasal
    // matches (shell32.dll,-9340 "Lokalizacje sieciowe" = "Network
    // locations", ExplorerFrame.dll,-50198 "Często odwiedzane miejsca"
    // = "Frequently visited places", ExplorerFrame.dll,-34818 "Wszystkie
    // lokalizacje" = "All locations"). shell32.dll,-8991 is "Lokalizacja"
    // (singular Location). None reads cleanly as a bare column / flyout
    // header, so this falls through to the JSON bundle, where Polish
    // gets "Miejsca" (the literal noun used historically in the Win 7
    // file dialog Places bar and in shell32 -50198's "...miejsca"
    // suffix). Other languages get their native plural-noun term.
    public static string Places                  => Resolve("Places");
    // SignOut: shell32.dll,-51523 returns the bare "Wyloguj" / "Sign out"
    // verb that the Win 11 user-account flyout uses.
    public static string SignOut                 => Resolve("SignOut", $@"@{Sys}\shell32.dll,-51523");
    // Lock: powercpl.dll,-360 hosts the power-options "Zablokuj" / "Lock"
    // label. shell32.dll's "Zablokuj" matches are all unrelated (ACL,
    // cookies, taskbar) — verified during the deep probe.
    public static string Lock                    => Resolve("Lock", $@"@{Sys}\powercpl.dll,-360");
    // Searching: shell32.dll,-32950 = "Trwa wyszukiwanie..." / "Searching..."
    // — the shell's own in-progress search status. Shown as the search
    // overlay's header ONLY while a query is in flight (SearchViewModel
    // .IsSearching); once results settle it's replaced by SearchResults
    // below. (Earlier this used themecpl,-1107 "Ładowanie" / "Loading"; the
    // shell32 string is the exact, semantically-correct phrase for the state.)
    public static string Searching               => Resolve("Searching", $@"@{Sys}\shell32.dll,-32950");
    // SearchResults: shell32.dll,-34133 = "Wyniki wyszukiwania" / "Search
    // results" — the canonical, decades-stable shell string Explorer titles
    // its own search-results view with. Found via a live SHLoadIndirectString
    // brute-force over the shell DLLs; ships in every Windows display
    // language, so the header localizes for free. The Jeek "SearchResults"
    // key backs up the (Windows-only, so practically never) no-DLL case.
    public static string SearchResults           => Resolve("SearchResults", $@"@{Sys}\shell32.dll,-34133");
    public static string SearchPlaceholder       => Resolve("SearchPlaceholder");
    public static string OpenMenYou              => Resolve("OpenMenYou");
    // Custom-theme empty-state preview placeholder label. JSON-only.
    public static string Preview                 => Resolve("Preview");
    // Skin: shell32.dll,-51729 = "Motyw" / "Theme" — same ID as
    // Strings.Theme. The user explicitly merged the two visually (Skin
    // tray-submenu and Theme settings label both read "Motyw"); the
    // C# properties stay separate so callers can refactor later
    // without touching this resolver.
    public static string Skin                    => Resolve("Skin", $@"@{Sys}\shell32.dll,-51729");
    // Exit: usercpl.dll,-3204 hosts the User Accounts CPL's "Zakończ"
    // / "Exit" verb — bare and unambiguous, same word every modern
    // Windows app's File menu uses. Found via max-effort probe.
    public static string Exit                    => Resolve("Exit", $@"@{Sys}\usercpl.dll,-3204");
    public static string MirrorWinStart          => Resolve("MirrorWinStart");
    public static string PushToWinStart          => Resolve("PushToWinStart");
    public static string MirrorUnavailableTitle  => Resolve("MirrorUnavailableTitle");
    public static string MirrorUnavailableBody   => Resolve("MirrorUnavailableBody");

    // ---- first-run "ready" tray balloon (MenYou-specific, JSON-backed) -----
    public static string ReadyTitle              => Resolve("ReadyTitle");
    public static string ReadyBody               => Resolve("ReadyBody");

    // ---- Settings dialog labels ------------------------------------------
    // Appearance: shell32.dll,-32004 — the classic Display Properties
    // "Wygląd" / "Appearance" tab label.
    public static string Appearance              => Resolve("Appearance", $@"@{Sys}\shell32.dll,-32004");
    // MenuStyle: shares shell32.dll,-51729 = "Motyw" / "Theme" with
    // Strings.Skin per user merge — the Settings → MenuStyle row and
    // the tray Skin submenu both display the same word.
    public static string MenuStyle               => Resolve("MenuStyle", $@"@{Sys}\shell32.dll,-51729");
    // Theme / Dark / Light live at consecutive shell32.dll resource IDs
    // 51729–51731 — the Win 11 system-wide theme picker resources. These
    // are stable since 22H2 and ship the right phrase in every Windows
    // display language.
    public static string Theme                   => Resolve("Theme", $@"@{Sys}\shell32.dll,-51729");
    // Accent: themecpl.dll,-1108 = "Kolor" / "Color". Drops the
    // "Accent" specificity but reads as a colour-picker label, which
    // is what the Settings → Appearance row is.
    public static string Accent                  => Resolve("Accent", $@"@{Sys}\themecpl.dll,-1108");
    public static string FollowWindowsAccent     => Resolve("FollowWindowsAccent");
    public static string FollowWindowsTheme      => Resolve("FollowWindowsTheme");
    public static string ShowPinned              => Resolve("ShowPinned");
    public static string ShowRecent              => Resolve("ShowRecent");
    public static string ShowSearch              => Resolve("ShowSearch");
    public static string MaxRecentItems          => Resolve("MaxRecentItems");
    // Behavior: per user, repurposed to display "Advanced" instead of
    // a literal "Behavior" verb (no standalone "Zachowanie" exists in
    // any shell DLL). comres.dll,-2739 = "Zaawansowane" / "Advanced"
    // — the classic Windows control-panel tab label, used in Internet
    // Options / System Properties / many dialogs. The C# property name
    // stays Behavior for stability; the user-visible label is now
    // "Advanced".
    public static string Behavior                => Resolve("Behavior", $@"@{Sys}\comres.dll,-2739");
    public static string ReplaceStart            => Resolve("ReplaceStart");
    public static string ReplaceStartDescription => Resolve("ReplaceStartDescription");
    // StartWithWindows: shell32.dll,-21787 returns the bare "Autostart"
    // label Windows uses for the same concept (run at sign-in). Short
    // and reads natively in every Windows locale.
    public static string StartWithWindows        => Resolve("StartWithWindows", $@"@{Sys}\shell32.dll,-21787");
    public static string HideOnFocusLost         => Resolve("HideOnFocusLost");

    // ---- Developer tab ---------------------------------------------------
    // Developer: a deep SHLoadIndirectString probe over shell32 / twinui /
    // comres / themecpl / windows.storage / twinui.pcshell found no clean
    // standalone "Developer" / "Deweloper" resource (the only matches were
    // unrelated — font caches, "Maximize"), so the tab title comes from the
    // JSON bundle.
    public static string Developer               => Resolve("Developer");
    // Cache + Immediate: no shell-DLL match for a user-facing "cache" noun
    // (every hit was an internal font/COM cache) or an "open immediately"
    // phrase, so these are JSON-only.
    public static string UseDiscoveryCache            => Resolve("UseDiscoveryCache");
    public static string UseDiscoveryCacheDescription => Resolve("UseDiscoveryCacheDescription");
    public static string ImmediateReveal              => Resolve("ImmediateReveal");
    public static string ImmediateRevealDescription   => Resolve("ImmediateRevealDescription");
    // DiagnosticLogging: JSON-only. comres.dll,-2860 ("Rejestrowanie" /
    // "Logging") was the system term, but the toggle now carries MenYou's own
    // "Diagnostic logging" / "Logi diagnostyczne" wording — clearer than the
    // bare COM+ "Logging" label — so it resolves straight from the JSON bundle.
    public static string DiagnosticLogging            => Resolve("DiagnosticLogging");
    public static string DiagnosticLoggingDescription => Resolve("DiagnosticLoggingDescription");
    // MaxLogSize: shell32.dll,-8978 = "Rozmiar" / "Size" (the file-properties
    // Size label) + an explicit "(MB)" unit. No single shell string carries
    // "max log size", so the field label is the system "Size" word with the
    // unit appended; the description spells out that it's the log cap.
    public static string MaxLogSizeMb            => $"{Resolve("LogSize", $@"@{Sys}\shell32.dll,-8978")} (MB)";
    // Synchronization: comres.dll,-2027 is the canonical COM-services
    // "Synchronization" string used across Windows sync UIs.
    public static string Synchronization         => Resolve("Synchronization", $@"@{Sys}\comres.dll,-2027");
    // CustomTab: themecpl.dll,-18 = "Niestandardowe" / "Custom" — the
    // Personalization "Custom" option label.
    public static string CustomTab               => Resolve("CustomTab", $@"@{Sys}\themecpl.dll,-18");
    // Settings → Custom tab labels. Save's "Zapisz" comes from the
    // common-dialog Save button (comdlg32.dll,-369). The other four
    // (Load, Forget, UseCustomTheme, SelectUploadedTheme) don't have
    // standalone Windows DLL sources — Load/Forget are present in
    // shell DLLs only as substrings of longer phrases, and the two
    // descriptive labels are MenYou-specific.
    public static string Save                    => Resolve("Save", $@"@{Sys}\comdlg32.dll,-369");
    // Load: removed in favor of Strings.Open (StartTileData.dll,-1000).
    // The user opted to display "Open" on the Custom-theme Load button
    // since the action *is* opening a file picker; AXAML references
    // Strings.Open directly. No Strings.Load property needed.
    public static string Forget                  => Resolve("Forget");
    public static string UseCustomTheme          => Resolve("UseCustomTheme");
    public static string SelectUploadedTheme     => Resolve("SelectUploadedTheme");
    public static string MirrorWinStartDescription => Resolve("MirrorWinStartDescription");
    public static string PushToWinStartDescription => Resolve("PushToWinStartDescription");
    // ResetToDefaults: comres.dll,-2775 = "&Resetuj" / "&Reset" — the
    // common-controls Reset button label. Mnemonic stripped by
    // Resolve()'s StripMnemonic helper so the user sees "Resetuj" /
    // "Reset" without the leading ampersand.
    public static string ResetToDefaults         => Resolve("ResetToDefaults", $@"@{Sys}\comres.dll,-2775");
    // Update-checker button + four status strings. There's no exact
    // Windows shell-DLL match — the closest are wuapp.dll labels
    // ("Sprawdź aktualizacje" lives in mu_*StringTable Windows Update
    // resources that differ by SKU/build), so we don't gamble and use
    // the Jeek fallback unconditionally.
    public static string CheckForUpdates         => Resolve("CheckForUpdates");
    // About: the menu/button label that opens MenYou's GitHub page in the
    // browser. No reliable shell-DLL source for a bare "About" verb across
    // SKUs, so it comes from the JSON bundle.
    public static string About                   => Resolve("About");
    // UpdateChecking: themecpl.dll,-1107 = "Ładowanie" / "Loading" —
    // the Personalization spinner label. Not literally "Checking" but
    // works as a generic in-progress status.
    public static string UpdateChecking          => Resolve("UpdateChecking", $@"@{Sys}\themecpl.dll,-1107");
    public static string UpdateUpToDate          => Resolve("UpdateUpToDate");
    public static string UpdateDownloaded        => Resolve("UpdateDownloaded");
    public static string UpdateCheckFailed       => Resolve("UpdateCheckFailed");
    // Apply: Personalization control panel's "Apply" button —
    // themecpl.dll,-1190. Verified via SHLoadIndirectString probe.
    public static string Apply                   => Resolve("Apply", $@"@{Sys}\themecpl.dll,-1190");
    // Close: window-management label in twinui.dll,-2704. The same DLL
    // hosts a longer "Zamknij aplikację" string at -10802 — we want the
    // bare verb, which is -2704.
    public static string Close                   => Resolve("Close", $@"@{Sys}\twinui.dll,-2704");
    // Yes / No: simple confirm-dialog buttons. The MessageBox verbs live
    // in user32 and aren't reachable via SHLoadIndirectString, so these
    // come straight from the JSON bundle rather than a shell DLL.
    public static string Yes                     => Resolve("Yes");
    public static string No                      => Resolve("No");
    // Confirmation shown when the user clicks Apply with "Use custom
    // theme" on but the editor empty — applying would leave a blank menu.
    public static string ConfirmEmptyThemeTitle  => Resolve("ConfirmEmptyThemeTitle");
    public static string ConfirmEmptyThemeBody   => Resolve("ConfirmEmptyThemeBody");
    // Dark / Light: shell32.dll,-51731 / -51730 — the same Win 11 theme
    // picker resource block as Theme above. Triple of consecutive IDs.
    public static string Dark                    => Resolve("Dark",  $@"@{Sys}\shell32.dll,-51731");
    public static string Light                   => Resolve("Light", $@"@{Sys}\shell32.dll,-51730");
    // System: shell32.dll,-8770 is the bare "System" string Windows
    // uses for the System Folder display name. Same word in every
    // locale we care about.
    public static string System                  => Resolve("System", $@"@{Sys}\shell32.dll,-8770");
    public static string StyleWin7               => Resolve("StyleWin7");
    public static string StyleWin7Desc           => Resolve("StyleWin7Desc");
    public static string StyleWindows11          => Resolve("StyleWindows11");
    public static string StyleWindows11Desc      => Resolve("StyleWindows11Desc");
    public static string StyleMintCinnamon       => Resolve("StyleMintCinnamon");
    public static string StyleMintCinnamonDesc   => Resolve("StyleMintCinnamonDesc");
    public static string StyleClassic2           => Resolve("StyleClassic2");
    public static string StyleClassic2Desc       => Resolve("StyleClassic2Desc");
    public static string StyleClassic1           => Resolve("StyleClassic1");
    public static string StyleClassic1Desc       => Resolve("StyleClassic1Desc");
    public static string SettingsTitle           => $"MenYou — {Settings}";
    public static string TrayTooltip             => $"MenYou — {Resolve("StartMenuReplacementTail")}";

    // ---- shell-DLL tooltips (cached because they don't change) -----------
    // Cog-button tooltip. Suffixed with the app name so it reads
    // "Ustawienia (MenYou)" / "Settings (MenYou)" — making clear it opens
    // MenYou's own settings, not the Windows Settings app (which is the
    // separate "settings" Place in the shell-shortcut list).
    public static string SettingsTooltip  => $"{Settings} (MenYou)";
    public static string ShutdownTooltip  { get; } = Shutdown;
    public static string RestartTooltip   { get; } = Restart;
    public static string SleepTooltip     { get; } = Sleep;
    public static string SignOutTooltip   => SignOut;
    public static string LockTooltip      => Lock;
    // Phone-strip button label. The button launches Phone Link when it's
    // installed, otherwise opens Settings → Mobile devices, so it carries
    // the OS's own "Mobile devices" term — wpdshext.dll,-510 resolves to
    // "Urządzenia przenośne" / the locale equivalent, pulled live from the
    // shell so it matches the Windows display language (a deep-dive over
    // SHLoadIndirectString confirmed no shell DLL exposes the literal
    // "Phone Link" UWP app name). Falls back to the JSON "PhoneLink" key
    // only on the rare SKU missing wpdshext.dll.
    public static string PhoneLink        => Resolve("PhoneLink", $@"@{Sys}\wpdshext.dll,-510");
    public static string PhoneLinkTooltip => PhoneLink;

    // ---- helpers ---------------------------------------------------------

    /// Tries each Windows indirect-string resource in order, then falls
    /// back to the Jeek JSON bundle keyed by <paramref name="key"/>. The
    /// shell DLLs ship in the active Windows display language, so when
    /// the resource exists we get the system's own phrasing for free —
    /// no per-locale JSON entry required. When the resource is missing
    /// (different SKU, removed in a build update, UWP-only string), the
    /// JSON entry kicks in.
    private static string Resolve(string key, params string[] indirects)
    {
        foreach (var indirect in indirects)
        {
            var v = ShellLocalization.LoadIndirectString(indirect);
            if (!string.IsNullOrWhiteSpace(v)) return StripMnemonic(v!);
        }
        return Localizer.Get(key);
    }

    /// Older form kept for the eagerly-evaluated static fields above —
    /// those don't need lazy Localizer access since DLL strings don't
    /// change during a session.
    private static string ResolveShell(string indirect, string fallback)
    {
        var v = ShellLocalization.LoadIndirectString(indirect);
        return string.IsNullOrWhiteSpace(v) ? fallback : StripMnemonic(v!);
    }

    /// Strips Win32 keyboard mnemonics from a resource string. Windows
    /// resources mark the keyboard shortcut with a single <c>&amp;</c>
    /// before the underlined character (e.g. <c>&amp;Zapisz</c> means
    /// Alt+Z activates the Save button), with <c>&amp;&amp;</c>
    /// representing a literal ampersand. Avalonia renders <c>&amp;</c>
    /// literally — it has no Win32-style mnemonic handling — so leaving
    /// the prefix in gives the user "&amp;Zapisz" on a button. This
    /// helper unescapes <c>&amp;&amp;</c> to <c>&amp;</c> and drops
    /// every remaining single <c>&amp;</c>.
    private static string StripMnemonic(string s)
    {
        if (s.IndexOf('&') < 0) return s;
        var sb = new System.Text.StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] != '&') { sb.Append(s[i]); continue; }
            if (i + 1 < s.Length && s[i + 1] == '&') { sb.Append('&'); i++; }
            // single & — mnemonic prefix, drop it
        }
        return sb.ToString();
    }
}
