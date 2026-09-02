using System.Globalization;
using Avalonia.Data.Converters;

namespace MenYou.Views.Converters;

/// <summary>
/// Upper-cases the first letter of a string, leaving the rest untouched.
/// </summary>
/// <remarks>
///   Text="{Binding Source={x:Static loc:Strings.WindowBorderLabel},
///                  Converter={x:Static conv:FirstLetterUpperConverter.Instance}}"
///
/// Exists for one specific case: labels sourced from Windows' MSAA role-name
/// table (<c>oleaccrc.dll</c>). Those strings are what a screen reader speaks
/// mid-sentence — "obramowanie", "okno dialogowe", "pasek narzędzi" — so they
/// are deliberately lower-cased at the resource level. Reusing the system's
/// noun buys a label that follows the Windows display language for free in
/// every locale, including the ones with no JSON bundle, but it needs sentence
/// case before it can sit in a settings grid next to "Motyw" and "Kolor".
///
/// Culture: casing follows <see cref="CultureInfo.CurrentUICulture"/>, NOT the
/// <c>culture</c> argument Avalonia passes (which is the *formatting* culture,
/// <see cref="CultureInfo.CurrentCulture"/>). The string we are casing came out
/// of a MUI resource picked by the Windows DISPLAY language, so the display
/// language is what decides the casing rule. The two genuinely differ — a
/// Turkish display language with en-US regional format is the case that bites:
/// only under tr does "i" upper-case to "İ" rather than "I".
///
/// A no-op when the value is not a string, is empty, or already starts with an
/// upper-case character — so the JSON fallback ("Window border", "Obramowanie
/// okna") passes through unchanged when the shell resource misses.
/// </remarks>
public sealed class FirstLetterUpperConverter : IValueConverter
{
    public static FirstLetterUpperConverter Instance { get; } = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string s || s.Length == 0) return value;

        // Take the first CHARACTER, not the first char: a non-BMP letter is a
        // surrogate pair and slicing it in half corrupts the string.
        var head = char.IsHighSurrogate(s[0]) && s.Length > 1 && char.IsLowSurrogate(s[1]) ? 2 : 1;
        var upper = CultureInfo.CurrentUICulture.TextInfo.ToUpper(s[..head]);
        return upper == s[..head] ? s : string.Concat(upper, s.AsSpan(head));
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
