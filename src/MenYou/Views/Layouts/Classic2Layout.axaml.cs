using Avalonia.Controls;
using Avalonia.Input;
using MenYou.ViewModels;
using MenYou.ViewModels.Items;

namespace MenYou.Views.Layouts;

public partial class Classic2Layout : UserControl
{
    public Classic2Layout()
    {
        InitializeComponent();
        // Same pattern as Win7Layout: mouse double-click on a per-app
        // Recent destination opens it. The Enter keybinding declared in
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
