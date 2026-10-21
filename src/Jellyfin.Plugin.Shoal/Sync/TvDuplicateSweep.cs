using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.Shoal.Sync;

/// <summary>
/// 2.9.3: finds the extra copies of a series folder that 2.9.0 to 2.9.2 left behind (a new <c>[shoal-xxxxxxxx]</c> copy every sync, see
/// <c>TvSyncEngine.CreateSeriesAsync</c>). A folder is a leftover copy only when all of these hold: its name is the series' own folder name with an id tag
/// (<c>[tvdbid-N]</c>, <c>[tmdbid-N]</c> or <c>[imdbid-...]</c>), optionally followed by one <c>[shoal-key]</c> tag; no catalog records it; nothing in it is a link;
/// every file in it is a Shoal stub file (NFO, image, sentinel <c>.strm</c>); it holds at least one sentinel; and no catalog knows any of its sentinel keys.
/// Anything else (a user's file, another server's copy, a folder a catalog records) is never touched. The names come from the catalog and the disk, never
/// from peer text.
/// </summary>
internal static partial class TvDuplicateSweep
{
    /// <summary>The most files a folder may hold to be checked at all.</summary>
    public const int MaxFiles = 20_000;

    private const int MaxDepth = 4;

    private static readonly string[] ImageExtensions = [".jpg", ".jpeg", ".png", ".webp"];

    /// <summary>
    /// The name every copy of a series folder starts with: the folder without its trailing <c>[shoal-key]</c> tag, when it ends in a provider id tag.
    /// </summary>
    /// <param name="folderRel">The series folder as a catalog records it.</param>
    /// <returns>The base name, or null when the folder has no provider id tag (a title-only name could be another series: never swept).</returns>
    public static string? BaseOf(string? folderRel)
    {
        if (string.IsNullOrEmpty(folderRel) || folderRel.Contains('/', StringComparison.Ordinal))
        {
            return null;
        }

        var match = ShoalTag().Match(folderRel);
        var name = match.Success ? folderRel[..match.Index] : folderRel;
        return IdTag().IsMatch(name) ? name : null;
    }

    /// <summary>
    /// Whether a folder name is a copy of a series folder: exactly the base, or the base with one <c>[shoal-key]</c> tag.
    /// </summary>
    /// <param name="name">The folder name on disk.</param>
    /// <param name="baseName">The base from <see cref="BaseOf"/>.</param>
    /// <returns>True for a copy name.</returns>
    public static bool IsCopyName(string name, string baseName)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(baseName);
        if (string.Equals(name, baseName, StringComparison.Ordinal))
        {
            return true;
        }

        return name.StartsWith(baseName, StringComparison.Ordinal) && ShoalTag().Match(name) is { Success: true } tag && tag.Index == baseName.Length;
    }

    /// <summary>
    /// The folder names in a library folder that are copies of one of the given live series folders (those folders themselves left out).
    /// </summary>
    /// <param name="libraryFolder">The library folder.</param>
    /// <param name="liveFolders">The live series folders of the mapping.</param>
    /// <returns>The candidate names.</returns>
    public static List<string> Candidates(string libraryFolder, IEnumerable<string> liveFolders)
    {
        var live = liveFolders.ToHashSet(StringComparer.Ordinal);
        var bases = live.Select(BaseOf).Where(static b => b is not null).Select(static b => b!).ToHashSet(StringComparer.Ordinal);
        if (bases.Count == 0 || !Directory.Exists(libraryFolder))
        {
            return [];
        }

        var found = new List<string>();
        foreach (var path in Directory.EnumerateDirectories(libraryFolder))
        {
            var name = Path.GetFileName(path);
            if (live.Contains(name))
            {
                continue;
            }

            var match = ShoalTag().Match(name);
            var head = match.Success ? name[..match.Index] : name;
            if (bases.Contains(head) && IsCopyName(name, head))
            {
                found.Add(name);
            }
        }

        return found;
    }

    /// <summary>
    /// Whether a folder holds only Shoal stub files whose keys no catalog knows, and so may be deleted.
    /// </summary>
    /// <param name="folder">The confined full path of the folder.</param>
    /// <param name="knownKeys">Every stub key any catalog or title group records.</param>
    /// <param name="bytes">The bytes in the folder.</param>
    /// <returns>True for a leftover copy.</returns>
    public static bool IsLeftover(string folder, IReadOnlySet<string> knownKeys, out long bytes)
    {
        ArgumentNullException.ThrowIfNull(knownKeys);
        bytes = 0;
        var root = new DirectoryInfo(folder);
        if (!root.Exists || root.LinkTarget is not null || (root.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            return false;
        }

        var sentinels = 0;
        var files = 0;
        var pending = new Stack<(DirectoryInfo Dir, int Depth)>();
        pending.Push((root, 0));
        while (pending.Count > 0)
        {
            var (dir, depth) = pending.Pop();
            foreach (var child in dir.EnumerateFileSystemInfos())
            {
                if (child.LinkTarget is not null || (child.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    return false;
                }

                if (child is DirectoryInfo sub)
                {
                    if (depth + 1 >= MaxDepth)
                    {
                        return false;
                    }

                    pending.Push((sub, depth + 1));
                    continue;
                }

                var file = (FileInfo)child;
                if (++files > MaxFiles)
                {
                    return false;
                }

                var extension = file.Extension.ToLowerInvariant();
                if (extension == ".strm")
                {
                    if (file.Length > 256 || !StubSentinel.TryParse(File.ReadAllText(file.FullName), out var key) || knownKeys.Contains(key))
                    {
                        return false; // not a sentinel, or a stub something still records
                    }

                    sentinels++;
                }
                else if (extension != ".nfo" && !ImageExtensions.Contains(extension, StringComparer.Ordinal))
                {
                    return false; // not a file Shoal writes
                }

                bytes += file.Length;
            }
        }

        return sentinels > 0;
    }

    [GeneratedRegex(@" \[shoal-[0-9a-z]{8,32}\]$", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex ShoalTag();

    [GeneratedRegex(@" \[(tvdbid|tmdbid)-[0-9]{1,12}\]$| \[imdbid-tt[0-9]{1,12}\]$", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex IdTag();
}
