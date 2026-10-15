using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.Configuration;
using Jellyfin.Plugin.Shoal.Peers;
using Jellyfin.Plugin.Shoal.Security;
using Jellyfin.Plugin.Shoal.Storage;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Shoal.Sync;

/// <summary>
/// How writing a stub ended.
/// </summary>
public enum StubWriteOutcome
{
    /// <summary>The stub was written.</summary>
    Written,

    /// <summary>A folder with that name is already on disk; nothing was written.</summary>
    Exists,

    /// <summary>The path left the library or crossed a link; nothing was written.</summary>
    Unsafe,

    /// <summary>The disk quota has no room for the NFO and the <c>.strm</c>; nothing was written (W6 L-8).</summary>
    QuotaFull,

    /// <summary>A file could not be written; what was written is cleaned up.</summary>
    Failed,
}

/// <summary>
/// The text of the <c>.strm</c> file: a URL that never resolves (RFC 6761 <c>.invalid</c>) with the random stub key as its only content
/// (DESIGN 3.4). Shoal never reads the file at runtime; it only keeps core from finding anything in it.
/// </summary>
public static class StubSentinel
{
    private const string Prefix = "http://shoal.invalid/v1/";

    /// <summary>
    /// Builds the file content for a stub key.
    /// </summary>
    /// <param name="stubKey">The stub key.</param>
    /// <returns>The sentinel URL and a line feed.</returns>
    public static string Content(string stubKey) => Prefix + stubKey + "\n";

    /// <summary>
    /// Reads the stub key out of a sentinel.
    /// </summary>
    /// <param name="content">The file content.</param>
    /// <param name="stubKey">The stub key.</param>
    /// <returns>True when the content is exactly a sentinel with a valid key.</returns>
    public static bool TryParse(string? content, out string stubKey)
    {
        stubKey = string.Empty;
        if (content is null || !content.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var key = content[Prefix.Length..].TrimEnd('\n', '\r');
        if (key.Length is < 8 or > 32 || !key.All(static c => c is (>= '0' and <= '9') or (>= 'a' and <= 'z')))
        {
            return false;
        }

        stubKey = key;
        return true;
    }
}

/// <summary>
/// The place a stub is written to.
/// </summary>
/// <param name="StubRoot">The validated StubRoot.</param>
/// <param name="LibraryFolder">The library's folder below it.</param>
/// <param name="Peer">The peer (for the images and the NFO tag).</param>
public sealed record StubTarget(string StubRoot, string LibraryFolder, PeerEndpoint Peer);

/// <summary>
/// Writes, updates and removes the files of a stub (DESIGN 5.2 phase D). A stub is a folder of <c>.nfo</c>, images and a <c>.strm</c>; the
/// <c>.strm</c> is written last because it is what makes the folder an item, so a crash mid-write leaves no half-item. Every path is confined
/// to the library folder and checked for links before anything is written or deleted. Folders are never renamed.
/// </summary>
public sealed class StubWriter
{
    private readonly ImageFetcher _images;
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="StubWriter"/> class.
    /// </summary>
    /// <param name="images">The image fetcher.</param>
    /// <param name="logger">The logger.</param>
    public StubWriter(ImageFetcher images, ILogger<StubWriter> logger)
        : this(images, (ILogger)logger)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="StubWriter"/> class (tests: any logger).
    /// </summary>
    /// <param name="images">The image fetcher.</param>
    /// <param name="logger">The logger.</param>
    internal StubWriter(ImageFetcher images, ILogger logger)
    {
        _images = images;
        _logger = logger;
    }

    /// <summary>
    /// The stub's folder inside the library, from the stored relative path.
    /// </summary>
    /// <param name="libraryFolder">The library folder.</param>
    /// <param name="stubRelPath">The stored <c>folder/file.strm</c> path.</param>
    /// <returns>The confined folder path, or null when the stored path is not safe.</returns>
    public static string? FolderOf(string libraryFolder, string? stubRelPath)
    {
        if (!CatalogStore.IsSafeStubPath(stubRelPath))
        {
            return null;
        }

        var folderName = stubRelPath!.Split('/')[0];
        return PathSanitizer.TryConfine(libraryFolder, folderName, out var full) ? full : null;
    }

    /// <summary>
    /// Creates a stub: folder, NFO, images, then the <c>.strm</c>.
    /// </summary>
    /// <param name="target">Where to write.</param>
    /// <param name="folderName">The unique folder name from <see cref="PathSanitizer"/>.</param>
    /// <param name="stubKey">The stub key.</param>
    /// <param name="movie">The sanitized movie.</param>
    /// <param name="art">The image size.</param>
    /// <param name="quota">The peer's disk budget.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The outcome; never throws for a file system problem.</returns>
    public async Task<StubWriteResult> AddAsync(StubTarget target, string folderName, string stubKey, SanitizedMovie movie, ArtQuality art, DiskQuota quota, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(movie);
        ArgumentNullException.ThrowIfNull(quota);
        if (!PathSanitizer.TryConfine(target.LibraryFolder, folderName, out var folder) || PathSanitizer.HasLinkBelow(target.StubRoot, folder))
        {
            _logger.LogWarning("A stub folder for peer {Peer} left its library and was not created", Redact.Id(target.Peer.PeerKey));
            return StubWriteResult.Unsafe;
        }

        var created = false;
        var imageBytes = 0L;
        var reserved = 0L;
        try
        {
            if (Directory.Exists(folder))
            {
                return StubWriteResult.Exists; // the name was free in the catalog, but something is on disk: the caller picks another
            }

            var nfo = NfoWriter.Write(movie, target.Peer.DisplayName);
            var strm = Encoding.UTF8.GetBytes(StubSentinel.Content(stubKey));
            if (!quota.TryReserve(nfo.Length + strm.Length))
            {
                return StubWriteResult.QuotaFull; // the text of a title counts against the budget too (W6 L-8)
            }

            reserved = nfo.Length + strm.Length;
            Directory.CreateDirectory(folder);
            created = true;
            AtomicFile.Write(Path.Combine(folder, folderName + ".nfo"), nfo);

            var images = 0;
            var posterStored = false;
            var backdropStored = false;
            var quotaReached = false;
            foreach (var (kind, tag) in new[] { (ImageKind.Poster, movie.PrimaryImageTag), (ImageKind.Backdrop, movie.BackdropImageTag) })
            {
                var result = await _images.FetchAsync(target.Peer, movie.PeerItemId, kind, tag, art, target.StubRoot, folder, quota, cancellationToken).ConfigureAwait(false);
                if (result.Outcome == ImageOutcome.Stored)
                {
                    images++;
                    posterStored |= kind == ImageKind.Poster;
                    backdropStored |= kind == ImageKind.Backdrop;
                    imageBytes += result.Bytes;
                }
                else if (result.Outcome == ImageOutcome.QuotaReached)
                {
                    quotaReached = true;
                }
            }

            return Finish(folder, folderName, nfo.Length, strm, images, quotaReached) with { PosterStored = posterStored, BackdropStored = backdropStored };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("A stub for peer {Peer} could not be written ({Error})", Redact.Id(target.Peer.PeerKey), ex.GetType().Name);
            if (created)
            {
                DeleteFolderQuietly(folder);
                quota.Add(-imageBytes);
            }

            quota.Add(-reserved);

            return StubWriteResult.Failed;
        }
    }

    /// <summary>
    /// Brings an existing stub up to date: rewrites the NFO when its content changed and fetches the images whose tag changed. The folder and
    /// the <c>.strm</c> stay as they are.
    /// </summary>
    /// <param name="target">Where the stub is.</param>
    /// <param name="stubRelPath">The stored <c>folder/file.strm</c> path.</param>
    /// <param name="movie">The sanitized movie.</param>
    /// <param name="fetchPoster">Whether the poster tag changed (or the image is missing).</param>
    /// <param name="fetchBackdrop">Whether the backdrop tag changed (or the image is missing).</param>
    /// <param name="art">The image size.</param>
    /// <param name="quota">The peer's disk budget.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The outcome.</returns>
    public async Task<StubWriteResult> UpdateAsync(StubTarget target, string stubRelPath, SanitizedMovie movie, bool fetchPoster, bool fetchBackdrop, ArtQuality art, DiskQuota quota, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(movie);
        ArgumentNullException.ThrowIfNull(quota);
        var folder = FolderOf(target.LibraryFolder, stubRelPath);
        if (folder is null || PathSanitizer.HasLinkBelow(target.StubRoot, folder))
        {
            return StubWriteResult.Unsafe;
        }

        try
        {
            if (!Directory.Exists(folder))
            {
                return StubWriteResult.Failed; // the stub vanished; the next scan and sync sort it out
            }

            var name = stubRelPath.Split('/')[0];
            var nfoPath = Path.Combine(folder, name + ".nfo");
            var nfo = NfoWriter.Write(movie, target.Peer.DisplayName);
            var existing = File.Exists(nfoPath) ? await File.ReadAllBytesAsync(nfoPath, cancellationToken).ConfigureAwait(false) : null;
            var changed = false;
            var nfoRefused = false;
            if (existing is null || !existing.AsSpan().SequenceEqual(nfo))
            {
                // The growth of an NFO counts against the quota like a new one (Warden 07 L-5); when it does not fit, the old NFO stays.
                if (quota.TryReserve(nfo.Length, existing?.Length ?? 0))
                {
                    AtomicFile.Write(nfoPath, nfo);
                    changed = true;
                }
                else
                {
                    nfoRefused = true;
                }
            }

            var imagesStored = 0;
            var posterStored = false;
            var backdropStored = false;
            foreach (var (kind, tag, fetch) in new[] { (ImageKind.Poster, movie.PrimaryImageTag, fetchPoster), (ImageKind.Backdrop, movie.BackdropImageTag, fetchBackdrop) })
            {
                if (!fetch)
                {
                    continue;
                }

                var result = await _images.FetchAsync(target.Peer, movie.PeerItemId, kind, tag, art, target.StubRoot, folder, quota, cancellationToken).ConfigureAwait(false);
                if (result.Outcome == ImageOutcome.Stored)
                {
                    imagesStored++;
                    posterStored |= kind == ImageKind.Poster;
                    backdropStored |= kind == ImageKind.Backdrop;
                    changed = true;
                }
            }

            return new StubWriteResult(StubWriteOutcome.Written, changed, imagesStored, QuotaReached: nfoRefused, PosterStored: posterStored, BackdropStored: backdropStored);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("A stub for peer {Peer} could not be updated ({Error})", Redact.Id(target.Peer.PeerKey), ex.GetType().Name);
            return StubWriteResult.Failed;
        }
    }

    /// <summary>
    /// Removes a stub: only the files Shoal creates (<c>*.strm</c>, <c>*.nfo</c>, <c>poster.*</c>, <c>backdrop.*</c>), then the folder if it is empty.
    /// Anything else in the folder stays, and so does the folder.
    /// </summary>
    /// <param name="target">Where the stub is.</param>
    /// <param name="stubRelPath">The stored <c>folder/file.strm</c> path.</param>
    /// <param name="quota">The peer's disk budget; the bytes removed are given back.</param>
    /// <returns>True when the stub is gone (or never existed); false when a file could not be deleted or the path was not safe.</returns>
    public bool Remove(StubTarget target, string stubRelPath, DiskQuota quota)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(quota);
        var folder = FolderOf(target.LibraryFolder, stubRelPath);
        if (folder is null || PathSanitizer.HasLinkBelow(target.StubRoot, folder))
        {
            _logger.LogWarning("A stub path for peer {Peer} is not safe and nothing was deleted", Redact.Id(target.Peer.PeerKey));
            return false;
        }

        try
        {
            if (!Directory.Exists(folder))
            {
                return true;
            }

            var freed = 0L;
            foreach (var file in Directory.EnumerateFiles(folder).ToList())
            {
                var fileName = Path.GetFileName(file);
                if (!IsOurs(fileName))
                {
                    continue;
                }

                var info = new FileInfo(file);
                if (info.LinkTarget is not null || (info.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    return false; // never follow a link out of the library
                }

                freed += info.Length;
                File.Delete(file);
            }

            quota.Add(-freed);
            if (!Directory.EnumerateFileSystemEntries(folder).Any())
            {
                Directory.Delete(folder);
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("A stub for peer {Peer} could not be removed ({Error})", Redact.Id(target.Peer.PeerKey), ex.GetType().Name);
            return false;
        }
    }

    /// <summary>
    /// Writes the <c>.strm</c> of an extra version next to the primary: <c>&lt;folder&gt; - &lt;label&gt;.strm</c>, which is how core groups files of one folder
    /// into versions (DESIGN v2.6.4). No NFO and no images: they exist once, for the primary.
    /// </summary>
    /// <param name="target">Where the title is.</param>
    /// <param name="primaryRelPath">The primary's stored <c>folder/file.strm</c> path.</param>
    /// <param name="label">The label built by <see cref="SourceSelector"/> (letters, digits and spaces).</param>
    /// <param name="stubKey">The stub key of the extra version.</param>
    /// <param name="quota">The peer's disk budget.</param>
    /// <returns>The outcome with the new file's relative path.</returns>
    public StubWriteResult AddExtra(StubTarget target, string primaryRelPath, string label, string stubKey, DiskQuota quota)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(quota);
        var folder = FolderOf(target.LibraryFolder, primaryRelPath);
        if (folder is null || PathSanitizer.HasLinkBelow(target.StubRoot, folder) || label.Length is 0 or > 40 || !label.All(static c => c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or ' '))
        {
            return StubWriteResult.Unsafe;
        }

        var stem = primaryRelPath.Split('/')[0];
        var fileName = stem + " - " + label + ".strm";
        if (fileName.Length > 240 || !PathSanitizer.TryConfine(folder, fileName, out var full))
        {
            return StubWriteResult.Unsafe;
        }

        var strm = Encoding.UTF8.GetBytes(StubSentinel.Content(stubKey));
        try
        {
            if (!Directory.Exists(folder))
            {
                return StubWriteResult.Failed;
            }

            if (File.Exists(full))
            {
                return StubWriteResult.Exists;
            }

            if (!quota.TryReserve(strm.Length))
            {
                return StubWriteResult.QuotaFull;
            }

            try
            {
                AtomicFile.Write(full, strm);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                quota.Add(-strm.Length);
                throw;
            }

            return new StubWriteResult(StubWriteOutcome.Written, true, 0, strm.Length, RelPath: stem + "/" + fileName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("An extra version for peer {Peer} could not be written ({Error})", Redact.Id(target.Peer.PeerKey), ex.GetType().Name);
            return StubWriteResult.Failed;
        }
    }

    /// <summary>
    /// Deletes the <c>.strm</c> of one extra version and nothing else.
    /// </summary>
    /// <param name="target">Where the title is.</param>
    /// <param name="stubRelPath">The stored <c>folder/file.strm</c> path of the extra version.</param>
    /// <param name="quota">The peer's disk budget; the bytes removed are given back.</param>
    /// <returns>True when the file is gone (or never existed); false when the path was not safe or the file could not be deleted.</returns>
    public bool RemoveFile(StubTarget target, string stubRelPath, DiskQuota quota)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(quota);
        var folder = FolderOf(target.LibraryFolder, stubRelPath);
        if (folder is null || PathSanitizer.HasLinkBelow(target.StubRoot, folder) || !PathSanitizer.TryConfine(folder, stubRelPath.Split('/')[1], out var full))
        {
            return false;
        }

        try
        {
            var info = new FileInfo(full);
            if (!info.Exists)
            {
                return true;
            }

            if (info.LinkTarget is not null || (info.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                return false;
            }

            var length = info.Length;
            File.Delete(full);
            quota.Add(-length);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("An extra version for peer {Peer} could not be removed ({Error})", Redact.Id(target.Peer.PeerKey), ex.GetType().Name);
            return false;
        }
    }

    private static bool IsOurs(string fileName) =>

        fileName.EndsWith(".strm", StringComparison.Ordinal)
        || fileName.EndsWith(".nfo", StringComparison.Ordinal)
        || fileName is "poster.jpg" or "poster.png" or "backdrop.jpg" or "backdrop.png";

    private static StubWriteResult Finish(string folder, string folderName, long nfoLength, byte[] strm, int images, bool quotaReached)
    {
        AtomicFile.Write(Path.Combine(folder, folderName + ".strm"), strm);
        return new StubWriteResult(StubWriteOutcome.Written, true, images, nfoLength + strm.Length, quotaReached, folderName + "/" + folderName + ".strm");
    }

    private static void DeleteFolderQuietly(string folder)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(folder))
            {
                File.Delete(file);
            }

            Directory.Delete(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // best effort: a leftover folder without a .strm is not an item
        }
    }
}

/// <summary>
/// The result of <see cref="StubWriter.AddAsync"/> and <see cref="StubWriter.UpdateAsync"/>.
/// </summary>
/// <param name="Outcome">How it ended.</param>
/// <param name="Changed">Whether anything on disk changed.</param>
/// <param name="Images">How many images were stored.</param>
/// <param name="FileBytes">The bytes of the NFO and the <c>.strm</c> that were written (images are counted by the quota itself).</param>
/// <param name="QuotaReached">Whether an image was refused because the quota is full.</param>
/// <param name="RelPath">The stub's <c>.strm</c> path relative to the library folder, for a stub that was just created.</param>
/// <param name="PosterStored">Whether a poster was stored by this call.</param>
/// <param name="BackdropStored">Whether a backdrop was stored by this call.</param>
public sealed record StubWriteResult(StubWriteOutcome Outcome, bool Changed = false, int Images = 0, long FileBytes = 0, bool QuotaReached = false, string? RelPath = null, bool PosterStored = false, bool BackdropStored = false)
{
    /// <summary>Gets a result for a folder that already exists.</summary>
    public static StubWriteResult Exists { get; } = new(StubWriteOutcome.Exists);

    /// <summary>Gets a result for an unsafe path.</summary>
    public static StubWriteResult Unsafe { get; } = new(StubWriteOutcome.Unsafe);

    /// <summary>Gets a result for a write failure.</summary>
    public static StubWriteResult Failed { get; } = new(StubWriteOutcome.Failed);

    /// <summary>Gets the result of a stub the quota had no room for.</summary>
    public static StubWriteResult QuotaFull { get; } = new(StubWriteOutcome.QuotaFull, QuotaReached: true);
}
