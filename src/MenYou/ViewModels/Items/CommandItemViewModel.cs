using System.Windows.Input;

namespace MenYou.ViewModels.Items;

public sealed class CommandItemViewModel : MenuItemViewModel
{
    public override ICommand? Command { get; }
    public object? CommandParameter { get; }

    public CommandItemViewModel(string displayName, ICommand command, object? parameter = null)
    {
        DisplayName = displayName;
        Command = command;
        CommandParameter = parameter;
    }
}
