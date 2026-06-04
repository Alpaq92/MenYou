using System.Globalization;
using Avalonia.Data.Converters;

namespace MenYou.Views.Converters;

/// True when the bound enum value equals the parameter (parsed against the
/// bound value's enum type). Used to drive style triggers / visibility off the
/// active MenuStyle.
public sealed class EnumEqualsConverter : IValueConverter
{
    public static readonly EnumEqualsConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is null || parameter is null) return false;
        if (value.GetType().IsEnum && parameter is string s)
            return string.Equals(value.ToString(), s, StringComparison.OrdinalIgnoreCase);
        return value.Equals(parameter);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
