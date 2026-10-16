using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Extensions.Json;
using Jellyfin.Plugin.Shoal.Peers;
using MediaBrowser.Model.Dto;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Shoal.Sync;

/// <summary>
/// A library of the peer that the share user can see (<c>/UserViews</c>).
/// </summary>
/// <param name="Id">The peer's library id.</param>
/// <param name="Name">The sanitized name.</param>
/// <param name="CollectionType">The collection type in lower case, or empty.</param>
public sealed record PeerView(Guid Id, string Name, string CollectionType);

/// <summary>
/// One line of the cheap listing: enough to diff and to dedup.
/// </summary>
/// <param name="Id">The peer's item id.</param>
/// <param name="Etag">The etag, sanitized (letters, digits, dash; at most 64).</param>
/// <param name="ProviderIds">The canonical provider ids.</param>
/// <param name="Title">The sanitized title.</param>
/// <param name="Year">The production year, or null.</param>
public sealed record ListedMovie(Guid Id, string Etag, CanonicalIds ProviderIds, string Title, int? Year);

/// <summary>
/// The whole listing of one library.
/// </summary>
/// <param name="Items">The items, in the order the peer listed them, each id once.</param>
/// <param name="Complete">True only if every page was read and the number of ids equals the peer's <c>TotalRecordCount</c> (a few items more or
/// less are tolerated for items added or removed while paging). Removals are decided only on a complete listing.</param>
/// <param name="TotalRecordCount">The total the peer announced on its last page.</param>
/// <param name="Skipped">How many items could not be read.</param>
public sealed record Listing(IReadOnlyList<ListedMovie> Items, bool Complete, int TotalRecordCount, int Skipped);

/// <summary>
/// Reads a peer's catalog with the queries of DESIGN 5.1. Every response goes through <c>BoundedReadStream</c> (6.10) and is parsed
/// item by item with depth 32, so an item that fails to parse (an enum from a newer server, say) is skipped and counted without failing its
/// page. Every field is sanitized (5.5) before it leaves this class.
/// </summary>
public sealed class PeerCatalogReader
{
    /// <summary>The page size of the cheap listing.</summary>
    public const int ListPageSize = 200;

    /// <summary>The number of ids in one details request.</summary>
    public const int DetailsBatchSize = 50;

    private const string ListFields = "ProviderIds,Etag";

    private const string DetailFields = "ProviderIds,Etag,Overview,Genres,Studios,People,OriginalTitle,Taglines,OfficialRating,CommunityRating,PremiereDate,ProductionYear,SortName,MediaSources,Trickplay";

    private static readonly JsonSerializerOptions ItemOptions = CreateItemOptions();

    private readonly PeerClient _client;
    private readonly PeerTrust _trust;
    private readonly PeerRegistry _registry;
    private readonly MetadataSanitizer _sanitizer;
    private readonly ILogger _logger;
    private long _optionalDropped;

    /// <summary>
    /// Initializes a new instance of the <see cref="PeerCatalogReader"/> class.
    /// </summary>
    /// <param name="client">The peer client.</param>
    /// <param name="trust">The trust window.</param>
    /// <param name="registry">The runtime peer table.</param>
    /// <param name="sanitizer">The metadata sanitizer.</param>
    /// <param name="logger">The logger.</param>
    public PeerCatalogReader(PeerClient client, PeerTrust trust, PeerRegistry registry, MetadataSanitizer sanitizer, ILogger<PeerCatalogReader> logger)
        : this(client, trust, registry, sanitizer, (ILogger)logger)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="PeerCatalogReader"/> class (tests: any logger).
    /// </summary>
    /// <param name="client">The peer client.</param>
    /// <param name="trust">The trust window.</param>
    /// <param name="registry">The runtime peer table.</param>
    /// <param name="sanitizer">The metadata sanitizer.</param>
    /// <param name="logger">The logger.</param>
    internal PeerCatalogReader(PeerClient client, PeerTrust trust, PeerRegistry registry, MetadataSanitizer sanitizer, ILogger logger)
    {
        _client = client;
        _trust = trust;
        _registry = registry;
        _sanitizer = sanitizer;
        _logger = logger;
    }

    /// <summary>
    /// Gets how many times optional data of a peer item (a trickplay manifest, the attachments of a source) could not be read and was left out while the title was kept.
    /// </summary>
    public long OptionalDataDropped => Interlocked.Read(ref _optionalDropped);

    /// <summary>
    /// Lists the peer's libraries that the share user can see.
    /// </summary>
    /// <param name="peer">The peer.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The libraries; items that cannot be read are left out.</returns>
    /// <exception cref="PeerCallException">The peer could not be asked or answered something unusable.</exception>
    public async Task<IReadOnlyList<PeerView>> ViewsAsync(PeerEndpoint peer, CancellationToken cancellationToken)
    {
        var page = await ReadPageAsync(peer, "/UserViews", 200, cancellationToken).ConfigureAwait(false);
        var views = new List<PeerView>();
        foreach (var item in page.Items)
        {
            if (item.Id != Guid.Empty)
            {
                views.Add(new PeerView(item.Id, MetadataSanitizer.DisplayName(item.Name), (item.CollectionType?.ToString() ?? string.Empty).ToLowerInvariant()));
            }
        }

        return views;
    }

    /// <summary>
    /// Asks how many movies a library of the peer holds (one call with <c>Limit=0</c>; nothing but the count is read).
    /// </summary>
    /// <param name="peer">The peer.</param>
    /// <param name="libraryId">The peer's library id.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The announced number of movies.</returns>
    /// <exception cref="PeerCallException">The peer could not be asked or answered something unusable.</exception>
    public Task<int> CountMoviesAsync(PeerEndpoint peer, Guid libraryId, CancellationToken cancellationToken) => CountAsync(peer, libraryId, "Movie", cancellationToken);

    /// <summary>
    /// Asks how many items of one type a library holds, with a <c>Limit=0</c> call (the picker's "N series, M episodes", DESIGN v2.7.6).
    /// </summary>
    /// <param name="peer">The peer.</param>
    /// <param name="libraryId">The peer's library id.</param>
    /// <param name="itemType">One of <c>Movie</c>, <c>Series</c>, <c>Season</c>, <c>Episode</c>.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The count the peer announced, or 0.</returns>
    public async Task<int> CountAsync(PeerEndpoint peer, Guid libraryId, string itemType, CancellationToken cancellationToken)
    {
        if (itemType is not ("Movie" or "Series" or "Season" or "Episode"))
        {
            throw new ArgumentOutOfRangeException(nameof(itemType));
        }

        var query = "/Items?ParentId=" + libraryId.ToString("N") + "&IncludeItemTypes=" + itemType + "&Recursive=true&StartIndex=0&Limit=0&EnableTotalRecordCount=true&EnableImages=false";
        var page = await ReadPageAsync(peer, query, 1, cancellationToken).ConfigureAwait(false);
        return Math.Max(0, page.Total);
    }

    /// <summary>
    /// Pages through one library's movies.
    /// </summary>
    /// <param name="peer">The peer.</param>
    /// <param name="libraryId">The peer's library id.</param>
    /// <param name="entryCap">Stop paging when the peer announces more titles than this.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The listing; a page that fails ends it early and marks it incomplete. A peer that announces more than <paramref name="entryCap"/> titles yields an incomplete listing with no items.</returns>
    public async Task<Listing> ListAsync(PeerEndpoint peer, Guid libraryId, int entryCap, CancellationToken cancellationToken)
    {
        var items = new List<ListedMovie>();
        var seen = new HashSet<Guid>();
        var skipped = 0;
        var total = 0;
        var failed = false;
        for (var start = 0; start < entryCap + ListPageSize; start += ListPageSize)
        {
            var query = string.Create(
                CultureInfo.InvariantCulture,
                $"/Items?ParentId={libraryId:N}&Recursive=true&IncludeItemTypes=Movie&Fields={ListFields}&EnableImages=false&EnableUserData=false&SortBy=SortName,Id&StartIndex={start}&Limit={ListPageSize}&EnableTotalRecordCount=true");
            ItemPage page;
            try
            {
                page = await ReadPageAsync(peer, query, ListPageSize, cancellationToken).ConfigureAwait(false);
            }
            catch (PeerCallException ex)
            {
                _logger.LogInformation("A listing page from peer {Peer} failed ({Kind})", Security.Redact.Id(peer.PeerKey), ex.Kind);
                failed = true;
                break;
            }

            total = page.Total;
            skipped += page.Skipped;
            if (total > entryCap)
            {
                return new Listing([], false, total, skipped); // the caller stops and raises the entry-cap alert
            }

            foreach (var dto in page.Items)
            {
                var movie = Describe(dto);
                if (movie is null)
                {
                    skipped++;
                }
                else if (seen.Add(movie.Id))
                {
                    items.Add(movie);
                }
            }

            if (page.Items.Count + page.Skipped == 0 || start + ListPageSize >= total)
            {
                break;
            }
        }

        var tolerance = Math.Max(5, total / 100);
        var complete = !failed && items.Count >= total - tolerance && items.Count <= total + tolerance;
        return new Listing(items, complete, total, skipped);
    }

    /// <summary>
    /// Fetches the details of up to 50 items. Only items that were asked for are returned, each once.
    /// </summary>
    /// <param name="peer">The peer.</param>
    /// <param name="ids">The peer's item ids.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The sanitized movies and how many items could not be read.</returns>
    /// <exception cref="PeerCallException">The peer could not be asked or answered something unusable.</exception>
    public async Task<(IReadOnlyList<SanitizedMovie> Movies, int Skipped)> DetailsAsync(PeerEndpoint peer, IReadOnlyList<Guid> ids, CancellationToken cancellationToken)
    {
        if (ids.Count is 0 or > DetailsBatchSize)
        {
            throw new ArgumentOutOfRangeException(nameof(ids));
        }

        var asked = ids.ToHashSet();
        var query = "/Items?Ids=" + string.Join(',', ids.Select(static id => id.ToString("N"))) + "&Fields=" + DetailFields
            + "&EnableImages=true&ImageTypeLimit=1&EnableImageTypes=Primary,Backdrop";
        var page = await ReadPageAsync(peer, query, DetailsBatchSize, cancellationToken).ConfigureAwait(false);
        var movies = new List<SanitizedMovie>();
        var skipped = page.Skipped;
        var returned = new HashSet<Guid>();
        foreach (var dto in page.Items)
        {
            if (!asked.Contains(dto.Id) || !returned.Add(dto.Id))
            {
                continue; // an item nobody asked for, or twice
            }

            SanitizedMovie? movie;
            try
            {
                movie = _sanitizer.Sanitize(dto);
            }
            catch (Exception ex) when (ex is not (OperationCanceledException or OutOfMemoryException))
            {
                movie = null; // one hostile item is skipped, never the sync (K11 M-1)
            }

            if (movie is null)
            {
                skipped++;
            }
            else
            {
                movies.Add(movie);
            }
        }

        return (movies, skipped);
    }

    /// <summary>
    /// Asks which of the given items the peer still has (used to confirm a removal before it happens).
    /// </summary>
    /// <param name="peer">The peer.</param>
    /// <param name="ids">The peer's item ids.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The ids the peer returned.</returns>
    /// <exception cref="PeerCallException">The peer could not be asked or answered something unusable.</exception>
    public async Task<HashSet<Guid>> PresentAsync(PeerEndpoint peer, IReadOnlyList<Guid> ids, CancellationToken cancellationToken)
    {
        var present = new HashSet<Guid>();
        foreach (var batch in ids.Chunk(DetailsBatchSize))
        {
            var asked = batch.ToHashSet();
            var query = "/Items?Ids=" + string.Join(',', batch.Select(static id => id.ToString("N"))) + "&Fields=ProviderIds&EnableImages=false&EnableUserData=false";
            var page = await ReadPageAsync(peer, query, DetailsBatchSize, cancellationToken).ConfigureAwait(false);
            foreach (var dto in page.Items.Where(i => asked.Contains(i.Id)))
            {
                present.Add(dto.Id);
            }
        }

        return present;
    }

    /// <summary>
    /// Reads one page of any <c>/Items</c> query through the same bounded, item-by-item path as the movie listing (used by the TV reader).
    /// </summary>
    /// <param name="peer">The peer.</param>
    /// <param name="pathAndQuery">The path and query, built from validated values.</param>
    /// <param name="maxItems">The most items the answer may hold.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The readable items, the total the peer announced, and how many items could not be read.</returns>
    /// <exception cref="PeerCallException">The peer could not be asked or answered something unusable.</exception>
    public async Task<(IReadOnlyList<BaseItemDto> Items, int Total, int Skipped)> ReadItemsAsync(PeerEndpoint peer, string pathAndQuery, int maxItems, CancellationToken cancellationToken)
    {
        var page = await ReadPageAsync(peer, pathAndQuery, maxItems, cancellationToken).ConfigureAwait(false);
        return (page.Items, page.Total, page.Skipped);
    }

    // Optional data must never cost a title. Jellyfin 12.1's TrickplayInfoDto cannot be deserialized by System.Text.Json (its only constructor takes a database
    // entity), so the manifest is read from the raw JSON by hand and the property is taken out before the item is deserialized. If anything else optional makes
    // the item unreadable (an attachment from a newer server, say), a second try leaves the optional parts out and the title is kept (Pilot round 7).
    private BaseItemDto? ReadItem(JsonElement element)
    {
        JsonObject? stripped = null;
        Dictionary<string, Dictionary<int, TrickplayInfoDto>>? trickplay = null;
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, "Trickplay", StringComparison.OrdinalIgnoreCase))
                {
                    trickplay = ReadTrickplay(property.Value);
                    stripped = RemoveKey(JsonNode.Parse(element.GetRawText())!.AsObject(), "Trickplay");
                    break;
                }
            }
        }

        BaseItemDto? dto;
        try
        {
            dto = stripped is null ? element.Deserialize<BaseItemDto>(ItemOptions) : stripped.Deserialize<BaseItemDto>(ItemOptions);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                throw;
            }

            // The retry: no trickplay, and no attachments in any source. A failure here is a failure of the item itself and is not hidden.
            var bare = stripped ?? JsonNode.Parse(element.GetRawText())!.AsObject();
            RemoveKey(bare, "Trickplay");
            if (bare.FirstOrDefault(static p => string.Equals(p.Key, "MediaSources", StringComparison.OrdinalIgnoreCase)).Value is JsonArray sources)
            {
                foreach (var source in sources.OfType<JsonObject>())
                {
                    RemoveKey(source, "MediaAttachments");
                }
            }

            dto = bare.Deserialize<BaseItemDto>(ItemOptions);
            trickplay = null;
            Interlocked.Increment(ref _optionalDropped);
            _logger.LogDebug("Optional data of a peer item could not be read and was left out; the title is kept");
        }

        if (dto is not null && trickplay is not null)
        {
            dto.Trickplay = trickplay;
        }

        return dto;
    }

    private static JsonObject RemoveKey(JsonObject node, string name)
    {
        foreach (var key in node.Select(static p => p.Key).Where(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase)).ToList())
        {
            node.Remove(key);
        }

        return node;
    }

    // media source id -> thumbnail width -> level, built by hand from the raw JSON and bounded (16 sources, 32 widths each). The numbers are range-checked later by
    // the trickplay sanitizer. Anything unexpected drops the manifest and is counted; it never fails the item.
    private Dictionary<string, Dictionary<int, TrickplayInfoDto>>? ReadTrickplay(JsonElement manifest)
    {
        try
        {
            if (manifest.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var result = new Dictionary<string, Dictionary<int, TrickplayInfoDto>>(StringComparer.Ordinal);
            foreach (var source in manifest.EnumerateObject().Take(16))
            {
                if (source.Value.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var levels = new Dictionary<int, TrickplayInfoDto>();
                foreach (var level in source.Value.EnumerateObject().Take(32))
                {
                    if (int.TryParse(level.Name, NumberStyles.None, CultureInfo.InvariantCulture, out var width) && level.Value.ValueKind == JsonValueKind.Object
                        && TryInt(level.Value, "Width", out var w) && TryInt(level.Value, "Height", out var h) && TryInt(level.Value, "TileWidth", out var tw)
                        && TryInt(level.Value, "TileHeight", out var th) && TryInt(level.Value, "ThumbnailCount", out var count) && TryInt(level.Value, "Interval", out var interval)
                        && TryInt(level.Value, "Bandwidth", out var bandwidth))
                    {
                        levels[width] = new TrickplayInfoDto(new TrickplayInfo { Width = w, Height = h, TileWidth = tw, TileHeight = th, ThumbnailCount = count, Interval = interval, Bandwidth = bandwidth });
                    }
                }

                result[source.Name] = levels;
            }

            return result;
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException or ArgumentException)
        {
            Interlocked.Increment(ref _optionalDropped);
            return null;
        }
    }

    private static bool TryInt(JsonElement element, string name, out int value)
    {
        value = 0;
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetInt32(out value);
            }
        }

        return false;
    }

    private static JsonSerializerOptions CreateItemOptions()
    {
        var options = new JsonSerializerOptions(JsonDefaults.Options)
        {
            MaxDepth = PeerJson.MaxDepth,
            ReadCommentHandling = JsonCommentHandling.Disallow,
            AllowTrailingCommas = false,
            UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Skip,
        };
        return options;
    }

    private static ListedMovie? Describe(BaseItemDto dto)
    {
        if (dto.Id == Guid.Empty)
        {
            return null;
        }

        var etag = dto.Etag is { Length: > 0 and <= 64 } tag && tag.All(static c => c is (>= '0' and <= '9') or (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or '-') ? tag : string.Empty;
        var year = dto.ProductionYear is { } y and >= 1870 and <= 2100 ? y : (int?)null;
        return new ListedMovie(dto.Id, etag, MetadataSanitizer.CanonicalProviderIds(dto.ProviderIds), PeerText.Clean(dto.Name, MetadataSanitizer.TitleMax), year);
    }

    private async Task<ItemPage> ReadPageAsync(PeerEndpoint peer, string pathAndQuery, int maxItems, CancellationToken cancellationToken)
    {
        // The trust window is 120 s and a sync lasts minutes, so it is renewed before every call (at most every 10 s, DESIGN 4.4).
        if (_registry.Check(peer.PeerKey, carriesToken: true, TrustRequirement.PinFresh) != TrustDenial.None
            && !await _trust.EnsureAsync(peer, TrustRequirement.PinFresh, cancellationToken).ConfigureAwait(false))
        {
            throw new PeerCallException(PeerFailureKind.TrustLapsed);
        }

        using var response = await _client.SendAsync(peer, PeerRequest.Get(pathAndQuery), PeerCallProfile.Items, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.OK || !response.HasBody || !string.Equals(response.MediaType, "application/json", StringComparison.Ordinal))
        {
            throw new PeerCallException(PeerFailureKind.BadResponse, response.StatusCode);
        }

        var bytes = await response.ReadBytesAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = PeerJson.MaxDepth });
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("Items", out var array)
                || array.ValueKind != JsonValueKind.Array)
            {
                throw new PeerCallException(PeerFailureKind.BadResponse);
            }

            if (array.GetArrayLength() > maxItems)
            {
                throw new PeerCallException(PeerFailureKind.BadResponse); // more than the page size that was asked for
            }

            var total = document.RootElement.TryGetProperty("TotalRecordCount", out var count) && count.ValueKind == JsonValueKind.Number && count.TryGetInt32(out var n) && n >= 0 ? n : array.GetArrayLength();
            var items = new List<BaseItemDto>(array.GetArrayLength());
            var skipped = 0;
            foreach (var element in array.EnumerateArray())
            {
                try
                {
                    var dto = ReadItem(element);
                    if (dto is null)
                    {
                        skipped++;
                    }
                    else
                    {
                        items.Add(dto);
                    }
                }
                catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
                {
                    skipped++;
                }
            }

            return new ItemPage(items, total, skipped);
        }
        catch (JsonException)
        {
            throw new PeerCallException(PeerFailureKind.BadResponse);
        }
    }

    private sealed record ItemPage(List<BaseItemDto> Items, int Total, int Skipped);
}
