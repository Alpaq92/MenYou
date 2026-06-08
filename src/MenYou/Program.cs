using Avalonia;
using MenYou.Platform.Windows;

namespace MenYou;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Single-instance guard (named mutex + cross-process "show" doorbell).
        // If MenYou is already running in this session, TryAcquire signals the
        // existing instance to surface its menu and returns false — so we exit
        // here instead of spawning a duplicate tray icon / hotkey / window.
        if (!SingleInstance.TryAcquire())
            return 0;

        // MenYou installs/updates via an Inno Setup installer + an in-app
        // GitHub-Releases update check (GitHubUpdateService). Inno handles
        // install / upgrade / uninstall out of process, so there's no boot hook
        // to run here before Avalonia starts.
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
