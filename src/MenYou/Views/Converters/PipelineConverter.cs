using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Metadata;

namespace MenYou.Views.Converters;

/// Chains a user-defined ordered list of <see cref="PipelineStep"/>s. Each
/// step's <c>Converter</c> is invoked with the prior step's output and its
/// own <c>Parameter</c>. Useful for composing image transforms — e.g. invert
/// then brighten — without writing one-off compound converters.
///
/// Any step may return <see cref="Skip"/> to short-circuit the rest of the
/// pipeline; in that case the original input is returned untouched. Gate
/// converters such as <see cref="ThemeGateConverter"/> use this to make
/// the whole chain conditional on, e.g., the active theme.
public sealed class PipelineConverter : IValueConverter
{
    /// Sentinel a step may return to abort the chain. When PipelineConverter
    /// sees this it stops calling subsequent steps and returns the original
    /// pipeline input. Compare with <see cref="ReferenceEquals"/>.
    public static readonly object Skip = new();

    [Content]
    public List<PipelineStep> Steps { get; } = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var current = value;
        foreach (var step in Steps)
        {
            if (step.Converter is null) continue;
            var result = step.Converter.Convert(current, targetType, step.Parameter, culture);
            if (ReferenceEquals(result, Skip)) return value;
            current = result;
        }
        return current;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class PipelineStep
{
    public IValueConverter? Converter { get; set; }
    public object? Parameter { get; set; }
}
