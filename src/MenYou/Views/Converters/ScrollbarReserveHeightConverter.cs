using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;

namespace MenYou.Views.Converters;

/// For the pinned strip's horizontal ScrollViewer (AppGrid): returns a
/// MinHeight that reserves room below the tiles for the slim overlay
/// horizontal scrollbar ONLY when the content actually overflows
/// horizontally (Extent.Width &gt; Viewport.Width). When everything fits
/// there's no scrollbar, so it returns 0 and the strip stays compact
/// instead of padding dead space under the tiles.
///
/// Toggling a *vertical* size (MinHeight) off a *horizontal* comparison
/// can't feed back into the horizontal extent/viewport, so there's no
/// measure loop.
///
/// Inputs (in order): [ScrollViewer.Extent (Size), ScrollViewer.Viewport (Size)].
public sealed class ScrollbarReserveHeightConverter : IMultiValueConverter
{
    public static readonly ScrollbarReserveHeightConverter Instance = new();

    /// Height the strip grows to while the horizontal bar is shown: the
    /// tile box (~68 px) plus a comfortable gap that drops the bar clear
    /// of the labels.
    private const double WithScrollbar = 90d;

    public object Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Count >= 2
            && values[0] is Size extent
            && values[1] is Size viewport
            && extent.Width > viewport.Width + 0.5)
        {
            return WithScrollbar;
        }
        return 0d;
    }
}
