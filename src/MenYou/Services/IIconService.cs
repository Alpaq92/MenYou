using Avalonia.Media.Imaging;
using MenYou.Models;

namespace MenYou.Services;

public interface IIconService
{
    Task<Bitmap?> GetIconAsync(AppEntry entry);
    /// <summary>
    /// Extracts icons for a whole batch of items across CPU cores, calling
    /// <paramref name="apply"/> for each non-null icon AS IT LANDS.
    /// </summary>
    /// <remarks>
    /// <paramref name="apply"/> runs on the extraction worker, so callers
    /// marshal to the UI thread themselves (a fire-and-forget Dispatcher.Post
    /// per icon). This is the cold-start bulk fill: awaiting GetIconAsync per
    /// item ran the ~N shell-COM extractions strictly serially.
    /// </remarks>
    Task LoadIconsAsync<T>(IReadOnlyList<T> items, Func<T, AppEntry> entryOf, Action<T, Bitmap> apply);
    Task<Bitmap?> GetIconForPathAsync(string path);
    Task<Bitmap?> GetIconForSearchResultAsync(SearchResult result);
    /// Returns a higher-resolution icon (48×48 via SHIL_EXTRALARGE) for
    /// surfaces that render the icon prominently — e.g. the search
    /// context panel. Falls back to the standard icon on failure.
    Task<Bitmap?> GetLargeIconForSearchResultAsync(SearchResult result);
    Bitmap? PlaceholderIcon { get; }
}
