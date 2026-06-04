using System.Collections;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.VisualTree;
using MenYou.ViewModels.Items;

namespace MenYou.Views.Controls;

public partial class ProgramsTree : UserControl
{
    public static readonly StyledProperty<IEnumerable?> ItemsProperty =
        AvaloniaProperty.Register<ProgramsTree, IEnumerable?>(nameof(Items));

    public IEnumerable? Items
    {
        get => GetValue(ItemsProperty);
        set => SetValue(ItemsProperty, value);
    }

    public ProgramsTree()
    {
        InitializeComponent();
        // Single-click semantics, matching Win 11 / Open-Shell: tap a folder
        // → toggle expand, tap an app → launch. Default TreeView only
        // expands via the chevron and ignores body taps; we add Tapped
        // here to handle the rest of the row.
        Tapped += OnTapped;
        KeyDown += OnKeyDown;
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        var tree = this.GetVisualDescendants().OfType<TreeView>().FirstOrDefault();
        switch (tree?.SelectedItem)
        {
            case AppItemViewModel app:
                app.LaunchCommand.Execute(null);
                e.Handled = true;
                break;
            case FolderItemViewModel:
                // Find the realized container and toggle it.
                if (tree?.SelectedItem is { } selected
                    && tree.TreeContainerFromItem(selected) is TreeViewItem tvi)
                {
                    tvi.IsExpanded = !tvi.IsExpanded;
                    e.Handled = true;
                }
                break;
        }
    }

    private void OnTapped(object? sender, TappedEventArgs e)
    {
        if (e.Source is not Visual src) return;

        // If the user actually clicked the chevron, leave the default
        // TreeView behavior alone — it's already going to toggle.
        if (src.GetSelfAndVisualAncestors().OfType<ToggleButton>().Any()) return;

        var tvi = src.GetSelfAndVisualAncestors().OfType<TreeViewItem>().FirstOrDefault();
        if (tvi is null) return;

        switch (tvi.DataContext)
        {
            case FolderItemViewModel:
                tvi.IsExpanded = !tvi.IsExpanded;
                e.Handled = true;
                break;
            case AppItemViewModel app:
                app.LaunchCommand.Execute(null);
                e.Handled = true;
                break;
        }
    }
}
