# MenYou

A Windows Start-menu replacement written in C# / Avalonia. Ships five
built-in looks — Modern (Windows 7), Windows 11, Linux Mint Cinnamon,
Classic XP and Classic 9x — rendered on top of modern Windows shell
metadata (localized labels, account picture, taskbar pins, Start mirror,
JumpLists) rather than reinventing them.

> [!TIP]
> Press **Shift+Win** to toggle the menu.

## Why

A little backstory. I'd set up a slimmed-down Windows 11 using the
excellent [Tiny11](https://github.com/ntdevlabs/tiny11builder) builder
script — a genuinely great project. One trade-off of a debloated image,
though, is that Start-menu **search stopped finding my locally installed
apps**: that search path quietly leans on a few components a minimal
install leaves out. (It turns out the browser is more load-bearing in the
Start menu than you'd ever expect — searching your own machine shouldn't
really need Edge, but here we are. 🙂)

So I went looking for a Start-menu replacement. The standout free and
open-source option was the wonderful
[Open-Shell](https://github.com/Open-Shell/Open-Shell-Menu), which I have
a lot of respect for — the bundled skins just weren't quite to my taste.
I tried building my own skin for it, and after digging into how it works
under the hood I realised what I really wanted was something a bit more
flexible, on a more familiar and modern tech stack.

And that's how **MenYou** came to be — a Start-menu replacement built on
**.NET 10** and **Avalonia 12**, designed to be skinnable from the ground
up.

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
[`samples/custom-themes/README.md`](samples/custom-themes/README.md)
for the authoring constraints (no `x:Class`, no compiled bindings,
SVG paths for glyphs).

The Windows 11 and Linux Mint Cinnamon looks that used to ship as
custom-theme samples are now **built-in styles** — pick them (alongside
Modern (Windows 7), Classic XP and Classic 9x) from
**Settings → Wygląd (Appearance)**.

## Documentation

- [`docs/overview.md`](docs/overview.md) — architecture, inspirations,
  credits.
- [`docs/AUTOMATION.md`](docs/AUTOMATION.md) — CI/CD map, code-signing
  options (SignPath Foundation / Certum / EV), required secrets, and
  the release pipeline detail.
- [`samples/custom-themes/`](samples/custom-themes/) — runnable AXAML
  examples for the Settings → Custom feature.
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
