using MenYou.Models;

namespace MenYou.Services;

public interface IAppDiscoveryService
{
    /// Returns the root of the All Programs tree, merged from the per-user and
    /// common Start Menu directories.
    Task<MenuFolder> BuildProgramsTreeAsync(CancellationToken ct = default);

    /// Flat enumeration of every discovered app for search and quick lookup.
    Task<IReadOnlyList<AppEntry>> GetAllAppsAsync(CancellationToken ct = default);

    AppEntry? FindById(string id);
}
