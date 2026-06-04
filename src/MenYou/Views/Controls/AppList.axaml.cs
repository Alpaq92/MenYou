using System.Collections;
using Avalonia;
using Avalonia.Controls;

namespace MenYou.Views.Controls;

public partial class AppList : UserControl
{
    public static readonly StyledProperty<IEnumerable?> ItemsProperty =
        AvaloniaProperty.Register<AppList, IEnumerable?>(nameof(Items));

    public IEnumerable? Items
    {
        get => GetValue(ItemsProperty);
        set => SetValue(ItemsProperty, value);
    }

    public AppList() => InitializeComponent();
}
