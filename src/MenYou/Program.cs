using Avalonia;

namespace MenYou;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // MenYou installs/updates via an Inno Setup installer (see
        // installer/inno/menyou.iss) and an in-app GitHub-Releases update
        // check (GitHubUpdateService). Inno handles install / upgrade /
        // uninstall and its own Restart-Manager-driven relaunch entirely
        // out of process, so there's no boot hook to run here before
        // Avalonia starts.
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
