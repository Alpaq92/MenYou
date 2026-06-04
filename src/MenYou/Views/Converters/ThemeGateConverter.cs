using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Styling;

namespace MenYou.Views.Converters;

/// Pipeline gate that opens or closes based on the active app theme. Place
/// as a step inside a <see cref="PipelineConverter"/>: when the current
/// theme matches the parameter (<c>Dark</c>, <c>Light</c>, or <c>All</c>)
/// the step returns the input unchanged so subsequent steps run; when it
/// doesn't match, the step returns <see cref="PipelineConverter.Skip"/> and
/// the pipeline aborts, passing the original input through untouched.
///
/// Parameter accepts a <see cref="ThemeFilter"/> value or its
/// case-insensitive string name from XAML.
public sealed class ThemeGateConverter : IValueConverter
{
    public static readonly ThemeGateConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var filter = ParseFilter(parameter);
        if (filter == ThemeFilter.All) return value;

        var current = Application.Current?.ActualThemeVariant;
        var matches = filter switch
        {
            ThemeFilter.Dark => current == ThemeVariant.Dark,
            ThemeFilter.Light => current == ThemeVariant.Light,
            _ => true,
        };
        return matches ? value : PipelineConverter.Skip;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();

    private static ThemeFilter ParseFilter(object? parameter) => parameter switch
    {
        ThemeFilter t => t,
        string s when Enum.TryParse<ThemeFilter>(s, ignoreCase: true, out var v) => v,
        _ => ThemeFilter.All,
    };
}

public enum ThemeFilter
{
    All,
    Dark,
    Light,
}
