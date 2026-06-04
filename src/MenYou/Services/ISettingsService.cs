using MenYou.Models;

namespace MenYou.Services;

public interface ISettingsService
{
    UserSettings Current { get; }
    event Action? Changed;
    void Save();
    void Reset();
}
