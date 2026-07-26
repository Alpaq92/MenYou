using System.Text.Json;
using Avalonia.Media.Imaging;

namespace MenYou.Services;

/// Persists extracted app icons as PNGs under
/// <c>%AppData%\MenYou\icons\</c> so a cold start can decode a small PNG per
/// tile instead of re-running ~N shell-COM extractions (measured ~6 s under
/// the post-login storm). Keyed by <c>AppEntry.Id</c>; an <c>index.json</c>
/// records, per id, the icon SOURCE file's last-write time so an in-place app
/// update (same .lnk path, new icon) invalidates just that entry, and whether
/// the app has an icon at all (a negative result is cached too — otherwise
/// every icon-less app would re-run the full extraction chain every boot).
///
/// Owned by <see cref="IconService"/>; instance state (the index + a lock) is
/// hit from the parallel icon-fill workers. A pure optimization — every
/// failure path falls back to live extraction, so a corrupt/locked cache is
/// never fatal.
internal sealed class IconDiskCache
{
    /// Bump to discard every cached icon after a format / extraction change
    /// (e.g. a different icon size or PNG encoding).
    private const int SchemaVersion = 1;

    private sealed record Entry(long Stamp, string? File);
    private sealed record IndexFile(int Version, Dictionary<string, Entry> Entries);

    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    private readonly string _dir;
    private readonly string _indexPath;
    private readonly object _lock = new();
    private readonly Dictionary<string, Entry> _index = new(StringComparer.OrdinalIgnoreCase);
    private bool _dirty;

    public IconDiskCache()
    {
        _dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MenYou", "icons");
        _indexPath = Path.Combine(_dir, "index.json");
        Load();
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_indexPath)) return;
            var idx = JsonSerializer.Deserialize<IndexFile>(File.ReadAllText(_indexPath), Options);
            // Wrong schema → ignore (leaves stale PNGs on disk; they're
            // overwritten as icons re-extract, and pruning isn't worth it).
            if (idx is null || idx.Version != SchemaVersion || idx.Entries is null) return;
            foreach (var kv in idx.Entries) _index[kv.Key] = kv.Value;
        }
        catch { /* unreadable index → start empty, re-extract everything */ }
    }

    /// True when a valid cached result exists for <paramref name="id"/> at the
    /// given source <paramref name="stamp"/>. <paramref name="bmp"/> is the
    /// cached icon, or null for a cached "this app has no icon" result — both
    /// are hits (return true) so the caller skips COM. A stamp mismatch or a
    /// failed PNG decode is a miss (false) → the caller re-extracts.
    public bool TryGet(string id, long stamp, out Bitmap? bmp)
    {
        bmp = null;
        Entry? e;
        lock (_lock)
        {
            if (!_index.TryGetValue(id, out e) || e.Stamp != stamp) return false;
        }
        if (e.File is null) return true; // negative result cached
        try
        {
            var png = Path.Combine(_dir, e.File);
            if (!File.Exists(png)) return false;
            bmp = new Bitmap(png);
            return true;
        }
        catch
        {
            return false; // corrupt/locked PNG → re-extract
        }
    }

    /// Stores an extraction result: the PNG for a real icon (atomic write), or
    /// a negative marker when <paramref name="bmp"/> is null. Updates the
    /// in-memory index; call <see cref="Flush"/> to persist it (batched — the
    /// icon fill Puts hundreds of entries, so the index is written once per
    /// batch, not per icon).
    public void Put(string id, long stamp, Bitmap? bmp)
    {
        string? file = null;
        if (bmp is not null)
        {
            file = id + ".png";
            try
            {
                Directory.CreateDirectory(_dir);
                var dst = Path.Combine(_dir, file);
                var tmp = dst + ".tmp";
                using (var s = File.Create(tmp)) bmp.Save(s);
                File.Move(tmp, dst, overwrite: true);
            }
            catch
            {
                return; // couldn't persist the PNG → don't index a missing file
            }
        }
        lock (_lock)
        {
            _index[id] = new Entry(stamp, file);
            _dirty = true;
        }
    }

    /// Persists the index atomically if anything changed since the last flush.
    public void Flush()
    {
        IndexFile snapshot;
        lock (_lock)
        {
            if (!_dirty) return;
            snapshot = new IndexFile(SchemaVersion, new Dictionary<string, Entry>(_index, StringComparer.OrdinalIgnoreCase));
            _dirty = false;
        }
        try
        {
            Directory.CreateDirectory(_dir);
            var tmp = _indexPath + ".tmp";
            using (var s = File.Create(tmp)) JsonSerializer.Serialize(s, snapshot, Options);
            File.Move(tmp, _indexPath, overwrite: true);
        }
        catch
        {
            // Best-effort — a lost index just means re-extraction next boot;
            // the PNGs already on disk get re-adopted as they re-extract.
            lock (_lock) _dirty = true; // retry on the next flush
        }
    }
}
