using Avalonia.Media.Imaging;

namespace MenYou.Services;

/// Result of an avatar lookup. <see cref="Bitmap"/> is null when nothing
/// usable was found. <see cref="IsDefault"/> is true when the bitmap is one
/// of the generic Windows silhouettes (e.g. <c>user-192.png</c>) rather
/// than a picture the user actually picked — the dark-mode invert/brighten
/// pipeline only makes sense for the silhouette, not a real photo.
public readonly record struct AvatarResult(Bitmap? Bitmap, bool IsDefault);

public interface IUserAvatarService
{
    /// Returns the current user's account picture as an Avalonia Bitmap, or
    /// null if no usable image could be located on disk. Prefers the user's
    /// configured picture; falls back to the Windows default silhouette.
    AvatarResult LoadAvatar();
}
