using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.Shoal.State;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Shoal.Sync;

/// <summary>
/// File work in a shared library folder <c>&lt;StubRoot&gt;/libs/&lt;libKey&gt;</c> (PLAN v2.9 K44). Several connections write into one such folder, so nothing here ever
/// deletes or moves a folder by itself: only the title folders a connection's catalog recorded, each confined below the library folder and never through a link
/// (Warden: deletion confined to recorded paths).
/// </summary>
public static class LibraryTree
{
    /// <summary>
    /// The title folder a catalog entry recorded directly below its library folder: a movie's stub folder, or a series folder. Seasons and episodes live inside their
    /// series folder and record nothing of their own here.
    /// </summary>
    /// <param name="entry">The entry.</param>
    /// <returns>The folder name, or null.</returns>
    public static string? RecordedTop(CatalogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.State is not (EntryState.Active or EntryState.HeldRemoval))
        {
            return null;
        }

        return entry.Kind switch
        {
            EntryKind.Movie => entry.StubRelPath?.Split('/')[0],
            EntryKind.Series => entry.FolderRel is { Length: > 0 } folder && !folder.Contains('/', StringComparison.Ordinal) ? folder : null,
            _ => null,
        };
    }

    /// <summary>
    /// The full path of a recorded title folder, or null when the name is not one plain segment below the library folder, or a link is on the way.
    /// </summary>
    /// <param name="stubRoot">The stub root.</param>
    /// <param name="libraryFolder">The library folder.</param>
    /// <param name="top">The recorded folder name.</param>
    /// <returns>The full path, or null.</returns>
    public static string? Confine(string stubRoot, string libraryFolder, string top)
    {
        if (!PathSanitizer.TryConfine(libraryFolder, top, out var full))
        {
            return null;
        }

        try
        {
            return PathSanitizer.HasLinkBelow(stubRoot, full) ? null : full;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// Deletes the title folders that the given entries recorded, and nothing else. The library folder itself stays.
    /// </summary>
    /// <param name="stubRoot">The stub root.</param>
    /// <param name="libraryFolder">The library folder.</param>
    /// <param name="entries">The entries whose folders go.</param>
    /// <param name="logger">The logger.</param>
    /// <returns>How many folders were deleted.</returns>
    public static int DeleteRecorded(string stubRoot, string libraryFolder, IEnumerable<CatalogEntry> entries, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(logger);
        var deleted = 0;
        foreach (var top in entries.Select(RecordedTop).Where(static t => t is not null).Distinct(StringComparer.Ordinal))
        {
            if (Confine(stubRoot, libraryFolder, top!) is not { } full || !Directory.Exists(full))
            {
                continue;
            }

            try
            {
                Directory.Delete(full, recursive: true);
                deleted++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning("A title folder in a shared library could not be deleted ({Error})", ex.GetType().Name);
            }
        }

        return deleted;
    }

    /// <summary>
    /// Deletes a library folder when it is empty and it is the folder the library recorded (<c>libs/&lt;libKey&gt;</c> of a library of v2.9 or later). A folder that
    /// still holds anything is left alone.
    /// </summary>
    /// <param name="stubRoot">The stub root.</param>
    /// <param name="library">The library.</param>
    /// <param name="logger">The logger.</param>
    /// <returns>True when the folder was deleted (or did not exist).</returns>
    public static bool DeleteIfEmpty(string stubRoot, ShoalLibrary library, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(logger);
        if (library.IsLegacy || LibraryFolders.PathOf(stubRoot, library) is not { } folder)
        {
            return false;
        }

        try
        {
            if (!Directory.Exists(folder))
            {
                return true;
            }

            if (PathSanitizer.HasLinkBelow(stubRoot, folder) || Directory.EnumerateFileSystemEntries(folder).Any())
            {
                return false;
            }

            Directory.Delete(folder, recursive: false);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("The folder of a removed library could not be deleted ({Error})", ex.GetType().Name);
            return false;
        }
    }
}
