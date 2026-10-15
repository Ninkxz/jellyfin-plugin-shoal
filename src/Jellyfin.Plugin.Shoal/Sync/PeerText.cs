using System;
using System.Globalization;
using System.Text;

namespace Jellyfin.Plugin.Shoal.Sync;

/// <summary>
/// The common text rules for every peer string (DESIGN 5.5): NFKC; strip C0/C1 controls, characters that XML 1.0 cannot carry, and bidi
/// controls; remove <c>&lt;</c> and <c>&gt;</c>; trim; truncate on a rune boundary.
/// </summary>
internal static class PeerText
{
    /// <summary>
    /// Cleans one peer string. The input is cut to a window first, so a 1 MB value costs the same as a short one; the window is generous
    /// enough that removing characters cannot shorten a normal value below its cap.
    /// </summary>
    /// <param name="value">The raw peer text, or null.</param>
    /// <param name="maxUnits">The most UTF-16 units to keep; a surrogate pair is never split.</param>
    /// <param name="keepNewlines">True to keep line feeds (an overview has paragraphs); tabs and carriage returns always become a space or vanish.</param>
    /// <returns>The cleaned text, never null; empty when nothing usable is left or the text cannot be normalized.</returns>
    public static string Clean(string? value, int maxUnits, bool keepNewlines = false)
    {
        var normalized = NormalizeNfkc(value, maxUnits);
        if (normalized.Length == 0 || maxUnits <= 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder(Math.Min(normalized.Length, maxUnits));
        foreach (var rune in normalized.EnumerateRunes())
        {
            var v = rune.Value;
            if (v is '<' or '>')
            {
                continue;
            }

            if (v == '\t' || (v == '\n' && !keepNewlines))
            {
                v = ' ';
            }
            else if (v != '\n' && IsDropped(rune))
            {
                continue; // a kept line feed is the only control that survives
            }

            var piece = v == rune.Value ? rune : new Rune(v);
            if (builder.Length + piece.Utf16SequenceLength > maxUnits)
            {
                break;
            }

            builder.Append(piece.ToString());
        }

        return builder.ToString().Trim();
    }

    /// <summary>
    /// Drops unpaired surrogates and applies NFKC to the leading window of a value. Returns empty text on any failure.
    /// </summary>
    /// <param name="value">The raw text.</param>
    /// <param name="maxUnits">The cap the caller will apply afterwards; the window is four times this plus a margin.</param>
    /// <returns>The normalized window; empty for null, empty or unnormalizable input.</returns>
    public static string NormalizeNfkc(string? value, int maxUnits)
    {
        if (string.IsNullOrEmpty(value) || maxUnits <= 0)
        {
            return string.Empty;
        }

        var window = (int)Math.Min(((long)maxUnits * 4) + 64, int.MaxValue);
        var text = value.Length > window ? value[..window] : value;
        var paired = DropNoncharacters(DropUnpairedSurrogates(text));
        try
        {
            return paired.Normalize(NormalizationForm.FormKC);
        }
        catch (ArgumentException)
        {
            return string.Empty; // a code point the normalizer refuses: treat the whole field as unusable
        }
    }

    /// <summary>
    /// Whether a code point is removed from every peer string: controls, bidi controls, line and paragraph separators, byte order marks,
    /// interlinear annotation marks, the replacement character, and the noncharacters XML 1.0 and the normalizer refuse.
    /// </summary>
    /// <param name="rune">The code point.</param>
    /// <returns>True when it is dropped.</returns>
    public static bool IsDropped(Rune rune)
    {
        var v = rune.Value;
        if (v is 0x200E or 0x200F or 0x061C or (>= 0x202A and <= 0x202E) or (>= 0x2066 and <= 0x2069))
        {
            return true; // bidi controls
        }

        if (v is 0x2028 or 0x2029 or 0xFEFF or 0xFFFC or 0xFFFD or (>= 0xFFF9 and <= 0xFFFB) or (>= 0xFDD0 and <= 0xFDEF))
        {
            return true;
        }

        if ((v & 0xFFFE) == 0xFFFE)
        {
            return true; // U+FFFE and U+FFFF of every plane
        }

        return Rune.GetUnicodeCategory(rune) is UnicodeCategory.Control or UnicodeCategory.Surrogate or UnicodeCategory.LineSeparator
            or UnicodeCategory.ParagraphSeparator;
    }

    private static bool IsNoncharacter(int v) => v is >= 0xFDD0 and <= 0xFDEF || (v & 0xFFFE) == 0xFFFE;

    /// <summary>The normalizer refuses noncharacters; dropping them first keeps one stray code point from blanking a whole field.</summary>
    private static string DropNoncharacters(string text)
    {
        var any = false;
        foreach (var rune in text.EnumerateRunes())
        {
            if (IsNoncharacter(rune.Value))
            {
                any = true;
                break;
            }
        }

        if (!any)
        {
            return text;
        }

        var builder = new StringBuilder(text.Length);
        foreach (var rune in text.EnumerateRunes())
        {
            if (!IsNoncharacter(rune.Value))
            {
                builder.Append(rune.ToString());
            }
        }

        return builder.ToString();
    }

    private static string DropUnpairedSurrogates(string text)
    {
        var clean = true;
        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsSurrogate(text[i]))
            {
                clean = false;
                break;
            }
        }

        if (clean)
        {
            return text;
        }

        var builder = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                builder.Append(c).Append(text[i + 1]);
                i++;
            }
            else if (!char.IsSurrogate(c))
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }
}
