using Avalonia;
using MenYou.Platform.Windows;

namespace MenYou;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Dev aid: preview the first-run splash in isolation (no app startup, no
        // settings/task side effects) — run "MenYou.exe --splash-test". Lets the
        // splash's look be iterated without a full install/reinstall cycle.
        if (args.Contains("--splash-test"))
        {
            NativeSplash.Show("MenYou", "Setting up MenYou…");
            Thread.Sleep(9000);
            NativeSplash.Close();
            return 0;
        }

        // Earliest-possible one-time first-run splash: shown BEFORE Avalonia and
        // the Fluid theme cold-load — the bulk of the first-start wait on a
        // fresh install. Gated by a bare settings.json existence check (needs
        // nothing initialized); NativeSplash is pure Win32 on its own thread, so
        // it paints while the rest of startup blocks this thread.
        // App.OnFrameworkInitializationCompleted tears it down once the tray +
        // hotkey are live (a safety timer closes it otherwise).
        TryShowFirstRunSplash();

        // MenYou installs/updates via an Inno Setup installer + an in-app
        // GitHub-Releases update check (GitHubUpdateService). Inno handles
        // install / upgrade / uninstall out of process, so there's no boot hook
        // to run here before Avalonia starts.
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    /// Pop the first-run splash (fresh install == no settings.json yet) as the
    /// very first thing the process does, so it covers the cold load that
    /// follows. Best-effort: never let it affect launch.
    private static void TryShowFirstRunSplash()
    {
        try
        {
            var settings = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "MenYou", "settings.json");
            if (!System.IO.File.Exists(settings))
                NativeSplash.Show("MenYou", "Setting up MenYou…");
        }
        catch { /* splash is best-effort */ }
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
