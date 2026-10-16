using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.Authorization;
using Jellyfin.Plugin.Shoal.Security;
using Jellyfin.Plugin.Shoal.State;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
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
}

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
public sealed class StubCatalog
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
    public CatalogEntry? Find(string peerKey, string folderName)
    {
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
            foreach (var entry in load.Document.Entries.Where(static e => e.StubRelPath is not null))
            {
                // A movie is found by its folder; an episode by its whole path below the library, because every episode of a series shares the first segment.
                byFolder[entry.Kind == EntryKind.Episode ? entry.StubRelPath! : entry.StubRelPath!.Split('/')[0]] = entry;
            }

            cached = new Cached(stamp, byFolder);
            _cache[peerKey] = cached;
        }

        return cached.ByFolder.GetValueOrDefault(folderName);
    }

    private sealed record Cached((long Ticks, long Length)? Stamp, Dictionary<string, CatalogEntry> ByFolder);
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
    public StubEnricher(ShoalIndex index, IStubItems items, StubCatalog catalog, IStateStore state, ILogger<StubEnricher> logger)
        : this(index, items, catalog, state, (ILogger)logger)
    {
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
            if (ShoalIndex.Classify(state, item.Path) is not { } stub)
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

    /// <summary>
    /// Initializes a new instance of the <see cref="ShoalPostScan"/> class.
    /// </summary>
    /// <param name="index">The index.</param>
    /// <param name="enricher">The enricher.</param>
    public ShoalPostScan(ShoalIndex index, StubEnricher enricher)
    {
        _index = index;
        _enricher = enricher;
    }

    /// <inheritdoc />
    public async Task Run(IProgress<double> progress, CancellationToken cancellationToken)
    {
        await _index.RebuildAsync(cancellationToken).ConfigureAwait(false);
        progress?.Report(50);
        await _enricher.RunAsync(cancellationToken).ConfigureAwait(false);
        progress?.Report(100);
    }
}
