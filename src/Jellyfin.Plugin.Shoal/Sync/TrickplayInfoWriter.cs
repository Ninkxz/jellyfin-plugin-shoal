using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Shoal.Authorization;
using Jellyfin.Plugin.Shoal.Playback;
using Jellyfin.Plugin.Shoal.State;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Trickplay;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Shoal.Sync;

/// <summary>
/// The trickplay levels of a catalog entry (DESIGN v2.6.5): which levels a source has, whether a tile index exists, and a signature that changes when
/// the Peer regenerates its tiles.
/// </summary>
public static class TrickplayLevels
{
    /// <summary>
    /// The levels of the entry's source that the Peer calls <paramref name="peerSourceId"/>.
    /// </summary>
    /// <param name="entry">The catalog entry.</param>
    /// <param name="peerSourceId">The Peer's media source id.</param>
    /// <returns>The levels, possibly none.</returns>
    public static IReadOnlyList<TrickplayLevel> For(CatalogEntry entry, Guid peerSourceId)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var wanted = peerSourceId.ToString("N");
        var source = entry.Sources.FirstOrDefault(s => string.Equals(s.PeerMediaSourceId, wanted, StringComparison.Ordinal))
            ?? (entry.Sources.Count > 0 && peerSourceId == Guid.Empty ? entry.Sources[0] : null);
        return source is null ? [] : [.. source.Trickplay.Where(static l => l.IsValid()).Take(TrickplayLevel.MaxPerSource)];
    }

    /// <summary>
    /// Finds the level of a width.
    /// </summary>
    /// <param name="entry">The catalog entry.</param>
    /// <param name="peerSourceId">The Peer's media source id.</param>
    /// <param name="width">The thumbnail width of the request.</param>
    /// <param name="level">The level.</param>
    /// <returns>True when the source has that width.</returns>
    public static bool TryFind(CatalogEntry entry, Guid peerSourceId, int width, out TrickplayLevel level)
    {
        level = For(entry, peerSourceId).FirstOrDefault(l => l.Width == width)!;
        return level is not null;
    }

    /// <summary>
    /// The number of tile images of a level: <c>ceil(thumbnailCount / (tileWidth * tileHeight))</c>.
    /// </summary>
    /// <param name="level">The level.</param>
    /// <returns>The count; tile indexes are <c>0..count-1</c>.</returns>
    public static int TileCount(TrickplayLevel level)
    {
        ArgumentNullException.ThrowIfNull(level);
        var perTile = Math.Max(1, level.TileWidth * level.TileHeight);
        return (level.ThumbnailCount + perTile - 1) / perTile;
    }

    /// <summary>
    /// A text that changes whenever the levels do.
    /// </summary>
    /// <param name="levels">The levels.</param>
    /// <returns>The signature (empty for none).</returns>
    public static string Signature(IReadOnlyList<TrickplayLevel> levels)
    {
        ArgumentNullException.ThrowIfNull(levels);
        return string.Join(
            ';',
            levels.OrderBy(static l => l.Width).Select(static l => string.Create(CultureInfo.InvariantCulture, $"{l.Width},{l.Height},{l.TileWidth},{l.TileHeight},{l.ThumbnailCount},{l.IntervalMs}")));
    }
}

/// <summary>
/// After every library scan: save the Peer's trickplay levels as the stub items' own <c>TrickplayInfo</c>, so core advertises them in
/// <c>BaseItemDto.Trickplay</c> and clients ask for tiles (DESIGN v2.6.5). When the switch is off, or the Peer has none, the item's info is removed.
/// Only the rows are written: the tile images are fetched from the Peer when a client scrubs. It also drops cached tiles of stubs that no longer exist.
/// </summary>
public sealed class TrickplayInfoWriter : ILibraryPostScanTask
{
    private readonly IStubItems _items;
    private readonly StubCatalog _catalog;
    private readonly IVersionResolver _resolver;
    private readonly ITrickplayManager _trickplay;
    private readonly TrickplayCache _cache;
    private readonly IStateStore _state;
    private readonly Func<bool> _enabled;
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="TrickplayInfoWriter"/> class.
    /// </summary>
    /// <param name="items">The stub items.</param>
    /// <param name="catalog">The catalogs.</param>
    /// <param name="resolver">Maps a stub item to its version.</param>
    /// <param name="trickplay">Core's trickplay manager.</param>
    /// <param name="cache">The tile cache.</param>
    /// <param name="state">The state store.</param>
    /// <param name="logger">The logger.</param>
    public TrickplayInfoWriter(IStubItems items, StubCatalog catalog, IVersionResolver resolver, ITrickplayManager trickplay, TrickplayCache cache, IStateStore state, ILogger<TrickplayInfoWriter> logger)
        : this(items, catalog, resolver, trickplay, cache, state, static () => Plugin.Instance?.Configuration.TrickplayEnabled ?? true, logger)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="TrickplayInfoWriter"/> class (tests: the switch and any logger).</summary>
    /// <param name="items">The stub items.</param>
    /// <param name="catalog">The catalogs.</param>
    /// <param name="resolver">Maps a stub item to its version.</param>
    /// <param name="trickplay">Core's trickplay manager.</param>
    /// <param name="cache">The tile cache.</param>
    /// <param name="state">The state store.</param>
    /// <param name="enabled">Reads the switch.</param>
    /// <param name="logger">The logger.</param>
    internal TrickplayInfoWriter(IStubItems items, StubCatalog catalog, IVersionResolver resolver, ITrickplayManager trickplay, TrickplayCache cache, IStateStore state, Func<bool> enabled, ILogger logger)
    {
        _items = items;
        _catalog = catalog;
        _resolver = resolver;
        _trickplay = trickplay;
        _cache = cache;
        _state = state;
        _enabled = enabled;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task Run(IProgress<double> progress, CancellationToken cancellationToken)
    {
        await RunAsync(cancellationToken).ConfigureAwait(false);
        progress?.Report(100);
    }

    /// <summary>
    /// Runs one pass over every stub item.
    /// </summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>How many items were written to and how many cleared.</returns>
    internal async Task<(int Written, int Cleared)> RunAsync(CancellationToken cancellationToken)
    {
        if (_state.Status != StateStatus.Ready)
        {
            return (0, 0);
        }

        var enabled = _enabled();
        var state = _state.Current;
        var live = new HashSet<(string PeerKey, string StubKey)>();
        var written = 0;
        var cleared = 0;
        var lost = 0;
        var seen = 0;
        foreach (var item in _items.List())
        {
            cancellationToken.ThrowIfCancellationRequested();
            seen++;
            if (ShoalIndex.Classify(state, item.Path, _catalog) is not { } stub)
            {
                continue;
            }

            IReadOnlyList<TrickplayLevel> wanted = [];
            if (_catalog.Find(stub.PeerKey, stub.FolderName) is { State: EntryState.Active or EntryState.HeldRemoval } entry
                && _resolver.Resolve(item.Id, null) is { } version)
            {
                live.Add((stub.PeerKey, version.StubKey));
                if (enabled)
                {
                    wanted = TrickplayLevels.For(entry, version.PeerMediaSourceId);
                }
            }

            try
            {
                switch (await ApplyAsync(item.Id, wanted, cancellationToken).ConfigureAwait(false))
                {
                    case 1:
                        written++;
                        break;
                    case 2:
                        written++;
                        lost++;
                        break;
                    case -1:
                        cleared++;
                        break;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
            {
                _logger.LogWarning("Shoal could not save the trickplay info of a stub item ({Error})", ex.GetType().Name);
            }
        }

        if (seen > 0)
        {
            _cache.Reconcile(live); // a pass that saw no stub at all is not trusted to say that every cached tile is an orphan
        }

        if (written > 0)
        {
            _logger.LogInformation("Shoal saved the trickplay info of {Written} stub items after a library scan; {Lost} of them did not read back from core", written, lost);
        }

        return (written, cleared);
    }

    // 1: rows were written, -1: rows were removed, 0: nothing to do.
    private async Task<int> ApplyAsync(Guid itemId, IReadOnlyList<TrickplayLevel> wanted, CancellationToken cancellationToken)
    {
        var current = await _trickplay.GetTrickplayResolutions(itemId).ConfigureAwait(false);
        var same = current.Count == wanted.Count
            && wanted.All(l => current.TryGetValue(l.Width, out var row)
                && row.Height == l.Height && row.TileWidth == l.TileWidth && row.TileHeight == l.TileHeight && row.ThumbnailCount == l.ThumbnailCount && row.Interval == l.IntervalMs && row.Bandwidth == l.Bandwidth);
        if (same)
        {
            return 0;
        }

        if (current.Count > 0)
        {
            await _trickplay.DeleteTrickplayDataAsync(itemId, cancellationToken).ConfigureAwait(false);
        }

        foreach (var level in wanted)
        {
            await _trickplay.SaveTrickplayInfo(new TrickplayInfo
            {
                ItemId = itemId,
                Width = level.Width,
                Height = level.Height,
                TileWidth = level.TileWidth,
                TileHeight = level.TileHeight,
                ThumbnailCount = level.ThumbnailCount,
                Interval = level.IntervalMs,
                Bandwidth = level.Bandwidth,
            }).ConfigureAwait(false);
        }

        if (wanted.Count == 0)
        {
            return -1;
        }

        // Read back what was just saved: if core does not keep it, say so (a row core drops would explain a manifest that stays empty).
        var kept = await _trickplay.GetTrickplayResolutions(itemId).ConfigureAwait(false);
        return kept.Count == wanted.Count ? 1 : 2;
    }
}
