using Avalonia.Controls;

namespace MenYou.Views.Layouts;

/// Linux Mint Cinnamon-style built-in layout. Pinned + Recent render via
/// ctrl:AppList (which handles its own launch dispatch), so there's no
/// named Recent ListBox to wire here — the codebehind is just the
/// generated InitializeComponent() call. Bindings resolve against the
/// inherited StartMenuViewModel DataContext at runtime
/// (x:CompileBindings="False" in the .axaml keeps them reflection-based).
public partial class MintCinnamonLayout : UserControl
{
    public MintCinnamonLayout()
    {
        InitializeComponent();
    }
}
