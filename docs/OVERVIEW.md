# MenYou — overview

MenYou is a Windows Start-menu replacement written in C# on [Avalonia](https://avaloniaui.net/). Instead of reinventing the shell, it reads Windows' own metadata — localized labels, the account picture, taskbar pins, the Start pin layout, and per-app JumpLists — and presents it through one of five swappable layouts (Windows 11 — the default — Modern/Windows 7, Linux Mint Cinnamon, Classic XP, Classic 9x). It is deliberately Windows-only (`[SupportedOSPlatform("windows")]` throughout), even though Avalonia itself is cross-platform.

## Tech stack

| Piece | Version | Role |
|---|---|---|
| **.NET** | `net10.0-windows`, C# 13 | Target framework. |
| **Avalonia** | 12.0.4 | UI framework (`Avalonia`, `.Desktop`, `.Themes.Fluent`, `.Fonts.Inter`, `.Markup.Xaml.Loader`). |
| **Fluid.Avalonia** | 1.1.1 | Fluent-2 theme, applied **app-wide** in `App.axaml` (vanilla, no overrides). The StartMenu opts back out to the stock `FluentTheme` via its own `Window.Styles` so its tuned brushes/layouts render unchanged. |
| **CommunityToolkit.Mvvm** | 8.4.2 | `[ObservableProperty]` / `[RelayCommand]` source generators. |
| **Microsoft.Extensions.DependencyInjection** | 10.0.8 | Composition root (`App.axaml.cs:BuildServices()`). |
| **Jeek.Avalonia.Localization** | 12.0.2 | JSON-bundle localizer; the fallback under `Strings.Resolve`. |
| **System.Drawing.Common** | 10.0.8 | `IconExtractor` round-trips native HICONs through GDI+ into Avalonia `Bitmap`s. |
| **Inno Setup** | — | Installer (`installer/inno/menyou.iss`). |

No logging framework is referenced — a tiny custom `HookTrace` file-logger covers the native bridge / hook paths, opt-in via `MENYOU_TRACE_HOOKS=1` (off by default).

### Theming note

Fluid.Avalonia must live in `Application.Styles` to theme composite controls (e.g. `NumericUpDown`) correctly — window-scoping it broke them. So it is app-wide and the StartMenu window re-declares the stock `FluentTheme` locally. Its `AccentColorService` publishes the OS accent ramp into app resources automatically.

## Architecture

| Layer | Folder | Responsibility |
|---|---|---|
| **Models** | `Models/` | Plain records: `AppEntry`, `MenuFolder`, `SearchResult`, `UserSettings`. |
| **Services** | `Services/` | DI-injected. App discovery (`.lnk` + UWP merge), icon extraction/caching, pin management, the Win 11 Start mirror, the account-avatar resolver, and `GitHubUpdateService` (in-app updates). |
| **Platform/Windows** | `Platform/Windows/` | Win32 / shell-COM glue: `IconExtractor`, `ShellLinkReader`, `UwpAppEnumerator`, `ControlPanelEnumerator`, `Win11StartLayoutReader`, `JumpListReader`, `Win32HotkeyService`, `Strings` (shell-DLL label resolution), `Win32Foreground`, and the `BridgeInjector` / `CopyDataListener` pair. |
| **ViewModels** | `ViewModels/` | MVVM. `StartMenuViewModel` is the root; child VMs cover Programs, Search, Power, RightPanel, Settings. `Items/` holds the polymorphic menu-item hierarchy. The tray menu is wired directly in `App.axaml.cs`. |
| **Views** | `Views/` | Avalonia XAML. `StartMenuWindow` hosts one of `Win7Layout` / `Windows11Layout` / `MintCinnamonLayout` / `Classic1Layout` / `Classic2Layout`, switched by `MenuStyle`. `Controls/` has reusable pieces; `Converters/` holds the value converters, including `XamlStringToControlConverter` (the Settings → Custom live preview). |

## How it works — notable mechanisms

**Activation**
- *Start-button interception* — an optional native bridge (`MenYou.Bridge`, C++) subclasses the real Windows Start button and forwards `WM_COPYDATA` to `CopyDataListener`. If MSVC isn't installed the DLL is absent and MenYou falls back to a managed WinEvent monitor.
- *Shift+Win hotkey* — registered via `RegisterHotKey`. Bare Win can't be reliably intercepted from outside the shell on Win 11 24H2, so Shift+Win is the toggle (the approach Open-Shell also recommends).
- *Foreground activation* — Win 11's focus-stealing prevention leaves a freshly shown window inactive, so `Win32Foreground.Bring` does the `AttachThreadInput` → `SetForegroundWindow` dance.
- *Window sizing re-measure* — the five layouts are stacked in one `SizeToContent` window gated by `IsVisible`; `ShowMenu` toggles `SizeToContent` after bindings resolve so the window fits only the active layout.

**Startup performance** — full write-up and measurements in [`OPTIMIZATION.md`](OPTIMIZATION.md).
- *Autostart via logon task* — `Win32AutostartService` registers a per-user logon-triggered Scheduled Task (`InteractiveToken` / `LeastPrivilege`, `PT1S` delay) rather than an `HKCU\Run` value, because Windows throttles Run-key/Startup-folder autostarts ~15 s after sign-in. The task is exempt, so MenYou launches ~1 s after the desktop (a ~15 s → ~1 s cold-start win). Falls back to the Run-key (plus a zeroed `StartupDelayInMSec`) where task creation is blocked; a one-time, self-healing migration moves existing installs over and only marks itself done once autostart is verifiably in place.
- *App-discovery cache* — `AppDiscoveryService` persists the resolved app list to `%AppData%\MenYou\discovery-cache.json` (`DiscoveryCache`). On a cold start it paints from that snapshot — a plain file read, no shell COM, so it sidesteps the ~5× COM slowdown while Windows is still bringing up the shell. Because that path is COM-free, `PreloadFromCacheAsync` runs it *eagerly at the very start of startup* (≈150 ms in) instead of waiting for the idle warm-up, so the menu's data is ready almost immediately. A cheap, COM-free filesystem fingerprint (paths + mtimes + sizes) gates the snapshot, so a changed Start Menu is never shown stale, and a background live scan always runs as a backstop (after a settle delay) — swapping in and firing `Refreshed` only if the list actually changed. Toggle: Settings → Developer → *App-list cache*.
- *Immediate reveal* — by default the menu reveals the instant it's opened and fills tiles in as discovery resolves, instead of gating the reveal on the full scan. Off = wait for data first (never an empty frame). Toggle: Settings → Developer → *Open immediately*.
- *Warm-up + pre-render* — the window is built, its data loaded, then the populated tree is realized once off-screen (transparent, unactivated), so the first real open is instant. `ScheduleWarmup` runs it immediately at Input priority on a warm launch, but *holds* it ~20 s on a cold boot so the heavy build doesn't pile onto the post-login storm. `LoadAsync` is single-flight; the pinned/recent rebuild is diff-aware (unchanged tiles keep their icons — no cog flash on repeat opens); the reveal runs at `DispatcherPriority.Loaded` (≈1 frame, not starved at `Background`).
- *Parallel discovery* — the `.lnk` walk parses shortcuts across cores (`Parallel.For`) and overlaps the `shell:AppsFolder` UWP enumeration.
- *First-run splash* — `NativeSplash`, a Win32/GDI splash on its own thread, covers the one-time cold first-install load (Defender scanning the fresh unsigned payload). It's shown from `Program.Main` *before* Avalonia loads so it can paint while the UI thread is still cold-loading; theme-aware, with a one-time "MenYou is ready" tray balloon when startup finishes.

**Diagnostics** — `HookTrace` writes an opt-in trace (hook events + load timings) to `%TEMP%\menyou-hooks.log`. Off by default; enable via Settings → Developer → *Logging* (or the `MENYOU_TRACE_HOOKS=1` env var). A background sweep on startup drops the log once it passes the configured size (Developer → *Size (MB)*) or is a few days old.

**Reading the real shell**
- *Localized labels* — `Strings.cs` resolves `@%SystemRoot%\System32\<dll>, -<id>` references via `SHLoadIndirectString`, so "Settings", "Pinned", "Sleep", etc. match the OS display language for free; misses fall through to `src/MenYou/Languages/<lang>.json`.
- *Win 11 pin mirroring* — `Win11StartMirrorService` file-watches `start2.bin`, debounces, runs `Export-StartLayout`, and reconciles the pin list (preserving UWP pins that the 24H2 export drops if the app is still installed).
- *Per-app JumpLists* — `JumpListReader` ports Open-Shell's reverse- engineered COM: `IAutomaticDestinationList` (Recent/Frequent), `IDestinationList` (Tasks/custom destinations), and `IApplicationResolver` (the implicit AUMID for plain `.lnk`s). Surfaces in the search context panel's **Recent** + **Tasks** sections.
- *Control Panel "All Tasks"* — `ControlPanelEnumerator` reads the GodMode namespace for Control Panel search results and launches them via `InvokeVerb("open")`.
- *Avatar pipeline* — reads the registry-pointed account picture; the generic silhouette is routed through a `ThemeGate → Invert → Brightness` pipeline in dark mode so it doesn't read as a glaring blob.

**Icons** — `IconExtractor` prefers the native `SHGetFileInfo` HICON for an app's tile (crisp 1:1 at 32 px) and only falls back to `IShellItemImageFactory::GetImage` (a 48 px PNG, slightly soft once downscaled) for the few packages where the HICON path returns null. The high-res search-context icon pulls the 256×256 variant straight into a `WriteableBitmap` (no GDI+ round-trip, so alpha survives).

**Places + Phone Link** — `RightPanelViewModel` builds the Win 7-style shell-shortcut set (Documents, Pictures, This PC, Control Panel, Run…, …) with localized titles and Explorer-matching icons. The same list feeds the Windows 11 Places flyout, the Mint Cinnamon sidebar, and the tray **Places** submenu. Each layout's power strip carries a **Phone Link** button that launches the Phone Link app when installed, else `ms-settings:mobile-devices`.

**Newly-installed flash** — `UserSettings.SeenAppIds` records every AppId ever seen; new ones get a translucent accent highlight on their first appearance (first run baselines everything so existing apps don't flash).

**Settings → Custom theme** — `XamlStringToControlConverter` (ported from SukiUI's live-preview pattern) feeds the editor text through `AvaloniaRuntimeXamlLoader.Parse<Control>` on each keystroke; parse errors render inline as a red `TextBlock`. `CustomThemesService` manages `%AppData%\MenYou\CustomThemes\*.axaml`. See [`THEMING.md`](THEMING.md).

**In-app updates** — `GitHubUpdateService` backs Settings → **Check for updates**: it reads the installed version from Inno's `{AppId}_is1` uninstall key, queries `releases/latest`, and downloads + silently runs the newer `MenYou-Setup-*.exe`. Inno's Restart Manager closes and relaunches MenYou around the file swap. The **About** button (and tray entry) open the project page (`GitHubUpdateService.RepositoryUrl`).

## Build & run

```powershell
# Requires the .NET 10 SDK.
dotnet build src/MenYou/MenYou.csproj
src/MenYou/bin/Debug/net10.0-windows/MenYou.exe
```

The `BuildNativeBridge` MSBuild target runs `tools/build-bridge.ps1` first; it locates VS's `msbuild` via `vswhere` and exits 0 if MSVC is missing (MenYou then runs without the start-button hook). A tray icon appears on launch — press **Shift+Win** to toggle the menu.

## Branding

App-icon assets live at the repo root (`icon.svg`, `icon_v2.svg`, `icon_v2.png`, `icon_v2.ico`), with copies under `src/MenYou/Assets/` for `avares://` resolution. `icon.svg` is a flat four-tile glyph based on MDI's `view-dashboard` (attribution in [`../CREDITS.md`](../CREDITS.md)); `icon_v2.*` wraps it in a hand-authored Liquid-Glass disc. `icon_v2.ico` (256→16 multi-size) is referenced from `MenYou.csproj` as `<ApplicationIcon>` and from the windows/tray via `avares://`.

## User data

Everything MenYou persists lives under `%AppData%\MenYou\`:

| Path | What |
|---|---|
| `settings.json` | `Models/UserSettings` — mirror state, pin/exclusion lists, per-skin selection, accent override, custom-theme toggle + active XAML, the `SeenAppIds` baseline, the Developer-tab flags (cache / immediate-reveal / logging), and one-shot migration flags. |
| `CustomThemes\*.axaml` | Custom-theme files imported via Settings → Custom → Load. |
| `discovery-cache.json` | Persisted app-discovery snapshot (`DiscoveryCache`) for instant cold-start paint. Rebuilt automatically when the Start Menu changes; safe to delete. |

See [`AUTOMATION.md`](AUTOMATION.md) for CI/CD, signing, and distribution, [`THEMING.md`](THEMING.md) for authoring custom themes, and [`../CREDITS.md`](../CREDITS.md) for inspirations and attributions.
