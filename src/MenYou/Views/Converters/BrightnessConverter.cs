using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace MenYou.Views.Converters;

/// Adjusts the brightness of an Avalonia <see cref="Bitmap"/> by a signed
/// fraction in the parameter. Positive values brighten, negative values
/// dim. Useful range is roughly [-1.0, 1.0]; values outside that clamp at
/// the channel extremes. The parameter is parsed culture-invariantly so
/// XAML literals like <c>Parameter="0.15"</c> work the same across locales.
///
/// Caches per (source, amount) so repeated bindings reuse the same pixel
/// walk. Returns the source unchanged when the amount rounds to zero so we
/// don't pay for a no-op copy.
public sealed class BrightnessConverter : IValueConverter
{
    public static readonly BrightnessConverter Instance = new();

    private readonly Dictionary<(Bitmap source, int delta), Bitmap> _cache = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not Bitmap source) return value;
        var amount = ParseAmount(parameter);
        var delta = (int)Math.Round(amount * 255.0);
        if (delta == 0) return source;

        var key = (source, delta);
        if (!_cache.TryGetValue(key, out var result))
        {
            result = Adjust(source, delta);
            _cache[key] = result;
        }
        return result;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();

    private static double ParseAmount(object? parameter) => parameter switch
    {
        double d => d,
        float f => f,
        decimal m => (double)m,
        int i => i,
        string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) => v,
        _ => 0,
    };

    private static Bitmap Adjust(Bitmap source, int delta)
    {
        var size = source.PixelSize;
        var writeable = new WriteableBitmap(size, source.Dpi, PixelFormat.Bgra8888, AlphaFormat.Premul);
        using (var fb = writeable.Lock())
        {
            source.CopyPixels(new PixelRect(size), fb.Address, fb.RowBytes * size.Height, fb.RowBytes);
            unsafe
            {
                var row = (byte*)fb.Address;
                for (int y = 0; y < size.Height; y++)
                {
                    var px = row;
                    for (int x = 0; x < size.Width; x++)
                    {
                        px[0] = Clamp(px[0] + delta);
                        px[1] = Clamp(px[1] + delta);
                        px[2] = Clamp(px[2] + delta);
                        px += 4;
                    }
                    row += fb.RowBytes;
                }
            }
        }
        return writeable;
    }

    private static byte Clamp(int v) => (byte)(v < 0 ? 0 : v > 255 ? 255 : v);
}
