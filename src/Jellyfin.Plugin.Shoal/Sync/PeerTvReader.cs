using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.Peers;
using MediaBrowser.Model.Dto;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Shoal.Sync;

/// <summary>
/// Reads a TV library from a peer with stock Jellyfin calls and the share user's token (DESIGN v2.7.2). Three cheap listings (series, seasons, episodes) in pages of
/// <see cref="TvLimits.PageSize"/>, each with its own completeness check <c>|collected - Total| &lt;= max(5, 1%)</c>, and details in batches of
/// <see cref="TvLimits.DetailBatch"/>. Pages go through <see cref="PeerCatalogReader"/>, so every answer is read through the bounded stream and parsed item by item.
/// A listing page of 200 episodes is about 140 KB against the 8 MiB cap of the <c>items</c> profile: the cap needs no change.
/// </summary>
public sealed class PeerTvReader : IPeerTvReader
{
    private const string ListFields = "ProviderIds,Etag";

    private const string DetailFields = "ProviderIds,Etag,Overview,Genres,Studios,People,OriginalTitle,OfficialRating,CommunityRating,PremiereDate,EndDate,Status,ProductionYear,SortName,MediaSources,Trickplay";

    private readonly PeerCatalogReader _pages;
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="PeerTvReader"/> class.
    /// </summary>
    /// <param name="pages">The reader that fetches and parses one page.</param>
    /// <param name="logger">The logger.</param>
    public PeerTvReader(PeerCatalogReader pages, ILogger<PeerTvReader> logger)
        : this(pages, (ILogger)logger)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="PeerTvReader"/> class (tests: any logger).
    /// </summary>
    /// <param name="pages">The reader that fetches and parses one page.</param>
    /// <param name="logger">The logger.</param>
    internal PeerTvReader(PeerCatalogReader pages, ILogger logger)
    {
        _pages = pages;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<TvListings> ListAsync(PeerEndpoint peer, Guid peerLibraryId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(peer);
        var series = await ListKindAsync(peer, peerLibraryId, "Series", TvLimits.MaxSeriesPerPeer + TvLimits.PageSize, cancellationToken).ConfigureAwait(false);
        var seasons = await ListKindAsync(peer, peerLibraryId, "Season", TvLimits.MaxSeasonsPerPeer + TvLimits.PageSize, cancellationToken).ConfigureAwait(false);
        var episodes = await ListKindAsync(peer, peerLibraryId, "Episode", CatalogDocument.MaxEntries + TvLimits.PageSize, cancellationToken).ConfigureAwait(false);
        return new TvListings(series.Items, seasons.Items, episodes.Items, series.Complete, seasons.Complete, episodes.Complete);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<BaseItemDto>> DetailsAsync(PeerEndpoint peer, IReadOnlyList<Guid> ids, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(peer);
        if (ids.Count is 0 or > TvLimits.DetailBatch)
        {
            throw new ArgumentOutOfRangeException(nameof(ids));
        }

        var asked = ids.ToHashSet();
        var path = "/Items?Ids=" + string.Join(',', ids.Select(static id => id.ToString("N"))) + "&Fields=" + DetailFields + "&EnableImages=true&ImageTypeLimit=1&EnableImageTypes=Primary,Backdrop,Banner,Thumb";
        var page = await _pages.ReadItemsAsync(peer, path, TvLimits.DetailBatch, cancellationToken).ConfigureAwait(false);
        var returned = new HashSet<Guid>();
        return [.. page.Items.Where(i => asked.Contains(i.Id) && returned.Add(i.Id))];
    }

    private async Task<(List<BaseItemDto> Items, bool Complete)> ListKindAsync(PeerEndpoint peer, Guid libraryId, string type, int cap, CancellationToken cancellationToken)
    {
        var items = new List<BaseItemDto>();
        var seen = new HashSet<Guid>();
        var total = 0;
        var failed = false;
        var images = type == "Season" ? "true&ImageTypeLimit=1&EnableImageTypes=Primary" : "false"; // a season has no details of its own: its poster tag comes with the listing
        for (var start = 0; start < cap; start += TvLimits.PageSize)
        {
            var path = string.Create(
                CultureInfo.InvariantCulture,
                $"/Items?ParentId={libraryId:N}&Recursive=true&IncludeItemTypes={type}&Fields={ListFields}&EnableImages={images}&EnableUserData=false&SortBy=SortName,Id&IsMissing=false&StartIndex={start}&Limit={TvLimits.PageSize}&EnableTotalRecordCount=true");
            (IReadOnlyList<BaseItemDto> Items, int Total, int Skipped) page;
            try
            {
                page = await _pages.ReadItemsAsync(peer, path, TvLimits.PageSize, cancellationToken).ConfigureAwait(false);
            }
            catch (PeerCallException ex)
            {
                _logger.LogInformation("A {Type} listing page from peer {Peer} failed ({Kind})", type, Security.Redact.Id(peer.PeerKey), ex.Kind);
                failed = true;
                break;
            }

            total = page.Total;
            foreach (var dto in page.Items)
            {
                if (dto.Id != Guid.Empty && seen.Add(dto.Id))
                {
                    items.Add(dto);
                }
            }

            if (page.Items.Count + page.Skipped == 0 || start + TvLimits.PageSize >= total)
            {
                break;
            }
        }

        // The total is the peer's own number and is never used to size anything; it only judges whether the listing is whole.
        var tolerance = Math.Max(5, total / 100);
        var complete = !failed && items.Count >= total - tolerance && items.Count <= total + tolerance;
        return (items, complete);
    }
}
