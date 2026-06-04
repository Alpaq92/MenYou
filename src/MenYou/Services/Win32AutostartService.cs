using System.Runtime.Versioning;
using Microsoft.Win32;

namespace MenYou.Services;

[SupportedOSPlatform("windows")]
public sealed class Win32AutostartService : IAutostartService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "MenYou";

    public bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
            return key?.GetValue(ValueName) is string existing
                && string.Equals(NormalizePath(existing), NormalizePath(CurrentExePath()),
                    StringComparison.OrdinalIgnoreCase);
        }
    }

    public void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        if (key is null) return;
        if (enabled)
            key.SetValue(ValueName, QuoteIfNeeded(CurrentExePath()), RegistryValueKind.String);
        else
            key.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    private static string CurrentExePath()
    {
        var p = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(p)) return p;
        // Fallback for tests / odd hosts
        return System.Reflection.Assembly.GetExecutingAssembly().Location;
    }

    private static string QuoteIfNeeded(string path) =>
        path.Contains(' ', StringComparison.Ordinal) && !path.StartsWith('"')
            ? $"\"{path}\""
            : path;

    private static string NormalizePath(string path) =>
        path.Trim().Trim('"');
}
