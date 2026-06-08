using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using MenYou.Platform.Windows;
using MenYou.Services;
using MenYou.ViewModels.Items;
using Microsoft.Extensions.DependencyInjection;

namespace MenYou.Views.Behaviors;

/// One shared context menu for every app tile / row in MenYou — the Win 11
/// Pinned / Recent / All-Programs grids, the Win 7 pinned strip + recent list,
/// the All-Programs tree, and the folder flyout. Opt a control in with
/// <c>behaviors:AppContextMenuBehavior.Enable="True"</c> and it builds the menu
/// in code on right-click, so the SAME verbs + Pin/Unpin + per-app JumpList
/// (the app's published Tasks like "New window" / "New private window" and its
/// Recent destinations) appear everywhere — matching the search results' menu
/// (<see cref="Views.Controls.SearchResultsList"/>) instead of the five
/// divergent declarative menus this replaced.
///
/// The JumpList loads lazily off the UI thread, so the menu opens instantly
/// with the verbs and the app-specific entries stream into the open menu a
/// moment later (same pattern as the search menu).
public sealed class AppContextMenuBehavior
{
    private AppContextMenuBehavior() { } // attached-property host; never instantiated

    public static readonly AttachedProperty<bool> EnableProperty =
        AvaloniaProperty.RegisterAttached<AppContextMenuBehavior, Control, bool>("Enable");

    public static void SetEnable(Control control, bool value) => control.SetValue(EnableProperty, value);
    public static bool GetEnable(Control control) => control.GetValue(EnableProperty);

    static AppContextMenuBehavior()
    {
        EnableProperty.Changed.AddClassHandler<Control>(OnEnableChanged);
    }

    private static void OnEnableChanged(Control control, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.GetNewValue<bool>())
            control.AddHandler(InputElement.ContextRequestedEvent, OnContextRequested);
        else
            control.RemoveHandler(InputElement.ContextRequestedEvent, OnContextRequested);
    }

    private static async void OnContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (sender is not Control control || control.DataContext is not AppItemViewModel vm) return;
        e.Handled = true;

        var menu = new ContextMenu { Placement = PlacementMode.Pointer };
        AddVerbs(menu, vm);
        menu.Open(control);

        // Resolve the app's implicit JumpList key (AUMID / .lnk-derived id /
        // path) and read its Tasks + Recent off the UI thread, then append to
        // the still-open menu. No-op for apps that publish neither.
        var key = JumpListReader.ResolveKey(vm.Entry.Aumid, vm.Entry.SourceLnkPath, vm.Entry.TargetPath);
        if (string.IsNullOrEmpty(key)) return;

        // User-configurable cap for recent files in the context menu (default 8;
        // 0 hides them). The Win 7 / Classic side panel is uncapped — see
        // SearchViewModel.LoadJumpListAsync.
        var cap = App.Services.GetService<ISettingsService>()?.Current.ContextMenuRecentCount ?? 8;

        IReadOnlyList<JumpListReader.JumpTask> tasks;
        IReadOnlyList<JumpListReader.Destination> recent;
        try
        {
            (tasks, recent) = await Task.Run(() => (
                (IReadOnlyList<JumpListReader.JumpTask>)JumpListReader.ReadTasks(key)
                    .Where(t => !t.IsSeparator && !string.IsNullOrEmpty(t.Title)).ToList(),
                JumpListReader.ReadRecent(key, cap)));
        }
        catch { return; }

        if (!menu.IsOpen) return; // user dismissed it before the load finished
        AppendJumpList(menu, vm, tasks, recent);
    }

    private static void AddVerbs(ContextMenu menu, AppItemViewModel vm)
    {
        // Same order as the search menu: Open, Run as administrator, then
        // Pin/Unpin (+ its separator) gated on CanModifyPin, then Open file
        // location. App items always support Run-as-admin / Open-file-location,
        // so those aren't gated here (unlike search, which can hit non-app
        // results).
        menu.Items.Add(MenuItemFactory.Create(Strings.Open, vm.LaunchCommand));
        menu.Items.Add(MenuItemFactory.Create(Strings.RunAsAdmin, vm.RunAsAdministratorCommand));
        if (vm.CanModifyPin)
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(MenuItemFactory.Create(vm.PinToggleLabel, vm.TogglePinCommand));
        }
        menu.Items.Add(MenuItemFactory.Create(Strings.OpenFileLocation, vm.OpenFileLocationCommand));
    }

    private static void AppendJumpList(ContextMenu menu, AppItemViewModel vm,
        IReadOnlyList<JumpListReader.JumpTask> tasks, IReadOnlyList<JumpListReader.Destination> recent)
    {
        if (tasks.Count > 0)
        {
            menu.Items.Add(new Separator());
            foreach (var t in tasks)
            {
                var item = MenuItemFactory.Create(t.Title, tooltip: true);
                var target = t.Target;
                var args = t.Arguments;
                item.Click += (_, _) => vm.OpenJumpListTarget(target, args);
                menu.Items.Add(item);
            }
        }

        if (recent.Count > 0)
        {
            menu.Items.Add(new Separator());
            foreach (var d in recent)
            {
                var item = MenuItemFactory.Create(d.DisplayName, tooltip: true);
                var path = d.Path;
                item.Click += (_, _) => vm.OpenJumpListTarget(path, null);
                menu.Items.Add(item);
            }
        }
    }
}
