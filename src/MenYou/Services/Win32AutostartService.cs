using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;

namespace MenYou.Services;

/// Autostart via a per-user logon-triggered Scheduled Task rather than the
/// classic HKCU\Run value. Windows deliberately delays + serializes Run-key
/// (and Startup-folder) autostarts after sign-in to keep login responsive —
/// measured on a cold boot, MenYou's Run-key launch fired ~16 s AFTER the
/// desktop was already interactive. A logon-triggered task is exempt from that
/// throttle and fires promptly, so the tray + hotkey come up right when the
/// user reaches their desktop.
///
/// The task runs as the current user with an interactive token at LEAST
/// privilege — explicitly NOT elevated. MenYou installs low-level input hooks
/// and manipulates Explorer's foreground, which only works at the same medium
/// integrity level as the shell; an elevated autostart would break the hooks
/// (UIPI). Creating it needs no admin: a user may freely register tasks that
/// run as themselves at their own integrity level.
///
/// Fallbacks keep autostart working everywhere: if task creation is blocked
/// (locked-down box, group policy) we fall back to the legacy HKCU\Run value —
/// throttled, but functional — and either way we zero the per-user Run-key
/// startup delay (Serialize\StartupDelayInMSec) so that fallback path is as
/// prompt as Windows allows.
[SupportedOSPlatform("windows")]
public sealed class Win32AutostartService : IAutostartService
{
    private const string TaskName = "MenYou";

    // Legacy autostart mechanism — retained only to clean it up on migration
    // and to fall back to it when scheduled-task creation fails.
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "MenYou";

    // Per-user Run/Startup launch delay. Absent or non-zero => Windows applies
    // its default startup-app delay; zeroing it removes the baseline ~10 s wait
    // on the Run-key fallback path. HKCU, so it only affects this user.
    private const string SerializeKey =
        @"Software\Microsoft\Windows\CurrentVersion\Explorer\Serialize";
    private const string StartupDelayValue = "StartupDelayInMSec";

    // Small post-logon delay before the task launches MenYou. Logon-trigger
    // tasks can fire before the shell's notification area (Shell_TrayWnd)
    // exists; a 1 s nudge lets Explorer come up so the tray icon registers on
    // the first try. Measured: the shell was already up ~1 s before the task
    // fired even at PT3S, so PT1S still lands after Explorer while shaving ~2 s
    // off the cold start. Avalonia also re-adds the icon on TaskbarCreated as a
    // backstop. (Vastly faster than the Run-key throttle this replaces.)
    private const string LogonDelay = "PT1S";

    public bool IsEnabled => TaskExists() || RunValueMatchesUs();

    public void SetEnabled(bool enabled)
    {
        if (enabled)
        {
            // Prefer the task. On success drop the legacy Run value so MenYou
            // can't be launched twice — once promptly by the task, once
            // throttled by the Run key.
            if (TryCreateLogonTask())
                RemoveRunValue();
            else
                WriteRunValue();   // fallback: throttled, but functional
            ZeroStartupDelay();    // belt-and-suspenders for the fallback path
        }
        else
        {
            DeleteTask();
            RemoveRunValue();
        }
    }

    // ---- scheduled task --------------------------------------------------

    private static bool TaskExists() => RunSchtasks($"/query /tn \"{TaskName}\"");

    private static void DeleteTask()
    {
        if (TaskExists())
            RunSchtasks($"/delete /tn \"{TaskName}\" /f");
    }

    private static bool TryCreateLogonTask()
    {
        var exe = CurrentExePath();
        if (string.IsNullOrEmpty(exe)) return false;
        var sid = CurrentUserSid();
        if (string.IsNullOrEmpty(sid)) return false;

        var xmlPath = Path.Combine(Path.GetTempPath(), "menyou-autostart.xml");
        try
        {
            // schtasks /xml expects UTF-16; WriteAllText(Encoding.Unicode)
            // emits UTF-16 LE + BOM to match the <?xml encoding="UTF-16"?> head.
            File.WriteAllText(xmlPath, BuildTaskXml(sid, exe), Encoding.Unicode);
            return RunSchtasks($"/create /tn \"{TaskName}\" /xml \"{xmlPath}\" /f");
        }
        catch
        {
            return false;
        }
        finally
        {
            try { File.Delete(xmlPath); } catch { /* temp cleanup best-effort */ }
        }
    }

    /// Task XML: fire at this user's logon (small delay), run as them with an
    /// interactive token at least privilege, no execution time limit (a tray
    /// app runs for the whole session), and start even on battery (laptops).
    private static string BuildTaskXml(string sid, string exePath)
    {
        // <Command> is a single XML value, NOT a command line — Task Scheduler
        // launches the path verbatim, so it must be XML-escaped but NOT quoted.
        var cmd = SecurityElement.Escape(exePath);
        return $"""
        <?xml version="1.0" encoding="UTF-16"?>
        <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
          <RegistrationInfo>
            <Description>Starts MenYou at sign-in (replaces the throttled Run-key autostart).</Description>
            <URI>\{TaskName}</URI>
          </RegistrationInfo>
          <Triggers>
            <LogonTrigger>
              <Enabled>true</Enabled>
              <UserId>{sid}</UserId>
              <Delay>{LogonDelay}</Delay>
            </LogonTrigger>
          </Triggers>
          <Principals>
            <Principal id="Author">
              <UserId>{sid}</UserId>
              <LogonType>InteractiveToken</LogonType>
              <RunLevel>LeastPrivilege</RunLevel>
            </Principal>
          </Principals>
          <!-- Both the SCHEMA VERSION and element ORDER are load-bearing. Task
               Scheduler rejects the whole XML ("unexpected node") if a child is
               out of schema order OR not valid for the declared version. We keep
               the task at 1.2 for the widest compatibility, which means ONLY 1.2
               settings: <UseUnifiedSchedulingEngine> (1.3+) and friends are out
               — including them is what made schtasks reject this twice. Order is
               the schema sequence: AllowStartOnDemand first, ExecutionTimeLimit
               before Enabled, RunOnlyIfIdle before IdleSettings. Don't reshuffle
               and don't add 1.3+ nodes without bumping the version above. -->
          <Settings>
            <AllowStartOnDemand>true</AllowStartOnDemand>
            <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
            <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
            <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
            <AllowHardTerminate>true</AllowHardTerminate>
            <StartWhenAvailable>false</StartWhenAvailable>
            <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
            <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
            <Enabled>true</Enabled>
            <Hidden>false</Hidden>
            <Priority>7</Priority>
            <RunOnlyIfIdle>false</RunOnlyIfIdle>
            <IdleSettings>
              <StopOnIdleEnd>false</StopOnIdleEnd>
              <RestartOnIdle>false</RestartOnIdle>
            </IdleSettings>
          </Settings>
          <Actions Context="Author">
            <Exec>
              <Command>{cmd}</Command>
            </Exec>
          </Actions>
        </Task>
        """;
    }

    /// Invoke schtasks.exe with output swallowed; success == exit 0. Output is
    /// localized, so we never parse it — only the exit code matters. A capped
    /// wait keeps a wedged call from hanging startup / the Settings Apply.
    private static bool RunSchtasks(string arguments)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                // No stream redirection: schtasks output is localized and we
                // only read the exit code. Redirecting the pipes without
                // draining them risks a buffer-fill deadlock against the capped
                // WaitForExit below — the child blocks on write, we time out.
            });
            if (p is null) return false;
            if (!p.WaitForExit(10_000)) { try { p.Kill(); } catch { /* ignore */ } return false; }
            return p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    // ---- legacy Run value + startup-delay tweak --------------------------

    private static bool RunValueMatchesUs()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
        return key?.GetValue(ValueName) is string existing
            && string.Equals(NormalizePath(existing), NormalizePath(CurrentExePath()),
                StringComparison.OrdinalIgnoreCase);
    }

    private static void WriteRunValue()
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        key?.SetValue(ValueName, QuoteIfNeeded(CurrentExePath()), RegistryValueKind.String);
    }

    private static void RemoveRunValue()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        key?.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    private static void ZeroStartupDelay()
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(SerializeKey, writable: true);
            key?.SetValue(StartupDelayValue, 0, RegistryValueKind.DWord);
        }
        catch
        {
            // Best-effort: a policy-locked Explorer key shouldn't fail the whole
            // autostart toggle, and the task path doesn't depend on this.
        }
    }

    // ---- helpers ---------------------------------------------------------

    private static string? CurrentUserSid()
    {
        try { return WindowsIdentity.GetCurrent().User?.Value; }
        catch { return null; }
    }

    private static string CurrentExePath()
    {
        var p = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(p)) return p;
        // Fallback for odd hosts where ProcessPath is empty. MainModule's
        // FileName is single-file-safe; Assembly.Location returns "" inside a
        // single-file bundle (IL3000) and would write a broken autostart path.
        return Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty;
    }

    private static string QuoteIfNeeded(string path) =>
        path.Contains(' ', StringComparison.Ordinal) && !path.StartsWith('"')
            ? $"\"{path}\""
            : path;

    private static string NormalizePath(string path) =>
        path.Trim().Trim('"');
}
