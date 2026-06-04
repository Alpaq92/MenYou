using Avalonia.Controls;
using Avalonia.Input;
using MenYou.ViewModels;
using MenYou.ViewModels.Items;

namespace MenYou.Views.Layouts;

public partial class Win7Layout : UserControl
{
    public Win7Layout()
    {
        InitializeComponent();
        // DoubleTapped is routed; wire once on the RecentList ListBox so a
        // mouse double-click on a per-app Recent destination opens it —
        // mirrors the SearchResultsList behavior. The Enter keybinding in
        // XAML handles the keyboard path.
        RecentList.DoubleTapped += OnRecentDoubleTapped;
    }

    private void OnRecentDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is StartMenuViewModel vm
            && vm.Search.SelectedRecent is SearchResultViewModel.RecentDestination dest)
        {
            vm.Search.OpenRecentCommand.Execute(dest);
        }
    }
}
