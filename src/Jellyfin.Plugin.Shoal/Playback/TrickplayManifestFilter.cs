using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Shoal.Authorization;
using Jellyfin.Plugin.Shoal.Http;
using Jellyfin.Plugin.Shoal.State;
using Jellyfin.Plugin.Shoal.Sync;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Shoal.Playback;

/// <summary>
/// Where the trickplay manifest of a Shoal title comes from when core does not advertise it itself (DESIGN v2.6.5 fallback; Pilot round 7).
/// </summary>
public interface ITrickplayManifestSource
{
    /// <summary>
    /// Builds the manifest that <c>BaseItemDto.Trickplay</c> would hold for a federated item.
    /// </summary>
    /// <param name="itemId">The local item id.</param>
    /// <param name="mediaSourceIds">The media source ids the item's DTO lists (the versions of a title), or none.</param>
    /// <returns>Media source id to thumbnail width to level, or null when the item is not federated or has no trickplay.</returns>
    Dictionary<string, Dictionary<int, TrickplayInfoDto>>? ManifestFor(Guid itemId, IEnumerable<string> mediaSourceIds);
}

/// <summary>
/// The manifest from Home's own catalog: the levels validated at sync, keyed by the stub's local media source id (its item id), which is what the clients
/// look up when they play it. Nothing here is read from the Peer at request time.
/// </summary>
public sealed class CatalogTrickplayManifest : ITrickplayManifestSource
{
    private readonly ShoalIndex _index;
    private readonly StubCatalog _catalog;
    private readonly IVersionResolver _resolver;
    private readonly Func<bool> _enabled;

    /// <summary>
    /// Initializes a new instance of the <see cref="CatalogTrickplayManifest"/> class.
    /// </summary>
    /// <param name="index">The federated items.</param>
    /// <param name="catalog">The catalogs.</param>
    /// <param name="resolver">Maps a stub item to its version.</param>
    /// <param name="enabled">Reads the <c>TrickplayEnabled</c> switch.</param>
    public CatalogTrickplayManifest(ShoalIndex index, StubCatalog catalog, IVersionResolver resolver, Func<bool> enabled)
    {
        _index = index;
        _catalog = catalog;
        _resolver = resolver;
        _enabled = enabled;
    }

    /// <inheritdoc />
    public Dictionary<string, Dictionary<int, TrickplayInfoDto>>? ManifestFor(Guid itemId, IEnumerable<string> mediaSourceIds)
    {
        ArgumentNullException.ThrowIfNull(mediaSourceIds);
        if (!_enabled() || !_index.Contains(itemId))
        {
            return null;
        }

        var result = new Dictionary<string, Dictionary<int, TrickplayInfoDto>>(StringComparer.Ordinal);
        var ids = new List<Guid> { itemId };
        foreach (var text in mediaSourceIds.Take(8))
        {
            if (Guid.TryParse(text, out var id) && id != itemId && _index.Contains(id))
            {
                ids.Add(id);
            }
        }

        foreach (var id in ids)
        {
            if (_index.Get(id) is not { IsCopy: false } stub || _catalog.Find(stub.PeerKey, stub.FolderName) is not { } entry
                || entry.State is not (EntryState.Active or EntryState.HeldRemoval) || _resolver.Resolve(id, null) is not { } version)
            {
                continue;
            }

            var levels = TrickplayLevels.For(entry, version.PeerMediaSourceId);
            if (levels.Count > 0)
            {
                result[version.VersionItemId.ToString("N", CultureInfo.InvariantCulture)] = levels.ToDictionary(
                    static l => l.Width,
                    static l => new TrickplayInfoDto(new TrickplayInfo { Width = l.Width, Height = l.Height, TileWidth = l.TileWidth, TileHeight = l.TileHeight, ThumbnailCount = l.ThumbnailCount, Interval = l.IntervalMs, Bandwidth = l.Bandwidth }));
            }
        }

        return result.Count == 0 ? null : result;
    }
}

/// <summary>
/// Puts the trickplay manifest into the item answers of core for Shoal titles (DESIGN v2.6.5, the fallback for the case that core does not advertise the rows
/// Shoal saved: Pilot round 7 found <c>Trickplay {}</c> for every stub, so no client asked for a tile). It only fills <c>BaseItemDto.Trickplay</c> when core left
/// it empty, only on an answer core already built for a caller who may see the item, and only when the client asked for the field (or for all fields, as a request
/// without <c>Fields</c> does). It adds no route and reads nothing from the Peer.
/// </summary>
public sealed class TrickplayManifestFilter : IAsyncResultFilter
{
    private readonly ITrickplayManifestSource _source;
    private readonly ILogger<TrickplayManifestFilter> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="TrickplayManifestFilter"/> class.
    /// </summary>
    /// <param name="source">Where the manifest comes from.</param>
    /// <param name="logger">The logger.</param>
    public TrickplayManifestFilter(ITrickplayManifestSource source, ILogger<TrickplayManifestFilter> logger)
    {
        _source = source;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task OnResultExecutionAsync(ResultExecutingContext context, ResultExecutionDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);
#pragma warning disable RS0030 // Read only: the filter looks at the value core is about to send; Shoal's own answers still go through ShoalResult (DESIGN 8.6).
        var value = (context.Result as Microsoft.AspNetCore.Mvc.ObjectResult)?.Value;
#pragma warning restore RS0030
        if (value is not null && Carries(value) && Wanted(context.HttpContext.Request))
        {
            try
            {
                Patch(value);
            }
            catch (Exception ex) when (ex is not (OutOfMemoryException or OperationCanceledException))
            {
                _logger.LogDebug("The trickplay manifest of an item answer could not be added ({Error})", ex.GetType().Name);
            }
        }

        await next().ConfigureAwait(false);
    }

    private static bool Carries(object value) => value is BaseItemDto or QueryResult<BaseItemDto> or IEnumerable<BaseItemDto>;

    // Core fills the field when the request names it or names no fields at all.
    private static bool Wanted(HttpRequest request)
    {
        var fields = ShoalRequest.Values(request, "fields");
        return fields.Count == 0 || fields.Any(static f => f.Split(',', StringSplitOptions.TrimEntries).Contains("Trickplay", StringComparer.OrdinalIgnoreCase));
    }

    private void Patch(object value)
    {
        switch (value)
        {
            case BaseItemDto one:
                Fill(one);
                break;
            case QueryResult<BaseItemDto> page:
                foreach (var item in page.Items ?? [])
                {
                    Fill(item);
                }

                break;
            case IEnumerable<BaseItemDto> many:
                foreach (var item in many)
                {
                    Fill(item);
                }

                break;
        }
    }

    private void Fill(BaseItemDto? dto)
    {
        if (dto is null || dto.Id == Guid.Empty || dto.Trickplay is { Count: > 0 })
        {
            return;
        }

        if (_source.ManifestFor(dto.Id, (dto.MediaSources ?? []).Where(static s => s is not null).Select(static s => s.Id ?? string.Empty)) is { } manifest)
        {
            dto.Trickplay = manifest;
        }
    }
}
