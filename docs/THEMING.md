# Custom themes

MenYou's **Settings → Custom** tab loads a self-contained AXAML file,
parses it through `AvaloniaRuntimeXamlLoader` on every keystroke, and
renders the result in a live preview pane next to the editor. This guide
covers the bundled sample, how loading works, and how to author your own.

> **Why this rules out NativeAOT.** Parsing a theme at runtime needs the
> JIT, which NativeAOT strips out — so as long as MenYou supports custom
> themes it can't be published with NativeAOT. It ships with ReadyToRun
> instead, which keeps the speed *and* this feature.

The example files under
[`samples/custom-themes/`](../samples/custom-themes/) are **full working
themes wired to MenYou's live data**, not standalone mockups: each binds to
the real `StartMenuViewModel` (Pinned / Recent / Programs / Search / Power
/ RightPanel), paints with MenYou's app-level brushes
(`MenuBackgroundBrush`, `PowerDangerBrush`, …), and embeds MenYou's own
controls (`ctrl:AppGrid`, `ctrl:ProgramsTree`, …).

What lets them parse through `AvaloniaRuntimeXamlLoader.Parse<Control>` is
that they omit a compiled `x:Class` and use **reflection-style bindings**
(no `x:DataType`, no compiled-binding casts). They render in full only when
run **inside MenYou** — selected as the active theme, where the app's
resources and view-model are in scope. The in-dialog **preview pane** has
the app resources but not the `StartMenuViewModel` data context, so it
shows the chrome (backgrounds, section headers, power glyphs) with the
app/folder lists empty — that's expected, not a bug.

| File | What it mimics |
|---|---|
| [`Windows7Square.axaml`](../samples/custom-themes/Windows7Square.axaml) | A clone of the built-in "Modern (Windows 7)" layout with every corner squared off — pinned tile grid + user header on top, scrollable Recent / All Programs and the shell-shortcut / search-context column in the middle, search box + six power glyphs along the bottom. Re-declares MenYou's rounded control styles (`Button.menu`, the circular power buttons, the search box, list highlights, the round avatar) with `CornerRadius=0`, so the whole interior reads sharp/pointy instead of rounded. |

> **Heads up — the Windows 11 and Linux Mint Cinnamon samples graduated to
> built-in styles.** They used to ship here as `Windows11.axaml` and
> `MintCinnamon.axaml`; both are now first-class layouts you pick from
> **Settings → Wygląd (Appearance)** (alongside Modern (Windows 7),
> Classic XP and Classic 9x) instead of loading by hand. `Windows7Square`
> stays as a sample because it's a small, self-contained illustration of
> the "re-skin an existing layout by overriding its styles" technique.

## How to load one

1. Open MenYou Settings (tray icon → Settings, or Shift+Win → Settings).
2. Switch to the **Custom** tab.
3. Tick **Use custom theme** (enables the editor + buttons).
4. Click **Load…** and pick a file from `samples/custom-themes/`.
5. The XAML lands in the editor; the right-hand preview pane renders it
   live.
6. Edit, then **Save** to write the modified version back out to disk
   (the loaded copy in `%AppData%\MenYou\CustomThemes\` stays untouched —
   Save is export, not in-place overwrite).

## Authoring your own

Hard rule (a limit of runtime-loaded Avalonia XAML, not a MenYou
invention):

- **No `x:Class`.** The runtime XAML loader can't bind to a compiled
  partial class — there's no codebehind in this pipeline. Use
  reflection-style bindings too: no `x:DataType` and no compiled-binding
  casts like `((vm:StartMenuViewModel)DataContext)`. Plain
  `{Binding Pinned}` and `{Binding $parent[ItemsControl].DataContext.X}`
  resolve fine at runtime.

Then pick a lane:

- **MenYou-integrated (what the bundled samples do).** Bind to the live
  `StartMenuViewModel` and lean on MenYou's app-level brushes
  (`MenuBackgroundBrush`, `MenuForegroundBrush`, `PowerDangerBrush`, …)
  and custom controls (`ctrl:AppGrid`, `ctrl:AppList`,
  `ctrl:ProgramsTree`, `ctrl:SearchResultsList`). The result behaves
  exactly like a built-in layout. Trade-off: the in-dialog preview can't
  populate the view-model, so the app/folder lists read empty there —
  judge the real look by selecting the theme and opening the menu.
- **Fully portable / preview-accurate.** If you want the file to render
  identically in the preview and outside MenYou, avoid app-level
  `DynamicResource`/`StaticResource` (define every brush inline) and
  avoid view-model `{Binding ...}` (use static literals or
  `{Binding ..., Source=...}` with an explicit source). You lose the
  live app/folder data but gain a self-contained mockup.

Either way:

- **Stick to controls Avalonia ships out of the box** — `Grid`,
  `StackPanel`, `Border`, `Button`, `TextBlock`, `TextBox`,
  `ScrollViewer`, `Image`, `Path`, etc. Third-party controls (a
  library's custom `NavigationView`, charting widgets, …) need
  their xmlns declared at the root AND need their assemblies to be
  loaded in the host process, which limits portability.
- **Use SVG path data for icons** — `Path Data="..."` with a Material
  Design Icons path (Apache-2.0) is the lightest option. No image
  files = no asset-path resolution problems.

## License notes

`Windows7Square.axaml` ships no third-party assets of its own — it reuses
MenYou's own control styles and Segoe Fluent Icons glyphs, so there's
nothing extra you need to attribute.
