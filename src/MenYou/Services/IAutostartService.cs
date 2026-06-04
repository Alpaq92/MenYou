namespace MenYou.Services;

public interface IAutostartService
{
    bool IsEnabled { get; }
    void SetEnabled(bool enabled);
}
