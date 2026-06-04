# MenYou

A Windows Start-menu replacement written in C# / Avalonia. Ships five
built-in looks — Modern (Windows 7), Windows 11, Linux Mint Cinnamon,
Classic XP and Classic 9x — rendered on top of modern Windows shell
metadata (localized labels, account picture, taskbar pins, Start mirror,
JumpLists) rather than reinventing them.

> [!TIP]
> Press **Shift+Win** to toggle the menu.

## Why

This started with a debloated Windows 11
([Tiny11](https://github.com/ntdevlabs/tiny11builder)). Stripping the
image also strips out whatever Start-menu search uses to find local apps
— apparently locating Notepad on your own PC routes through Edge. With
search broken, I needed a new Start menu.
[Open-Shell](https://github.com/Open-Shell/Open-Shell-Menu) was the one
solid free/OSS option, but none of its skins did it for me. I tried
theming it, went down the rabbit hole of how it works, and concluded I
wanted something more flexible on a stack I'd rather maintain. MenYou is
the result: a ground-up-skinnable Start menu on **.NET 10** + **Avalonia
12**.

## Install

| Channel              | Command                                |
|----------------------|----------------------------------------|
| **winget**           | `winget install Alpaq.MenYou`          |
| **Scoop** (extras)   | `scoop install menyou`                 |
| **Chocolatey**       | `choco install menyou`                 |
| **GitHub Releases**  | [Latest release](https://github.com/Alpaq92/MenYou/releases/latest) — download the `MenYou-Setup-x64.exe` |

The installer is built with [Inno Setup](https://jrsoftware.org/isinfo.php) —
a standard setup wizard where you can override the install location,
Start-Menu folder, and shortcuts (per-user by default, with a
per-machine option). Updates are checked in-app against GitHub Releases:
**Settings → Sprawdź aktualizacje** downloads the latest installer and
runs it to upgrade in place. Code-signing status (SignPath Foundation
when available, otherwise unsigned) is noted in the release body — see
[`docs/AUTOMATION.md`](docs/AUTOMATION.md) for the deployment pipeline.

## Build from source

```powershell
# Requires the .NET 10 SDK. The project targets net10.0-windows.
dotnet build src/MenYou/MenYou.csproj

# Run
src/MenYou/bin/Debug/net10.0-windows/MenYou.exe
```

The native input bridge under `src/MenYou.Bridge/` is compiled by
`build-bridge.ps1` as a pre-build step. It's optional — if MSVC isn't
installed the script exits 0 and MenYou falls back to the managed WinEvent
monitor at runtime.

Press **Shift+Win** to toggle the menu.

## Translate MenYou

MenYou is partly translated for free: every system label
("Settings", "Pinned", "Apply", "Sign out", …) pulls live from the
Windows shell DLLs, so it reads in your locale automatically. The
MenYou-specific strings ("Mirror Windows Start pins", the update-check
statuses, etc.) live in
[`src/MenYou/Languages/*.json`](src/MenYou/Languages/) and need
humans.

**Help out without a GitHub account**: open the Crowdin project at
<https://crowdin.com/project/menyou>, pick your language, translate
whatever reads awkwardly, click Save. A scheduled GitHub Action
([`crowdin.yml`](.github/workflows/crowdin.yml)) pulls completed
translations into a PR every Monday, which lands in the next release
automatically.

See [`docs/TRANSLATIONS.md`](docs/TRANSLATIONS.md) for the full flow
(Crowdin, or editing the JSONs directly, plus alternatives like
Weblate / Tolgee).

## Custom themes

The Settings dialog has a **Custom** tab that loads a self-contained
AXAML file, parses it through `AvaloniaRuntimeXamlLoader` on every
keystroke, and renders the result in a live preview pane next to the
editor. Loaded files are copied into `%AppData%\MenYou\CustomThemes\`
so they survive uninstalls; the **Save** button re-exports the current
editor content to a path of your choice.

A worked example lives in
[`samples/custom-themes/Windows7Square.axaml`](samples/custom-themes/Windows7Square.axaml)
— the Modern (Windows 7) layout with every corner squared off, a
compact illustration of re-skinning an existing layout by overriding
its styles. Load it from Settings → Custom → Load… and edit live. See
[`docs/THEMING.md`](docs/THEMING.md)
for the authoring constraints (no `x:Class`, no compiled bindings,
SVG paths for glyphs).

The Windows 11 and Linux Mint Cinnamon looks that used to ship as
custom-theme samples are now **built-in styles** — pick them (alongside
Modern (Windows 7), Classic XP and Classic 9x) from
**Settings → Wygląd (Appearance)**.

## Documentation

- [`docs/OVERVIEW.md`](docs/OVERVIEW.md) — architecture, tech stack, how it
  works, credits.
- [`docs/AUTOMATION.md`](docs/AUTOMATION.md) — CI/CD map, code-signing
  options, required secrets, and the release pipeline.
- [`docs/THEMING.md`](docs/THEMING.md) — authoring custom themes for the
  Settings → Custom feature.
- [`docs/TRANSLATIONS.md`](docs/TRANSLATIONS.md) — translating MenYou.
- [`CHANGELOG.md`](CHANGELOG.md) — versioned change log (auto-generated
  by release-please from Conventional Commits).
- [`CONTRIBUTING.md`](CONTRIBUTING.md) — building, layout, PR flow.

## Inspiration

MenYou stands on the shoulders of projects I learned a great deal from:

- **[Open-Shell](https://github.com/Open-Shell/Open-Shell-Menu)** — beyond
  being the project that started this whole journey, its source taught me
  an enormous amount about how the Windows shell works under the hood.
- **[Fluid.Avalonia](https://github.com/Alpaq92/Fluid.Avalonia)** — the
  Fluent-2 theme that gives MenYou's Settings window its look.
- **Windows 10** — the classic, no-nonsense Start menu that several of
  MenYou's built-in layouts pay homage to (alongside Windows 7 and the
  XP / 9x classics).
- **[SukiUI](https://github.com/kikipoulet/SukiUI)** — its slim
  scrollbar templates and live-XAML-preview pattern inspired the
  equivalents in MenYou.
- **[Avalonia](https://avaloniaui.net/)** — the cross-platform UI
  framework the whole app is built on.

## License

[MIT](LICENSE) © Alpaq and MenYou contributors.

The app icon is adapted from
[`view-dashboard`](https://pictogrammers.com/library/mdi/icon/view-dashboard/),
part of [Material Design Icons](https://pictogrammers.com/library/mdi/) by
Pictogrammers (Apache-2.0).
