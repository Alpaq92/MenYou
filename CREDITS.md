# Credits

## Inspiration

MenYou stands on the shoulders of projects I learned a great deal from:

- **[Open-Shell](https://github.com/Open-Shell/Open-Shell-Menu)** — beyond being the project that started this whole journey, its source taught me an enormous amount about how the Windows shell works under the hood.
- **[Fluid.Avalonia](https://github.com/Alpaq92/Fluid.Avalonia)** — the Fluent-2 theme that gives MenYou's Settings window its look.
- **Good old Windows** — the classic, no-nonsense Start menu that several of MenYou's built-in layouts pay homage to, alongside the Windows 11 menu (but with working search).
- **[SukiUI](https://github.com/kikipoulet/SukiUI)** — its slim scrollbar templates and live-XAML-preview pattern inspired the equivalents in MenYou.
- **[Avalonia](https://avaloniaui.net/)** — the cross-platform UI framework the whole app is built on.

## Third-party code & assets

- **[Open-Shell](https://github.com/Open-Shell/Open-Shell-Menu)** — the JumpList COM-reading approach (per-app Recent + Tasks + implicit AUMID derivation) is ported from its `JumpLists.cpp`, and it informed the Shift+Win activation and pin-fallback strategies.
- **[SukiUI](https://github.com/kikipoulet/SukiUI)** — the slim ScrollBar / ScrollViewer ControlTemplates in `Styles/Scrollbar.axaml` and the `XamlStringToControlConverter` live-preview pattern are adapted from it.
- **[Fluid.Avalonia](https://github.com/Alpaq92/Fluid.Avalonia)** — the app-wide Fluent-2 theme + system-accent integration.
- **Avalonia, CommunityToolkit.Mvvm, Jeek.Avalonia.Localization, Inno Setup, .NET 10** — the rest of the runtime / build stack.

## App icon

The icon is adapted from [`view-dashboard`](https://pictogrammers.com/library/mdi/icon/view-dashboard/), part of [Material Design Icons](https://pictogrammers.com/library/mdi/) by Pictogrammers (**Apache-2.0**). `icon.svg` is the flat four-tile source glyph; `icon_v2.*` wraps it in a hand-authored Liquid-Glass disc.
