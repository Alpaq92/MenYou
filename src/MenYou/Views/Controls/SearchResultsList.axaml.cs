using Avalonia.Controls;
using Avalonia.Input;
using MenYou.ViewModels;
using MenYou.ViewModels.Items;

namespace MenYou.Views.Controls;

public partial class SearchResultsList : UserControl
{
    public SearchResultsList()
    {
        InitializeComponent();
        AddHandler(DoubleTappedEvent, OnDoubleTapped);
    }

    private void OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is SearchViewModel vm && vm.Selected is SearchResultViewModel r)
            vm.LaunchCommand.Execute(r);
    }
}
