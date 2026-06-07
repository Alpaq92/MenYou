namespace MenYou.Models;

public enum MenuStyle
{
    Classic1,    // single column with cascading submenus (Win 9x / 2000)
    Classic2,    // two-column with right-side shell links (Win XP)
    Win7,        // Win 7-style with pinned + recent + search at the bottom
    Windows11,   // Win 11-style: search top, tile grid, account + power bottom
    MintCinnamon // Linux Mint Cinnamon-style: dark sidebar + content pane
}

public enum AppTheme
{
    Dark,
    Light,
    System
}

public sealed class UserSettings
{
    // Windows 11 is the default look — it's the layout new users expect on a
    // modern machine, so first-run (no settings.json yet) opens to it. The
    // post-login warm-up and first-paint follow this automatically: the window
    // renders whichever layout MenuStyle selects. Existing settings.json files
    // keep their saved value untouched.
    public MenuStyle MenuStyle { get; set; } = MenuStyle.Windows11;
    public AppTheme Theme { get; set; } = AppTheme.System;
    /// When true, the user-supplied <see cref="CustomThemeXaml"/> is
    /// parsed at runtime via AvaloniaRuntimeXamlLoader and the resulting
    /// control is mounted as a live preview inside Settings (same
    /// pattern as SukiUI's Playground). When false the override is
    /// inert and nothing extra is rendered.
    public bool UseCustomTheme { get; set; } = false;
    /// Inline XAML fragment the user is iterating on. Stored verbatim
    /// (multi-line) and re-parsed on every edit; parse failures surface
    /// as inline error text in the preview area rather than crashing
    /// the dialog.
    public string CustomThemeXaml { get; set; } = "";
    /// When true (default) the Settings window's accent tracks Windows'
    /// personalization accent at runtime. When false, <see cref="Accent"/>
    /// is parsed as a 6-digit hex color and applied as the override.
    public bool UseSystemAccent { get; set; } = true;
    /// Manual accent override in CSS hex form ("#RRGGBB"). Only honored
    /// while <see cref="UseSystemAccent"/> is false. Empty string means
    /// "no manual override yet" — the Settings UI surfaces this as an
    /// empty TextBox with a placeholder hint rather than a literal value
    /// the user has to delete. Kept as a string (not Color) so legacy
    /// settings.json files round-trip and so an in-progress edit ("#FF")
    /// doesn't blow up Color parsing.
    public string Accent { get; set; } = "";
    public bool ShowRecent { get; set; } = true;
    public bool ShowPinned { get; set; } = true;
    public bool ShowSearch { get; set; } = true;
    // When true: register Shift+Win as the keyboard activation hotkey and
    // install StartClickHook for taskbar Start-button clicks. Lone Win key
    // keeps opening the system Start menu — that's Microsoft's path on Win 11
    // 24H2 and not interceptable cleanly from outside the shell (Open-Shell
    // hits the same wall and recommends Shift+Win for the same reason).
    public bool ReplaceWinKey { get; set; } = true;
    // Default to true so the user gets MenYou on next sign-in without
    // having to remember the toggle. The autostart entry is written /
    // removed by Win32AutostartService whenever Apply runs.
    public bool StartWithWindows { get; set; } = true;
    // One-shot migration flag: settings.json files created before the
    // StartWithWindows-default flip still carry the old false value.
    // App startup checks this and, if false, forces StartWithWindows=true
    // + writes the autostart registry entry, then sets the flag so the
    // migration never runs again. Setting the flag explicitly via a
    // future migration is enough to opt out — the user's manual toggle
    // in Settings persists normally afterwards.
    public bool AutostartDefaultApplied { get; set; } = false;
    // One-shot migration flag: profiles created before MenYou moved its
    // autostart from the HKCU\Run value to a logon-triggered scheduled task
    // (Run-key apps are deliberately throttled ~10-16 s after sign-in by
    // Windows; the task fires promptly). App startup checks this and, if false,
    // re-applies StartWithWindows through Win32AutostartService — creating the
    // task and clearing the legacy Run value — then sets the flag so it never
    // re-runs. See [[Win32AutostartService]].
    public bool AutostartTaskMigrated { get; set; } = false;
    public bool HideOnFocusLost { get; set; } = true;
    public int MaxRecentItems { get; set; } = 8;
    public List<PinnedItem> Pinned { get; set; } = new();
    public List<RecentEntry> Recent { get; set; } = new();

    /// True once the Pinned list has been seeded from the user's taskbar
    /// pin folder. Stays true after that even if the user unpins everything
    /// (we don't want to keep re-seeding on every launch).
    public bool PinnedSeeded { get; set; } = false;

    /// AppEntry IDs the user has already seen at least once. Drives the
    /// "newly installed" highlight (Open-Shell style): apps discovered now
    /// but missing from this list are flashed with the accent tint. After
    /// the first menu open the list is refreshed to the current full set,
    /// so the flash only shows once per install.
    public List<string> SeenAppIds { get; set; } = new();

    /// When true, MenYou's Pinned list is a live mirror of the Windows 11
    /// Start menu pins (read via Export-StartLayout + a file-watcher on
    /// start2.bin). Manual Pin/Unpin inside MenYou is disabled — the system
    /// Start is the source of truth. The feature degrades gracefully if
    /// Export-StartLayout is broken on the current SKU: the last good
    /// snapshot is kept and a one-time tray balloon warns the user.
    public bool MirrorWindowsStart { get; set; } = true;

    /// Set to true after we've shown the "mirror unavailable" tray balloon
    /// once, so we don't nag the user every time MenYou starts on a SKU
    /// where Export-StartLayout is broken.
    public bool MirrorUnavailableNotified { get; set; } = false;

    /// AppIds the user has explicitly unpinned from MenYou while the mirror
    /// is active. The mirror reads from Windows but filters these out, so
    /// the user can hide an app from MenYou's Pinned without touching the
    /// Windows Start menu. Entries auto-clear when the corresponding app
    /// leaves Windows' pin list (so re-pinning in Windows restores it).
    public List<string> MirrorExclusions { get; set; } = new();

    /// AppIds the user pinned directly inside MenYou (the "Pin to Start"
    /// context verb). Kept separate from the mirrored Windows pins so they
    /// survive the mirror's per-sync ReplaceAll: that merge lists the
    /// Windows pins first, then appends these — deduplicated, so an app
    /// pinned in BOTH places shows exactly once. In manual mode (mirror
    /// off) the Pinned list is authoritative, but ManualPins is still
    /// maintained so the pins carry over if mirroring is later turned on.
    public List<string> ManualPins { get; set; } = new();

    // ---- Developer / advanced -------------------------------------------

    /// When true (default), app discovery is served from a persisted
    /// snapshot at <c>%AppData%\MenYou\discovery-cache.json</c> on launch —
    /// a plain file read with no shell COM, so the menu paints instantly on
    /// a cold start. The live scan still runs in the background and swaps in
    /// if anything changed. A fast filesystem fingerprint guards against a
    /// stale snapshot ever being shown. Off = always do the live scan.
    public bool UseDiscoveryCache { get; set; } = true;

    /// When true (default), the Start menu reveals immediately on open and
    /// fills its tiles in as discovery resolves, rather than waiting for the
    /// full scan before showing. Only matters on a cold cache miss; with the
    /// cache warm the data is already there. Off = wait for data before
    /// revealing (never shows an empty frame).
    public bool ImmediateMenuReveal { get; set; } = true;

    /// When true, MenYou writes its opt-in hook/timing trace to
    /// <c>%TEMP%\menyou-hooks.log</c>. Off by default — this is a developer
    /// diagnostic. (The <c>MENYOU_TRACE_HOOKS=1</c> environment variable
    /// force-enables it regardless, for debugging before settings load.)
    public bool DiagnosticLogging { get; set; } = false;

    /// Cap for the diagnostic log file in megabytes. On startup a background
    /// sweep deletes the log if it exceeds this size (or is older than a few
    /// days), so it can never grow unbounded. Default 5 MB.
    public int MaxLogSizeMb { get; set; } = 5;
}
