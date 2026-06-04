using Avalonia.Media.Imaging;
using MenYou.Models;

namespace MenYou.Services;

public interface IIconService
{
    Task<Bitmap?> GetIconAsync(AppEntry entry);
    Task<Bitmap?> GetIconForPathAsync(string path);
    Task<Bitmap?> GetIconForSearchResultAsync(SearchResult result);
    /// Returns a higher-resolution icon (48×48 via SHIL_EXTRALARGE) for
    /// surfaces that render the icon prominently — e.g. the search
    /// context panel. Falls back to the standard icon on failure.
    Task<Bitmap?> GetLargeIconForSearchResultAsync(SearchResult result);
    Bitmap? PlaceholderIcon { get; }
}
