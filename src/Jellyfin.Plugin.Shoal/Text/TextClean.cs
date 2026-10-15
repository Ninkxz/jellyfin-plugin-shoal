using System;
using System.Globalization;
using System.Text;

namespace Jellyfin.Plugin.Shoal.Text;

/// <summary>
/// Cleans free text that is displayed or logged: no control characters, no characters that reorder or hide text, bounded length,
/// cut on a character boundary. It is not an HTML escaper; every consumer renders text as text.
/// </summary>
internal static class TextClean
{
    /// <summary>
    /// Removes control, line and paragraph separators, unpaired surrogates, bidirectional controls and similar characters,
    /// trims the result and cuts it to <paramref name="maxChars"/> UTF-16 units without splitting a character.
    /// </summary>
    /// <param name="value">The text, or null.</param>
    /// <param name="maxChars">The most UTF-16 units to keep.</param>
    /// <returns>The cleaned text, never null.</returns>
    public static string Display(string? value, int maxChars)
    {
        if (string.IsNullOrEmpty(value) || maxChars <= 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder(Math.Min(value.Length, maxChars));
        foreach (var rune in value.EnumerateRunes())
        {
            if (rune == Rune.ReplacementChar || IsHidden(rune))
            {
                continue;
            }

            if (builder.Length + rune.Utf16SequenceLength > maxChars)
            {
                break;
            }

            builder.Append(rune.ToString());
        }

        return builder.ToString().Trim();
    }

    private static bool IsHidden(Rune rune)
    {
        var value = rune.Value;
        if (value is 0x200E or 0x200F or (>= 0x202A and <= 0x202E) or (>= 0x2066 and <= 0x2069) or 0xFEFF or (>= 0xFFF9 and <= 0xFFFB) or 0x2028 or 0x2029)
        {
            return true;
        }

        return Rune.GetUnicodeCategory(rune) is UnicodeCategory.Control or UnicodeCategory.Surrogate or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator;
    }
}
