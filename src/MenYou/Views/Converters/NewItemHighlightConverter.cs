using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace MenYou.Views.Converters;

/// Returns the AccentSubtleBrush when the bound boolean is true,
/// otherwise <see cref="Brushes.Transparent"/>. Used to flash a soft
/// accent wash over Pinned / Recent rows that just got added — same
/// concept as Open-Shell's "new item" flash but drives an Avalonia
/// brush rather than a Win32 ListView background.
///
/// Pulls the brush from the app resources (theme-aware: lighter on
/// light, brighter on dark) so it tracks the user's current theme.
public sealed class NewItemHighlightConverter : IValueConverter
{
    public static readonly NewItemHighlightConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not bool b || !b) return Brushes.Transparent;
        if (Application.Current is { } app
            && app.Resources.TryGetResource("AccentSubtleBrush", null, out var brush)
            && brush is IBrush ib)
            return ib;
        return Brushes.Transparent;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
