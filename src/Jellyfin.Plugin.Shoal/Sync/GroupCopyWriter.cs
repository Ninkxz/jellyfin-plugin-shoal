using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.State;
using Jellyfin.Plugin.Shoal.Storage;
using Jellyfin.Plugin.Shoal.Visibility;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Shoal.Sync;

/// <summary>
/// What one pass of the copy writer did.
/// </summary>
/// <param name="Written">Extra copies written.</param>
/// <param name="Retagged">Copies whose NFO got new <c>shoal-alt-</c> tags.</param>
/// <param name="Removed">Extra copy folders deleted.</param>
public sealed record GroupCopyResult(int Written, int Retagged, int Removed);

/// <summary>
/// One stub per library per group (PLAN v2.9 K44/K45, movies): a grouped title gets an extra copy in every shared library one of its members feeds, so each user sees
/// it in the first library they can open, once.
/// <list type="number">
/// <item>The plan is recorded first (groups.json): new extra copies with their folder names, copies whose library no member feeds any more retired, and every copy's
/// <c>shoal-alt-</c> tags (one for each higher-ordered library that also holds a copy).</item>
/// <item>Then the alt tags are written to the users (the tag service): every user who can open library X blocks <c>shoal-alt-X</c> before any lower copy exists.</item>
/// <item>Then the files: a new copy is the owner's NFO and images with its own sentinel <c>.strm</c>, the copy's library tag and alt tags; an existing copy whose
/// alt tags changed gets its NFO retagged; a retired copy's recorded folder is deleted (and only then leaves the record).</item>
/// <item>Then a targeted scan of each folder that changed. Never doubles: a copy appears already carrying its alt tags.</item>
/// </list>
/// Copies go only into libraries of v2.9 (a legacy per-friend library keeps one copy per group, as before). An extra copy plays only from members whose library the
/// caller may open (CallerContext, then the group pick).
/// </summary>
public sealed class GroupCopyWriter : IDisposable
{
    private const string KeyAlphabet = "abcdefghijklmnopqrstuvwxyz0123456789";
    private const long MaxCopiedFileBytes = 16L * 1024 * 1024;

    private readonly IStateStore _state;
    private readonly GroupBook _groups;
    private readonly StubCatalog _catalog;
    private readonly ILibraryRefresh _refresh;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// Initializes a new instance of the <see cref="GroupCopyWriter"/> class.
    /// </summary>
    /// <param name="state">The state store.</param>
    /// <param name="groups">The title groups.</param>
    /// <param name="catalog">The catalogs (the owner's stub of each group).</param>
    /// <param name="refresh">The library refresh (targeted scans).</param>
    /// <param name="logger">The logger.</param>
    public GroupCopyWriter(IStateStore state, GroupBook groups, StubCatalog catalog, ILibraryRefresh refresh, ILogger logger)
    {
        _state = state;
        _groups = groups;
        _catalog = catalog;
        _refresh = refresh;
        _logger = logger;
    }

    /// <summary>Gets or sets what writes the users' tags (the tag service's reconcile) and says whether every write held; it runs before any copy file is written.</summary>
    public Func<CancellationToken, Task<bool>>? TagsFirst { get; set; }

    /// <summary>Gets or sets the local progress capture required before deleting a copy.</summary>
    public Jellyfin.Plugin.Shoal.Progress.IKeepMyPlaceRetention? Retention { get; set; }

    /// <summary>
    /// The <c>shoal-alt-</c> tags of a copy: one for each other library holding a copy of the title that comes before it in the library order.
    /// </summary>
    /// <param name="order">The library order.</param>
    /// <param name="copyLibraries">The libraries that hold a copy.</param>
    /// <param name="libKey">The copy's library.</param>
    /// <returns>The tags, in order.</returns>
    public static List<string> AltTagsFor(IReadOnlyList<string> order, IEnumerable<string> copyLibraries, string libKey)
    {
        ArgumentNullException.ThrowIfNull(order);
        ArgumentNullException.ThrowIfNull(copyLibraries);
        int Position(string key) => order.Select((k, i) => (k, i)).FirstOrDefault(p => string.Equals(p.k, key, StringComparison.Ordinal)) is { k: not null } found ? found.i : int.MaxValue;
        var mine = Position(libKey);
        return [.. copyLibraries.Where(k => k.Length > 0 && !string.Equals(k, libKey, StringComparison.Ordinal) && Position(k) < mine).Distinct(StringComparer.Ordinal).OrderBy(Position).Select(LibraryFolders.AltTag)];
    }

    /// <summary>
    /// The shared libraries of v2.9 a group needs an extra copy in: those its members feed (and still feed), except the one the owner's own stub is in.
    /// </summary>
    /// <param name="state">The state.</param>
    /// <param name="group">The group.</param>
    /// <param name="primaryLibKey">The library of the owner's own stub.</param>
    /// <returns>The library keys.</returns>
    public static HashSet<string> WantedLibraries(ShoalState state, TitleGroup group, string primaryLibKey)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(group);
        var wanted = new HashSet<string>(StringComparer.Ordinal);
        foreach (var member in group.Members)
        {
            if (LibraryFolders.FedBy(state, member.PeerKey, "movies") is { } fed && string.Equals(fed.LibKey, member.LibKey, StringComparison.Ordinal)
                && !string.Equals(fed.LibKey, primaryLibKey, StringComparison.Ordinal))
            {
                wanted.Add(fed.LibKey);
            }
        }

        return wanted;
    }

    /// <summary>
    /// One pass over every group. Safe to run any time (after a sync, a move, an order change); one pass at a time.
    /// </summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>What was done.</returns>
    public async Task<GroupCopyResult> RunAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_state.Status != StateStatus.Ready || !Path.IsPathRooted(_state.Current.StubRoot))
            {
                return new GroupCopyResult(0, 0, 0);
            }

            var state = _state.Current;
            if (state.Home.Peers.Any(p => !_catalog.IsReadable(p.PeerKey)))
            {
                return new GroupCopyResult(0, 0, 0);
            }

            // Capture while the old groups still classify their copies, before retirement changes ownership.
            var retiring = _groups.All().SelectMany(g =>
            {
                var primary = g.Copies.FirstOrDefault(static c => !c.Extra)?.LibKey ?? string.Empty;
                var wanted = WantedLibraries(state, g, primary);
                return g.Copies.Where(c => c.Extra && !wanted.Contains(c.LibKey));
            }).Concat(_groups.RetiredCopies()).ToList();
            foreach (var copy in retiring)
            {
                var library = state.Home.Libraries.FirstOrDefault(l => l.LibKey == copy.LibKey);
                var root = library is null ? null : LibraryFolders.PathOf(state.StubRoot, library);
                var folder = root is null ? null : LibraryTree.Confine(state.StubRoot, root, copy.FolderRel);
                if (folder is not null && Directory.Exists(folder) && !await RetentionGuard.BeforeFolderAsync(Retention, state.StubRoot, folder, cancellationToken).ConfigureAwait(false))
                {
                    return new GroupCopyResult(0, 0, 0);
                }
            }

            // 1. The plan, recorded before anything is written.
            var tagsChanged = new HashSet<string>(StringComparer.Ordinal);
            _groups.Update(document => Plan(state, document, tagsChanged));
            var snapshot = _groups.All();
            var pending = snapshot.SelectMany(static g => g.Copies.Where(static c => c.Extra && !c.Written).Select(c => (Group: g, Copy: c))).ToList();
            var retired = _groups.RetiredCopies().ToList();

            // 2. The users' alt tags first.
            // Warden 11 L2: when an account did not get its tag, no new copy is written this pass (that account would see the title twice); the next pass tries again.
            var tagsHeld = true;
            if ((tagsChanged.Count > 0 || pending.Count > 0) && TagsFirst is { } tags)
            {
                tagsHeld = await tags(cancellationToken).ConfigureAwait(false);
            }

            if (!tagsHeld)
            {
                _logger.LogWarning("Shoal did not write title copies this time: an account did not get its tags; the next pass tries again");
                pending.Clear(); // planned copies stay unwritten and are tried again; retagging existing copies is safe either way
            }

            // 3. The files.
            var changedFolders = new HashSet<string>(StringComparer.Ordinal);
            var written = 0;
            foreach (var (group, copy) in pending)
            {
                if (Write(state, group, copy, changedFolders))
                {
                    written++;
                }
            }

            var retagged = 0;
            foreach (var group in snapshot.Where(g => g.Copies.Any(c => tagsChanged.Contains(c.StubKey))))
            {
                foreach (var copy in group.Copies.Where(c => tagsChanged.Contains(c.StubKey) && (!c.Extra || c.Written)))
                {
                    if (FolderOf(state, group, copy) is { } folder && NfoRetag.Apply(state.StubRoot, folder, copy.Extra ? copy.LibKey : null, copy.AltTags) > 0)
                    {
                        retagged++;
                        changedFolders.Add(Path.GetDirectoryName(folder)!);
                    }
                }
            }

            var removed = await RemoveRetiredAsync(state, retired, changedFolders, cancellationToken).ConfigureAwait(false);

            // 4. A targeted scan of each library folder that changed.
            foreach (var folder in changedFolders)
            {
                _refresh.ReportChanged(folder);
            }

            if (written + retagged + removed > 0)
            {
                _logger.LogInformation("Shoal title copies: {Written} written, {Retagged} retagged, {Removed} removed", written, retagged, removed);
            }

            return new GroupCopyResult(written, retagged, removed);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose() => _gate.Dispose();

    private static string NewKey() => RandomNumberGenerator.GetString(KeyAlphabet, 16);

    private static bool Plan(ShoalState state, GroupsDocument document, HashSet<string> tagsChanged)
    {
        var changed = false;
        var keys = document.Groups.SelectMany(static g => g.Copies).Select(static c => c.StubKey).ToHashSet(StringComparer.Ordinal);
        foreach (var group in document.Groups.Where(static g => g.Kind == GroupKind.Movie))
        {
            var primary = group.Copies.FirstOrDefault(static c => !c.Extra);
            if (primary is null)
            {
                continue;
            }

            var wanted = GroupPlanner.HidesExtras(group) ? WantedLibraries(state, group, primary.LibKey) : [];
            foreach (var gone in group.Copies.Where(c => c.Extra && !wanted.Contains(c.LibKey)).ToList())
            {
                group.Copies.Remove(gone);
                document.Retired.Add(gone);
                changed = true;
            }

            foreach (var libKey in wanted.Where(k => !group.Copies.Any(c => string.Equals(c.LibKey, k, StringComparison.Ordinal))).Order(StringComparer.Ordinal))
            {
                var key = NewKey();
                while (!keys.Add(key))
                {
                    key = NewKey();
                }

                group.Copies.Add(new GroupCopy { Extra = true, Written = false, LibKey = libKey, StubKey = key, FolderRel = primary.FolderRel });
                changed = true;
            }

            var libraries = group.Copies.Select(static c => c.LibKey).ToList();
            foreach (var copy in group.Copies)
            {
                var alt = AltTagsFor(state.Home.LibraryOrder, libraries, copy.LibKey);
                if (!alt.SequenceEqual(copy.AltTags, StringComparer.Ordinal))
                {
                    copy.AltTags = alt;
                    tagsChanged.Add(copy.StubKey);
                    changed = true;
                }
            }
        }

        return changed;
    }

    // The folder of a copy: an extra copy's recorded folder in its library; the owner's own stub folder in the owner's library.
    private string? FolderOf(ShoalState state, TitleGroup group, GroupCopy copy)
    {
        if (copy.Extra)
        {
            var library = state.Home.Libraries.FirstOrDefault(l => !l.IsLegacy && string.Equals(l.LibKey, copy.LibKey, StringComparison.Ordinal));
            return library is null || LibraryFolders.PathOf(state.StubRoot, library) is not { } root ? null : LibraryTree.Confine(state.StubRoot, root, copy.FolderRel);
        }

        var entry = _catalog.Find(group.MetaOwner, copy.FolderRel);
        var mapping = entry is null ? null : state.Home.Peers.FirstOrDefault(p => p.PeerKey == group.MetaOwner)?.Libraries.FirstOrDefault(m => m.MappingKey == entry.MappingKey);
        return mapping is null || LibraryFolders.FolderOf(state, group.MetaOwner, mapping) is not { } ownerRoot ? null : LibraryTree.Confine(state.StubRoot, ownerRoot, copy.FolderRel);
    }

    // Writes one extra copy: the name is settled and recorded first, then the folder is made from the owner's NFO and images and its own sentinel.
    private bool Write(ShoalState state, TitleGroup group, GroupCopy planned, HashSet<string> changedFolders)
    {
        var primary = group.Copies.FirstOrDefault(static c => !c.Extra);
        var library = state.Home.Libraries.FirstOrDefault(l => !l.IsLegacy && string.Equals(l.LibKey, planned.LibKey, StringComparison.Ordinal));
        if (primary is null || library is null || LibraryFolders.PathOf(state.StubRoot, library) is not { } root || FolderOf(state, group, primary) is not { } source || !Directory.Exists(source))
        {
            return false;
        }

        try
        {
            SecureDirectory.Ensure(root);
            var name = planned.FolderRel;
            if (LibraryTree.Confine(state.StubRoot, root, name) is not { } target)
            {
                return false;
            }

            if (Directory.Exists(target) || File.Exists(target))
            {
                // Another title has that name here: a unique one, recorded before the folder exists.
                var used = Directory.EnumerateFileSystemEntries(root).Select(static p => PathSanitizer.CollisionKey(Path.GetFileName(p))).ToHashSet(StringComparer.Ordinal);
                name = PathSanitizer.Unique(name, planned.StubKey, used);
                if (LibraryTree.Confine(state.StubRoot, root, name) is not { } renamed || Directory.Exists(renamed))
                {
                    return false;
                }

                target = renamed;
                var settled = name;
                _groups.Update(document => SetCopy(document, planned.StubKey, c => c.FolderRel = settled));
            }

            Directory.CreateDirectory(target);
            foreach (var file in Directory.EnumerateFiles(source))
            {
                var info = new FileInfo(file);
                var fileName = info.Name;
                if (info.LinkTarget is not null || info.Length > MaxCopiedFileBytes)
                {
                    continue;
                }

                if (fileName is "poster.jpg" or "poster.png" or "backdrop.jpg" or "backdrop.png")
                {
                    File.Copy(file, Path.Combine(target, fileName), overwrite: true);
                }
                else if (string.Equals(fileName, primary.FolderRel + ".nfo", StringComparison.Ordinal))
                {
                    File.Copy(file, Path.Combine(target, name + ".nfo"), overwrite: true);
                }
            }

            AtomicFile.Write(Path.Combine(target, name + ".strm"), Encoding.UTF8.GetBytes(StubSentinel.Content(planned.StubKey)));
            var current = _groups.All().SelectMany(static g => g.Copies).FirstOrDefault(c => c.StubKey == planned.StubKey) ?? planned;
            NfoRetag.Apply(state.StubRoot, target, planned.LibKey, current.AltTags);
            if (state.Home.Libraries.FirstOrDefault(l => l.LibKey == planned.LibKey) is { } copyLibrary && LibraryFolders.HidesFacets(LibraryFolders.AccessOf(state, copyLibrary)))
            {
                NfoRetag.StripFacets(state.StubRoot, target); // Warden 11 L3: the copy's library is not visible to everyone
            }

            _groups.Update(document => SetCopy(document, planned.StubKey, static c => c.Written = true));
            changedFolders.Add(root);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("A title copy could not be written ({Error}); the next pass tries again", ex.GetType().Name);
            return false;
        }
    }

    // Deletes the recorded folders of retired copies, then forgets them. A folder that a catalog records (a connection's own stub) is never deleted.
    private async Task<int> RemoveRetiredAsync(ShoalState state, List<GroupCopy> retired, HashSet<string> changedFolders, CancellationToken cancellationToken)
    {
        if (retired.Count == 0)
        {
            return 0;
        }

        var removed = 0;
        var done = new HashSet<string>(StringComparer.Ordinal);
        foreach (var copy in retired)
        {
            var library = state.Home.Libraries.FirstOrDefault(l => !l.IsLegacy && string.Equals(l.LibKey, copy.LibKey, StringComparison.Ordinal));
            var root = library is null ? null : LibraryFolders.PathOf(state.StubRoot, library);
            if (root is null)
            {
                done.Add(copy.StubKey); // the library is gone, and its folder with it
                continue;
            }

            if (LibraryTree.Confine(state.StubRoot, root, copy.FolderRel) is { } folder && Directory.Exists(folder))
            {
                var sentinel = Path.Combine(folder, copy.FolderRel + ".strm");
                var ours = File.Exists(sentinel) && StubSentinel.TryParse(await File.ReadAllTextAsync(sentinel, cancellationToken).ConfigureAwait(false), out var key) && string.Equals(key, copy.StubKey, StringComparison.Ordinal);
                if (!ours || library!.Feeds.Any(peer => _catalog.Find(peer, copy.FolderRel) is not null))
                {
                    done.Add(copy.StubKey); // not this copy's folder any more: never deleted
                    continue;
                }

                try
                {
                    Directory.Delete(folder, recursive: true);
                    removed++;
                    changedFolders.Add(root);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _logger.LogWarning("A retired title copy could not be deleted ({Error}); the next pass tries again", ex.GetType().Name);
                    continue;
                }
            }

            done.Add(copy.StubKey);
        }

        _groups.Update(document => document.Retired.RemoveAll(c => done.Contains(c.StubKey)) > 0);
        return removed;
    }

    private static bool SetCopy(GroupsDocument document, string stubKey, Action<GroupCopy> change)
    {
        var copy = document.Groups.SelectMany(static g => g.Copies).FirstOrDefault(c => c.StubKey == stubKey);
        if (copy is null)
        {
            return false;
        }

        change(copy);
        return true;
    }
}
