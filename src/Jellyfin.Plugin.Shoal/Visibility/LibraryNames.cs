using System;
using System.IO;
using System.Linq;
using System.Text;

namespace Jellyfin.Plugin.Shoal.Visibility;

/// <summary>
/// Library names Shoal gives Jellyfin (Warden 10 M3). Jellyfin keeps a library as a folder named after it below the user views path, and a rename moves that
/// folder, so a name is a path segment: it must be one plain file name, never a separator, a drive, "..", or a rooted path.
/// </summary>
public static class LibraryNames
{
    private static readonly char[] Unsafe = ['/', '\\', ':', '*', '?', '"', '<', '>', '|'];

    /// <summary>
    /// Whether a (cleaned) name is safe as one folder name on every platform.
    /// </summary>
    /// <param name="name">The name.</param>
    /// <returns>True when it is one plain segment.</returns>
    public static bool IsSafe(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 200 || name != name.Trim() || name.EndsWith('.') || name.Contains("..", StringComparison.Ordinal))
        {
            return false;
        }

        if (IsReserved(name))
        {
            return false; // Warden 11 I-b: CON, NUL, COM1 and the like are not usable folder names on Windows
        }

        return !name.Any(static c => Array.IndexOf(Unsafe, c) >= 0 || char.IsControl(c)) && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && !Path.IsPathRooted(name);
    }

    /// <summary>
    /// Whether a name is one of Windows' reserved device names (with or without an extension).
    /// </summary>
    /// <param name="name">The name.</param>
    /// <returns>True for CON, PRN, AUX, NUL, COM0-9 and LPT0-9.</returns>
    public static bool IsReserved(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var stem = name.Split('.')[0].TrimEnd().ToUpperInvariant();
        return stem is "CON" or "PRN" or "AUX" or "NUL" || (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) && (char.IsAsciiDigit(stem[3]) || stem[3] is (char)0xB9 or (char)0xB2 or (char)0xB3));
    }

    /// <summary>
    /// A safe name built from text a friend chose (its server name): unsafe characters become spaces, runs of dots and spaces collapse, and an empty result
    /// becomes the fallback.
    /// </summary>
    /// <param name="text">The friend-chosen text (already cleaned for display).</param>
    /// <param name="fallback">The name to use when nothing usable is left.</param>
    /// <returns>A name for which <see cref="IsSafe"/> is true (when the fallback is safe).</returns>
    public static string FromFriend(string? text, string fallback)
    {
        var builder = new StringBuilder();
        foreach (var c in text ?? string.Empty)
        {
            var safe = Array.IndexOf(Unsafe, c) < 0 && !char.IsControl(c) && Array.IndexOf(Path.GetInvalidFileNameChars(), c) < 0;
            var next = safe ? c : ' ';
            if ((next == ' ' || next == '.') && builder.Length > 0 && builder[^1] == next)
            {
                continue;
            }

            builder.Append(next);
        }

        var name = builder.ToString().Trim(' ', '.');
        if (IsReserved(name))
        {
            name = "Friend " + name;
        }

        while (name.Contains("..", StringComparison.Ordinal))
        {
            name = name.Replace("..", ".", StringComparison.Ordinal);
        }

        return IsSafe(name) ? name : fallback;
    }

    /// <summary>
    /// The folder of a library below a root, refusing anything that would not be a direct child of it.
    /// </summary>
    /// <param name="root">The root (the user views path).</param>
    /// <param name="name">The library name.</param>
    /// <returns>The full path.</returns>
    /// <exception cref="InvalidOperationException">The name is not one safe segment directly below the root.</exception>
    public static string ChildOf(string root, string name)
    {
        if (!IsSafe(name))
        {
            throw new InvalidOperationException("The library name is not a plain folder name.");
        }

        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var target = Path.GetFullPath(Path.Combine(fullRoot, name));
        var parent = Path.GetDirectoryName(target);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (parent is null || !string.Equals(parent.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), fullRoot, comparison))
        {
            throw new InvalidOperationException("The library folder would not be below the user views path.");
        }

        return target;
    }
}
