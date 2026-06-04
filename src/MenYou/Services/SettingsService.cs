using System.Text.Json;
using System.Text.Json.Serialization;
using MenYou.Models;

namespace MenYou.Services;

public sealed class SettingsService : ISettingsService
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "MenYou", "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    public UserSettings Current { get; private set; } = new();
    public event Action? Changed;

    public SettingsService()
    {
        Load();
    }

    private void Load()
    {
        // Defensive ladder:
        //   1. Parse with the full options. Happy path.
        //   2. If that throws (legacy field type, bad enum value), do NOT
        //      blow the whole file away — keep the in-memory defaults so
        //      Pinned/Recent/etc. don't get wiped on an upgrade where one
        //      single field changed shape. Bail without rewriting.
        // The earlier version had a single broad catch that replaced
        // Current with a fresh UserSettings(); during a past Accent enum
        // migration that path silently wiped users' Recent lists. The
        // catch below now leaves the on-disk file alone.
        if (!File.Exists(FilePath)) return;
        try
        {
            var json = File.ReadAllText(FilePath);
            var s = JsonSerializer.Deserialize<UserSettings>(json, JsonOptions);
            if (s is not null) Current = s;
        }
        catch
        {
            // Leave Current as the fresh-defaults object. We deliberately
            // do NOT overwrite the on-disk file — the next Save() will,
            // but only after the user actually changes something, giving
            // them a chance to bail out (close MenYou without saving) if
            // their settings.json got corrupted by an external editor.
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(Current, JsonOptions));
            Changed?.Invoke();
        }
        catch
        {
            // best-effort
        }
    }

    public void Reset()
    {
        Current = new UserSettings();
        Save();
    }
}
