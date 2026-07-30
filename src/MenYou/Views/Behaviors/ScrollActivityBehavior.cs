using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace MenYou.Views.Behaviors;

/// Pulses a <c>scrolling</c> style class on a ScrollViewer while its offset is
/// actively changing, then drops it after a short linger. Styles/Scrollbar.axaml
/// opts every ScrollViewer in via its global style and uses the class to keep
/// the overlay scrollbar visible during wheel / keyboard / programmatic
/// scrolling — the pointer-over reveal alone would hide the bar exactly while
/// arrow-key navigation is moving the list under a parked pointer.
///
/// Extent/viewport-only changes are ignored on purpose: they fire on every
/// content (re)load, and reacting to them would flash the bar each time the
/// menu opens or a list rebuilds.
public sealed class ScrollActivityBehavior
{
    private ScrollActivityBehavior() { } // attached-property host; never instantiated

    /// How long the class (and so the bar) lingers after the last offset change.
    private static readonly TimeSpan Linger = TimeSpan.FromMilliseconds(900);

    public static readonly AttachedProperty<bool> EnableProperty =
        AvaloniaProperty.RegisterAttached<ScrollActivityBehavior, ScrollViewer, bool>("Enable");

    public static void SetEnable(ScrollViewer viewer, bool value) => viewer.SetValue(EnableProperty, value);
    public static bool GetEnable(ScrollViewer viewer) => viewer.GetValue(EnableProperty);

    /// Per-instance linger timer, parked on the viewer so this class stays a
    /// stateless attached-property host.
    private static readonly AttachedProperty<DispatcherTimer?> TimerProperty =
        AvaloniaProperty.RegisterAttached<ScrollActivityBehavior, ScrollViewer, DispatcherTimer?>("Timer");

    static ScrollActivityBehavior()
    {
        EnableProperty.Changed.AddClassHandler<ScrollViewer>(OnEnableChanged);
    }

    private static void OnEnableChanged(ScrollViewer viewer, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.GetNewValue<bool>())
        {
            viewer.ScrollChanged += OnScrollChanged;
        }
        else
        {
            viewer.ScrollChanged -= OnScrollChanged;
            viewer.GetValue(TimerProperty)?.Stop();
            viewer.Classes.Set("scrolling", false);
        }
    }

    private static void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (sender is not ScrollViewer viewer) return;
        // ScrollChanged bubbles: a nested list's scroll would otherwise light
        // up every ancestor ScrollViewer's bar too. React only to our own.
        if (!ReferenceEquals(e.Source, viewer)) return;
        // Offset movement only — see the class doc for why extent/viewport
        // deltas (content loads) must not reveal the bar.
        if (e.OffsetDelta == default) return;

        viewer.Classes.Set("scrolling", true);

        var timer = viewer.GetValue(TimerProperty);
        if (timer is null)
        {
            timer = new DispatcherTimer { Interval = Linger };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                viewer.Classes.Set("scrolling", false);
            };
            viewer.SetValue(TimerProperty, timer);
        }
        // Restart the linger on every movement, so the bar stays up while
        // scrolling is continuous and fades ~1 s after it stops.
        timer.Stop();
        timer.Start();
    }
}
