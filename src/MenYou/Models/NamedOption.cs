using MenYou.Platform.Windows;

namespace MenYou.Models;

/// Pairs an enum value with the human-readable label we show in pickers and
/// menus. Keeps view code from depending on enum identifiers and lets one
/// definition drive both the Settings dialog and the tray submenu.
public sealed record NamedOption<T>(T Value, string Label, string? Description = null)
{
    public override string ToString() => Label;
}

public static class NamedOptions
{
    // Properties (not fields) so the Strings lookups are deferred until
    // first read. Strings.cs resolves system DLL / culture-dict entries
    // at static-init time of the Strings class — accessing them at field
    // initialization time inside another static class can race when the
    // JIT picks an unexpected init order. Properties are evaluated lazily
    // on every access and the JIT inlines them at the call site, so the
    // ordering hazard disappears with no measurable cost.
    // Order = picker/tray display order. Windows 11 leads because it's the
    // default look (see UserSettings.MenuStyle); the rest descend from modern
    // to classic.
    public static IReadOnlyList<NamedOption<MenuStyle>> MenuStyles => new[]
    {
        new NamedOption<MenuStyle>(MenuStyle.Windows11,    Strings.StyleWindows11,    Strings.StyleWindows11Desc),
        new NamedOption<MenuStyle>(MenuStyle.Win7,         Strings.StyleWin7,         Strings.StyleWin7Desc),
        new NamedOption<MenuStyle>(MenuStyle.MintCinnamon, Strings.StyleMintCinnamon, Strings.StyleMintCinnamonDesc),
        new NamedOption<MenuStyle>(MenuStyle.Classic2,     Strings.StyleClassic2,     Strings.StyleClassic2Desc),
        new NamedOption<MenuStyle>(MenuStyle.Classic1,     Strings.StyleClassic1,     Strings.StyleClassic1Desc),
    };

    public static IReadOnlyList<NamedOption<AppTheme>> Themes => new[]
    {
        new NamedOption<AppTheme>(AppTheme.Dark,   Strings.Dark,   null),
        new NamedOption<AppTheme>(AppTheme.Light,  Strings.Light,  null),
        new NamedOption<AppTheme>(AppTheme.System, Strings.System, Strings.FollowWindowsTheme),
    };

    // PureAlphabetical leads because it's the default (see UserSettings.ProgramsOrder).
    public static IReadOnlyList<NamedOption<ProgramsOrder>> ProgramsOrders => new[]
    {
        new NamedOption<ProgramsOrder>(ProgramsOrder.PureAlphabetical, Strings.OrderAlphabetical, null),
        new NamedOption<ProgramsOrder>(ProgramsOrder.FoldersFirst,     Strings.OrderFoldersFirst, null),
        new NamedOption<ProgramsOrder>(ProgramsOrder.AppsFirst,        Strings.OrderAppsFirst,    null),
    };
}
