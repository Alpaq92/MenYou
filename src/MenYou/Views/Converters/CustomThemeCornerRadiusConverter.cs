using System;
using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;

namespace MenYou.Views.Converters;

/// Maps <c>UseCustomTheme</c> to the menu window's corner radius: the
/// built-in layouts keep MenYou's rounded 10 px chrome, while a loaded
/// custom theme gets square (0) corners so it owns its own edge
/// treatment. Without this the window's rounded Border + clip would
/// round off the corners of every custom theme — e.g. the Windows7Square
/// sample looked rounded despite squaring everything inside.
public sealed class CustomThemeCornerRadiusConverter : IValueConverter
{
    public static readonly CustomThemeCornerRadiusConverter Instance = new();

    private static readonly CornerRadius Rounded = new(10);
    private static readonly CornerRadius Square = new(0);

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? Square : Rounded;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
