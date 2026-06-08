using System.Windows.Input;
using Avalonia.Controls;
using Avalonia.Media;

namespace MenYou.Views.Behaviors;

/// Builds context-menu items with a width-capped, ellipsis-trimmed header, so a
/// long JumpList entry (a recent file with a very long name, say) can't stretch
/// the menu off-screen. When the text might be trimmed it's also offered as a
/// tooltip so the full name stays reachable on hover. Shared by
/// <see cref="AppContextMenuBehavior"/> and
/// <see cref="Controls.SearchResultsList"/> so every MenYou context menu trims
/// identically.
public static class MenuItemFactory
{
    /// Header width cap before the text trims with "…". Roughly a comfortably
    /// wide menu; long recent-file names trim past it.
    private const double MaxHeaderWidth = 320;

    public static MenuItem Create(string header, ICommand? command = null,
        object? parameter = null, bool tooltip = false)
    {
        var item = new MenuItem
        {
            Header = new TextBlock
            {
                Text = header,
                MaxWidth = MaxHeaderWidth,
                TextTrimming = TextTrimming.CharacterEllipsis,
            },
            Command = command,
            CommandParameter = parameter,
        };
        if (tooltip) ToolTip.SetTip(item, header);
        return item;
    }
}
