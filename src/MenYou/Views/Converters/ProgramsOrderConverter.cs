using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Globalization;
using Avalonia.Data.Converters;
using MenYou.Models;
using MenYou.ViewModels.Items;

namespace MenYou.Views.Converters;

/// <summary>
/// Re-orders a menu-item collection (the "All" section's <c>Programs.Items</c>,
/// or any folder's <c>ChildItems</c>) per a <see cref="ProgramsOrder"/> given
/// as the ConverterParameter — so a CUSTOM THEME can choose its own ordering
/// independently of the app-wide Settings → "All apps order" preference (which
/// ProgramsViewModel applies when it builds the tree; this converter re-orders
/// whatever it is handed, so the two compose).
/// </summary>
/// <remarks>
///   ItemsSource="{Binding Programs.Items,
///       Converter={x:Static conv:ProgramsOrderConverter.Instance},
///       ConverterParameter=PureAlphabetical}"
///
/// Returns a LIVE view, not a snapshot: the view subscribes to the source's
/// CollectionChanged and re-sorts itself when a background refresh rebuilds
/// the source in place — a plain sorting converter would go stale there,
/// because a binding only re-runs its converter when the bound REFERENCE
/// changes. Ordering applies to ONE level; apply the converter again inside
/// the folder item template (on <c>ChildItems</c>) to order nested levels.
///
/// Parameter: a <see cref="ProgramsOrder"/> value or its string name
/// (case-insensitive). Missing/unparsable falls back to PureAlphabetical
/// (the app-wide default).
/// Grouped modes keep each block's incoming relative order; PureAlphabetical
/// sorts the whole level by DisplayName with the same culture-aware
/// comparison discovery uses.
/// </remarks>
public sealed class ProgramsOrderConverter : IValueConverter
{
    public static ProgramsOrderConverter Instance { get; } = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not IEnumerable<MenuItemViewModel> source) return value;

        var order = parameter switch
        {
            ProgramsOrder o => o,
            string s when Enum.TryParse<ProgramsOrder>(s, ignoreCase: true, out var parsed) => parsed,
            _ => ProgramsOrder.PureAlphabetical, // app-wide default; see UserSettings
        };

        // Live view when the source notifies (the VM's ObservableCollections
        // do); plain ordered list otherwise. The view holds a strong handler
        // on the source — both live for the app/theme lifetime here, so no
        // practical leak; a theme that churns bindings should reuse one view.
        return value is INotifyCollectionChanged incc
            ? new OrderedView(source, incc, order)
            : Order(source, order).ToList();
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    // Semantics live in the shared ProgramsOrdering helper (also used by
    // ProgramsViewModel.BuildOrdered) so the app-side and theme-side
    // orderings can never diverge.
    private static IEnumerable<MenuItemViewModel> Order(IEnumerable<MenuItemViewModel> items, ProgramsOrder order) =>
        ProgramsOrdering.Apply(items, order);

    /// The bound, self-resorting projection. Full re-sort on any source
    /// change — sources are a few hundred items at most and refreshes are
    /// rare (install/uninstall, order-setting change), so simplicity wins
    /// over incremental diffing.
    private sealed class OrderedView : ObservableCollection<MenuItemViewModel>
    {
        private readonly IEnumerable<MenuItemViewModel> _source;
        private readonly ProgramsOrder _order;

        public OrderedView(IEnumerable<MenuItemViewModel> source, INotifyCollectionChanged incc, ProgramsOrder order)
        {
            _source = source;
            _order = order;
            incc.CollectionChanged += (_, _) => Resort();
            Resort();
        }

        private void Resort()
        {
            Clear();
            foreach (var item in Order(_source, _order)) Add(item);
        }
    }
}
