namespace MenYou.Services;

public interface IWin11StartMirror : IDisposable
{
    /// True after Start() has been called and the file-watcher is live.
    bool IsRunning { get; }

    /// True when the last Export-StartLayout call succeeded. False after a
    /// cmdlet failure (some Win 11 24H2 SKUs are broken). The UI uses this
    /// to show a one-time "mirror unavailable" tray balloon.
    bool LastReadSucceeded { get; }

    string? LastError { get; }

    /// Fires after every successful or failed mirror refresh so the host
    /// (App) can decide whether to surface the "mirror unavailable" toast.
    event Action? StatusChanged;

    /// Starts (or no-ops if already started) the FileSystemWatcher and runs
    /// an initial export. Honors UserSettings.MirrorWindowsStart — if the
    /// setting is off, this is a cheap no-op.
    Task StartAsync();

    /// Stops the watcher. Safe to call if not running.
    void Stop();

    /// Force an immediate re-export, ignoring debounce. Used after the
    /// user toggles MirrorWindowsStart on so they don't wait for the
    /// next start2.bin write.
    Task RefreshNowAsync();
}
