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

        // Record and recover from crashes; see CrashGuard. BCL-only, so it can go
        // in before EarlyStartup without loading Avalonia ahead of the hooks.
        CrashGuard.Install();

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
        // The rendering mode must be chosen BEFORE AppBuilder is configured,
        // and it depends on a user setting. EarlyStartup has already loaded
        // settings for the hooks, so read it from there rather than parsing
        // settings.json a second time on the cold-start path. Null only if
        // EarlyStartup failed outright, in which case the default (off) holds.
        return BuildAvaloniaApp(EarlyStartup.Settings?.Current.UseWindowTransparency == true)
            .StartWithClassicDesktopLifetime(args);
    }

    /// Parameterless overload kept for the Avalonia designer / tooling, which
    /// looks for exactly this signature by convention.
    public static AppBuilder BuildAvaloniaApp() => BuildAvaloniaApp(false);

    // Cold start is dominated by loading the UI stack, not by MenYou's own init:
    // a traced cold boot reached the first line of our code at +7.3 s and then
    // finished every sync step (cache preload, tray, hooks, bridge) in 244 ms.
    // So the levers here are about NOT loading bytes we never use.
    public static AppBuilder BuildAvaloniaApp(bool wantsBackdrop) =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            // No WithInterFont() — the Avalonia.Fonts.Inter package is
            // deliberately not referenced, so the method isn't available here.
            // It registers a font no style in this app asks for: every
            // FontFamily is Segoe-based ("Segoe UI Variable, Segoe UI", "Segoe
            // Fluent Icons", "Cascadia Code, Consolas, monospace"), all present
            // on Win10+. Dropping the CALL alone (0.9.17) kept it off the load
            // path but left the package reference behind, still shipping the
            // 1.8 MB DLL inside every installer — so the reference went too.
            .With(new Win32PlatformOptions
            {
                // Default is [AngleEgl, Wgl, Software], so the ANGLE path pulls
                // av_libGLESv2.dll (~5.1 MB) plus the OpenGL/Vulkan assemblies
                // into a cold start to draw what is, visually, a list of tiles.
                // Software keeps the same Skia rasterizer, just without the GPU
                // bring-up — and the window stays a per-pixel-alpha composition
                // surface, so the rounded corners and the card's BoxShadow are
                // unaffected (verified on screen).
                // Software UNLESS the user asked for the native backdrop.
                //
                // Measured, one variable, everything else identical:
                //   default (ANGLE/GPU) -> requested=[Mica,None] granted=Mica
                //   Software            -> requested=[Mica,None] granted=None
                // Mica is only granted on a composition-backed window, and
                // Software gives a per-pixel-alpha redirection surface instead.
                // Requesting it under Software fails SILENTLY — Avalonia reports
                // None and the menu quietly falls back to plain alpha, which
                // looks like translucency but has no blur and no wallpaper tint.
                //
                // So the cold-start win is kept for everyone who leaves the
                // option off (the default), and only those who turn it on pay
                // for ANGLE. Leaving it hardcoded to Software would make the
                // setting a no-op; forcing GPU for everyone would hand the
                // whole user base a startup regression for an off-by-default
                // feature.
                RenderingMode = wantsBackdrop
                    ? [Win32RenderingMode.AngleEgl, Win32RenderingMode.Wgl, Win32RenderingMode.Software]
                    : [Win32RenderingMode.Software],
            })
            .LogToTrace();
}
