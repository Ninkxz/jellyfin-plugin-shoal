using System;
using System.IO;
using System.Linq;
using System.Text;
using Jellyfin.Plugin.Shoal.Security;
using Jellyfin.Plugin.Shoal.Storage;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Shoal.Sync;

/// <summary>
/// Writes and removes the files of a TV library (DESIGN v2.7.1, v2.7.7). Every folder is confined to the library folder one component at a time and no component may be a
/// link. An episode is written NFO first and <c>.strm</c> last (temp file and rename), so a crash leaves no <c>.strm</c> without its NFO; a series folder with no <c>.strm</c>
/// is not an item. Removal deletes only paths the catalog recorded plus fixed names, and a folder only when it is empty.
/// </summary>
public sealed class TvStubWriter
{
    private static readonly string[] SeriesFiles = ["tvshow.nfo", "poster.jpg", "poster.png", "backdrop.jpg", "backdrop.png", "banner.jpg", "banner.png", "landscape.jpg", "landscape.png"];

    private static readonly string[] SeasonFiles = ["season.nfo", "poster.jpg", "poster.png"];

    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="TvStubWriter"/> class.
    /// </summary>
    /// <param name="logger">The logger.</param>
    public TvStubWriter(ILogger<TvStubWriter> logger)
        : this((ILogger)logger)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="TvStubWriter"/> class (tests: any logger).
    /// </summary>
    /// <param name="logger">The logger.</param>
    internal TvStubWriter(ILogger logger) => _logger = logger;

    /// <summary>
    /// The confined full path of a relative folder or file below the library folder, or null when any component is unsafe.
    /// </summary>
    /// <param name="target">Where to write.</param>
    /// <param name="relative">Components separated by '/'.</param>
    /// <returns>The path, or null.</returns>
    public static string? Confine(StubTarget target, string relative)
    {
        ArgumentNullException.ThrowIfNull(target);
        var current = target.LibraryFolder;
        foreach (var part in relative.Split('/'))
        {
            if (!PathSanitizer.TryConfine(current, part, out var next))
            {
                return null;
            }

            current = next;
        }

        return PathSanitizer.HasLinkBelow(target.StubRoot, current) ? null : current;
    }

    /// <summary>
    /// Creates a series folder with its <c>tvshow.nfo</c>.
    /// </summary>
    /// <param name="target">Where to write.</param>
    /// <param name="folderName">The series folder name.</param>
    /// <param name="nfo">The NFO XML.</param>
    /// <param name="quota">The peer's disk budget.</param>
    /// <returns>Written; Exists when something is on disk under the name; QuotaFull; Unsafe; Failed.</returns>
    public StubWriteResult WriteSeries(StubTarget target, string folderName, string nfo, DiskQuota quota)
    {
        ArgumentNullException.ThrowIfNull(quota);
        if (!CatalogStore.IsSafeFolder(folderName, 1))
        {
            return StubWriteResult.Unsafe;
        }

        if (Confine(target, folderName) is not { } folder)
        {
            return StubWriteResult.Unsafe;
        }

        if (Directory.Exists(folder))
        {
            return StubWriteResult.Exists;
        }

        return WriteFolder(target, folder, "tvshow.nfo", nfo, quota, createFolder: true, folderName);
    }

    /// <summary>
    /// Creates a season folder with its <c>season.nfo</c> inside an existing series folder.
    /// </summary>
    /// <param name="target">Where to write.</param>
    /// <param name="seriesFolder">The series folder name.</param>
    /// <param name="seasonFolder">The season folder name (<c>Season 01</c>).</param>
    /// <param name="nfo">The NFO XML.</param>
    /// <param name="quota">The peer's disk budget.</param>
    /// <returns>The outcome.</returns>
    public StubWriteResult WriteSeason(StubTarget target, string seriesFolder, string seasonFolder, string nfo, DiskQuota quota)
    {
        ArgumentNullException.ThrowIfNull(quota);
        if (!CatalogStore.IsSafeFolder(seriesFolder, 1) || !CatalogStore.IsSafeFolder(seasonFolder, 1))
        {
            return StubWriteResult.Unsafe;
        }

        var relative = seriesFolder + "/" + seasonFolder;
        if (Confine(target, relative) is not { } folder)
        {
            return StubWriteResult.Unsafe;
        }

        return WriteFolder(target, folder, "season.nfo", nfo, quota, createFolder: true, relative);
    }

    /// <summary>
    /// Writes an episode: the NFO, then the <c>.strm</c> last. The season folder must exist.
    /// </summary>
    /// <param name="target">Where to write.</param>
    /// <param name="seriesFolder">The series folder name.</param>
    /// <param name="seasonFolder">The season folder name.</param>
    /// <param name="stem">The file stem from integers (<c>S01E02-E03</c>).</param>
    /// <param name="nfo">The NFO XML.</param>
    /// <param name="stubKey">The stub key for the sentinel.</param>
    /// <param name="quota">The peer's disk budget.</param>
    /// <returns>Written with the stored relative path, Exists when the file is already there, or another outcome.</returns>
    public StubWriteResult WriteEpisode(StubTarget target, string seriesFolder, string seasonFolder, string stem, string nfo, string stubKey, DiskQuota quota)
    {
        ArgumentNullException.ThrowIfNull(quota);
        if (!CatalogStore.IsSafeFolder(seriesFolder, 1) || !CatalogStore.IsSafeFolder(seasonFolder, 1))
        {
            return StubWriteResult.Unsafe;
        }

        if (Confine(target, seriesFolder + "/" + seasonFolder) is not { } folder || !IsPlainStem(stem))
        {
            return StubWriteResult.Unsafe;
        }

        var strmPath = Path.Combine(folder, stem + ".strm");
        if (File.Exists(strmPath))
        {
            return StubWriteResult.Exists;
        }

        var nfoBytes = Encoding.UTF8.GetBytes(nfo);
        var strm = Encoding.UTF8.GetBytes(StubSentinel.Content(stubKey));
        if (!quota.TryReserve(nfoBytes.Length + strm.Length))
        {
            return StubWriteResult.QuotaFull;
        }

        try
        {
            Directory.CreateDirectory(folder);
            AtomicFile.Write(Path.Combine(folder, stem + ".nfo"), nfoBytes);
            AtomicFile.Write(strmPath, strm);
            return new StubWriteResult(StubWriteOutcome.Written, true, 0, nfoBytes.Length + strm.Length, false, seriesFolder + "/" + seasonFolder + "/" + stem + ".strm");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("An episode stub could not be written ({Error})", ex.GetType().Name);
            quota.Add(-(nfoBytes.Length + strm.Length));
            TryDelete(Path.Combine(folder, stem + ".nfo"));
            return StubWriteResult.Failed;
        }
    }

    /// <summary>
    /// Rewrites the NFO of a series, a season or an episode in place (the folder and the <c>.strm</c> stay).
    /// </summary>
    /// <param name="target">Where the item is.</param>
    /// <param name="relativeNfo">The NFO path relative to the library folder.</param>
    /// <param name="nfo">The new XML.</param>
    /// <param name="quota">The peer's disk budget.</param>
    /// <returns>The outcome; Written when the content changed.</returns>
    public StubWriteResult RewriteNfo(StubTarget target, string relativeNfo, string nfo, DiskQuota quota)
    {
        ArgumentNullException.ThrowIfNull(quota);
        if (!relativeNfo.EndsWith(".nfo", StringComparison.Ordinal) || Confine(target, relativeNfo) is not { } path)
        {
            return StubWriteResult.Unsafe;
        }

        var bytes = Encoding.UTF8.GetBytes(nfo);
        try
        {
            var old = File.Exists(path) ? new FileInfo(path).Length : 0;
            if (File.Exists(path) && File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes))
            {
                return new StubWriteResult(StubWriteOutcome.Written);
            }

            if (!quota.TryReserve(bytes.Length, old))
            {
                return StubWriteResult.QuotaFull;
            }

            AtomicFile.Write(path, bytes);
            return new StubWriteResult(StubWriteOutcome.Written, true, 0, bytes.Length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("An NFO could not be rewritten ({Error})", ex.GetType().Name);
            return StubWriteResult.Failed;
        }
    }

    /// <summary>
    /// Removes an episode: its <c>.strm</c>, <c>.nfo</c> and thumbnail, nothing else. The season and series folders stay (see <see cref="RemoveContainerIfEmpty"/>).
    /// </summary>
    /// <param name="target">Where the episode is.</param>
    /// <param name="stubRelPath">The stored <c>series/Season NN/stem.strm</c> path.</param>
    /// <param name="quota">The peer's disk budget; the bytes removed are given back.</param>
    /// <returns>True when the episode is gone (or never existed); false when a file could not be deleted or the path was not safe.</returns>
    public bool RemoveEpisode(StubTarget target, string stubRelPath, DiskQuota quota)
    {
        ArgumentNullException.ThrowIfNull(quota);
        if (!CatalogStore.IsSafeEpisodePath(stubRelPath) || Confine(target, stubRelPath) is not { } strm)
        {
            _logger.LogWarning("An episode path is not safe and nothing was deleted");
            return false;
        }

        var stem = Path.GetFileNameWithoutExtension(strm);
        var folder = Path.GetDirectoryName(strm)!;
        try
        {
            var freed = 0L;
            foreach (var name in new[] { stem + ".strm", stem + ".nfo", stem + "-thumb.jpg", stem + "-thumb.png" })
            {
                var path = Path.Combine(folder, name);
                var info = new FileInfo(path);
                if (!info.Exists)
                {
                    continue;
                }

                if (info.LinkTarget is not null || (info.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    return false;
                }

                freed += info.Length;
                File.Delete(path);
            }

            quota.Add(-freed);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("An episode could not be removed ({Error})", ex.GetType().Name);
            return false;
        }
    }

    /// <summary>
    /// Removes a series or season folder when no episode <c>.strm</c> is left in it: the fixed files (NFO and posters), then the folder if it is empty. Anything else in the folder stays.
    /// </summary>
    /// <param name="target">Where the folder is.</param>
    /// <param name="folderRel">The stored <c>Show</c> or <c>Show/Season NN</c> path.</param>
    /// <param name="isSeries">True for a series folder.</param>
    /// <param name="quota">The peer's disk budget.</param>
    /// <returns>True when the folder is gone or was never there; false when episodes remain, or it could not be removed.</returns>
    public bool RemoveContainerIfEmpty(StubTarget target, string folderRel, bool isSeries, DiskQuota quota)
    {
        ArgumentNullException.ThrowIfNull(quota);
        if (!CatalogStore.IsSafeFolder(folderRel, isSeries ? 1 : 2) || Confine(target, folderRel) is not { } folder)
        {
            return false;
        }

        try
        {
            if (!Directory.Exists(folder))
            {
                return true;
            }

            if (Directory.EnumerateFiles(folder, "*.strm", SearchOption.AllDirectories).Any())
            {
                return false; // an episode is still there
            }

            var freed = 0L;
            foreach (var name in isSeries ? SeriesFiles : SeasonFiles)
            {
                var info = new FileInfo(Path.Combine(folder, name));
                if (!info.Exists)
                {
                    continue;
                }

                if (info.LinkTarget is not null || (info.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    return false;
                }

                freed += info.Length;
                info.Delete();
            }

            // the episode NFOs and thumbnails of removed episodes are gone already; the season folders of a series are removed by their own entries
            quota.Add(-freed);
            if (!Directory.EnumerateFileSystemEntries(folder).Any())
            {
                Directory.Delete(folder);
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("A TV folder could not be removed ({Error})", ex.GetType().Name);
            return false;
        }
    }

    private static bool IsPlainStem(string stem) =>
        stem.Length is > 0 and <= 40 && stem.All(static c => c is (>= '0' and <= '9') or 'S' or 'E' or '-');

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // best effort: an NFO without a .strm is not an item
        }
    }

    private StubWriteResult WriteFolder(StubTarget target, string folder, string fileName, string nfo, DiskQuota quota, bool createFolder, string relative)
    {
        _ = target;
        var bytes = Encoding.UTF8.GetBytes(nfo);
        if (!quota.TryReserve(bytes.Length))
        {
            return StubWriteResult.QuotaFull;
        }

        var created = false;
        try
        {
            if (createFolder && !Directory.Exists(folder))
            {
                Directory.CreateDirectory(folder);
                created = true;
            }

            AtomicFile.Write(Path.Combine(folder, fileName), bytes);
            return new StubWriteResult(StubWriteOutcome.Written, true, 0, bytes.Length, false, relative);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("A TV folder could not be written ({Error})", ex.GetType().Name);
            quota.Add(-bytes.Length);
            if (created)
            {
                try
                {
                    Directory.Delete(folder, recursive: false);
                }
                catch (Exception inner) when (inner is IOException or UnauthorizedAccessException)
                {
                    // best effort
                }
            }

            return StubWriteResult.Failed;
        }
    }
}
