using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.Authorization;
using Jellyfin.Plugin.Shoal.Security;
using Jellyfin.Plugin.Shoal.State;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Shoal.Sync;

/// <summary>
/// Lists the federated items of core's library for the enricher.
/// </summary>
public interface IStubItems
{
    /// <summary>
    /// Lists the stub items the index knows.
    /// </summary>
    /// <returns>The items; ones that core no longer has are left out.</returns>
    IReadOnlyList<StubItemView> List();

    /// <summary>
    /// Lists the movies, series and episodes the index knows, with the overview and tags core holds now (PLAN v2.9 K48). Seasons are never listed.
    /// </summary>
    /// <returns>The items; empty when the source does not support labels.</returns>
    IReadOnlyList<LabelItemView> ListLabelled() => [];

    /// <summary>
    /// Gets one movie, series or episode for the label, after core reported it updated.
    /// </summary>
    /// <param name="itemId">The local item id.</param>
    /// <returns>The item, or null when it is not one of those kinds (or the source does not support labels).</returns>
    LabelItemView? GetLabelled(Guid itemId) => null;
}

/// <summary>
/// A movie, series or episode as the label step of the enricher sees it.
/// </summary>
/// <param name="Id">The local item id.</param>
/// <param name="Path">The item's path (classified against the stub root before anything is written).</param>
/// <param name="Overview">The overview core holds now, or null.</param>
/// <param name="Tags">The tags core holds now.</param>
/// <param name="Studios">The studios core holds now (2.9.2 K6).</param>
/// <param name="Apply">Writes a new overview, tag list and studio list to the item and saves it.</param>
public sealed record LabelItemView(Guid Id, string Path, string? Overview, IReadOnlyList<string> Tags, IReadOnlyList<string> Studios, Func<string, IReadOnlyList<string>, IReadOnlyList<string>, CancellationToken, Task> Apply);

/// <summary>
/// The technical facts core shows for a stub: duration, container and size. They come from the catalog, never from the stub files, because
/// the stub has no media (DESIGN 3.4, decision 15).
/// </summary>
/// <param name="RunTimeTicks">The duration, or 0.</param>
/// <param name="Container">The container list, or null.</param>
/// <param name="Size">The size in bytes, or 0.</param>
/// <param name="Width">The width, or 0.</param>
/// <param name="Height">The height, or 0.</param>
/// <param name="TotalBitrate">The bitrate, or 0.</param>
public sealed record StubFacts(long RunTimeTicks, string? Container, long Size, int Width, int Height, int TotalBitrate);

/// <summary>
/// A stub item in core's library, as the enricher sees it.
/// </summary>
/// <param name="Id">The local item id.</param>
/// <param name="Path">The item's path.</param>
/// <param name="Title">The item's title (for the merged-version warning).</param>
/// <param name="Current">The facts core holds now.</param>
/// <param name="IsMerged">Whether the item has linked alternate versions or is an alternate of another item.</param>
/// <param name="Apply">Writes new facts to the item and saves it.</param>
/// <param name="VersionPaths">The paths of every version in the item's group (its own and its alternates'), or null when core did not say.</param>
/// <param name="HasLinkedAlternates">Whether the item has linked (manually merged) alternate versions, which are never Shoal's own grouping.</param>
public sealed record StubItemView(Guid Id, string Path, string Title, StubFacts Current, bool IsMerged, Func<StubFacts, CancellationToken, Task> Apply, IReadOnlyList<string>? VersionPaths = null, bool HasLinkedAlternates = false);

/// <summary>
/// A stub that has been merged with another version (DESIGN 6.1, W A-20.2). Core may then serve the stub's sentinel source under the other
/// item's id: safe (the sentinel leaks nothing) but broken, so the admin is told.
/// </summary>
/// <param name="ItemId">The local item id.</param>
/// <param name="Title">The title.</param>
public sealed record MergedVersionWarning(Guid ItemId, string Title);

/// <summary>
/// The result of one enrichment pass.
/// </summary>
/// <param name="Checked">How many stub items were looked at.</param>
/// <param name="Updated">How many had a value that differed and were saved.</param>
/// <param name="Merged">The stubs that are merged with another version.</param>
public sealed record EnrichmentResult(int Checked, int Updated, IReadOnlyList<MergedVersionWarning> Merged);

/// <summary>
/// Finds a stub's catalog entry from its folder name, without reading the catalog file again until it changes.
/// </summary>
public sealed class StubCatalog : ILibraryTreeOwners
{
    private readonly CatalogStore _store;
    private readonly ConcurrentDictionary<string, Cached> _cache = new(StringComparer.Ordinal);

    /// <summary>
    /// Initializes a new instance of the <see cref="StubCatalog"/> class.
    /// </summary>
    /// <param name="store">The catalog store.</param>
    public StubCatalog(CatalogStore store) => _store = store;

    /// <summary>
    /// Finds the entry of a stub.
    /// </summary>
    /// <param name="peerKey">The peer.</param>
    /// <param name="folderName">The stub's folder name (the first segment of its stored path), or for an episode its whole stored path (<see cref="Jellyfin.Plugin.Shoal.Authorization.IndexedStub.FolderName"/>).</param>
    /// <returns>The entry, or null when the catalog has none (or is unreadable).</returns>
    public CatalogEntry? Find(string peerKey, string folderName) => Load(peerKey)?.ByFolder.GetValueOrDefault(folderName);

    /// <summary>
    /// Finds a movie entry by the peer's item id, with or without a stub: a hidden member of a title group has none (PLAN v2.9 K42).
    /// </summary>
    /// <param name="peerKey">The peer.</param>
    /// <param name="peerItemId">The peer's item id (32 hex characters).</param>
    /// <returns>The entry, or null when the catalog has none (or is unreadable).</returns>
    public CatalogEntry? FindMovie(string peerKey, string peerItemId) => Load(peerKey)?.MovieByItem.GetValueOrDefault(peerItemId);

    /// <summary>
    /// Finds a server's hidden source of an episode (PLAN v2.9 K43): an episode of that server's series (by the server's series id) with these numbers, kept with no stub.
    /// </summary>
    /// <param name="peerKey">The server.</param>
    /// <param name="peerSeriesId">Its series id (32 hex).</param>
    /// <param name="season">The season.</param>
    /// <param name="episode">The episode number.</param>
    /// <returns>The entry, or null.</returns>
    public CatalogEntry? FindHiddenEpisode(string peerKey, string peerSeriesId, int season, int episode) => Load(peerKey)?.HiddenEpisodes.GetValueOrDefault((peerSeriesId, season, episode));

    /// <summary>
    /// Finds the series entry that owns a top folder of a server's library (K47).
    /// </summary>
    /// <param name="peerKey">The server.</param>
    /// <param name="topFolder">The series folder.</param>
    /// <returns>The series entry, or null.</returns>
    public CatalogEntry? FindSeriesByTop(string peerKey, string topFolder) => Load(peerKey)?.ByTop.GetValueOrDefault(topFolder) is { Kind: EntryKind.Series } series ? series : null;

    /// <inheritdoc />
    public TreeOwner? OwnerOf(ShoalState state, ShoalLibrary library, string topFolder)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(library);
        if (library.IsLegacy || string.IsNullOrEmpty(topFolder))
        {
            return null;
        }

        foreach (var peerKey in library.Feeds)
        {
            if (Load(peerKey)?.ByTop.GetValueOrDefault(topFolder) is not { } entry)
            {
                continue;
            }

            // The folder counts only when the entry's mapping lands in this very library (a connection's other type, or a stale entry, does not).
            var mapping = state.Home.Peers.FirstOrDefault(p => string.Equals(p.PeerKey, peerKey, StringComparison.Ordinal))?.Libraries.FirstOrDefault(m => string.Equals(m.MappingKey, entry.MappingKey, StringComparison.Ordinal));
            if (mapping is not null && LibraryFolders.FedBy(state, peerKey, mapping.CollectionType) is { } fed && string.Equals(fed.LibKey, library.LibKey, StringComparison.Ordinal))
            {
                return new TreeOwner(peerKey, entry.MappingKey);
            }
        }

        return null;
    }

    private Cached? Load(string peerKey)
    {
        // Pilot 14 POSTSCAN: an item whose owner is not known yet (a shared library folder no catalog records, e.g. right after a layout move) classifies with an
        // empty key; it has no catalog, which is not an error.
        if (!Jellyfin.Plugin.Shoal.Storage.ShoalPaths.IsValidPeerKey(peerKey))
        {
            return null;
        }

        var stamp = _store.Stamp(peerKey);
        if (stamp is null)
        {
            return null;
        }

        if (!_cache.TryGetValue(peerKey, out var cached) || cached.Stamp != stamp)
        {
            var load = _store.Load(peerKey);
            if (load.Unreadable)
            {
                return null;
            }

            var byFolder = new Dictionary<string, CatalogEntry>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            var byItem = new Dictionary<string, CatalogEntry>(StringComparer.Ordinal);
            var byTop = new Dictionary<string, CatalogEntry>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            var hidden = new Dictionary<(string Series, int Season, int Episode), CatalogEntry>();
            foreach (var entry in load.Document.Entries)
            {
                if (entry.Kind == EntryKind.Movie)
                {
                    byItem.TryAdd(entry.PeerItemId, entry);
                }
                else if (entry.Kind == EntryKind.Episode && entry.State == EntryState.DuplicatePeer && entry.Season is { } season && entry.Episode is { } number)
                {
                    hidden.TryAdd((entry.PeerSeriesId, season, number), entry); // K43: a hidden source of another server's episode
                }

                if (entry.StubRelPath is not null)
                {
                    // A movie is found by its folder; an episode by its whole path below the library, because every episode of a series shares the first segment.
                    byFolder[entry.Kind == EntryKind.Episode ? entry.StubRelPath! : entry.StubRelPath!.Split('/')[0]] = entry;
                    if (entry.State is EntryState.Active or EntryState.HeldRemoval)
                    {
                        byTop.TryAdd(entry.StubRelPath!.Split('/')[0], entry);
                    }
                }
                else if (entry.Kind == EntryKind.Series && entry.FolderRel is { Length: > 0 } folder && entry.State is EntryState.Active or EntryState.HeldRemoval)
                {
                    byTop[folder.Split('/')[0]] = entry; // a series owns its folder (its episodes are below it)
                }
            }

            cached = new Cached(stamp, byFolder, byItem, byTop, hidden);
            _cache[peerKey] = cached;
        }

        return cached;
    }

    private sealed record Cached((long Ticks, long Length)? Stamp, Dictionary<string, CatalogEntry> ByFolder, Dictionary<string, CatalogEntry> MovieByItem, Dictionary<string, CatalogEntry> ByTop, Dictionary<(string Series, int Season, int Episode), CatalogEntry> HiddenEpisodes);
}

/// <summary>
/// After a scan, writes the duration, container, size, dimensions and bitrate from the catalog onto the stub items, and only when a value
/// differs (DESIGN 17 in the component table). Core takes the runtime from the media file and ignores the NFO's, so a stub has none until this
/// runs, and a forced refresh drops it again; the next scan puts it back. It never writes stream titles or languages (W A-12), and it also
/// notices stubs that were merged with another version.
/// </summary>
public sealed class StubEnricher
{
    private readonly ShoalIndex _index;
    private readonly IStubItems _items;
    private readonly StubCatalog _catalog;
    private readonly IStateStore _state;
    private readonly ILogger _logger;
    private IReadOnlyList<MergedVersionWarning> _merged = [];

    /// <summary>
    /// Initializes a new instance of the <see cref="StubEnricher"/> class.
    /// </summary>
    /// <param name="index">The index.</param>
    /// <param name="items">The stub items.</param>
    /// <param name="catalog">The catalog lookup.</param>
    /// <param name="state">The state store.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="groups">The title groups (each copy's shoal-alt tags, K45), or null.</param>
    public StubEnricher(ShoalIndex index, IStubItems items, StubCatalog catalog, IStateStore state, ILogger<StubEnricher> logger, GroupBook? groups = null)
        : this(index, items, catalog, state, (ILogger)logger)
    {
        Groups = groups;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="StubEnricher"/> class (tests: any logger).
    /// </summary>
    /// <param name="index">The index.</param>
    /// <param name="items">The stub items.</param>
    /// <param name="catalog">The catalog lookup.</param>
    /// <param name="state">The state store.</param>
    /// <param name="logger">The logger.</param>
    internal StubEnricher(ShoalIndex index, IStubItems items, StubCatalog catalog, IStateStore state, ILogger logger)
    {
        _index = index;
        _items = items;
        _catalog = catalog;
        _state = state;
        _logger = logger;
    }

    /// <summary>Gets the merged stubs found by the last pass (the admin screens show them as warnings).</summary>
    public IReadOnlyList<MergedVersionWarning> MergedVersions => _merged;

    /// <summary>Gets or sets the title groups: with them, extra copies are classified and every copy keeps exactly its shoal-alt tags (K45).</summary>
    internal GroupBook? Groups { get; set; }

    /// <summary>
    /// The facts a catalog entry says a stub should have.
    /// </summary>
    /// <param name="entry">The entry.</param>
    /// <returns>The facts.</returns>
    public static StubFacts FactsOf(CatalogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return new StubFacts(entry.RuntimeTicks, entry.Container, entry.SizeBytes, entry.Width, entry.Height, entry.Bitrate);
    }

    /// <summary>
    /// Runs one pass over every stub item.
    /// </summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>What was looked at and changed.</returns>
    public async Task<EnrichmentResult> RunAsync(CancellationToken cancellationToken = default)
    {
        var merged = new List<MergedVersionWarning>();
        var seen = 0;
        var updated = 0;
        if (_state.Status != StateStatus.Ready)
        {
            _merged = merged;
            return new EnrichmentResult(0, 0, merged);
        }

        var state = _state.Current;
        foreach (var item in _items.List())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ShoalIndex.Classify(state, item.Path, _catalog) is not { } stub)
            {
                continue; // not (or no longer) below a mapped library
            }

            seen++;
            var entry = _catalog.Find(stub.PeerKey, stub.FolderName);
            if (item.IsMerged && !IsCatalogSet(item, entry))
            {
                merged.Add(new MergedVersionWarning(item.Id, item.Title));
            }

            if (entry is null || entry.State is not (EntryState.Active or EntryState.HeldRemoval))
            {
                continue;
            }

            var wanted = Wanted(entry, FactsOfStub(entry, stub), item.Current);
            if (wanted == item.Current)
            {
                continue;
            }

            try
            {
                await item.Apply(wanted, cancellationToken).ConfigureAwait(false);
                updated++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
            {
                _logger.LogWarning("Shoal could not save the facts of a stub item ({Error})", ex.GetType().Name);
            }
        }

        if (merged.Count > 0)
        {
            _logger.LogWarning("{Count} Shoal titles are merged with another version; unmerge them in Jellyfin", merged.Count);
        }

        _merged = merged;
        return new EnrichmentResult(seen, updated, merged);
    }

    /// <summary>
    /// Puts the friend label back on every movie, series and episode below the stub root and re-asserts the <c>Shoal</c> tags (PLAN v2.9 K48) and the friend
    /// studios (2.9.2 K6: a grouped title names every member). A remote
    /// metadata provider or a forced refresh can replace the overview or the tags core read from the NFO; this runs after each scan. Only items whose
    /// path classifies below a mapped stub folder are touched, and an item that already holds the wanted text and tags is not saved (no-op guard).
    /// </summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>How many items were saved.</returns>
    public async Task<int> ApplyLabelsAsync(CancellationToken cancellationToken = default)
    {
        if (_state.Status != StateStatus.Ready)
        {
            return 0;
        }

        var state = _state.Current;
        var saved = 0;
        foreach (var item in _items.ListLabelled())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await ApplyLabelAsync(state, item, cancellationToken).ConfigureAwait(false))
            {
                saved++;
            }
        }

        return saved;
    }

    /// <summary>
    /// Puts the friend label back on one item after core reported it updated. Saving it raises the event again; the no-op guard ends that loop.
    /// </summary>
    /// <param name="itemId">The local item id.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>True when the item was saved.</returns>
    public async Task<bool> ReapplyLabelAsync(Guid itemId, CancellationToken cancellationToken = default)
    {
        if (_state.Status != StateStatus.Ready || !_index.Contains(itemId) || _items.GetLabelled(itemId) is not { } item)
        {
            return false;
        }

        return await ApplyLabelAsync(_state.Current, item, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The tags a Shoal item must carry: what it has, plus <c>Shoal</c> and <c>Shoal: &lt;Peer&gt;</c> when missing (stock <c>BlockedTags</c> relies on them).
    /// </summary>
    /// <param name="current">The tags core holds.</param>
    /// <param name="peerDisplayName">The peer's display name, or empty.</param>
    /// <returns>The tag list; the same instance when nothing is missing.</returns>
    public static IReadOnlyList<string> WantedTags(IReadOnlyList<string> current, string peerDisplayName) => WantedTags(current, peerDisplayName, null);

    /// <summary>
    /// The tags a Shoal item must carry, and for an item in a shared library folder (K44/K45) exactly that library's <c>shoal-lib-</c> tag: a tag of another library
    /// (left from before a move) is dropped, the library's own is added. Other tags, <c>shoal-alt-</c> ones included, stay.
    /// </summary>
    /// <param name="current">The tags core holds.</param>
    /// <param name="peerDisplayName">The peer's display name, or empty.</param>
    /// <param name="libKey">The shared library the item's file is in, or null for a legacy folder.</param>
    /// <param name="altTags">The item's copy's exact <c>shoal-alt-</c> tags (other ones are dropped), or null to leave them as they are.</param>
    /// <param name="parkTag">K47: the item's exact park tag (another one is dropped), or null to leave them as they are.</param>
    /// <returns>The tag list; the same instance when nothing changes.</returns>
    public static IReadOnlyList<string> WantedTags(IReadOnlyList<string> current, string peerDisplayName, string? libKey, IReadOnlyList<string>? altTags = null, string? parkTag = null)
    {
        ArgumentNullException.ThrowIfNull(current);
        var peer = MetadataSanitizer.DisplayName(peerDisplayName);
        var wanted = peer.Length > 0 ? new List<string> { "Shoal", "Shoal: " + peer } : ["Shoal"];
        var kept = current;
        if (!string.IsNullOrEmpty(libKey))
        {
            var own = LibraryFolders.LibTag(libKey);
            wanted.Add(own);
            if (kept.Any(t => t.StartsWith(LibraryFolders.LibTagPrefix, StringComparison.Ordinal) && !string.Equals(t, own, StringComparison.Ordinal)))
            {
                kept = [.. kept.Where(t => !t.StartsWith(LibraryFolders.LibTagPrefix, StringComparison.Ordinal) || string.Equals(t, own, StringComparison.Ordinal))];
            }
        }

        if (altTags is not null)
        {
            wanted.AddRange(altTags);
            if (kept.Any(t => t.StartsWith(LibraryFolders.AltTagPrefix, StringComparison.Ordinal) && !altTags.Contains(t, StringComparer.Ordinal)))
            {
                kept = [.. kept.Where(t => !t.StartsWith(LibraryFolders.AltTagPrefix, StringComparison.Ordinal) || altTags.Contains(t, StringComparer.Ordinal))];
            }
        }

        if (parkTag is not null)
        {
            wanted.Add(parkTag);
            if (kept.Any(t => t.StartsWith(LibraryFolders.ParkTagPrefix, StringComparison.Ordinal) && !string.Equals(t, parkTag, StringComparison.Ordinal)))
            {
                kept = [.. kept.Where(t => !t.StartsWith(LibraryFolders.ParkTagPrefix, StringComparison.Ordinal) || string.Equals(t, parkTag, StringComparison.Ordinal))];
            }
        }

        var missing = wanted.Where(w => !kept.Contains(w, StringComparer.Ordinal)).ToList();
        return missing.Count == 0 ? kept : [.. kept, .. missing];
    }

    /// <summary>
    /// The facts of one stub: a title with several versions has one stub per version, and each is enriched from its own source (DESIGN v2.6.4).
    /// </summary>
    /// <param name="entry">The entry.</param>
    /// <param name="stub">The stub as the index classified it (its file name picks the version).</param>
    /// <returns>The facts of that version, or of the entry when it is the primary or the file is not a known version.</returns>
    public static StubFacts FactsOfStub(CatalogEntry entry, IndexedStub stub)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(stub);
        var source = stub.FileName.Length == 0 ? null : entry.Sources.Skip(1).FirstOrDefault(s => s.StubRelPath is not null && string.Equals(s.StubRelPath.Split('/')[^1], stub.FileName, StringComparison.Ordinal));
        return source is null ? FactsOf(entry) : new StubFacts(source.RuntimeTicks > 0 ? source.RuntimeTicks : entry.RuntimeTicks, source.Container, source.SizeBytes, source.Width, source.Height, source.Bitrate);
    }

    /// <summary>
    /// Whether a merged item is exactly the version set Shoal wrote for the title: no linked (manual) alternates and the same files as the catalog's
    /// sources. Any other merge, in particular one that links a non-Shoal item, is still reported.
    /// </summary>
    private static bool IsCatalogSet(StubItemView item, CatalogEntry? entry)
    {
        if (entry is null || item.HasLinkedAlternates || item.VersionPaths is not { Count: > 1 } paths)
        {
            return false;
        }

        static string Pair(string path)
        {
            var parts = path.Replace('\\', '/').Split('/');
            return parts.Length >= 2 ? parts[^2] + "/" + parts[^1] : path;
        }

        var actual = paths.Select(Pair).ToHashSet(StringComparer.Ordinal);
        var expected = entry.Sources.Where(static s => s.StubRelPath is not null).Select(static s => s.StubRelPath!).ToHashSet(StringComparer.Ordinal);
        return expected.Count > 1 && actual.SetEquals(expected);
    }

    /// <summary>
    /// The facts to write: the catalog's values where it has one, what the item already holds where it has none.
    /// </summary>
    private static StubFacts Wanted(CatalogEntry entry, StubFacts facts, StubFacts current)
    {
        return new StubFacts(
            facts.RunTimeTicks > 0 ? facts.RunTimeTicks : current.RunTimeTicks,
            facts.Container ?? current.Container,
            facts.Size > 0 ? facts.Size : current.Size,
            facts.Width > 0 ? facts.Width : current.Width,
            facts.Height > 0 ? facts.Height : current.Height,
            facts.TotalBitrate > 0 ? facts.TotalBitrate : current.TotalBitrate);
    }

    // K47: the park tag of an item: a title group's copy (a grouped movie, an extra copy, a grouped series and its episodes) carries its group's, so it hides only
    // when every server of the group is parked; any other stub carries its server's.
    private static string ParkTagOf(TitleGroup? group, IndexedStub stub) => group is not null ? LibraryFolders.ParkGroupTag(group.GroupKey) : LibraryFolders.ParkPeerTag(stub.PeerKey);

    // The title group an item belongs to (a grouped movie, an extra copy, a grouped series and its episodes), or null for a title on one server.
    private TitleGroup? GroupOf(IndexedStub stub, string itemPath)
    {
        if (Groups is null)
        {
            return null;
        }

        if (stub.Kind == IndexKind.Container)
        {
            return _catalog.FindSeriesByTop(stub.PeerKey, stub.FolderName.Split('/')[0]) is { } series && Groups.ByCopy(series.StubKey) is { Kind: GroupKind.Series } g ? g : null;
        }

        if (stub.FolderName.Contains('/', StringComparison.Ordinal))
        {
            return _catalog.Find(stub.PeerKey, stub.FolderName) is { } episode && Groups.ByCopy(episode.ParentKey) is { Kind: GroupKind.Series } g ? g : null;
        }

        if (stub.IsCopy)
        {
            return Groups.ExtraCopyAt(stub.LibKey, Path.GetFileName(Path.GetDirectoryName(itemPath) ?? string.Empty))?.Group;
        }

        return Groups.ByCopy(_catalog.Find(stub.PeerKey, stub.FolderName)?.StubKey) is { Kind: GroupKind.Movie } movie ? movie : null;
    }

    /// <summary>
    /// The friends an item names (2.9.2 K6): every member of its title group in play priority (tier, then the admin's priority number, then the group's
    /// own order), or the item's own server. A member offline still counts (the label says who hosts it, not who is up); one no longer paired does not.
    /// Warden 18 M1: a member is named only when the library its copy is in admits at least every user this item's library admits, so a user who can see
    /// this copy never learns of a friend whose library is closed to them. When this item's library is not known, only its own server is named (the tag's fact).
    /// </summary>
    /// <param name="state">The state.</param>
    /// <param name="group">The item's group, or null.</param>
    /// <param name="peer">The item's own server, or null.</param>
    /// <param name="stub">The item as the index classified it.</param>
    /// <returns>The shown names.</returns>
    private static IReadOnlyList<string> NamesOf(ShoalState state, TitleGroup? group, PeerState? peer, IndexedStub stub)
    {
        IReadOnlyList<string> own = peer is null ? [] : [peer.ShownName()];
        if (group is null || group.Members.Count == 0)
        {
            return own;
        }

        var type = group.Kind == GroupKind.Series ? "tvshows" : "movies";
        if (ReachesOf(state, stub.PeerKey, stub.LibKey, stub.MappingKey, type) is not [var item])
        {
            return own;
        }

        IReadOnlyList<string> names = [.. group.Members
            .Select((m, index) => (Member: m, Peer: state.Home.Peers.FirstOrDefault(p => string.Equals(p.PeerKey, m.PeerKey, StringComparison.Ordinal)), Index: index))
            .Where(x => x.Peer is not null && ReachesOf(state, x.Member.PeerKey, x.Member.LibKey, null, type) is { Count: > 0 } reaches && reaches.All(r => Covers(r, item)))
            .OrderBy(static x => x.Peer!.PriorityTier switch { PriorityTier.Prefer => 0, PriorityTier.Normal => 1, _ => 2 })
            .ThenBy(static x => x.Peer!.Priority)
            .ThenBy(static x => x.Index)
            .Select(static x => x.Peer!.ShownName())];
        return names.Count > 0 ? names : own;
    }

    // M1: the libraries a server's copy of a title can be in, with who may open them. A known library key gives that library; with none (a legacy folder, or a
    // member recorded without one) every mapping of the server for the type counts (or the item's own mapping), so an unknown copy is judged by its narrowest.
    private static IReadOnlyList<Reach> ReachesOf(ShoalState state, string peerKey, string libKey, string? mappingKey, string type)
    {
        if (libKey.Length > 0)
        {
            return state.Home.Libraries.FirstOrDefault(l => string.Equals(l.LibKey, libKey, StringComparison.Ordinal)) is { } library
                ? [new Reach(library.LibKey, LibraryFolders.AccessOf(state, library), library)]
                : [];
        }

        if (state.Home.Peers.FirstOrDefault(p => string.Equals(p.PeerKey, peerKey, StringComparison.Ordinal)) is not { } peer)
        {
            return [];
        }

        return [.. peer.Libraries
            .Where(m => mappingKey is not null ? string.Equals(m.MappingKey, mappingKey, StringComparison.Ordinal) : string.Equals(m.CollectionType, type, StringComparison.Ordinal))
            .Select(m => (Mapping: m, Library: LibraryFolders.LibraryOf(state, peerKey, m)))
            .Select(x => new Reach(x.Library?.LibKey ?? string.Empty, LibraryFolders.AccessOfMapping(state, peerKey, x.Mapping), x.Library))];
    }

    // M1: whether every user who may open the narrow library may also open the wide one. The same library always does. A main-library placement adds core's own
    // access to that main library, so it covers only a copy added into the same main library. "Everyone except restricted profiles" never covers a selection
    // (the selection may hold a restricted profile and the enricher does not read accounts).
    private static bool Covers(Reach wide, Reach narrow)
    {
        if (wide.LibKey.Length > 0 && string.Equals(wide.LibKey, narrow.LibKey, StringComparison.Ordinal))
        {
            return true;
        }

        if (wide.Library?.Placement == LibraryPlacement.Main
            && !(narrow.Library is { Placement: LibraryPlacement.Main, JellyfinLibraryId: { } main } && wide.Library.JellyfinLibraryId == main))
        {
            return false;
        }

        var w = wide.Access;
        var n = narrow.Access;
        if (w.Mode == AccessMode.Everyone)
        {
            return w.IncludeRestricted || (n.Mode == AccessMode.Everyone && !n.IncludeRestricted);
        }

        return n.Mode == AccessMode.Selected && n.SelectedUserIds.All(w.SelectedUserIds.Contains);
    }

    private async Task<bool> ApplyLabelAsync(ShoalState state, LabelItemView item, CancellationToken cancellationToken)
    {
        var owners = Groups is null ? (ILibraryTreeOwners)_catalog : new LibraryTreeOwners(_catalog, Groups);
        if (ShoalIndex.Classify(state, item.Path, owners) is not { } stub)
        {
            return false; // not below a mapped stub folder: never touched
        }

        var peer = state.Home.Peers.FirstOrDefault(p => string.Equals(p.PeerKey, stub.PeerKey, StringComparison.Ordinal));
        var group = GroupOf(stub, item.Path);
        var names = NamesOf(state, group, peer, stub); // K6: a grouped title names its members (M1: those at least as visible); the NFO names only its own friend
        var overview = FriendLabel.Apply(item.Overview, names);

        // K6: the studios are exactly one "Shared by {name}" per friend (a refresh or a provider may have replaced them); with no known friend they stay.
        var wantedStudios = FriendLabel.Studios(names);
        var studios = wantedStudios.Count == 0 || wantedStudios.SequenceEqual(item.Studios, StringComparer.Ordinal) ? item.Studios : wantedStudios;

        // K45: a movie's copy keeps exactly its shoal-alt tags (one per higher-ordered library that also holds the title); a title with no group has none.
        IReadOnlyList<string>? alt = null;
        if (Groups is not null && stub.Kind == IndexKind.Playable && !stub.FolderName.Contains('/', StringComparison.Ordinal))
        {
            var folder = stub.IsCopy ? Path.GetFileName(Path.GetDirectoryName(item.Path) ?? string.Empty) : stub.FolderName;
            alt = Groups.AltTagsOf(stub.LibKey, folder, stub.OwnerUnknown ? null : _catalog.Find(stub.PeerKey, stub.FolderName)?.StubKey);
        }

        var tags = WantedTags(item.Tags, peer?.DisplayName ?? string.Empty, stub.LibKey.Length > 0 ? stub.LibKey : null, alt, stub.OwnerUnknown ? null : ParkTagOf(group, stub));
        if (string.Equals(overview, item.Overview, StringComparison.Ordinal) && ReferenceEquals(tags, item.Tags) && ReferenceEquals(studios, item.Studios))
        {
            return false; // no-op guard: nothing to save, which also ends the ItemUpdated loop
        }

        try
        {
            await item.Apply(overview, tags, studios, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            _logger.LogWarning("Shoal could not put the label on a stub item ({Error})", ex.GetType().Name);
            return false;
        }
    }

    // M1: one library a copy can be in (its key, empty when not known), who may open it, and the library itself when the state has it.
    private sealed record Reach(string LibKey, LibraryAccess Access, ShoalLibrary? Library);
}

/// <summary>
/// <see cref="IStubItems"/> from the library manager.
/// </summary>
public sealed class JellyfinStubItems : IStubItems
{
    private readonly ILibraryManager _library;
    private readonly ShoalIndex _index;

    /// <summary>
    /// Initializes a new instance of the <see cref="JellyfinStubItems"/> class.
    /// </summary>
    /// <param name="library">The library manager.</param>
    /// <param name="index">The index.</param>
    public JellyfinStubItems(ILibraryManager library, ShoalIndex index)
    {
        _library = library;
        _index = index;
    }

    /// <inheritdoc />
    public IReadOnlyList<StubItemView> List()
    {
        var views = new List<StubItemView>();
        foreach (var id in _index.Ids())
        {
#pragma warning disable RS0030 // Allowed here only: classification and enrichment of Shoal's own stub items, never authorization (DESIGN 8.8).
            var item = _library.GetItemById(id);
#pragma warning restore RS0030
            if (item is not Video video || video.Path is null)
            {
                continue;
            }

            var current = new StubFacts(video.RunTimeTicks ?? 0, video.Container, video.Size ?? 0, video.Width, video.Height, video.TotalBitrate ?? 0);
            var merged = video.PrimaryVersionId is not null || video.HasLocalAlternateVersions || video.LinkedAlternateVersions.Length > 0;
            views.Add(new StubItemView(id, video.Path, video.Name ?? string.Empty, current, merged, (facts, ct) => ApplyAsync(video, facts, ct), merged ? VersionPathsOf(video) : null, video.LinkedAlternateVersions.Length > 0));
        }

        return views;
    }

    /// <inheritdoc />
    public IReadOnlyList<LabelItemView> ListLabelled()
    {
        var views = new List<LabelItemView>();
        foreach (var id in _index.Ids())
        {
            if (GetLabelled(id) is { } view)
            {
                views.Add(view);
            }
        }

        return views;
    }

    /// <inheritdoc />
    public LabelItemView? GetLabelled(Guid itemId)
    {
#pragma warning disable RS0030 // Allowed here only: the label of Shoal's own stub items, never authorization (DESIGN 8.8).
        var item = _library.GetItemById(itemId);
#pragma warning restore RS0030
        if (item is not (MediaBrowser.Controller.Entities.Movies.Movie or MediaBrowser.Controller.Entities.TV.Episode or MediaBrowser.Controller.Entities.TV.Series) || item.Path is null)
        {
            return null; // seasons and anything else carry no label
        }

        return new LabelItemView(item.Id, item.Path, item.Overview, item.Tags ?? [], item.Studios ?? [], (overview, tags, studios, ct) => ApplyLabelAsync(item, overview, tags, studios, ct));
    }

    // The paths of the group the item belongs to: the primary's own and its local alternates. An alternate asks its primary.
    private IReadOnlyList<string> VersionPathsOf(Video video)
    {
        var primary = video;
        if (video.PrimaryVersionId is not null && Guid.TryParse(Convert.ToString(video.PrimaryVersionId, System.Globalization.CultureInfo.InvariantCulture), out var primaryId))
        {
#pragma warning disable RS0030 // Allowed here only: classification of Shoal's own stub items, never authorization (DESIGN 8.8).
            primary = _library.GetItemById(primaryId) as Video ?? video;
#pragma warning restore RS0030
        }

        return [.. new[] { primary.Path }.Concat(primary.LocalAlternateVersions ?? []).Where(static p => !string.IsNullOrEmpty(p))];
    }

    // 2.9.2 K6: BaseItem.Studios is a settable string[] persisted by UpdateToRepositoryAsync (scout G6, decompiled 12.1.0). A live write is still to be
    // confirmed in the lab.
    private static async Task ApplyLabelAsync(BaseItem item, string overview, IReadOnlyList<string> tags, IReadOnlyList<string> studios, CancellationToken cancellationToken)
    {
        item.Overview = overview;
        item.Tags = [.. tags];
        item.Studios = [.. studios];
        await item.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
    }

    private static async Task ApplyAsync(Video video, StubFacts facts, CancellationToken cancellationToken)
    {
        video.RunTimeTicks = facts.RunTimeTicks > 0 ? facts.RunTimeTicks : null;
        video.Container = facts.Container;
        video.Size = facts.Size > 0 ? facts.Size : null;
        video.Width = facts.Width;
        video.Height = facts.Height;
        video.TotalBitrate = facts.TotalBitrate > 0 ? facts.TotalBitrate : null;
        await video.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// After every library scan: rebuild the index, then put the catalog's facts on the stub items (the scan has just reset them).
/// </summary>
public sealed class ShoalPostScan : ILibraryPostScanTask
{
    private readonly ShoalIndex _index;
    private readonly StubEnricher _enricher;
    private readonly Jellyfin.Plugin.Shoal.Visibility.LibraryOptionsGuard? _options;

    /// <summary>
    /// Initializes a new instance of the <see cref="ShoalPostScan"/> class.
    /// </summary>
    /// <param name="index">The index.</param>
    /// <param name="enricher">The enricher.</param>
    /// <param name="options">Locks the options of Shoal's libraries again after each scan (Pilot 14 LBL3), or null.</param>
    public ShoalPostScan(ShoalIndex index, StubEnricher enricher, Jellyfin.Plugin.Shoal.Visibility.LibraryOptionsGuard? options = null)
    {
        _options = options;
        _index = index;
        _enricher = enricher;
    }

    /// <inheritdoc />
    public async Task Run(IProgress<double> progress, CancellationToken cancellationToken)
    {
        await _index.RebuildAsync(cancellationToken).ConfigureAwait(false);
        progress?.Report(50);
        await _enricher.RunAsync(cancellationToken).ConfigureAwait(false);
        progress?.Report(90);
        await _enricher.ApplyLabelsAsync(cancellationToken).ConfigureAwait(false);
        _options?.Enforce();
        progress?.Report(100);
    }
}

/// <summary>
/// Puts the friend label (and since 2.9.2 K6 the friend studios) back when core updates a Shoal item (a metadata refresh can replace them), PLAN v2.9 K48. Only items in the index are
/// looked at. Warden 10 L1: updates go through one bounded queue keyed by item (an item already waiting is not queued twice, a full queue drops the update: the
/// post-scan pass puts every label back anyway), one worker saves at a time, and the update the guard's own save raises is ignored (re-entrancy mark).
/// </summary>
public sealed class FriendLabelGuard : IHostedService, IDisposable
{
    /// <summary>The most items waiting at once.</summary>
    public const int MaxQueued = 1024;

    private readonly ILibraryManager _library;
    private readonly ShoalIndex _index;
    private readonly Func<Guid, CancellationToken, Task<bool>> _reapply;
    private readonly ILogger _logger;
    private readonly System.Threading.Channels.Channel<Guid> _queue = System.Threading.Channels.Channel.CreateBounded<Guid>(new System.Threading.Channels.BoundedChannelOptions(MaxQueued) { FullMode = System.Threading.Channels.BoundedChannelFullMode.Wait, SingleReader = true });
    private readonly ConcurrentDictionary<Guid, byte> _waiting = new();
    private readonly ConcurrentDictionary<Guid, byte> _saving = new();
    private readonly CancellationTokenSource _stop = new();
    private Task _worker = Task.CompletedTask;
    private bool _started;

    /// <summary>
    /// Initializes a new instance of the <see cref="FriendLabelGuard"/> class.
    /// </summary>
    /// <param name="library">The library manager.</param>
    /// <param name="index">The index.</param>
    /// <param name="enricher">The enricher.</param>
    /// <param name="logger">The logger.</param>
    public FriendLabelGuard(ILibraryManager library, ShoalIndex index, StubEnricher enricher, ILogger<FriendLabelGuard> logger)
        : this(library, index, enricher.ReapplyLabelAsync, logger)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="FriendLabelGuard"/> class (tests: any re-apply).
    /// </summary>
    /// <param name="library">The library manager.</param>
    /// <param name="index">The index.</param>
    /// <param name="reapply">Puts the label back on one item.</param>
    /// <param name="logger">The logger.</param>
    internal FriendLabelGuard(ILibraryManager library, ShoalIndex index, Func<Guid, CancellationToken, Task<bool>> reapply, ILogger logger)
    {
        _library = library;
        _index = index;
        _reapply = reapply;
        _logger = logger;
    }

    /// <summary>Gets how many items wait now (tests).</summary>
    internal int Waiting => _waiting.Count;

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_started)
        {
            _library.ItemUpdated += OnItemUpdated;
            _worker = Task.Run(() => WorkAsync(_stop.Token), CancellationToken.None);
            _started = true;
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        Dispose();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_started)
        {
            _library.ItemUpdated -= OnItemUpdated;
            _queue.Writer.TryComplete();
            _stop.Cancel();
            _started = false;
        }

        _stop.Dispose();
    }

    /// <summary>
    /// Queues one item (the event handler's work, also used by tests).
    /// </summary>
    /// <param name="itemId">The item.</param>
    /// <returns>True when it was queued; false when it already waits, is being saved by the guard itself, or the queue is full.</returns>
    internal bool Enqueue(Guid itemId)
    {
        if (_saving.ContainsKey(itemId) || !_waiting.TryAdd(itemId, 0))
        {
            return false;
        }

        if (_queue.Writer.TryWrite(itemId))
        {
            return true;
        }

        _waiting.TryRemove(itemId, out _);
        return false;
    }

    /// <summary>
    /// Runs the worker until the queue is empty (tests).
    /// </summary>
    /// <returns>A task.</returns>
    internal async Task DrainAsync()
    {
        while (_queue.Reader.TryRead(out var id))
        {
            await ProcessAsync(id, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private void OnItemUpdated(object? sender, ItemChangeEventArgs e)
    {
        if (e.Item is not { } item || !_index.Contains(item.Id))
        {
            return; // not a Shoal item: nothing to do, and no lookup
        }

        Enqueue(item.Id);
    }

    private async Task WorkAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var id in _queue.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                await ProcessAsync(id, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
    }

    private async Task ProcessAsync(Guid id, CancellationToken cancellationToken)
    {
        _waiting.TryRemove(id, out _);
        _saving[id] = 0; // the update the save below raises is the guard's own
        try
        {
            await _reapply(id, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or OperationCanceledException))
        {
            _logger.LogWarning("Shoal could not put the label back on an updated item ({Error})", ex.GetType().Name);
        }
        finally
        {
            _saving.TryRemove(id, out _);
        }
    }
}
