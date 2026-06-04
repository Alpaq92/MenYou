namespace MenYou.Services;

public interface IPinService
{
    bool IsPinned(string appId);
    void Pin(string appId);
    void Unpin(string appId);
    void Toggle(string appId);
    event Action? Changed;

    /// False when the pin list is a read-only mirror of the Windows 11
    /// Start menu (UserSettings.MirrorWindowsStart=true and the mirror is
    /// active). UI hides the Pin/Unpin context items in that mode.
    bool CanModify { get; }

    /// True when the user can remove the given AppId from MenYou's Pinned.
    /// In manual mode that's always true. In mirror mode it's true only
    /// for currently-pinned items — unpinning adds them to an exclusion
    /// list so the next refresh hides them, while Windows Start stays
    /// untouched.
    bool CanUnpin(string appId);

    /// First-run seeding: if Pinned is still empty and we haven't seeded
    /// before, pin every app whose target matches a .lnk in the user's
    /// taskbar pin folder. Gives a useful starting set on Win 11 where
    /// the Start menu's own pin list (start2.bin) is encrypted and
    /// unreadable to third-party apps. After this fires once we never
    /// re-seed — the user owns their pin list from then on.
    Task EnsureSeededAsync(IAppDiscoveryService discovery);

    /// Replace the entire pin list with the given AppEntry IDs in order.
    /// Used by Win11StartMirrorService to push the system Start's pin
    /// list into MenYou. Saves settings + fires Changed.
    void ReplaceAll(IReadOnlyList<string> appIdsInOrder);
}
