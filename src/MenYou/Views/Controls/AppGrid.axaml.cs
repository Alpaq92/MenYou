using System.Collections;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;

namespace MenYou.Views.Controls;

public partial class AppGrid : UserControl
{
    public static readonly StyledProperty<IEnumerable?> ItemsProperty =
        AvaloniaProperty.Register<AppGrid, IEnumerable?>(nameof(Items));

    public IEnumerable? Items
    {
        get => GetValue(ItemsProperty);
        set => SetValue(ItemsProperty, value);
    }

    public AppGrid() => InitializeComponent();

    /// Mouse wheel scrolls horizontally for the pinned strip. By default
    /// Avalonia's ScrollViewer routes the wheel to vertical offset, but
    /// this control's vertical scrolling is disabled — so the wheel
    /// would do nothing. Translate the wheel delta to horizontal offset
    /// instead.
    private void OnPointerWheel(object? sender, PointerWheelEventArgs e)
    {
        if (sender is not ScrollViewer sv) return;
        // Negative Y means scrolling down → move list to the right
        // (later items), which matches the same gesture users use to
        // scroll a horizontal tile strip on Win 11's Start.
        var delta = e.Delta.Y;
        if (delta == 0) return;
        var step = sv.Viewport.Width * 0.25; // a quarter-page per notch
        var newOffset = sv.Offset.X - delta * step;
        newOffset = System.Math.Max(0, System.Math.Min(newOffset, sv.Extent.Width - sv.Viewport.Width));
        sv.Offset = new Avalonia.Vector(newOffset, sv.Offset.Y);
        e.Handled = true;
    }
}
