using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Jellyfin.Plugin.Shoal.Sync;

/// <summary>
/// Builds the folder (and file) name of a stub from sanitized public metadata and keeps every stub path inside its library (DESIGN 5.5,
/// "File and folder names"). The same name serves the folder, the <c>.strm</c> and the <c>.nfo</c>. Nothing in a name identifies the Peer.
/// </summary>
public static class PathSanitizer
{
    /// <summary>The most UTF-8 bytes of the title part of a name.</summary>
    public const int MaxTitleBytes = 120;

    private const int MinYear = 1870;
    private const int MaxYear = 2100;
    private const int StubKeyPrefix = 8;
    private const string Untitled = "Untitled";

    private static readonly char[] InvalidChars = ['/', '\\', ':', '*', '?', '"', '<', '>', '|'];

    private static readonly HashSet<string> Reserved = new(StringComparer.Ordinal)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    private static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>
    /// Builds the name of a movie folder: the title part (steps 1 to 4) plus ` (year)` and one id tag (step 5), all from canonical values.
    /// </summary>
    /// <param name="title">The sanitized or raw title; it is cleaned here either way.</param>
    /// <param name="year">The production year, used only when valid.</param>
    /// <param name="ids">The canonical provider ids.</param>
    /// <param name="stubKey">The random stub key (at least 8 letters and digits).</param>
    /// <returns>A single path component, never empty.</returns>
    public static string FolderName(string? title, int? year, CanonicalIds ids, string stubKey)
    {
        var shortKey = ShortKey(stubKey);
        var name = new StringBuilder(TitlePart(title));
        if (year is >= MinYear and <= MaxYear)
        {
            name.Append(" (").Append(year.Value.ToString(CultureInfo.InvariantCulture)).Append(')');
        }

        if (ids.Tmdb is { } tmdb)
        {
            name.Append(" [tmdbid-").Append(tmdb.ToString(CultureInfo.InvariantCulture)).Append(']');
        }
        else if (ids.Imdb is { } imdb)
        {
            name.Append(" [imdbid-").Append(imdb).Append(']');
        }
        else
        {
            name.Append(" [shoal-").Append(shortKey).Append(']');
        }

        return name.ToString();
    }

    /// <summary>
    /// The key two names collide on: normalized (NFKC) and case-folded, so <c>É</c> and <c>E + combining acute</c>, or <c>ABC</c> and a
    /// full-width <c>ＡＢＣ</c>, are the same folder on a file system that folds either.
    /// </summary>
    /// <param name="name">A folder name.</param>
    /// <returns>The comparison key.</returns>
    public static string CollisionKey(string name) => PeerText.NormalizeNfkc(name, name.Length).ToUpperInvariant().ToLowerInvariant();

    /// <summary>
    /// Settles a name against the names already used in the library. A collision (case- and normalization-insensitive) gets
    /// <c> [shoal-&lt;stubKey[0..8]&gt;]</c>; if even that is taken, the whole stub key.
    /// </summary>
    /// <param name="name">The name from <see cref="FolderName"/>.</param>
    /// <param name="stubKey">The stub key.</param>
    /// <param name="used">The collision keys of the names in the library; the chosen name is added.</param>
    /// <returns>A name that is unique in the library.</returns>
    public static string Unique(string name, string stubKey, ISet<string> used)
    {
        ArgumentNullException.ThrowIfNull(used);
        if (used.Add(CollisionKey(name)))
        {
            return name;
        }

        var tag = " [shoal-" + ShortKey(stubKey) + "]";
        var withTag = name.EndsWith(tag, StringComparison.Ordinal) ? name : name + tag;
        if (used.Add(CollisionKey(withTag)))
        {
            return withTag;
        }

        var full = name + " [shoal-" + stubKey + "]";
        used.Add(CollisionKey(full));
        return full;
    }

    /// <summary>
    /// Step 6: the full path of <paramref name="name"/> under <paramref name="libraryRoot"/>, accepted only when it stays inside it.
    /// </summary>
    /// <param name="libraryRoot">The library root, for example <c>&lt;StubRoot&gt;/&lt;peerKey&gt;/&lt;Subfolder&gt;</c>.</param>
    /// <param name="name">A single path component.</param>
    /// <param name="fullPath">The canonical path.</param>
    /// <returns>True when the path starts with the root plus a separator.</returns>
    public static bool TryConfine(string libraryRoot, string name, out string fullPath)
    {
        fullPath = string.Empty;
        if (string.IsNullOrEmpty(name) || name is "." or ".." || name.IndexOfAny(['/', '\\']) >= 0)
        {
            return false;
        }

        var root = Path.GetFullPath(libraryRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var full = Path.GetFullPath(Path.Combine(root, name));
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, PathComparison))
        {
            return false;
        }

        fullPath = full;
        return true;
    }

    /// <summary>
    /// Step 7: whether any component of <paramref name="path"/> below <paramref name="stubRoot"/> is a symbolic link or reparse point.
    /// Components that do not exist yet are fine.
    /// </summary>
    /// <param name="stubRoot">The validated StubRoot.</param>
    /// <param name="path">A path under it.</param>
    /// <returns>True when a link was found, or when the path is not under the root (treated as unsafe).</returns>
    public static bool HasLinkBelow(string stubRoot, string path)
    {
        var root = Path.GetFullPath(stubRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var full = Path.GetFullPath(path);
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, PathComparison))
        {
            return true;
        }

        var current = root;
        foreach (var part in full[(root.Length + 1)..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (!info.Exists)
            {
                return false;
            }

            if (info.LinkTarget is not null || (info.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                return true;
            }
        }

        return false;
    }

    private static string ShortKey(string stubKey)
    {
        ArgumentNullException.ThrowIfNull(stubKey);
        if (stubKey.Length < StubKeyPrefix)
        {
            throw new ArgumentException("A stub key has at least 8 characters.", nameof(stubKey));
        }

        foreach (var c in stubKey)
        {
            if (c is not ((>= '0' and <= '9') or (>= 'a' and <= 'z') or (>= 'A' and <= 'Z')))
            {
                throw new ArgumentException("A stub key is letters and digits only.", nameof(stubKey));
            }
        }

        return stubKey[..StubKeyPrefix];
    }

    /// <summary>Steps 1 to 4: the title as a safe file name part.</summary>
    private static string TitlePart(string? title)
    {
        // 1. NFKC, then replace / \ : * ? " < > | and control characters with a space, then collapse whitespace.
        var normalized = PeerText.NormalizeNfkc(title, MaxTitleBytes);
        var builder = new StringBuilder(Math.Min(normalized.Length, 512));
        var lastSpace = true;
        foreach (var rune in normalized.EnumerateRunes())
        {
            var isSeparator = rune.Value < 0x10000 && Array.IndexOf(InvalidChars, (char)rune.Value) >= 0;
            if (!isSeparator && !Rune.IsWhiteSpace(rune) && !Rune.IsControl(rune) && PeerText.IsDropped(rune))
            {
                continue; // bidi marks and the like vanish; they are not word separators
            }

            if (isSeparator || Rune.IsWhiteSpace(rune) || Rune.IsControl(rune))
            {
                if (!lastSpace)
                {
                    builder.Append(' ');
                    lastSpace = true;
                }

                continue;
            }

            builder.Append(rune.ToString());
            lastSpace = false;
        }

        // 2. Trim dots and spaces at both ends; remove "..".
        var text = TrimDotsAndSpaces(builder.ToString());
        while (text.Contains("..", StringComparison.Ordinal))
        {
            text = TrimDotsAndSpaces(text.Replace("..", string.Empty, StringComparison.Ordinal));
        }

        // 3. Truncate to 120 UTF-8 bytes on a rune boundary, then trim again.
        text = TrimDotsAndSpaces(CutUtf8(text, MaxTitleBytes));

        // 4. Empty, or a Windows reserved name with any extension, becomes Untitled.
        return text.Length == 0 || IsReserved(text) ? Untitled : text;
    }

    private static string TrimDotsAndSpaces(string text) => text.Trim(' ', '.');

    private static string CutUtf8(string text, int maxBytes)
    {
        if (Encoding.UTF8.GetByteCount(text) <= maxBytes)
        {
            return text;
        }

        var bytes = 0;
        var end = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            var size = rune.Utf8SequenceLength;
            if (bytes + size > maxBytes)
            {
                break;
            }

            bytes += size;
            end += rune.Utf16SequenceLength;
        }

        return text[..end];
    }

    private static bool IsReserved(string text)
    {
        var dot = text.IndexOf('.', StringComparison.Ordinal);
        var stem = (dot >= 0 ? text[..dot] : text).TrimEnd(' ');
        return Reserved.Contains(stem.ToUpperInvariant());
    }
}
