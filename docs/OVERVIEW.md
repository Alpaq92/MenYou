# MenYou — overview

MenYou is a Windows Start-menu replacement written in C# on
[Avalonia](https://avaloniaui.net/). Instead of reinventing the shell, it
reads Windows' own metadata — localized labels, the account picture,
taskbar pins, the Start pin layout, and per-app JumpLists — and presents
it through one of five swappable layouts (Modern/Windows 7, Windows 11,
Linux Mint Cinnamon, Classic XP, Classic 9x). It is deliberately
Windows-only (`[SupportedOSPlatform("windows")]` throughout), even though
Avalonia itself is cross-platform.

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

No logging framework is referenced — a tiny custom `HookTrace` file-logger
covers the native bridge / hook paths, opt-in via `MENYOU_TRACE_HOOKS=1`
(off by default).

### Theming note

Fluid.Avalonia must live in `Application.Styles` to theme composite
controls (e.g. `NumericUpDown`) correctly — window-scoping it broke them.
So it is app-wide and the StartMenu window re-declares the stock
`FluentTheme` locally. Its `AccentColorService` publishes the OS accent
ramp into app resources automatically.

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
- *Start-button interception* — an optional native bridge (`MenYou.Bridge`,
  C++) subclasses the real Windows Start button and forwards `WM_COPYDATA`
  to `CopyDataListener`. If MSVC isn't installed the DLL is absent and
  MenYou falls back to a managed WinEvent monitor.
- *Shift+Win hotkey* — registered via `RegisterHotKey`. Bare Win can't be
  reliably intercepted from outside the shell on Win 11 24H2, so Shift+Win
  is the toggle (the approach Open-Shell also recommends).
- *Foreground activation* — Win 11's focus-stealing prevention leaves a
  freshly shown window inactive, so `Win32Foreground.Bring` does the
  `AttachThreadInput` → `SetForegroundWindow` dance. The Settings window
  also gets a startup `WarmupSettings` pass that realizes its control tree
  off-screen so the first open isn't slow.
- *Window sizing re-measure* — the five layouts are stacked in one
  `SizeToContent` window gated by `IsVisible`; `ShowMenu` toggles
  `SizeToContent` after bindings resolve so the window fits only the active
  layout.

**Reading the real shell**
- *Localized labels* — `Strings.cs` resolves `@%SystemRoot%\System32\<dll>,
  -<id>` references via `SHLoadIndirectString`, so "Settings", "Pinned",
  "Sleep", etc. match the OS display language for free; misses fall through
  to `src/MenYou/Languages/<lang>.json`.
- *Win 11 pin mirroring* — `Win11StartMirrorService` file-watches
  `start2.bin`, debounces, runs `Export-StartLayout`, and reconciles the
  pin list (preserving UWP pins that the 24H2 export drops if the app is
  still installed).
- *Per-app JumpLists* — `JumpListReader` ports Open-Shell's reverse-
  engineered COM: `IAutomaticDestinationList` (Recent/Frequent),
  `IDestinationList` (Tasks/custom destinations), and `IApplicationResolver`
  (the implicit AUMID for plain `.lnk`s). Surfaces in the search context
  panel's **Recent** + **Tasks** sections.
- *Control Panel "All Tasks"* — `ControlPanelEnumerator` reads the GodMode
  namespace for Control Panel search results and launches them via
  `InvokeVerb("open")`.
- *Avatar pipeline* — reads the registry-pointed account picture; the
  generic silhouette is routed through a `ThemeGate → Invert → Brightness`
  pipeline in dark mode so it doesn't read as a glaring blob.

**Icons** — `IconExtractor` prefers the native `SHGetFileInfo` HICON for an
app's tile (crisp 1:1 at 32 px) and only falls back to
`IShellItemImageFactory::GetImage` (a 48 px PNG, slightly soft once
downscaled) for the few packages where the HICON path returns null. The
high-res search-context icon pulls the 256×256 variant straight into a
`WriteableBitmap` (no GDI+ round-trip, so alpha survives).

**Places + Phone Link** — `RightPanelViewModel` builds the Win 7-style
shell-shortcut set (Documents, Pictures, This PC, Control Panel, Run…, …)
with localized titles and Explorer-matching icons. The same list feeds the
Windows 11 Places flyout, the Mint Cinnamon sidebar, and the tray
**Places** submenu. Each layout's power strip carries a **Phone Link**
button that launches the Phone Link app when installed, else
`ms-settings:mobile-devices`.

**Newly-installed flash** — `UserSettings.SeenAppIds` records every AppId
ever seen; new ones get a translucent accent highlight on their first
appearance (first run baselines everything so existing apps don't flash).

**Settings → Custom theme** — `XamlStringToControlConverter` (ported from
SukiUI's live-preview pattern) feeds the editor text through
`AvaloniaRuntimeXamlLoader.Parse<Control>` on each keystroke; parse errors
render inline as a red `TextBlock`. `CustomThemesService` manages
`%AppData%\MenYou\CustomThemes\*.axaml`. See [`THEMING.md`](THEMING.md).

**In-app updates** — `GitHubUpdateService` backs Settings → **Check for
updates**: it reads the installed version from Inno's `{AppId}_is1`
uninstall key, queries `releases/latest`, and downloads + silently runs
the newer `MenYou-Setup-*.exe`. Inno's Restart Manager closes and
relaunches MenYou around the file swap. The **About** button (and tray
entry) open the project page (`GitHubUpdateService.RepositoryUrl`).

## Build & run

```powershell
# Requires the .NET 10 SDK.
dotnet build src/MenYou/MenYou.csproj
src/MenYou/bin/Debug/net10.0-windows/MenYou.exe
```

The `BuildNativeBridge` MSBuild target runs `build-bridge.ps1` first; it
locates VS's `msbuild` via `vswhere` and exits 0 if MSVC is missing (MenYou
then runs without the start-button hook). A tray icon appears on launch —
press **Shift+Win** to toggle the menu.

## Branding

App-icon assets live at the repo root (`icon.svg`, `icon_v2.svg`,
`icon_v2.png`, `icon_v2.ico`), with copies under `src/MenYou/Assets/` for
`avares://` resolution. `icon.svg` is a flat four-tile glyph based on MDI's
`view-dashboard` (attribution in [`../CREDITS.md`](../CREDITS.md));
`icon_v2.*` wraps it in a hand-authored Liquid-Glass disc. `icon_v2.ico`
(256→16 multi-size)
is referenced from `MenYou.csproj` as `<ApplicationIcon>` and from the
windows/tray via `avares://`.

## User data

Everything MenYou persists lives under `%AppData%\MenYou\`:

| Path | What |
|---|---|
| `settings.json` | `Models/UserSettings` — mirror state, pin/exclusion lists, per-skin selection, accent override, custom-theme toggle + active XAML, the `SeenAppIds` baseline, and one-shot migration flags. |
| `CustomThemes\*.axaml` | Custom-theme files imported via Settings → Custom → Load. |

See [`AUTOMATION.md`](AUTOMATION.md) for CI/CD, signing, and distribution,
[`THEMING.md`](THEMING.md) for authoring custom themes, and
[`../CREDITS.md`](../CREDITS.md) for inspirations and attributions.
