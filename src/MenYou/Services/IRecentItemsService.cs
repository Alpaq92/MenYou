using MenYou.Models;

namespace MenYou.Services;

public interface IRecentItemsService
{
    IReadOnlyList<RecentEntry> Recent { get; }
    void RecordLaunch(string appId);
    void Clear();

    /// Fires after Recent changes (a launch was recorded or the list was
    /// cleared). The Start menu VM subscribes to refresh its bound
    /// collection without waiting for the window to be re-opened.
    event Action? Changed;
}
