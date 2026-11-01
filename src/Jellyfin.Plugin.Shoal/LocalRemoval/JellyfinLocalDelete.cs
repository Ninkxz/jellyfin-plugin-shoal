using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Shoal.LocalRemoval;

/// <summary>2.10.4 K2: "delete like Jellyfin" on the real core (plan rules B, E, F, G and the outcome table). Nothing here writes a path to a log.</summary>
internal sealed partial class JellyfinRemovalLibrary : ILocalDeleteLibrary
{
    /// <summary>The most files and folders a movie folder may hold before it is not treated as dedicated (each file is one index lookup).</summary>
    internal const int MaxFolderEntries = 1000;

    // W23-4: the only files Jellyfin does not index that may be deleted with the movie folder (metadata, artwork, subtitle sidecars).
    private static readonly HashSet<string> Sidecars = new(StringComparer.OrdinalIgnoreCase) { ".nfo", ".jpg", ".jpeg", ".png", ".webp", ".tbn", ".srt", ".ass", ".ssa", ".sub", ".idx", ".vtt", ".sup", ".smi" };

    private Func<IDisposable?>? _holdSync;

    /// <summary>Gets or sets the Jellyfin permission rule of the Delete button (tests replace it; real use is <see cref="BaseItem.CanDelete(User)"/>).</summary>
    internal Func<BaseItem, User, bool> CanDeleteRule { get; set; } = static (item, user) => item.CanDelete(user);

    /// <summary>Gets or sets what core will delete for a movie (tests replace it; real use is <see cref="BaseItem.GetDeletePaths"/>, gate S2).</summary>
    internal Func<Movie, IReadOnlyList<(string Path, bool IsDirectory)>> DeletePathsRule { get; set; } = static movie => movie.GetDeletePaths().Select(static f => (f.FullName, f.IsDirectory)).ToArray();

    /// <summary>Gets or sets the sync hold taken around the delete call (tests replace it; real use is <see cref="Sync.CatalogSyncService.TryHold"/>).</summary>
    internal Func<IDisposable?> HoldSync
    {
        get => _holdSync ?? (() => _sync.TryHold());
        set => _holdSync = value;
    }

    /// <summary>Gets or sets the stat call used to read whether a file is there (tests replace it to raise the errors a dropped share raises).</summary>
    internal Func<string, FileAttributes> FileAttributesRule { get; set; } = File.GetAttributes;

    /// <inheritdoc />
    public IReadOnlyList<RemovalTarget> Candidates(string peerKey) => Discover(peerKey, holdingFolder: false);

    /// <inheritdoc />
    public RemovalTarget Resolve(string peerKey, Guid localItemId, Guid peerItemId)
    {
        if (_refresh.IsScanRunning)
        {
            throw Refuse(LocalDeleteReason.LibraryBusy);
        }

        IReadOnlyList<RemovalTarget> found;
        try
        {
            found = Discover(peerKey, holdingFolder: false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            throw Refuse(_refresh.IsScanRunning ? LocalDeleteReason.LibraryBusy : LocalDeleteReason.CandidateChanged);
        }

        return found.SingleOrDefault(t => t.LocalItemId == localItemId && t.PeerItemId == peerItemId) ?? throw Refuse(LocalDeleteReason.CandidateChanged);
    }

    /// <inheritdoc />
    public void VerifyAdministrator(Guid admin) => VerifyPurgeAdministrator(admin);

    /// <inheritdoc />
    public IReadOnlyList<string> Inspect(RemovalTarget target, Guid admin, RemovalFileSnapshot hashed) => Check(target, admin, hashed).DeleteSet;

    /// <inheritdoc />
    public LocalDeleteOutcome Delete(RemovalTarget target, Guid admin, RemovalFileSnapshot hashed, Func<bool> allowed, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var held = HoldSync() ?? throw Refuse(LocalDeleteReason.Busy);
        if (_refresh.IsScanRunning)
        {
            throw Refuse(LocalDeleteReason.LibraryBusy);
        }

        // Re-stat and re-check everything just before the call (Warden 4: a path that changes between check and call).
        var check = Check(target, admin, hashed);
        if (!allowed())
        {
            throw Refuse(LocalDeleteReason.Disabled); // W23-1: the last look at the switch, under the hold, before core deletes anything
        }

        cancellationToken.ThrowIfCancellationRequested();
        var threw = false;
        var expected = false;
        try
        {
            // Gate S1: the 3-argument form is what the web Delete button calls (exists on 12.0 and 12.1).
            _library.DeleteItem(check.Item, new DeleteOptions { DeleteFileLocation = true }, true);
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException))
        {
            threw = true;
            expected = ex is IOException or UnauthorizedAccessException;
            _logger.LogWarning("Shoal asked Jellyfin to delete a movie and Jellyfin refused ({Kind})", ex.GetType().Name);
        }

        if (Outcome(target) is not { } after || after.File == LocalFileState.Unknown)
        {
            return LocalDeleteOutcome.Unconfirmed; // W23-5: an error is not a proof that the file is gone, and nothing is retried
        }

        if (after.File == LocalFileState.Absent && !after.Indexed)
        {
            return threw && !expected ? LocalDeleteOutcome.Unconfirmed : LocalDeleteOutcome.Done;
        }

        if (after.File == LocalFileState.Present && after.Indexed)
        {
            // Core throws on the first (required) path before it touches the index, so the movie file is intact. Not an IO error: we do not know why.
            return !threw || expected ? LocalDeleteOutcome.Failed : LocalDeleteOutcome.Unconfirmed;
        }

        if (after.File == LocalFileState.Absent && after.Indexed)
        {
            // File proven gone, row left: core swallows "not found" and only removes the row, so one retry is safe.
            try
            {
                _library.DeleteItem(check.Item, new DeleteOptions { DeleteFileLocation = true }, true);
            }
            catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException))
            {
                _logger.LogWarning("Shoal retried removing a deleted movie from the library and Jellyfin refused ({Kind})", ex.GetType().Name);
            }

            return Outcome(target) is { File: LocalFileState.Absent, Indexed: false } ? LocalDeleteOutcome.Done : LocalDeleteOutcome.Unconfirmed;
        }

        return LocalDeleteOutcome.Unconfirmed; // removed from the library, file still on disk
    }

    /// <inheritdoc />
    public Guid? FindSharedFor(RemovalTarget target)
    {
        try
        {
            return FindShared(target)?.Id;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }

    private static LocalDeleteRefusedException Refuse(LocalDeleteReason reason) => new(reason);

    private static long Seconds(long ticks) => ticks / TimeSpan.TicksPerSecond;

    // "p:{length}:{ticks}" from PortableFileProbe; anything else (a native snapshot) is not a delete proof.
    private static (long Length, long Ticks)? Parse(string identity)
    {
        var parts = identity.Split(':');
        return parts.Length == 3 && parts[0] == "p" && long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var length)
            && long.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks) ? (length, ticks) : null;
    }

    private static bool IsLink(FileSystemInfo info) => (info.Attributes & FileAttributes.ReparsePoint) != 0 || info.LinkTarget is not null;

    private static bool StrictlyInside(string path, string root) => !SamePath(path, root) && Inside(path, root);

    // W23-5: the file counts as gone only on the filesystem's own "not found" with the library root still answering; any other error is Unknown.
    private (LocalFileState File, bool Indexed)? Outcome(RemovalTarget target)
    {
        try
        {
            return (LocalFileStates.ReadUnder(target.Path, target.Root, FileAttributesRule), Indexed(target.LocalItemId) is not null);
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException))
        {
            return null;
        }
    }

    private DeleteCheck Check(RemovalTarget target, Guid admin, RemovalFileSnapshot hashed)
    {
        VerifyPurgeAdministrator(admin); // rule A, per item
        var movie = Identity(target, hashed); // rule E
        var user = _users.GetUserById(admin) ?? throw new InvalidOperationException("administratorRequired");
        var set = DeleteSet(target, movie); // rule F
        if (!CanDeleteRule(movie, user))
        {
            throw Refuse(LocalDeleteReason.DeleteNotPermitted); // rule G (gate S3: true for admins by default)
        }

        return new(movie, set);
    }

    // Rule E: still the same real local movie as the one hashed, never a stub, a federated item or something being played.
    private Movie Identity(RemovalTarget target, RemovalFileSnapshot hashed)
    {
        var movie = Indexed(target.LocalItemId);

        // W23-4: stacked parts are re-checked here too (discovery checked them first).
        if (movie is null || !RemovalOriginProof.SupportsMovieShape(movie) || _federated.Find(movie.Id) is not null || movie.GetAdditionalParts(null).Any())
        {
            throw Refuse(LocalDeleteReason.UnsupportedItem);
        }

        var stubRoot = _state.Current.StubRoot;
        if (!string.IsNullOrWhiteSpace(stubRoot) && (Inside(movie.Path, stubRoot) || Inside(target.Path, stubRoot)))
        {
            throw Refuse(LocalDeleteReason.UnsupportedItem); // Pilot 2 S3: core reports CanDelete true for stubs too
        }

        if (!SamePath(movie.Path, target.Path) || !SamePath(hashed.FullPath, target.Path) || !SamePath(hashed.Root, target.Root) || hashed.Length != target.Bytes
            || Parse(hashed.Identity) is not { } was || was.Length != hashed.Length || !Matches(movie.ProviderIds, target.ProviderIds) || Busy(movie.Id))
        {
            throw Refuse(LocalDeleteReason.RemovalChanged);
        }

        var info = new FileInfo(target.Path);
        if (!info.Exists || IsLink(info) || info.Length != was.Length || Seconds(info.LastWriteTimeUtc.Ticks) != Seconds(was.Ticks))
        {
            throw Refuse(LocalDeleteReason.RemovalChanged); // compared to the second (plan risk 9: coarse mtime on some SMB servers)
        }

        return movie;
    }

    // Rule F (with Scout 04 C2): core deletes the movie's whole folder, so that folder must hold this movie and its own extras only.
    private List<string> DeleteSet(RemovalTarget target, Movie movie)
    {
        if (movie.IsInMixedFolder)
        {
            throw Refuse(LocalDeleteReason.FolderNotDedicated); // flat folder: core's sidecar prefix match also takes neighbours' files
        }

        IReadOnlyList<(string Path, bool IsDirectory)> paths;
        try
        {
            paths = DeletePathsRule(movie);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or NullReferenceException)
        {
            throw Refuse(LocalDeleteReason.FolderNotDedicated);
        }

        var roots = _library.GetVirtualFolders().SelectMany(static v => v.Locations).Where(static r => !string.IsNullOrWhiteSpace(r) && Path.IsPathFullyQualified(r))
            .Select(static r => Path.TrimEndingDirectorySeparator(Path.GetFullPath(r))).Distinct().ToArray();
        var stubRoot = _state.Current.StubRoot;
        var result = new List<string>();
        var coversMovie = false;
        foreach (var (raw, isDirectory) in paths)
        {
            if (string.IsNullOrWhiteSpace(raw) || !Path.IsPathFullyQualified(raw))
            {
                throw Refuse(LocalDeleteReason.FolderNotDedicated);
            }

            var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(raw));
            var owners = roots.Where(r => StrictlyInside(path, r)).ToArray();
            if (owners.Length != 1 || roots.Any(r => Inside(r, path))
                || (!string.IsNullOrWhiteSpace(stubRoot) && (Inside(path, stubRoot) || Inside(stubRoot, path))))
            {
                throw Refuse(LocalDeleteReason.FolderNotDedicated); // a root, a parent of a root, two roots, or the stub root
            }

            // No link on the path or between it and its root.
            for (var at = path; at is not null && !SamePath(at, owners[0]); at = Path.GetDirectoryName(at))
            {
                if (LocalFileStates.Read(at, FileAttributesRule) == LocalFileState.Unknown)
                {
                    throw Refuse(LocalDeleteReason.FolderNotDedicated); // W23-5 sibling: an unreadable folder would skip its link check
                }

                FileSystemInfo info = isDirectory && SamePath(at, path) ? new DirectoryInfo(at) : (SamePath(at, path) ? new FileInfo(at) : new DirectoryInfo(at));
                if (info.Exists && IsLink(info))
                {
                    throw Refuse(LocalDeleteReason.FolderNotDedicated);
                }
            }

            if (isDirectory)
            {
                coversMovie |= Inside(target.Path, path);
                OnlyThisMovie(path, movie);
            }
            else
            {
                coversMovie |= SamePath(path, target.Path);
            }

            result.Add(path);
        }

        if (!coversMovie)
        {
            throw Refuse(LocalDeleteReason.FolderNotDedicated);
        }

        return result;
    }

    // Every file and folder below the delete folder: no link, and no indexed item other than this movie and extras it owns.
    private void OnlyThisMovie(string folder, Movie movie)
    {
        if (!Directory.Exists(folder))
        {
            throw Refuse(LocalDeleteReason.FolderNotDedicated);
        }

        var seen = 0;
        foreach (var entry in new DirectoryInfo(folder).EnumerateFileSystemInfos("*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0, IgnoreInaccessible = false }))
        {
            if (++seen > MaxFolderEntries || IsLink(entry))
            {
                throw Refuse(LocalDeleteReason.FolderNotDedicated);
            }

            var isDirectory = (entry.Attributes & FileAttributes.Directory) != 0;
            if (!isDirectory && SamePath(entry.FullName, movie.Path))
            {
                continue;
            }

            var other = _library.FindByPath(entry.FullName, isDirectory);
            if (other is not null && other.Id != movie.Id && other.OwnerId != movie.Id)
            {
                throw Refuse(LocalDeleteReason.FolderNotDedicated); // a second movie, an episode, or an extra of something else
            }

            // W23-4: a file core would delete with the folder but nothing proved. Only Jellyfin's own sidecars (art, metadata, subtitles) may go along.
            if (other is null && !isDirectory && !Sidecars.Contains(Path.GetExtension(entry.Name)))
            {
                throw Refuse(LocalDeleteReason.FolderNotDedicated);
            }
        }
    }

    private sealed record DeleteCheck(Movie Item, IReadOnlyList<string> DeleteSet);
}
