using System.Runtime.Versioning;
using MenYou.Platform.Windows;

namespace MenYou.Services;

/// Boot work that runs BEFORE Avalonia loads (called from Program.Main).
///
/// A traced cold boot reaches the first line of MenYou's own code at +7.3 s,
/// and the input hooks only went in later still, from
/// OnFrameworkInitializationCompleted. For that whole window the Start button
/// and the Win key were unhooked — so pressing Start right after logging in
/// opened WINDOWS' Start menu, which is the one thing installing MenYou is
/// meant to prevent. Nothing forced that ordering: StartClickHook, WinKeyHook
/// and HotkeyWindow each own an STA thread with its own GetMessage pump and
/// have no Avalonia dependency, so they can go in first.
///
/// A press that lands before the UI exists is QUEUED rather than passed
/// through — see Win32HotkeyService. Installing the hooks early only helps if
/// the press it captures eventually opens something.
///
/// The instances built here are handed to DI in App.BuildServices: the app
/// must keep ONE settings service (a second would fork the settings state)
/// and ONE hotkey service (a second would install a second set of low-level
/// hooks).
[SupportedOSPlatform("windows")]
internal static class EarlyStartup
{
    /// Non-null once Run() has loaded settings successfully.
    public static SettingsService? Settings { get; private set; }

    /// Non-null once Run() has installed the hooks successfully.
    public static Win32HotkeyService? Hotkeys { get; private set; }

    /// Non-null once Run() has installed the hooks successfully. Set together
    /// with <see cref="Hotkeys"/> — the listener only means anything if there
    /// is a hotkey service to route it into.
    public static CopyDataListener? Ipc { get; private set; }

    public static void Run()
    {
        // Loading settings is just a JSON read (no Avalonia, no shell COM) and
        // it does not create the file when absent, so App's first-run probe
        // still sees a missing settings.json.
        try
        {
            Settings = new SettingsService();
            // Trace early presses too. HookTrace still honours
            // MENYOU_TRACE_HOOKS on its own; this only adds the user setting.
            HookTrace.SetEnabled(Settings.Current.DiagnosticLogging);
        }
        catch (Exception ex)
        {
            HookTrace.Log($"EarlyStartup: settings load failed ({ex.GetType().Name}) — DI will build its own");
            Settings = null;
            return;
        }

        try
        {
            Hotkeys = new Win32HotkeyService();

            // The listener MUST exist before ApplyBindings injects the bridge.
            // On Win 11 24H2 the bridge is what catches the lone Win-tap: it
            // neuters the system Start menu and posts WM_COPYDATA to this
            // window. Inject first and the suppression is live while there is
            // nothing to receive the notification — the press would disappear
            // entirely, which is worse than the system menu opening.
            Ipc = new CopyDataListener();
            Ipc.Received += _ => Hotkeys?.Trigger();

            Hotkeys.ApplyBindings(Settings.Current);
            HookTrace.Log("EarlyStartup: input hooks installed before Avalonia");
        }
        catch (Exception ex)
        {
            // Dispose rather than just dropping the references: a partially
            // installed hook set (or a second message-only window under the
            // same well-known class name) left behind would be joined by the
            // fresh ones DI builds, and the Win key would fire twice.
            HookTrace.Log($"EarlyStartup: hook install failed ({ex.GetType().Name}) — DI will build its own");
            try { Hotkeys?.Dispose(); } catch { /* best-effort */ }
            try { Ipc?.Dispose(); } catch { /* best-effort */ }
            Hotkeys = null;
            Ipc = null;
        }
    }
}
