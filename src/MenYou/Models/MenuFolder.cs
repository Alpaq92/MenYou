using System.Text.Json.Serialization;

namespace MenYou.Models;

/// A folder node in the All Programs tree, mirroring the on-disk Start Menu
/// directory structure. Subfolders + apps are populated lazily by the discovery service.
public sealed class MenuFolder
{
    /// Display name. Starts as the raw on-disk folder name; the discovery
    /// service upgrades it to the shell's localized display name once
    /// resolved. Mutable so a second copy of the same folder (per-user vs.
    /// common Start Menu root) can promote the name from "Accessories" to
    /// "Akcesoria systemu" without re-creating the node.
    public required string Name { get; set; }
    public required string Path { get; init; }

    /// Original on-disk basename, used to dedup across the per-user and
    /// common Start Menu roots regardless of whether one variant localizes
    /// and the other doesn't.
    public string RawName { get; init; } = string.Empty;

    // Settable (not get-only) so System.Text.Json repopulates them when the
    // discovery cache is deserialized — get-only collection properties are
    // skipped by the serializer's default object-creation handling, which
    // otherwise leaves the cached All-Programs tree empty until a live rescan.
    public List<MenuFolder> Folders { get; set; } = new();
    public List<AppEntry> Apps { get; set; } = new();

    [JsonIgnore]
    public bool IsEmpty => Folders.Count == 0 && Apps.Count == 0;
}
