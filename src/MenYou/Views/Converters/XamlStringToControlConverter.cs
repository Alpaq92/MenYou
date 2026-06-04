using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Data.Converters;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;

namespace MenYou.Views.Converters;

/// Live XAML → Control converter, ported from SukiUI's
/// StringToControlConverter pattern (SukiUI.Demo/Converters). Takes the
/// raw text the user is editing in Settings → Use custom theme, wraps
/// it in a minimal root element so single-fragment input is legal XAML,
/// and runs it through <see cref="AvaloniaRuntimeXamlLoader"/>.
///
/// Returns a friendly inline error TextBlock instead of throwing when
/// the parse fails — the dialog stays interactive while the user
/// iterates and sees compile errors live.
public sealed class XamlStringToControlConverter : IValueConverter
{
    public static readonly XamlStringToControlConverter Instance = new();

    private const string RootOpen =
        "<Grid xmlns=\"https://github.com/avaloniaui\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\">";
    private const string RootClose = "</Grid>";

    public object? Convert(object? value, System.Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string xaml || string.IsNullOrWhiteSpace(xaml))
            return ArtboardPlaceholder();

        // If the user already typed a fully-qualified root element with
        // xmlns declarations, parse directly. Otherwise wrap in a Grid
        // so they can type a bare <Button .../> fragment.
        var wrapped = xaml.Contains("xmlns=", System.StringComparison.Ordinal)
            ? xaml
            : RootOpen + xaml + RootClose;

        try
        {
            return AvaloniaRuntimeXamlLoader.Parse<Control>(wrapped);
        }
        catch (System.Exception ex)
        {
            return ErrorBox(ex.Message);
        }
    }

    public object? ConvertBack(object? value, System.Type targetType, object? parameter, CultureInfo culture)
        => throw new System.NotSupportedException();

    // Material Design Icons "artboard" SVG path (viewBox 0 0 24 24).
    // Fetched once from Templarian/MaterialDesign-SVG and embedded so
    // the placeholder needs no asset roundtrip at runtime.
    private const string ArtboardPathData =
        "M17 9V15H7V9H17M19 3H17V6H19V3M7 3H5V6H7V3M23 7H20V9H23V7M19 7H5V17H19V7M4 7H1V9H4V7M23 15H20V17H23V15M4 15H1V17H4V15M19 18H17V21H19V18M7 18H5V21H7V18Z";

    /// Empty-state placeholder shown when the XAML input is blank.
    /// Renders the MDI artboard glyph + a "Preview" label centered in
    /// the preview area.
    ///
    /// IMPORTANT: the Settings preview hosts this inside a
    /// <c>Viewbox Stretch="Uniform"</c> (so real 720×620 themes scale to
    /// fit the pane). That means the *absolute* px size of the glyph is
    /// irrelevant — the Viewbox scales whatever we hand it to fill the
    /// frame. What controls the apparent size is the glyph's size
    /// *relative to the footprint box*: here 44 / 150 ≈ 29 % of the frame
    /// width, a comfortable medium between "fills the whole pane" (no
    /// footprint box) and "a tiny speck" (an oversized footprint).
    private static Control ArtboardPlaceholder()
    {
        var brush = new SolidColorBrush(Color.FromArgb(0xA0, 0xB0, 0xB0, 0xB0));
        var glyph = new Avalonia.Controls.Shapes.Path
        {
            Data = Geometry.Parse(ArtboardPathData),
            Fill = brush,
            Stretch = Stretch.Uniform,
            Width = 44,
            Height = 44,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        var label = new TextBlock
        {
            Text = "Preview",
            Foreground = brush,
            FontSize = 14,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Avalonia.Thickness(0, 8, 0, 0),
        };
        var stack = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Vertical,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        stack.Children.Add(glyph);
        stack.Children.Add(label);

        // Fixed footprint the Viewbox scales as a unit. The glyph is sized
        // relative to THIS box (44 / 110), so shrinking the box makes the
        // placeholder appear larger and enlarging it makes it smaller.
        return new Panel
        {
            Width = 110,
            Height = 95,
            Background = Brushes.Transparent,
            Children = { stack },
        };
    }

    /// Red-tinted error TextBlock shown when AvaloniaRuntimeXamlLoader
    /// throws on the current input. Wrapped in a ScrollViewer so a
    /// multi-line exception message doesn't push the surface out of
    /// the Border's clip rect. Carries a "Parse error" label so the
    /// user knows what they're looking at.
    private static Control ErrorBox(string message)
    {
        var header = new TextBlock
        {
            Text = "Parse error",
            Foreground = new SolidColorBrush(Color.FromRgb(0xC4, 0x2B, 0x1C)),
            FontSize = 12,
            FontWeight = FontWeight.SemiBold,
            Margin = new Avalonia.Thickness(0, 0, 0, 4),
        };
        var body = new TextBlock
        {
            Text = message,
            Foreground = new SolidColorBrush(Color.FromRgb(0xC4, 0x2B, 0x1C)),
            FontFamily = new FontFamily("Cascadia Code, Consolas, monospace"),
            FontSize = 11,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
        };
        var stack = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Vertical,
            Margin = new Avalonia.Thickness(10),
        };
        stack.Children.Add(header);
        stack.Children.Add(body);
        return new ScrollViewer
        {
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            Content = stack,
        };
    }
}
