using System.Collections.Generic;
using System.Windows.Input;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;

namespace MenYou.ViewModels.Items;

public abstract partial class MenuItemViewModel : ObservableObject
{
    /// App-wide fallback bitmap shown for any item whose per-item icon
    /// extraction returned null (e.g. UWP Settings icons that sporadically
    /// fail to load from the shell). Set once at startup by App.axaml.cs;
    /// setting it later refreshes all currently-live items that don't
    /// have a real icon yet, via the weak-reference instance registry
    /// below.
    public static Bitmap? FallbackIcon
    {
        get => _fallbackIcon;
        set
        {
            _fallbackIcon = value;
            RefreshFallbackForLiveInstances();
        }
    }
    private static Bitmap? _fallbackIcon;

    /// Folder-specific fallback. Items whose <see cref="FallbackForType"/>
    /// resolves through here (currently <see cref="FolderItemViewModel"/>)
    /// render this instead of the cog when their per-item icon is null.
    public static Bitmap? FolderFallbackIcon
    {
        get => _folderFallbackIcon;
        set
        {
            _folderFallbackIcon = value;
            RefreshFallbackForLiveInstances();
        }
    }
    private static Bitmap? _folderFallbackIcon;

    /// Per-subclass fallback hook. Base returns the cog
    /// (<see cref="FallbackIcon"/>); folders override to prefer
    /// <see cref="FolderFallbackIcon"/> when available.
    protected virtual Bitmap? FallbackForType => FallbackIcon;

    private static readonly List<WeakReference<MenuItemViewModel>> _instances = new();
    private static readonly object _instancesLock = new();

    private static void RefreshFallbackForLiveInstances()
    {
        lock (_instancesLock)
        {
            for (int i = _instances.Count - 1; i >= 0; i--)
            {
                if (!_instances[i].TryGetTarget(out var item))
                {
                    _instances.RemoveAt(i);
                    continue;
                }
                if (item._icon is null)
                    item.OnPropertyChanged(nameof(Icon));
            }
        }
    }

    protected MenuItemViewModel()
    {
        lock (_instancesLock)
            _instances.Add(new WeakReference<MenuItemViewModel>(this));
    }

    [ObservableProperty] private string _displayName = "";

    /// True when the item is a fresh addition the user hasn't seen yet
    /// (just-installed app, or a folder containing such an app). Drives
    /// the accent-tint highlight wherever the item is rendered.
    [ObservableProperty] private bool _isNew;

    private Bitmap? _icon;
    public Bitmap? Icon
    {
        get => _icon ?? FallbackForType;
        set => SetProperty(ref _icon, value);
    }

    /// Uniform shape used by skins that present this VM hierarchy as a
    /// cascading menu — folders return their children, leaves return empty.
    public virtual IReadOnlyList<MenuItemViewModel> Children { get; } =
        Array.Empty<MenuItemViewModel>();

    /// Command invoked when the item is activated (clicked / Enter). Null for
    /// non-leaf nodes so the menu just expands instead of doing anything.
    public virtual ICommand? Command => null;
}
