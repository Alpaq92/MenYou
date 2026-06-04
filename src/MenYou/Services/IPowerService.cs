namespace MenYou.Services;

public interface IPowerService
{
    void Shutdown();
    void Restart();
    void SignOut();
    void Lock();
    void Sleep();
}
