using System.Text.Json;
using System.Text.Json.Serialization;
using MenYou.Models;

namespace MenYou.Services;

/// Persists the resolved app-discovery snapshot to
/// <c>%AppData%\MenYou\discovery-cache.json</c> so a cold start can paint
/// from disk (a plain file read, no shell COM) instead of waiting on the
/// live scan. <see cref="AppDiscoveryService"/> owns the freshness policy:
/// a fast filesystem fingerprint decides whether the snapshot is still valid
/// for the initial paint, and a background live scan always runs as a
/// backstop and swaps in if anything changed.
internal static class DiscoveryCache
{
    /// Bump whenever the cached shape (AppEntry / MenuFolder fields) or the
    /// fingerprint scheme changes, so caches written by an older build are
    /// ignored after an update rather than deserialized into the wrong shape.
    /// v2: fingerprint now also covers %LOCALAPPDATA%\Packages (UWP signal).
    public const int SchemaVersion = 2;

    public sealed record Snapshot(int Version, string Fingerprint, MenuFolder Root, List<AppEntry> Flat);

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static string CachePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "MenYou", "discovery-cache.json");

    /// Loads + validates the snapshot. Returns null on any problem (missing,
    /// unreadable, wrong schema) — the caller then falls back to a live scan.
    /// Fingerprint validity is checked by the caller, not here.
    public static Snapshot? TryLoad()
    {
        try
        {
            var path = CachePath;
            if (!File.Exists(path)) return null;
            using var stream = File.OpenRead(path);
            var snap = JsonSerializer.Deserialize<Snapshot>(stream, Options);
            if (snap is null || snap.Version != SchemaVersion) return null;
            if (snap.Root is null || snap.Flat is null || snap.Flat.Count == 0) return null;
            return snap;
        }
        catch
        {
            return null;
        }
    }

    public static void Save(MenuFolder root, List<AppEntry> flat, string fingerprint)
    {
        try
        {
            var path = CachePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var snap = new Snapshot(SchemaVersion, fingerprint, root, flat);
            // Write to a temp file then atomically move, so a crash or a
            // concurrent read mid-write can't leave a truncated cache that
            // fails to parse on the next launch.
            var tmp = path + ".tmp";
            using (var stream = File.Create(tmp))
                JsonSerializer.Serialize(stream, snap, Options);
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            // The cache is a pure optimization — never let a write failure
            // (locked file, full disk, readonly profile) surface.
        }
    }
}
