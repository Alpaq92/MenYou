using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace MenYou.Views.Converters;

/// Inverts the RGB channels of an Avalonia <see cref="Bitmap"/>, preserving
/// alpha. Theme-agnostic — wrap it in a <see cref="PipelineConverter"/> with
/// <c>DarkModeOnly=True</c> when only dark-mode behavior is wanted.
///
/// The inverted bitmap is cached per source reference so repeated bindings
/// don't repeat the per-pixel walk.
public sealed class InvertConverter : IValueConverter
{
    public static readonly InvertConverter Instance = new();

    private readonly Dictionary<Bitmap, Bitmap> _cache = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not Bitmap source) return value;
        if (!_cache.TryGetValue(source, out var result))
        {
            result = Invert(source);
            _cache[source] = result;
        }
        return result;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();

    private static Bitmap Invert(Bitmap source)
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
                        px[0] = (byte)(255 - px[0]);
                        px[1] = (byte)(255 - px[1]);
                        px[2] = (byte)(255 - px[2]);
                        px += 4;
                    }
                    row += fb.RowBytes;
                }
            }
        }
        return writeable;
    }
}
