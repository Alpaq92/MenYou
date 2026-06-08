using System.Threading;

namespace MenYou.Platform.Windows;

/// Single-instance guard for MenYou, combining two classic mechanisms:
///
///   #1 a named <b>mutex</b> — the race-free "am I the first instance?"
///      check. The kernel guarantees exactly one creator, and the handle is
///      released automatically when the process dies (even on a hard crash),
///      so there's no stale lock to clean up.
///   #2 a named auto-reset <b>event</b> — a cross-process doorbell. The
///      primary instance parks a background thread on it; a second launch
///      opens the same event, rings it, and exits. The primary's listener
///      wakes and surfaces the Start menu — so re-running MenYou (or a stray
///      second logon-task firing) behaves like clicking the tray icon
///      instead of spawning a duplicate window / tray / hotkey set.
///
/// Names live in the session-local (<c>Local\</c>) namespace, so fast-user-
/// switched sessions each get their own single instance — correct for a
/// per-user Start-menu replacement (a system-wide <c>Global\</c> guard would
/// wrongly block the second signed-in user).
internal static class SingleInstance
{
    private const string MutexName = @"Local\MenYou.SingleInstance.Mutex";
    private const string ShowEventName = @"Local\MenYou.SingleInstance.Show";

    // Held for the lifetime of the primary process so the named objects stay
    // alive (and the mutex stays owned). The OS releases both on exit.
    private static Mutex? _mutex;
    private static EventWaitHandle? _showEvent;
    private static Thread? _listener;

    /// Returns true if this is the first/only instance — the caller should
    /// continue starting up. Returns false if another instance already owns
    /// the guard, in which case this call has already rung its "show"
    /// doorbell and the caller must exit immediately without starting a
    /// second UI.
    public static bool TryAcquire()
    {
        // createdNew is the authority: true only for the single process that
        // created the named mutex.
        _mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        if (createdNew)
        {
            // Primary instance: create the doorbell now (auto-reset, starts
            // unsignaled) so later instances can open and set it.
            _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
            return true;
        }

        // A primary already exists — ring its doorbell so it surfaces, then
        // tell the caller to exit. Best-effort: even if signaling fails we
        // must NOT start a second instance.
        try
        {
            if (EventWaitHandle.TryOpenExisting(ShowEventName, out var existing))
            {
                existing.Set();
                existing.Dispose();
            }
        }
        catch { /* signaling is best-effort */ }

        _mutex.Dispose();
        _mutex = null;
        return false;
    }

    /// Starts the background listener that invokes <paramref name="onShow"/>
    /// each time another instance rings the doorbell. Call once, from the
    /// primary instance, after the UI host is ready. The callback runs on the
    /// listener thread — marshal to the UI thread inside it.
    public static void StartShowListener(Action onShow)
    {
        if (_showEvent is null) return; // not the primary instance — nothing to listen on
        _listener = new Thread(() =>
        {
            while (true)
            {
                try
                {
                    _showEvent.WaitOne();
                }
                catch
                {
                    break; // handle disposed on shutdown — stop listening
                }
                onShow();
            }
        })
        {
            IsBackground = true,
            Name = "MenYou.SingleInstance.Listener",
        };
        _listener.Start();
    }
}
