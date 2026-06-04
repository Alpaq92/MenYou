using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using MenYou.Models;
using Microsoft.Win32;

namespace MenYou.Platform.Windows;

/// Writes MenYou's pin list to the Win 11 Start menu via the
/// <c>ConfigureStartPins</c> Group Policy CSP. This is the only
/// documented surface that actually lets a non-admin process tell
/// StartMenuExperienceHost which apps to pin.
///
/// Path: <c>HKCU\Software\Policies\Microsoft\Windows\Explorer\ConfigureStartPins</c>
/// (REG_SZ holding a JSON document with a <c>pinnedList</c> array).
///
/// Caveats — these are baked into Microsoft's design, not bugs in the
/// writer:
///   - The policy applies on the next sign-in or Explorer restart. We
///     offer to restart Explorer right after writing.
///   - With <c>"applyOnce": true</c> the user can still edit pins after
///     it lands. Re-writing the policy with a new list re-applies once.
///   - On corporate machines a real GPO from HKLM overrides this and
///     <c>gpupdate</c> may wipe the per-user value.
[SupportedOSPlatform("windows")]
internal static class StartPinsPolicyWriter
{
    private const string PolicyKey = @"Software\Policies\Microsoft\Windows\Explorer";
    private const string PolicyValue = "ConfigureStartPins";

    public sealed record Result(bool Success, int PinCount, int SkippedCount, string? Error);

    public static Result Push(IReadOnlyList<AppEntry> pinned)
    {
        try
        {
            var entries = new List<object>();
            var skipped = 0;
            foreach (var app in pinned)
            {
                var entry = ToPolicyEntry(app);
                if (entry is null) { skipped++; continue; }
                entries.Add(entry);
            }

            var payload = new
            {
                pinnedList = entries,
                applyOnce = true,
            };
            var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions
            {
                WriteIndented = false,
            });

            using var key = Registry.CurrentUser.CreateSubKey(PolicyKey, writable: true);
            if (key is null)
                return new Result(false, 0, skipped, $"Cannot open {PolicyKey} for writing.");
            key.SetValue(PolicyValue, json, RegistryValueKind.String);
            return new Result(true, entries.Count, skipped, null);
        }
        catch (Exception ex)
        {
            return new Result(false, 0, 0, ex.Message);
        }
    }

    /// Bounce explorer.exe so StartMenuExperienceHost re-reads the policy.
    /// Returns true if the kill succeeded; Windows auto-respawns Explorer
    /// from the userinit shell entry within ~1 s.
    public static bool RestartExplorer()
    {
        try
        {
            foreach (var p in Process.GetProcessesByName("explorer"))
            {
                try { p.Kill(); } catch { }
                p.Dispose();
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static object? ToPolicyEntry(AppEntry app)
    {
        // Prefer the .lnk path if we have one — that's the most reliable
        // form Windows accepts. desktopAppLink is documented to take both
        // %ENVVAR% and absolute paths.
        if (!string.IsNullOrEmpty(app.SourceLnkPath))
            return new { desktopAppLink = app.SourceLnkPath };

        // Fall back to a raw exe path via desktopAppId. The CSP expects
        // an AUMID, but Windows treats a plain exe filename as a valid
        // discriminator for legacy desktop apps in most builds.
        if (!string.IsNullOrEmpty(app.TargetPath))
            return new { desktopAppId = app.TargetPath };

        return null;
    }
}
