using System.Globalization;

namespace MenYou.Platform.Windows;

/// File-based tracer for the LL keyboard hook and the WinEvent monitor.
/// Off by default; opt in via env var MENYOU_TRACE_HOOKS=1 — writes to
/// %TEMP%\menyou-hooks.log. Hooks run on tight system-input paths, so we
/// keep the log strictly append-only and best-effort.
internal static class HookTrace
{
    // Opt-in via MENYOU_TRACE_HOOKS=1 (as the summary above promises). Off by
    // default so normal runs never write a log to %TEMP%; flip the env var on
    // only when diagnosing the hook / bridge / foreground paths, where the
    // append-only trace is invaluable.
    private static readonly bool _enabled =
        Environment.GetEnvironmentVariable("MENYOU_TRACE_HOOKS") == "1";

    private static readonly string _path = Path.Combine(Path.GetTempPath(), "menyou-hooks.log");
    private static readonly object _gate = new();

    public static bool Enabled => _enabled;

    public static void Log(string message)
    {
        if (!_enabled) return;
        try
        {
            var line = $"{DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture)} {message}{Environment.NewLine}";
            lock (_gate) File.AppendAllText(_path, line);
        }
        catch { /* tracing must never throw */ }
    }
}
