using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using MenYou.ViewModels;
using MenYou.ViewModels.Items;

namespace MenYou.Views.Controls;

public partial class SearchResultsList : UserControl
{
    /// When true, a single tap on a result launches it — matching the Pinned /
    /// Recent tiles and the All-Programs tree, which all launch on a single
    /// click. The Win 11 + Mint layouts set this: their search overlay has no
    /// side context panel, so there's nothing to "select to preview" and a
    /// click should just launch. The Win 7 / Classic layouts leave it false —
    /// there a single click selects a result to populate the adjacent
    /// Recent-files / Tasks panel, and a double-click (or Enter) launches.
    public static readonly StyledProperty<bool> SingleClickLaunchProperty =
        AvaloniaProperty.Register<SearchResultsList, bool>(nameof(SingleClickLaunch));

    public bool SingleClickLaunch
    {
        get => GetValue(SingleClickLaunchProperty);
        set => SetValue(SingleClickLaunchProperty, value);
    }

    public SearchResultsList()
    {
        InitializeComponent();
        AddHandler(TappedEvent, OnTapped);
        AddHandler(DoubleTappedEvent, OnDoubleTapped);
    }

    private void OnTapped(object? sender, TappedEventArgs e)
    {
        if (SingleClickLaunch) LaunchFrom(e.Source);
    }

    private void OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        // Only the double-click layouts (Win 7 / Classic, SingleClickLaunch =
        // false) launch on double-tap. When SingleClickLaunch is on, the first
        // tap already launched via OnTapped, so launching here would fire the
        // result a second time.
        if (!SingleClickLaunch) LaunchFrom(e.Source);
    }

    /// Launches the result whose row was tapped. Resolves the row from the
    /// event source (the ListBoxItem's data context) rather than the
    /// view-model's Selected, so a tap launches exactly the clicked row even
    /// if a still-settling search just reconciled the list.
    private void LaunchFrom(object? source)
    {
        if (DataContext is not SearchViewModel vm) return;
        var r = (source as Visual)?
                    .GetSelfAndVisualAncestors()
                    .OfType<ListBoxItem>()
                    .FirstOrDefault()?.DataContext as SearchResultViewModel
                ?? vm.Selected;
        if (r is not null) vm.LaunchCommand.Execute(r);
    }
}
