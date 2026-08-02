using Avalonia;
using MenYou.Platform.Windows;
using MenYou.Services;

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

        // Get the Start button and the Win key hooked BEFORE Avalonia loads.
        // The UI stack takes seconds to come up on a cold boot, and until it
        // did the hooks weren't in — so an early press opened Windows' Start
        // menu, the exact thing MenYou exists to replace. Presses captured
        // before there's a UI are queued and serviced once it appears; see
        // EarlyStartup.
        EarlyStartup.Run();

        // MenYou installs/updates via an Inno Setup installer + an in-app
        // GitHub-Releases update check (GitHubUpdateService). Inno handles
        // install / upgrade / uninstall out of process, so there's no other
        // boot hook to run here.
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Cold start is dominated by loading the UI stack, not by MenYou's own init:
    // a traced cold boot reached the first line of our code at +7.3 s and then
    // finished every sync step (cache preload, tray, hooks, bridge) in 244 ms.
    // So the levers here are about NOT loading bytes we never use.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            // WithInterFont() is deliberately NOT called: it loads
            // Avalonia.Fonts.Inter.dll (~1.8 MB) and registers a font no style
            // in this app asks for — every FontFamily here is Segoe-based
            // ("Segoe UI Variable, Segoe UI", "Segoe Fluent Icons", "Cascadia
            // Code, Consolas, monospace"), all present on Win10+. Pure load-path
            // weight for a fallback that never resolves.
            .With(new Win32PlatformOptions
            {
                // Default is [AngleEgl, Wgl, Software], so the ANGLE path pulls
                // av_libGLESv2.dll (~5.1 MB) plus the OpenGL/Vulkan assemblies
                // into a cold start to draw what is, visually, a list of tiles.
                // Software keeps the same Skia rasterizer, just without the GPU
                // bring-up — and the window stays a per-pixel-alpha composition
                // surface, so the rounded corners and the card's BoxShadow are
                // unaffected (verified on screen).
                RenderingMode = [Win32RenderingMode.Software],
            })
            .LogToTrace();
}
