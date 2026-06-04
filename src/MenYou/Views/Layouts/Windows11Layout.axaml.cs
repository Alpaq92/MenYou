using Avalonia.Controls;

namespace MenYou.Views.Layouts;

/// Windows 11-style built-in layout. Unlike Win7Layout / Classic*Layout
/// it has no named Recent ListBox to wire (Recent is a plain ItemsControl
/// of launch buttons), so the codebehind is just the generated
/// InitializeComponent() call. Bindings resolve against the inherited
/// StartMenuViewModel DataContext at runtime (x:CompileBindings="False"
/// in the .axaml keeps them reflection-based).
public partial class Windows11Layout : UserControl
{
    public Windows11Layout()
    {
        InitializeComponent();
    }
}
