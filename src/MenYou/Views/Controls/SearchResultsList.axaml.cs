using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using MenYou.Platform.Windows;
using MenYou.Services;
using MenYou.ViewModels;
using MenYou.ViewModels.Items;
using MenYou.Views.Behaviors;
using Microsoft.Extensions.DependencyInjection;

namespace MenYou.Views.Controls;

public partial class SearchResultsList : UserControl
{
    /// When true, a single tap on a result launches it — matching the Pinned /
    /// Recent tiles and the All-Programs tree, which all launch on a single
    /// click. The Win 11 + Mint layouts set this: their search overlay has no
    /// side context panel, so there's nothing to "select to preview" and a
    /// click should just launch. The Win 7 / Classic layouts leave it false —
    /// there a single click selects a result to populate the adjacent
    /// Recent-files / Tasks panel, and a double-click (or Enter) launches.
    public static readonly StyledProperty<bool> SingleClickLaunchProperty =
        AvaloniaProperty.Register<SearchResultsList, bool>(nameof(SingleClickLaunch));

    public bool SingleClickLaunch
    {
        get => GetValue(SingleClickLaunchProperty);
        set => SetValue(SingleClickLaunchProperty, value);
    }

    public SearchResultsList()
    {
        InitializeComponent();
        AddHandler(TappedEvent, OnTapped);
        AddHandler(DoubleTappedEvent, OnDoubleTapped);
        // Right-click / Menu key: build the per-result context menu in code so
        // it can mirror the selection detail panel — verbs + Pin/Unpin, then
        // the app's JumpList Tasks + Recent destinations (which load lazily).
        AddHandler(ContextRequestedEvent, OnContextRequested);
    }

    private void OnTapped(object? sender, TappedEventArgs e)
    {
        if (SingleClickLaunch) LaunchFrom(e.Source);
    }

    private void OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        // Only the double-click layouts (Win 7 / Classic, SingleClickLaunch =
        // false) launch on double-tap. When SingleClickLaunch is on, the first
        // tap already launched via OnTapped, so launching here would fire the
        // result a second time.
        if (!SingleClickLaunch) LaunchFrom(e.Source);
    }

    /// Launches the result whose row was tapped. Resolves the row from the
    /// event source (the ListBoxItem's data context) rather than the
    /// view-model's Selected, so a tap launches exactly the clicked row even
    /// if a still-settling search just reconciled the list.
    private void LaunchFrom(object? source)
    {
        if (DataContext is not SearchViewModel vm) return;
        var r = (source as Visual)?
                    .GetSelfAndVisualAncestors()
                    .OfType<ListBoxItem>()
                    .FirstOrDefault()?.DataContext as SearchResultViewModel
                ?? vm.Selected;
        if (r is not null) vm.LaunchCommand.Execute(r);
    }

    /// Builds and opens the per-result context menu at the pointer. Mirrors
    /// the selection detail panel: standard verbs + Pin/Unpin first, then the
    /// app's JumpList Tasks and Recent destinations. The JumpList loads
    /// lazily, so the menu opens immediately with the verbs and refills once
    /// the load completes (if it's still open).
    private async void OnContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (DataContext is not SearchViewModel owner) return;
        var row = (e.Source as Visual)?
            .GetSelfAndVisualAncestors()
            .OfType<ListBoxItem>()
            .FirstOrDefault();
        if (row?.DataContext is not SearchResultViewModel vm) return;
        e.Handled = true;

        // Right-click selects the row too — matches Windows, updates the
        // detail panel, and shares the same JumpList load.
        owner.Selected = vm;

        var menu = new ContextMenu { Placement = PlacementMode.Pointer };
        PopulateMenu(menu, vm, owner);
        menu.Open(row);

        // Fill the app-specific JumpList items in once they've loaded.
        try { await owner.EnsureJumpListLoadedAsync(vm); } catch { }
        if (menu.IsOpen) PopulateMenu(menu, vm, owner);
    }

    /// (Re)builds the menu's items from the result's current state. Called on
    /// open and again after the JumpList load resolves, so app-specific items
    /// appear as soon as they're available.
    private static void PopulateMenu(ContextMenu menu, SearchResultViewModel vm, SearchViewModel owner)
    {
        menu.Items.Clear();

        // Standard verbs — same order as the Pinned / All-Programs menus.
        menu.Items.Add(MenuItemFactory.Create(Strings.Open, vm.LaunchCommand));
        if (vm.CanRunAsAdmin)
            menu.Items.Add(MenuItemFactory.Create(Strings.RunAsAdmin, vm.RunAsAdministratorCommand));
        if (vm.CanModifyPin)
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(MenuItemFactory.Create(vm.PinToggleLabel, vm.TogglePinCommand));
        }
        if (vm.CanOpenFileLocation)
            menu.Items.Add(MenuItemFactory.Create(Strings.OpenFileLocation, vm.OpenFileLocationCommand));

        // App-published JumpList Tasks (Brave "New window" / "New private
        // window", Firefox "Open new tab", …) — the per-app verbs the
        // selection detail panel lists. Tooltip carries the full title since a
        // long one trims with an ellipsis.
        if (vm.Tasks.Count > 0)
        {
            menu.Items.Add(new Separator());
            foreach (var task in vm.Tasks)
                menu.Items.Add(MenuItemFactory.Create(task.Title, owner.OpenTaskCommand, task, tooltip: true));
        }

        // Per-app Recent destinations from the Windows JumpList (long file
        // names trim with an ellipsis; the full name is the tooltip).
        // Cap recent files in the context menu (default 8; 0 hides them). The
        // Win 7 / Classic side panel binds to vm.Recent directly and stays full.
        var cap = App.Services.GetService<ISettingsService>()?.Current.ContextMenuRecentCount ?? 8;
        var recent = vm.Recent.Take(cap).ToList();
        if (recent.Count > 0)
        {
            menu.Items.Add(new Separator());
            foreach (var dest in recent)
                menu.Items.Add(MenuItemFactory.Create(dest.DisplayName, owner.OpenRecentCommand, dest, tooltip: true));
        }
    }
}
