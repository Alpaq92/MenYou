using System.Text.Json.Serialization;
using MenYou.Models;
using MenYou.Platform.Windows;

namespace MenYou.Services;

// Source-generated System.Text.Json metadata. Moving every reflection-based
// (de)serialization onto these contexts removes the IL2026
// (RequiresUnreferencedCode) warnings that block a trim-safe publish — the
// generator emits the property/converter metadata at build time, so the
// trimmer can see exactly which members are used and never strips a type the
// serializer needs. See docs/OPTIMIZATION.md#trimming.
//
// Two contexts because the persisted shapes want different, behaviour-
// preserving options:
//   • settings.json is hand-editable and stores enums as strings
//     (JsonStringEnumConverter parity) — indented + UseStringEnumConverter.
//   • the machine-written JSON (discovery cache, Start-pins policy value,
//     localization bundles, icon-cache index) is compact and, for the
//     discovery cache, keeps enums NUMERIC to match every cache written before
//     this migration, so no schema bump / cold rescan is forced.

/// settings.json — indented, enums as strings.
[JsonSourceGenerationOptions(
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(UserSettings))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext
{
}

/// Machine-written JSON: discovery-cache snapshot, Win11 Start-pins policy
/// payload, the localization bundles, and the icon-cache index. Compact; enums
/// stay numeric (no UseStringEnumConverter) so the discovery-cache format is
/// byte-compatible with pre-source-gen caches. PropertyNameCaseInsensitive
/// mirrors the old DiscoveryCache / IconDiskCache options (and is harmless for
/// the dictionary + pins payload). WhenWritingNull only omits already-nullable
/// members (e.g. the icon index's negative-result File), which round-trip back
/// to null — so the icon index stays compatible with caches written before this
/// migration, no SchemaVersion bump.
[JsonSourceGenerationOptions(
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(DiscoveryCache.Snapshot))]
[JsonSerializable(typeof(StartPinsPayload))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(IconDiskCache.IndexFile))]
internal sealed partial class MachineJsonContext : JsonSerializerContext
{
}
