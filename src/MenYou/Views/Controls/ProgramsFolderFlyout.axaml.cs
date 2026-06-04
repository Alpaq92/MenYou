using System.Linq;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;

namespace MenYou.Views.Controls;

/// Recursive flyout body for a Programs folder. DataContext is supplied
/// by the owning flyout (a FolderItemViewModel); all bindings are
/// per-item, so the only codebehind is the generated InitializeComponent()
/// plus the leaf-click handler that tears the flyout cascade down on
/// launch. The control references itself inside the subfolder
/// DataTemplate's Flyout — that's only realized when a subfolder is
/// opened, so there's no construction-time recursion.
public partial class ProgramsFolderFlyout : UserControl
{
    public ProgramsFolderFlyout()
    {
        InitializeComponent();
    }

    /// Closes the whole open flyout cascade when an app row is clicked.
    /// Launching already hides the main menu window, but on Windows each
    /// flyout level is a separate top-level popup that doesn't go away
    /// with the parent window — so they'd linger on screen after launch.
    /// Avalonia's logical tree links a flyout's content to its Popup, the
    /// Popup to the button that owns it, and that button up into the next
    /// outer flyout, so walking the logical ancestors and closing every
    /// Popup dismisses the entire chain. Posted so the button's launch
    /// Command runs first (Click is raised before the Command executes).
    private void OnAppLeafClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control control) return;
        Dispatcher.UIThread.Post(() =>
        {
            foreach (var popup in control.GetLogicalAncestors().OfType<Popup>().ToList())
                popup.IsOpen = false;
        });
    }
}
