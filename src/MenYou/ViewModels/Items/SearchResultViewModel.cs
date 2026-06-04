using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MenYou.Models;

namespace MenYou.ViewModels.Items;

/// Thin wrapper over <see cref="SearchResult"/> that adds an observable
/// <see cref="Icon"/> property. SearchViewModel populates Results with
/// these and then kicks off an async icon-resolution pass — the UI shows
/// a letter-avatar fallback (TitleInitial) until the bitmap arrives.
///
/// Falls back to <see cref="MenuItemViewModel.FallbackIcon"/> (the
/// Settings cog) when no per-result bitmap is available, matching the
/// behavior of the main menu's pinned/recent items.
public sealed partial class SearchResultViewModel : ObservableObject
{
    public SearchResult Data { get; }

    /// Back-reference to the owning SearchViewModel. The context-menu
    /// commands below delegate to it so the launch logic (UWP /
    /// Control Panel / file-path branching, runas elevation, Explorer
    /// reveal) stays centralized in one place instead of being copied
    /// into the item VM. Mirrors how AppItemViewModel carries the
    /// services it needs — the difference is the search launch path is
    /// already implemented on the parent, so we route through it.
    private readonly SearchViewModel _owner;

    /// "Open" verb — same as left-clicking or pressing Enter.
    [RelayCommand]
    private void Launch() => _owner.Launch(this);

    /// "Run as administrator" verb. Only meaningful for the result
    /// kinds CanRunAsAdmin allows; hidden otherwise in the menu.
    [RelayCommand]
    private void RunAsAdministrator() => _owner.LaunchAsAdmin(this);

    /// "Open file location" verb — reveals the target highlighted in
    /// Explorer. Hidden when the result has no on-disk file (URI
    /// schemes, Control Panel tasks).
    [RelayCommand]
    private void OpenFileLocation() => _owner.OpenFileLocation(this);

    /// True only when the result points at something the shell can run
    /// via the `runas` verb. Mirrors SearchViewModel.CanRunAsAdmin but
    /// evaluated per-item so the context menu can gate the entry
    /// without depending on which row happens to be Selected.
    public bool CanRunAsAdmin
    {
        get
        {
            var r = Data;
            if (r.Kind is SearchResultKind.PackagedApp or SearchResultKind.ControlPanelTask) return false;
            var path = r.TargetPath;
            if (string.IsNullOrWhiteSpace(path)) return false;
            var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
            return ext is ".exe" or ".bat" or ".cmd" or ".msc" or ".lnk";
        }
    }

    /// True when the result has a real filesystem target we can reveal
    /// in Explorer (URI-scheme / Control Panel targets don't qualify).
    public bool CanOpenFileLocation
    {
        get
        {
            var path = Data.TargetPath;
            return !string.IsNullOrWhiteSpace(path) && System.IO.File.Exists(path);
        }
    }

    public string Title => Data.Title;
    public string? Subtitle => Data.Subtitle;
    public string TitleInitial =>
        string.IsNullOrEmpty(Data.Title) ? "?" : Data.Title.Substring(0, 1).ToUpperInvariant();

    private Bitmap? _icon;
    public Bitmap? Icon
    {
        get => _icon ?? MenuItemViewModel.FallbackIcon;
        set
        {
            // LargeIcon falls back to Icon when no large variant has
            // loaded yet — when Icon arrives mid-flight (the list-row
            // bitmap finishes before the lazy large extraction), the
            // context panel binding has to be told to re-evaluate too.
            if (SetProperty(ref _icon, value))
                OnPropertyChanged(nameof(LargeIcon));
        }
    }

    /// Higher-resolution variant loaded lazily by SearchViewModel when
    /// this result becomes Selected — drives the prominent context-panel
    /// icon at 48×48 without pixelation. Falls back to the list-row Icon
    /// (and ultimately the cog) if no large variant could be extracted.
    private Bitmap? _largeIcon;
    public Bitmap? LargeIcon
    {
        get => _largeIcon ?? Icon;
        set => SetProperty(ref _largeIcon, value);
    }

    /// True only when the large-resolution bitmap has actually been
    /// loaded — distinct from <see cref="LargeIcon"/>'s null-check, which
    /// would be hidden by the Icon fallback once the list-row bitmap
    /// arrives. Used by SearchViewModel to decide whether to kick off
    /// the lazy large-icon extraction.
    public bool HasLargeIcon => _largeIcon is not null;

    /// Per-app recent destinations populated from the Windows JumpList
    /// when this result becomes Selected. Empty for apps that don't
    /// publish a destination list (UWP apps without explicit AUMID,
    /// Control Panel tasks, settings deep-links).
    public System.Collections.ObjectModel.ObservableCollection<RecentDestination> Recent { get; } = new();
    public bool HasRecentLoaded { get; set; }

    /// File-level recent entry — path + display name + lazily-loaded icon.
    public sealed partial class RecentDestination : ObservableObject
    {
        public string Path { get; }
        public string DisplayName { get; }

        [ObservableProperty] private Bitmap? _icon;

        public RecentDestination(string path, string displayName)
        {
            Path = path;
            DisplayName = displayName;
        }
    }

    /// App-published Task entry — e.g. Firefox's "Open new tab",
    /// "New private window". Title is already SHLoadIndirectString-
    /// resolved for indirect-string references.
    public sealed record JumpTask(string Title, string Target, string Arguments);

    /// Tasks (custom destinations) loaded lazily alongside Recent.
    public System.Collections.ObjectModel.ObservableCollection<JumpTask> Tasks { get; } = new();

    public SearchResultViewModel(SearchResult data, SearchViewModel owner)
    {
        Data = data;
        _owner = owner;
    }
}
