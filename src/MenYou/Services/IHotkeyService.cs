using MenYou.Models;

namespace MenYou.Services;

public interface IHotkeyService : IDisposable
{
    /// Set the callback to invoke when any active binding fires. Calling twice
    /// replaces the previous callback. Bindings are not applied until
    /// <see cref="ApplyBindings"/> runs.
    void Initialize(Action onPressed);

    /// Switches between the supported activation strategies based on the
    /// current user settings (real Win key vs Win+F12 fallback). Safe to call
    /// repeatedly when the user toggles the setting.
    void ApplyBindings(UserSettings settings);

    void Unregister();
}
