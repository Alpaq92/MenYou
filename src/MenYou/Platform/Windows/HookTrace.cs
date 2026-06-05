using System.Diagnostics;
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
    // Force-on via MENYOU_TRACE_HOOKS=1 for debugging before settings load
    // (or from outside the app entirely). Independent of the in-app toggle.
    private static readonly bool _envEnabled =
        Environment.GetEnvironmentVariable("MENYOU_TRACE_HOOKS") == "1";

    // Driven by the user's "Diagnostic logging" Developer-tab setting; off by
    // default so normal runs never write a log. Volatile because Log() runs on
    // the hook STA threads, the UI thread, and the thread pool.
    private static volatile bool _settingEnabled;

    private static readonly string _path = Path.Combine(Path.GetTempPath(), "menyou-hooks.log");
    private static readonly object _gate = new();

    // OS process-start time, captured once. Lets every log line report
    // ms-since-launch, so a cold-run trace self-reports startup timing (e.g.
    // "data ready at +N ms") without needing the process start correlated
    // externally — the line that matters reads its own latency.
    private static readonly DateTime _procStart = ResolveProcStart();

    private static DateTime ResolveProcStart()
    {
        try { using var p = Process.GetCurrentProcess(); return p.StartTime; }
        catch { return DateTime.Now; }
    }

    public static bool Enabled => _envEnabled || _settingEnabled;

    /// Wired from <c>UserSettings.DiagnosticLogging</c> at startup and on every
    /// change. The env var still force-enables regardless of this.
    public static void SetEnabled(bool enabled) => _settingEnabled = enabled;

    public static void Log(string message)
    {
        if (!Enabled) return;
        try
        {
            var now = DateTime.Now;
            var elapsed = (int)Math.Max(0, (now - _procStart).TotalMilliseconds);
            var line = $"{now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture)} (+{elapsed,5}ms) {message}{Environment.NewLine}";
            lock (_gate) File.AppendAllText(_path, line);
        }
        catch { /* tracing must never throw */ }
    }

    /// Drops the log file if it has grown past <paramref name="maxSizeBytes"/>
    /// or is older than <paramref name="maxAge"/>. Meant to run once on a
    /// background thread at startup, so an old or oversized log from a prior
    /// debugging session is cleared without ever blocking the UI thread.
    public static void Cleanup(long maxSizeBytes, TimeSpan maxAge)
    {
        try
        {
            var info = new FileInfo(_path);
            if (!info.Exists) return;
            var tooBig = maxSizeBytes > 0 && info.Length > maxSizeBytes;
            var tooOld = info.LastWriteTimeUtc < DateTime.UtcNow - maxAge;
            if (tooBig || tooOld)
                lock (_gate) File.Delete(_path);
        }
        catch { /* best-effort cleanup */ }
    }
}
