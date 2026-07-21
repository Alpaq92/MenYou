using MenYou.Models;

namespace MenYou.ViewModels.Items;

/// <summary>
/// Single source of truth for <see cref="ProgramsOrder"/> semantics — how
/// folders and apps interleave at one level of the "All" section.
/// </summary>
/// <remarks>
/// Shared by <c>ProgramsViewModel.BuildOrdered</c> (which applies the user's
/// Settings preference while building the tree) and
/// <c>ProgramsOrderConverter</c> (the per-theme XAML override), so the two
/// can never diverge on tie-breaking or a future order value. Grouped modes
/// keep each block's incoming relative order; PureAlphabetical sorts the
/// whole level by DisplayName with the same culture-aware comparison the
/// discovery sort uses.
/// </remarks>
public static class ProgramsOrdering
{
    /// <summary>Orders pre-split folder and app sequences.</summary>
    public static IEnumerable<MenuItemViewModel> Apply(
        IEnumerable<MenuItemViewModel> folders,
        IEnumerable<MenuItemViewModel> apps,
        ProgramsOrder order) => order switch
    {
        ProgramsOrder.AppsFirst        => apps.Concat(folders),
        ProgramsOrder.PureAlphabetical => folders.Concat(apps)
            .OrderBy(i => i.DisplayName, StringComparer.CurrentCultureIgnoreCase),
        _                              => folders.Concat(apps), // FoldersFirst
    };

    /// <summary>Orders an already-mixed sequence (the converter's input).</summary>
    public static IEnumerable<MenuItemViewModel> Apply(
        IEnumerable<MenuItemViewModel> items, ProgramsOrder order) =>
        Apply(items.Where(i => i is FolderItemViewModel),
              items.Where(i => i is not FolderItemViewModel),
              order);
}
