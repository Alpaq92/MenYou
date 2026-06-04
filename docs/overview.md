# MenYou

A Windows Start-menu replacement written in C# / Avalonia. Restores
the layered "Win 7 + right-side shell links + All Programs cascade"
shape on top of modern Windows, while leaning on the system's real
shell metadata (localized labels, account picture, taskbar pins,
Start mirror, JumpLists) rather than reinventing them.

## Inspirations

- **[Open-Shell](https://github.com/Open-Shell/Open-Shell-Menu)** —
  the long-standing C++ Start-menu replacement. MenYou borrows its
  approach to several Windows pain points: the GodMode All-Tasks
  namespace as the source for Control Panel search results, the
  Shift+Win activation pattern (and the rationale that bare Win can't
  be reliably intercepted from outside the shell on Win 11 24H2), the
  fallback strategy of preserving stale pin lists when the Windows
  snapshot APIs misbehave, and — most importantly — the reverse-
  engineered shell COM (`CLSID_AutomaticDestinationList`, the private
  `IDestinationList` reader vtable, `IApplicationResolver`) that
  exposes per-app Recent destinations and Tasks for arbitrary apps.
- **[SukiUI](https://github.com/kikipoulet/SukiUI)** — Avalonia UI
  component library. The slim auto-hiding `ScrollBar` styling in
  `Styles/Scrollbar.axaml` is ported almost verbatim from SukiUI's
  `ScrollBarStyle.axaml` + `ScrollViewerStyles.axaml`.
- **Windows 11's Start menu** — overall layout cues for the search
  field, section header weight, two-tone left/right columns, slim
  pinned-tile grid, the rounded-corner outer chrome, and the
  "click-to-preview, click Open to launch" search-result context
  panel pattern.
- **GNOME-style and Win 11 Settings dark mode** — drove the dark-theme
  palette refresh (deeper backgrounds, brighter section headers).

## Tech stack

- **.NET 10** (`net10.0-windows` TFM), C# 13, `LangVersion=latest`.
- **[Avalonia 12.0.4](https://avaloniaui.net/)** UI framework
  (Avalonia, Avalonia.Desktop, Avalonia.Themes.Fluent,
  Avalonia.Fonts.Inter, Avalonia.Markup.Xaml.Loader). Cross-platform-
  capable but MenYou is intentionally
  `[SupportedOSPlatform("windows")]` everywhere.
- **[Fluid.Avalonia 1.1.1](https://github.com/Alpaq92/Fluid.Avalonia)** —
  a Fluent-2 / WinUI-3 theme that nests `Avalonia.Themes.Fluent` and
  layers Fluent-2 design tokens over it. It's the **app-wide** theme
  (`<fluid:FluidTheme />` in `App.axaml`'s `Application.Styles`) — used
  vanilla, with no MenYou-side overrides, the way the library is designed
  to be consumed (window-scoping it broke composite controls like
  `NumericUpDown`, which only theme correctly at app scope). Its
  `AccentColorService` publishes the OS accent ramp into the app
  resources. The **StartMenu opts back out** to the stock `FluentTheme`
  via its own `Window.Styles` (`StartMenuWindow.axaml`) so its tuned
  palette / brushes and built-in layouts render exactly as designed.
- **[CommunityToolkit.Mvvm](https://learn.microsoft.com/en-us/dotnet/communitytoolkit/mvvm/) 8.4.2**
  — `[ObservableProperty]` / `[RelayCommand]` source generators drive
  the view-model layer.
- **[Jeek.Avalonia.Localization 12.0.2](https://github.com/tifish/Jeek.Avalonia.Localization)** —
  JSON-bundle localizer. MenYou's `Strings.cs` is **Windows-first**:
  each label first tries one or more `SHLoadIndirectString` references
  against shell DLLs (so labels match the OS display language for
  free); on miss, it falls through to a Jeek key in
  `src/MenYou/Languages/<lang>.json` (21 languages today).
- **[Inno Setup](https://jrsoftware.org/isinfo.php)** — the installer
  (`installer/inno/menyou.iss`): a wizard with overridable install dir /
  Start-Menu group / shortcuts, per-user by default with a per-machine
  option, and AppId-keyed in-place upgrades. Updates are an in-app
  concern, not a toolchain one: `GitHubUpdateService` reads the installed
  version from Inno's uninstall key, queries the GitHub Releases API, and
  downloads + silently runs the newer `MenYou-Setup-*.exe` (Inno's
  Restart Manager closes and relaunches MenYou around the file swap). The
  **Settings → Sprawdź aktualizacje** / **Check for updates** button
  drives it on demand. An **About** button beside it — and an **About**
  entry in the tray menu — open the project's GitHub page (the shared
  `GitHubUpdateService.RepositoryUrl` constant) in the default browser.
- **`Microsoft.Extensions.DependencyInjection` 10.0.8** — service
  composition root in `App.axaml.cs:BuildServices()`.
- **Logging** — no logging framework is pulled in; a tiny custom
  `HookTrace` file-logger covers the native bridge / hook paths, opt-in
  via the `MENYOU_TRACE_HOOKS=1` env var, off (no log file) by default.
- **`System.Drawing.Common` 10.0.8** — used by `IconExtractor` to
  round-trip native HICONs through GDI+ before handing them to
  Avalonia as `Bitmap`s.
- **Native bridge DLL** (optional) — built by `build-bridge.ps1` via
  VS's `msbuild` if MSVC is installed; the bridge subclasses the real
  Windows Start button and posts `WM_COPYDATA` back to the managed
  `CopyDataListener`. MenYou falls back to a WinEvent monitor at
  runtime if the DLL isn't present.

## Architecture sketch

| Layer | Folder | Responsibility |
|---|---|---|
| **Models** | `Models/` | Plain records: `AppEntry`, `MenuFolder`, `SearchResult`, `UserSettings`. |
| **Services** | `Services/` | Composition-root injected. `IAppDiscoveryService` merges .lnk + UWP enumeration, `IIconService` extracts and caches HICONs as Avalonia Bitmaps, `IPinService` manages the pin list, `IWin11StartMirror` mirrors `start2.bin` pins, `IUserAvatarService` resolves the Windows account picture. |
| **Platform/Windows** | `Platform/Windows/` | Win32 / shell COM glue. `IconExtractor`, `ShellLinkReader`, `ShellLocalization`, `UwpAppEnumerator`, `SettingsDeepLinkEnumerator`, `ControlPanelEnumerator`, `Win11StartLayoutReader`, `JumpListReader`, `Win32HotkeyService`, the `BridgeInjector`/`CopyDataListener` pair. |
| **ViewModels** | `ViewModels/` | MVVM. `StartMenuViewModel` is the root; child VMs cover Programs, Search, Power, RightPanel, Settings. `Items/` holds the polymorphic menu-item hierarchy. (The tray menu is wired directly in `App.axaml.cs` — there's no tray view-model.) |
| **Views** | `Views/` | Avalonia XAML. `StartMenuWindow` hosts a `Win7Layout` / `Windows11Layout` / `MintCinnamonLayout` / `Classic1Layout` / `Classic2Layout` switched by `MenuStyle`. `Controls/` has reusable pieces (`AppGrid`, `ProgramsTree`, `ProgramsFolderFlyout`, `SearchResultsList`, …). `Converters/` holds `EnumEqualsConverter`, `InvertConverter`, `BrightnessConverter`, `ThemeGateConverter`, `PipelineConverter`, `NewItemHighlightConverter`, `ScrollbarReserveHeightConverter`, `CustomThemeCornerRadiusConverter`, and `XamlStringToControlConverter` (the Settings → Custom live-preview parser). |

## Notable platform tricks

- **Start-button click interception** — the optional native bridge
  subclasses the system Start button and forwards `WM_COPYDATA` to
  `CopyDataListener`. Without it, MenYou falls back to a WinEvent
  monitor.
- **Shift+Win replacement hotkey** — bare Win triggers the system
  Start; Shift+Win is registered via `RegisterHotKey` and is the
  approach Open-Shell recommends on Win 11 24H2.
- **Foreground activation** — Win 11's focus-stealing prevention leaves
  a freshly-shown window inactive (and `Deactivated` then never fires).
  `Platform/Windows/Win32Foreground.Bring` does the
  `AttachThreadInput` → `SetForegroundWindow` dance to force it to front;
  both the menu (`StartMenuWindow.ForceForeground`) and the Settings
  window use it. The Settings window also gets a startup `WarmupSettings`
  pass that realizes its control tree off-screen so the first cog click
  isn't slow.
- **Window sizing re-measure** — the built-in layouts are stacked in one
  `SizeToContent` window and gated by `IsVisible`; at first `Show()` they
  briefly all measure (bindings not yet resolved), so the width-less
  Classic layouts pin the window at `MaxWidth`. `StartMenuWindow.ShowMenu`
  toggles `SizeToContent` after the bindings resolve, forcing a fresh
  measure against only the active layout so the window fits it (e.g. the
  560 px Windows 11 layout stops floating centered in a 900 px shell).
- **Win 11 Start pin mirroring** — `Win11StartMirrorService`
  filewatches
  `%LocalAppData%\Packages\Microsoft.Windows.StartMenuExperienceHost_cw5n1h2txyewy\LocalState\start2.bin`,
  debounces 500 ms, then shells out to `Export-StartLayout` and
  reconciles the pin list. UWP pins missing from
  `Export-StartLayout` (a Win 11 24H2 regression) are preserved if
  the app is still installed.
- **Localized shell labels** — `Strings.cs` resolves
  `@%SystemRoot%\System32\<dll>,<-id>` indirect-string references via
  `SHLoadIndirectString`, so MenYou's "Settings", "Recent",
  "Pinned", "Sleep", etc. match the user's Windows display language.
- **Avatar pipeline** — `UserAvatarService` reads the registry-pointed
  account picture (`HKLM\…\AccountPicture\Users\<SID>\ImageNNN`). The
  generic Windows silhouette gets routed through the
  `ThemeGate → Invert → Brightness` pipeline in dark mode so a dark
  figure on a white background doesn't look like a glaring blob.
- **High-resolution context-panel icon** — uses the modern
  `IShellItemImageFactory::GetImage` API to pull the 256×256 embedded
  variant and copy raw 32 BPP BGRA pixels directly into an Avalonia
  `WriteableBitmap` (no System.Drawing round-trip, so alpha is
  preserved). `IconExtractor.ExtractForAumid` prefers the `SHGetFileInfo`
  HICON path against `shell:AppsFolder\<AUMID>` (the app's tile icon at the
  native, DPI-appropriate size — renders crisp 1:1 in the 32 px tiles) and
  only falls back to the image-factory `GetImage` path (a 48 px PNG asset,
  slightly soft once downscaled) for the few packages where the HICON path
  returns null — notably WebExperienceHost / "Get Started", which otherwise
  dropped the Start-menu Place to a plain folder icon.
- **Settings cog / folder icon fallbacks** —
  `MenuItemViewModel.FallbackIcon` is loaded once at startup from the
  Settings UWP AUMID; folder entries override the fallback with the
  shell's generic folder icon (`shell32.dll` #3).
- **Shell "Places" + Phone Link button** — `RightPanelViewModel` builds
  the Win 7-style shell-shortcut set (Start menu, Documents, Pictures,
  Music, Downloads, This PC, Network, Control Panel, Settings, Run…) with
  localized titles (known-folder display names / shell-DLL indirect
  strings) and Explorer-matching icons (`SHGetFileInfo` on the real path,
  `SHParseDisplayName` + PIDL for CLSID namespace items, `ExtractForAumid`
  for packaged-app tiles). The one list surfaces in three places: the
  Windows 11 layout's chevron-up Places flyout, the Mint Cinnamon
  sidebar, and the tray icon's **Places** submenu. Each built-in theme's
  power strip also carries a **Phone Link** button (`OpenPhoneLink`): it
  probes `IconExtractor.AppExists("Microsoft.YourPhone_…!App")` and
  launches the Phone Link app when present, else falls back to
  `ms-settings:mobile-devices`. Its label comes live from
  `wpdshext.dll,-510` ("Mobile devices" / "Urządzenia przenośne") — a
  deep-dive over `SHLoadIndirectString` confirmed no shell DLL exposes
  the literal "Phone Link" UWP name, so that wording stays JSON-only.
- **Control Panel "All Tasks" launching** —
  `ControlPanelEnumerator.LaunchTask` re-navigates the GodMode
  namespace via `Shell.Application` and calls `InvokeVerb("open")`;
  passing the shell-IDL path to `explorer.exe` opens Documents
  instead of dispatching the task.
- **Search-result context panel** — the right column switches from
  the shell-shortcut list to a per-result preview when a query
  produces a Selected item. Shows the icon, title, subtitle, and
  action buttons (`Open`, `Run as administrator`,
  `Open file location`) plus the per-app JumpList **Tasks** and
  **Recent** sections (see below). URI-scheme targets
  (`ms-settings:`, `shell:`) explicitly skip `SHGetFileInfo` icon
  extract so the cog fallback shows instead of the generic page icon.
- **Per-app JumpList Recent + Tasks** — `JumpListReader` ports
  Open-Shell's approach:
  - `CLSID_AutomaticDestinationList = {F0AE1542-…-B09144BA}` +
    private `IAutomaticDestinationList` (with Win10 10547+ variant
    where `GetList` grew a flags arg) for Recent/Frequent/Pinned
    files. Initializes with the AUMID; the shell internally resolves
    the matching `.automaticDestinations-ms` file, so the file-name
    CRC-64 hash never has to be reproduced.
  - `CLSID_DestinationList = {77F10CF0-…-D35ED6}` + the private
    `IDestinationList` reader vtable (three IID variants by build)
    for Tasks (custom destinations). `GetCategoryCount` →
    `GetCategory(i, 1, …)` until `cat.type == 2`, then
    `EnumerateCategoryDestinations` returns an `IObjectArray` of
    `IShellLink` items. `PKEY_Title` is resolved via
    `SHLoadIndirectString` to handle `@module,-id` localized strings.
  - `CLSID_ApplicationResolver = {660B90C8-…-7F55341B}` + the Win 8+
    `IApplicationResolver` IID for `GetAppIDForShortcut`, deriving
    the implicit AUMID Explorer uses for plain `.lnk` shortcuts
    without an explicit `System.AppUserModel.ID` property.
- **Newly-installed-app flash** — `UserSettings.SeenAppIds` is the
  persisted set of every AppId ever discovered. `StartMenuViewModel`
  diffs the current discovery against it on each menu load; new IDs
  get flagged on their `AppItemViewModel.IsNew`, which the
  `NewItemHighlightConverter` turns into a translucent
  `AccentSubtleBrush` background across Pinned tiles, Recent rows,
  and All-Programs entries. First run records every current AppId so
  existing apps don't all flash.
- **Settings → Custom theme** — `XamlStringToControlConverter`
  (ported from SukiUI's `StringToControlConverter` pattern) feeds the
  editor's current text through `AvaloniaRuntimeXamlLoader.Parse<Control>`
  on every keystroke. Fragments without `xmlns=` are wrapped in a
  minimal `<Grid xmlns="...">` so a bare `<Button .../>` is legal
  input; full-document XAML passes through unchanged. Parse failure
  is non-fatal — a red `TextBlock` with the exception message
  renders inline so the user iterates with live errors. Empty input
  shows the Material Design Icons *artboard* glyph as an
  empty-state placeholder. `CustomThemesService` manages
  `%AppData%\MenYou\CustomThemes\*.axaml` (Load = import-and-select,
  Save = export current editor content, Forget = delete from store).
  A worked example ships under `samples/custom-themes/`
  (`Windows7Square.axaml` — the Modern (Windows 7) layout re-skinned
  with squared corners). The Windows 11 and Linux Mint Cinnamon looks
  that began as samples are now built-in `MenuStyle` layouts
  (`Windows11Layout` / `MintCinnamonLayout`).
- **In-app update check** — `GitHubUpdateService` backs the Settings →
  **Sprawdź aktualizacje** button. It reads the installed version from
  Inno's `{AppId}_is1` uninstall key (so dev / `dotnet run` / portable
  builds report "nothing to update" rather than mis-firing), GETs the
  repo's `releases/latest` from the GitHub REST API, and when the tag is
  newer downloads the `MenYou-Setup-*.exe` asset and launches it with
  `/SILENT`. Inno keys the install off a fixed AppId, so the new Setup
  upgrades in place; its Restart Manager closes and relaunches MenYou
  around the file swap, so the running process doesn't coordinate its own
  exit. The button surfaces one of four status phrases (`UpdateChecking`
  / `UpdateUpToDate` / `UpdateDownloaded` / `UpdateCheckFailed`). There's
  no boot hook in `Program.Main` — Inno handles install / upgrade /
  uninstall entirely out of process.

## Branding

App icon assets live at the repo root (`icon.svg`, `icon_v2.svg`,
`icon_v2.png`, `icon_v2.ico`) with copies under
`src/MenYou/Assets/` for Avalonia `avares://` resolution.

- `icon.svg` is a flat 24×24 source — four blue tiles, `#165BFF` —
  based on the [`view-dashboard`](https://pictogrammers.com/library/mdi/icon/view-dashboard/)
  glyph from Material Design Icons (Pictogrammers).
- `icon_v2.svg` wraps that glyph on a white-glass disc: radial body
  gradient, upper sheen ellipse, specular catchlight, refracted
  inner rim, bottom bounce light — a Liquid Glass-style treatment.
  Hand-authored, no library.
- `icon_v2.png` (512×512) is the rendered raster, run through
  `pngquant --quality=70-95 --strip` (≈ 66 % smaller than the raw
  System.Drawing output).
- `icon_v2.ico` is a multi-variant icon (256, 128, 64, 48, 32, 16)
  built directly via PowerShell — each size rendered from the PNG
  at HighQualityBicubic and packed as PNG-embedded `.ico` payloads.
  Referenced from `MenYou.csproj` as `<ApplicationIcon>` so the
  compiled `.exe` carries it in its Win32 resources, and from
  `StartMenuWindow` / `SettingsWindow` / the `TrayIcon` via
  `avares://MenYou/Assets/icon_v2.ico`.

## Build

```powershell
# Requires the .NET 10 SDK.
dotnet build src/MenYou/MenYou.csproj
```

The `BuildNativeBridge` MSBuild target invokes `build-bridge.ps1` at
the repo root before the managed build. The script locates VS's
`msbuild` via `vswhere`; if MSVC is missing it exits 0 and MenYou
simply runs without the start-button hook.

## Run

`src/MenYou/bin/Debug/net10.0-windows/MenYou.exe`. Tray icon shows on
launch; press **Shift+Win** to toggle the menu.

## Settings + user-data storage

`%AppData%\MenYou\` holds everything MenYou persists between sessions:

| Path | What |
|---|---|
| `settings.json` | `Models/UserSettings.cs` — mirror state, pin/exclusion lists, per-skin selection, accent override, custom-theme toggle + active XAML, `SeenAppIds` baseline for the newly-installed flash, and the one-shot `AutostartDefaultApplied` migration flag. |
| `CustomThemes\*.axaml` | Custom-theme files imported via Settings → Custom → Load. `CustomThemesService` enumerates the directory on every Settings open. |

## Credits

- **App icon** — derived from the
  [`view-dashboard`](https://pictogrammers.com/library/mdi/icon/view-dashboard/)
  glyph from Material Design Icons via Pictogrammers, used under
  their Free license. `icon_v2.*` wraps the same glyph in a hand-
  rendered Liquid-Glass disc.
- **[Open-Shell-Menu](https://github.com/Open-Shell/Open-Shell-Menu)**
  — the JumpList COM-reading approach (per-app Recent + Tasks +
  implicit AUMID derivation) is ported from their
  `Src/StartMenu/StartMenuDLL/JumpLists.cpp`.
- **[SukiUI](https://github.com/kikipoulet/SukiUI)** — the slim
  ScrollBar/ScrollViewer ControlTemplates in
  `src/MenYou/Styles/Scrollbar.axaml` are adapted from theirs, and
  `XamlStringToControlConverter` is ported from SukiUI.Demo's
  `StringToControlConverter` (the live-XAML-preview pattern).
- **[Fluid.Avalonia](https://github.com/Alpaq92/Fluid.Avalonia)** —
  app-wide Fluent-2 theme + system-accent integration.
- **[Inno Setup](https://jrsoftware.org/isinfo.php)** — the Windows
  installer toolchain (`installer/inno/menyou.iss`).
- **[Jeek.Avalonia.Localization](https://github.com/tifish/Jeek.Avalonia.Localization)**
  — JSON-bundle localizer used as the fallback under
  `Strings.Resolve`.
- **Avalonia, CommunityToolkit.Mvvm, .NET 10** — runtime stack.
