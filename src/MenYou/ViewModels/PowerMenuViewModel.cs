using CommunityToolkit.Mvvm.Input;
using MenYou.Services;

namespace MenYou.ViewModels;

public sealed partial class PowerMenuViewModel : ViewModelBase
{
    private readonly IPowerService _power;

    public PowerMenuViewModel(IPowerService power) => _power = power;

    [RelayCommand] private void Shutdown() => _power.Shutdown();
    [RelayCommand] private void Restart() => _power.Restart();
    [RelayCommand] private void SignOut() => _power.SignOut();
    [RelayCommand] private void Lock() => _power.Lock();
    [RelayCommand] private void Sleep() => _power.Sleep();
}
