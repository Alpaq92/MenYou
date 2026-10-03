using System.Globalization;
using System.Text;
using MenYou.Platform.Windows;

namespace MenYou.Services;

/// Keeps a crash from silently ending MenYou.
///
/// MenYou IS the Start menu: when the process dies, the Win-key hook dies with
/// it and the system menu quietly takes over, so a crash looks exactly like
/// "MenYou was never running" and nothing tells the user otherwise. Two jobs:
///
/// RECORD — every crash goes to %LOCALAPPDATA%\MenYou\crash.log whether or not
/// diagnostic logging is on. Crash handling used to log only through HookTrace,
/// which is off by default, so the default install left no trace at all.
///
/// RECOVER — a fatal exception schedules a fresh MenYou before the process goes
/// down (AppRelaunch). This replaces the RestartOnFailure setting on the logon
/// task, which turned out to do nothing for crashes: Task Scheduler applies it
/// only when a task fails to START, never to the exit code of what it runs
/// (reported independently by kcap-cli #1274 and hermes-agent #91097). Only the
/// process itself can see its own crash.
///
/// What does NOT relaunch, by design or by necessity:
///   • a deliberate Exit, and a kill from Task Manager — no handler runs, and
///     both are the user's decision;
///   • a native crash (access violation in native code, stack overflow,
///     FailFast) — the CLR terminates without running managed handlers.
///   • a crash loop: after MaxRelaunches fatal crashes within RelaunchWindow it
///     stops, so a crash at startup cannot respawn forever.
internal static class CrashGuard
{
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MenYou", "crash.log");

    private const long MaxLogBytes = 256 * 1024;   // trimmed to its last quarter beyond this
    private const int MaxRelaunches = 3;
    private static readonly TimeSpan RelaunchWindow = TimeSpan.FromMinutes(10);

    // A UI fault burst: this many within this window means the UI is broken,
    // not glitching once (a binding that throws every frame trips it at once).
    private const int UiBurst = 5;
    private static readonly TimeSpan UiBurstWindow = TimeSpan.FromSeconds(10);

    private const string FatalTag = " FATAL ";

    private static readonly object Gate = new();
    private static readonly Queue<DateTime> UiFaults = new();
    private static bool _uiFaultsSuppressed;
    private static int _handlingFatal;

    /// BCL-only on purpose, so Program.Main can install it before EarlyStartup
    /// without loading Avalonia ahead of the early hooks. The UI-thread handler
    /// is wired separately in App, through <see cref="OnUiException"/>.
    public static void Install()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            OnFatal(e.ExceptionObject as Exception, e.IsTerminating);

        // Since .NET 4.5 an unobserved task fault does NOT terminate the process,
        // so it is not a crash and does not go to the always-on crash log —
        // routine background faults (an icon that fails to extract) would only
        // add noise. It is still recorded when diagnostic logging is on.
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            if (HookTrace.Enabled)
                HookTrace.Log($"Unobserved task fault: {e.Exception.GetType().Name}: {e.Exception.Message}");
            e.SetObserved();
        };
    }

    /// Decides whether a UI-thread exception is survived. Returns the value for
    /// Dispatcher's UnhandledException Handled flag.
    ///
    /// A one-off is survived — a glitched frame is better than losing the menu.
    /// A burst means the UI is broken, and swallowing forever would keep a
    /// broken process alive with nothing ever replacing it; so the exception is
    /// let through, becomes fatal, and OnFatal relaunches a fresh process.
    /// Except when relaunches are already exhausted: then a degraded but living
    /// menu beats a dead one, and further faults stop being logged so a
    /// per-frame fault cannot churn the disk.
    public static bool OnUiException(Exception ex)
    {
        bool burst, suppressed;
        lock (Gate)
        {
            suppressed = _uiFaultsSuppressed;
            var now = DateTime.UtcNow;
            UiFaults.Enqueue(now);
            while (UiFaults.Count > 0 && now - UiFaults.Peek() > UiBurstWindow) UiFaults.Dequeue();
            burst = UiFaults.Count >= UiBurst;
        }
        if (suppressed) return true;
        Record("UI", ex);
        if (!burst) return true;
        if (RecentFatalCount() < MaxRelaunches)
        {
            Write($"{UiBurst} UI faults within {UiBurstWindow.TotalSeconds:0}s: letting the process end so a fresh one replaces it");
            return false;
        }
        lock (Gate) _uiFaultsSuppressed = true;
        Write("UI fault burst, but relaunches are exhausted: staying alive degraded; further UI faults are not logged");
        return true;
    }

    private static void OnFatal(Exception? ex, bool terminating)
    {
        if (Interlocked.Exchange(ref _handlingFatal, 1) == 1) return;
        // Count BEFORE recording this crash, so the cap means "this many
        // relaunches", not one fewer.
        var allowed = RecentFatalCount() < MaxRelaunches;
        Record("FATAL", ex);
        if (!terminating) return;
        if (!allowed)
            Write($"not relaunching: {MaxRelaunches} crashes within {RelaunchWindow.TotalMinutes:0} min");
        else if (AppRelaunch.Schedule())
            Write("relaunch scheduled");
        else
            Write("relaunch could not be scheduled");
    }

    private static void Record(string kind, Exception? ex)
    {
        var version = typeof(CrashGuard).Assembly.GetName().Version?.ToString(3) ?? "?";
        var sb = new StringBuilder();
        sb.Append(Stamp()).Append(' ').Append(kind).Append(' ').Append(version).Append(' ')
          .Append(ex is null ? "(non-Exception object)" : $"{ex.GetType().FullName}: {ex.Message}")
          .AppendLine();
        if (ex?.StackTrace is { } trace)
            foreach (var line in trace.Split('\n'))
                sb.Append("    ").AppendLine(line.TrimEnd('\r'));
        Append(sb.ToString());
        HookTrace.Log($"{kind}: {ex?.GetType().Name}: {ex?.Message} (recorded in crash.log)");
    }

    private static void Write(string message) =>
        Append($"{Stamp()} {message}{Environment.NewLine}");

    private static string Stamp() =>
        DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

    private static void Append(string text)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                var info = new FileInfo(LogPath);
                if (info.Exists && info.Length > MaxLogBytes)
                {
                    // Keep the most recent quarter rather than deleting: the
                    // relaunch cap reads its history from this file.
                    var all = File.ReadAllText(LogPath);
                    File.WriteAllText(LogPath, all[^(int)Math.Min(all.Length, MaxLogBytes / 4)..]);
                }
                File.AppendAllText(LogPath, text);
            }
        }
        catch { /* crash handling must never throw */ }
    }

    /// Fatal crashes recorded within RelaunchWindow, read back from crash.log so
    /// the count survives the relaunches it is meant to limit.
    private static int RecentFatalCount()
    {
        try
        {
            if (!File.Exists(LogPath)) return 0;
            var cutoff = DateTime.UtcNow - RelaunchWindow;
            var count = 0;
            foreach (var line in File.ReadLines(LogPath))
            {
                if (line.Length < 25 || !line.Contains(FatalTag)) continue;
                if (DateTime.TryParse(line[..24], CultureInfo.InvariantCulture,
                        DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t)
                    && t >= cutoff)
                    count++;
            }
            return count;
        }
        catch
        {
            return 0;
        }
    }
}
