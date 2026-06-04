using System.Globalization;
using System.Text.Json;
using Avalonia.Platform;
using Jeek.Avalonia.Localization;

namespace MenYou.Services;

/// Custom <see cref="Jeek.Avalonia.Localization.ILocalizer"/> backed by the
/// JSON language files embedded under <c>Languages\*.json</c> as Avalonia
/// resources. Picked over the stock <see cref="JsonLocalizer"/> for two
/// reasons:
///
///   1. No filesystem dependency. The bundled <c>Languages\</c> folder
///      ships inside the assembly's avares:// namespace, so a single
///      self-contained .exe still localizes correctly without users
///      copying a Languages directory next to it.
///   2. Per-key fallback to English. The stock JsonLocalizer only loads
///      one language file at a time and returns <c>"Language:Key"</c> on
///      misses, which means a partial translation (say <c>pl.json</c>
///      missing the <c>Win11StartPins</c> key) would show literal
///      <c>pl:Win11StartPins</c> in the UI. We always load <c>en.json</c>
///      alongside the active culture and fall back to it on miss, then
///      to the key itself only as a last resort.
///
/// Language selection uses <see cref="CultureInfo.CurrentUICulture"/>
/// two-letter ISO code (Windows display language). On non-en startup we
/// load both the requested language and en; on en startup we load en
/// only.
public sealed class AvaloniaResourceLocalizer : BaseLocalizer
{
    private const string ResourceRoot = "avares://MenYou/Languages";
    private const string Fallback = "en";

    private Dictionary<string, string>? _primaryStrings;
    private Dictionary<string, string>? _fallbackStrings;

    public override void Reload()
    {
        _primaryStrings = null;
        _fallbackStrings = null;
        _languages.Clear();

        // Enumerate the language files we shipped. AssetLoader doesn't
        // expose a directory listing for avares:// URIs, so we keep the
        // canonical set here. Adding a new language means dropping a
        // <code>{lang}.json</code> file in src/MenYou/Languages/ and
        // appending it below.
        string[] shipped =
        [
            "en", "pl", "de", "fr", "es", "it", "pt", "nl",
            "ru", "uk", "cs", "sk", "hu", "sv", "da", "nb",
            "fi", "tr", "ja", "zh", "ko"
        ];
        _languages.AddRange(shipped);

        ValidateLanguage();

        _primaryStrings = LoadLanguageFile(_language);
        _fallbackStrings = _language == Fallback
            ? null
            : LoadLanguageFile(Fallback);

        _hasLoaded = true;
        UpdateDisplayLanguages();
    }

    protected override void OnLanguageChanged() => Reload();

    public override string Get(string key)
    {
        if (!_hasLoaded) Reload();

        if (_primaryStrings is not null
            && _primaryStrings.TryGetValue(key, out var primary))
            return primary.Replace("\\n", "\n");

        if (_fallbackStrings is not null
            && _fallbackStrings.TryGetValue(key, out var fallback))
            return fallback.Replace("\\n", "\n");

        // Last-resort: show the key itself so missing strings are
        // immediately visible in the UI during development without
        // crashing the binding pipeline.
        return key;
    }

    private static Dictionary<string, string>? LoadLanguageFile(string language)
    {
        var uri = new Uri($"{ResourceRoot}/{language}.json");
        try
        {
            using var stream = AssetLoader.Open(uri);
            return JsonSerializer.Deserialize<Dictionary<string, string>>(stream);
        }
        catch
        {
            // Missing optional language file is non-fatal — caller will
            // fall back further (to en, then to the literal key).
            return null;
        }
    }
}
