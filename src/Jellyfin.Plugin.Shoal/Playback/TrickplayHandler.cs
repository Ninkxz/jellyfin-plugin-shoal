using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.Authorization;
using Jellyfin.Plugin.Shoal.Http;
using Jellyfin.Plugin.Shoal.Peers;
using Jellyfin.Plugin.Shoal.Security;
using Jellyfin.Plugin.Shoal.State;
using Jellyfin.Plugin.Shoal.Sync;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

#pragma warning disable SA1600 // internal plumbing; the public surface is documented

namespace Jellyfin.Plugin.Shoal.Playback;

/// <summary>
/// One tile fetched from a Peer, shared by every request that joined the call. Immutable.
/// </summary>
/// <param name="Status">200 when <paramref name="Bytes"/> is a validated JPEG; otherwise the status to answer.</param>
/// <param name="Bytes">The tile.</param>
public sealed record TileFetch(int Status, ReadOnlyMemory<byte> Bytes);

/// <summary>
/// Lets identical tile requests share one Peer call and keeps one peer from being flooded: at most <see cref="GateSlots"/> calls run at once per peer
/// and at most <see cref="MaxWaiting"/> wait (DESIGN v2.6.5 step 4). A call outlives its waiters, so the tile still lands in the cache.
/// </summary>
public sealed class TileFlights
{
    /// <summary>The Peer calls one peer may run at once.</summary>
    public const int GateSlots = 4;

    /// <summary>The calls that may wait for a slot of one peer.</summary>
    public const int MaxWaiting = 32;

    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(60);

    private readonly object _lock = new();
    private readonly Dictionary<string, Task<TileFetch>> _inflight = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SemaphoreSlim> _gates = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _waiting = new(StringComparer.Ordinal);

    /// <summary>Gets the calls that run or wait now (tests).</summary>
    public int Active
    {
        get
        {
            lock (_lock)
            {
                return _inflight.Count;
            }
        }
    }

    /// <summary>
    /// Runs or joins a call.
    /// </summary>
    /// <param name="peerKey">The peer.</param>
    /// <param name="key">The request key (stub, width, index).</param>
    /// <param name="call">The Peer call; it must not throw other than for cancellation.</param>
    /// <param name="cancellationToken">The caller's abort token.</param>
    /// <returns>The shared outcome, or null when too many calls wait (answer 429).</returns>
    public async Task<TileFetch?> RunAsync(string peerKey, string key, Func<CancellationToken, Task<TileFetch>> call, CancellationToken cancellationToken)
    {
        Task<TileFetch> task;
        lock (_lock)
        {
            var flightKey = peerKey + "|" + key;
            if (!_inflight.TryGetValue(flightKey, out task!))
            {
                var waiting = _waiting.GetValueOrDefault(peerKey);
                if (waiting >= MaxWaiting)
                {
                    return null;
                }

                _waiting[peerKey] = waiting + 1;
                var gate = _gates.TryGetValue(peerKey, out var existing) ? existing : _gates[peerKey] = new SemaphoreSlim(GateSlots, GateSlots);
                task = Task.Run(() => FlightAsync(peerKey, flightKey, gate, call));
                _inflight[flightKey] = task;
            }
        }

        return await task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<TileFetch> FlightAsync(string peerKey, string flightKey, SemaphoreSlim gate, Func<CancellationToken, Task<TileFetch>> call)
    {
        var holding = false;
        try
        {
            using var budget = new CancellationTokenSource(Budget);
            try
            {
                await gate.WaitAsync(budget.Token).ConfigureAwait(false);
                holding = true;
            }
            finally
            {
                lock (_lock)
                {
                    _waiting[peerKey] = Math.Max(0, _waiting.GetValueOrDefault(peerKey) - 1);
                }
            }

            return await call(budget.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new TileFetch(StatusCodes.Status503ServiceUnavailable, default);
        }
        finally
        {
            if (holding)
            {
                gate.Release();
            }

            lock (_lock)
            {
                _inflight.Remove(flightKey);
            }
        }
    }
}

/// <summary>
/// Serves <c>GET Videos/{itemId}/Trickplay/{width}/{index}.jpg</c> for a federated item (DESIGN v2.6.5, K30): a cached tile from Home's disk, or a
/// tile fetched lazily from the Peer, validated as a JPEG of the dimensions the metadata promised, re-encoded when the image sandbox is on, cached,
/// and written with Home's own type and headers. The Peer's bytes are never forwarded unvalidated and a Peer-chosen string never reaches a file name.
/// </summary>
public sealed class TrickplayHandler : ITrickplayHandler
{
    private readonly ShoalIndex _index;
    private readonly StubCatalog _catalog;
    private readonly IVersionResolver _resolver;
    private readonly IPeerEndpoints _peers;
    private readonly PeerClient _client;
    private readonly PeerTrust _trust;
    private readonly PeerRegistry _registry;
    private readonly IStateStore _state;
    private readonly TrickplayCache _cache;
    private readonly IImageSanitizer _sanitizer;
    private readonly InterceptCounters _counters;
    private readonly RateLimiter _limiter;
    private readonly Func<bool> _enabled;
    private readonly TileFlights _flights = new();
    private readonly ILogger<TrickplayHandler> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="TrickplayHandler"/> class.
    /// </summary>
    /// <param name="index">The federated items.</param>
    /// <param name="catalog">The catalogs.</param>
    /// <param name="resolver">Maps a route item and a requested media source to a version.</param>
    /// <param name="peers">The peers with their credentials.</param>
    /// <param name="client">The peer client.</param>
    /// <param name="trust">The trust window.</param>
    /// <param name="registry">The runtime peer table.</param>
    /// <param name="state">The state store.</param>
    /// <param name="cache">The tile cache.</param>
    /// <param name="sanitizer">The image sanitizer (the child process, or the pass-through).</param>
    /// <param name="counters">The intercept counters.</param>
    /// <param name="limiter">The per-user rate (40 per second, burst 100).</param>
    /// <param name="enabled">Reads the <c>TrickplayEnabled</c> switch.</param>
    /// <param name="logger">The logger.</param>
    public TrickplayHandler(
        ShoalIndex index,
        StubCatalog catalog,
        IVersionResolver resolver,
        IPeerEndpoints peers,
        PeerClient client,
        PeerTrust trust,
        PeerRegistry registry,
        IStateStore state,
        TrickplayCache cache,
        IImageSanitizer sanitizer,
        InterceptCounters counters,
        RateLimiter limiter,
        Func<bool> enabled,
        ILogger<TrickplayHandler> logger)
    {
        _index = index;
        _catalog = catalog;
        _resolver = resolver;
        _peers = peers;
        _client = client;
        _trust = trust;
        _registry = registry;
        _state = state;
        _cache = cache;
        _sanitizer = sanitizer;
        _counters = counters;
        _limiter = limiter;
        _enabled = enabled;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IActionResult> HandleAsync(FederatedRoute route, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(route);
        var http = route.Http;
        if (!_enabled())
        {
            _counters.Increment(InterceptCounters.CoreHls);
            return new ShoalResult(StatusCodes.Status404NotFound);
        }

        // Credentials and the route item come first: nothing below runs for a caller who may not see the item.
        var decision = await http.RequestServices.GetRequiredService<CallerContext>().AuthorizeAsync(http, route.ItemId, cancellationToken).ConfigureAwait(false);
        if (!decision.Allowed)
        {
            if (decision.Status == StatusCodes.Status401Unauthorized)
            {
                _counters.Increment(InterceptCounters.NoCredentials);
            }

            return new ShoalResult(decision.Status);
        }

        var caller = decision.Caller!;
        if (!TryNumber(RouteValue(http, "width"), 4, out var width) || !TryNumber(RouteValue(http, "index"), 6, out var tileIndex))
        {
            return new ShoalResult(StatusCodes.Status404NotFound);
        }

        var requested = ShoalRequest.Values(http.Request, "mediaSourceId");
        if (requested.Count > 1)
        {
            return new ShoalResult(StatusCodes.Status400BadRequest);
        }

        if (!_limiter.TryTake(caller.UserId))
        {
            return new ShoalResult(StatusCodes.Status429TooManyRequests, null, 1);
        }

        // 2. the version, the stub and the stored level: an unknown width or index is a 404 with no Peer call
        // An extra copy of a grouped title (K45) has no thumbnails: they would come from the owner's server, whose library this caller may not open.
        if (_resolver.Resolve(route.ItemId, requested.Count == 1 ? requested[0] : null) is not { } version
            || _index.Get(version.VersionItemId) is not { IsCopy: false } stub
            || _catalog.Find(stub.PeerKey, stub.FolderName) is not { } entry
            || entry.State is not (EntryState.Active or EntryState.HeldRemoval)
            || !TrickplayLevels.TryFind(entry, version.PeerMediaSourceId, width, out var level)
            || tileIndex >= TrickplayLevels.TileCount(level))
        {
            return new ShoalResult(StatusCodes.Status404NotFound);
        }

        // 3. the cache, which works while the Peer is offline
        _cache.EnsureSignature(stub.PeerKey, version.StubKey, TrickplayLevels.Signature(TrickplayLevels.For(entry, version.PeerMediaSourceId)));
        if (_cache.TryGet(stub.PeerKey, version.StubKey, width, tileIndex) is { } cached)
        {
            return new TileResult(cached);
        }

        // 4. a miss: the Peer state as for a static stream
        var peer = _peers.Find(stub.PeerKey);
        if (peer is null || _state.Status != StateStatus.Ready
            || _registry.StatusOf(stub.PeerKey) is PeerStatus.AuthFailed or PeerStatus.IdentityMismatch or PeerStatus.TlsKeyChanged or PeerStatus.Disabled)
        {
            return new ShoalResult(StatusCodes.Status503ServiceUnavailable);
        }

        var key = $"{version.StubKey}|{width.ToString(CultureInfo.InvariantCulture)}|{tileIndex.ToString(CultureInfo.InvariantCulture)}";
        var fetched = await _flights.RunAsync(
            stub.PeerKey,
            key,
            token => FetchAsync(peer, stub.PeerKey, version, level, width, tileIndex, token),
            cancellationToken).ConfigureAwait(false);
        if (fetched is null)
        {
            return new ShoalResult(StatusCodes.Status429TooManyRequests, null, 2);
        }

        return fetched.Status == StatusCodes.Status200OK && !fetched.Bytes.IsEmpty ? new TileResult(fetched.Bytes) : new ShoalResult(fetched.Status);
    }

    private static string? RouteValue(HttpContext http, string name) =>
        http.Request.RouteValues.TryGetValue(name, out var value) && value is not null ? Convert.ToString(value, CultureInfo.InvariantCulture) : null;

    private static bool TryNumber(string? text, int maxDigits, out int value)
    {
        value = 0;
        return text is { Length: > 0 } && text.Length <= maxDigits && text.All(static c => c is >= '0' and <= '9') && int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }

    // One Peer call, validated and cached. It never throws except for cancellation; the outcome is shared by every joiner.
    private async Task<TileFetch> FetchAsync(PeerEndpoint peer, string peerKey, ResolvedVersion version, TrickplayLevel level, int width, int tileIndex, CancellationToken cancellationToken)
    {
        try
        {
            if (!await _trust.EnsureAsync(peer, TrustRequirement.PinFresh, cancellationToken).ConfigureAwait(false))
            {
                return new TileFetch(StatusCodes.Status503ServiceUnavailable, default);
            }

            var path = string.Create(CultureInfo.InvariantCulture, $"/Videos/{version.PeerItemId:N}/Trickplay/{width}/{tileIndex}.jpg?mediaSourceId={version.PeerMediaSourceId:N}");
            using var response = await _client.SendAsync(peer, PeerRequest.GetMedia(path).WithTrust(TrustRequirement.PinFresh), PeerCallProfile.Trickplay, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return new TileFetch(StatusCodes.Status404NotFound, default);
            }

            if (response.StatusCode != HttpStatusCode.OK || !response.HasBody || !ResponseWriter.IsAcceptedUpstream(ResponseKind.TrickplayTile, response.MediaType))
            {
                return Bad();
            }

            var bytes = await response.ReadBytesAsync(cancellationToken).ConfigureAwait(false);
            if (!IsTile(bytes, level))
            {
                _logger.LogDebug("A trickplay tile from peer {Peer} was refused", Redact.Id(peerKey));
                return Bad();
            }

            var cleaned = await _sanitizer.SanitizeAsync(bytes, new ImageSpec(ImageKind.Tile), cancellationToken).ConfigureAwait(false);
            if (!cleaned.Ok)
            {
                return Bad();
            }

            var final = cleaned.Bytes.Span.SequenceEqual(bytes) ? bytes : cleaned.Bytes.ToArray();
            if (!ReferenceEquals(final, bytes) && !IsTile(final, level))
            {
                return Bad();
            }

            _cache.Store(peerKey, version.StubKey, width, tileIndex, final);
            return new TileFetch(StatusCodes.Status200OK, final);
        }
        catch (PeerCallException ex)
        {
            _logger.LogInformation("A trickplay call to peer {Peer} failed ({Kind})", Redact.Id(peerKey), ex.Kind);
            return ex.Kind switch
            {
                PeerFailureKind.BadResponse or PeerFailureKind.TooLarge or PeerFailureKind.Redirect => Bad(),
                _ => new TileFetch(StatusCodes.Status503ServiceUnavailable, default),
            };
        }
    }

    private static TileFetch Bad() => new(StatusCodes.Status502BadGateway, default);

    // A JPEG (by its magic and a header walk, never a decode) no larger than a tile of the promised dimensions.
    private static bool IsTile(byte[] bytes, TrickplayLevel level) =>
        ImageProbe.Inspect(bytes, out var info, 8192, 8192, ImageProbe.MaxPixels) == ImageRefusal.None
        && info.Format == ImageFormat.Jpeg
        && info.Width <= (long)level.Width * level.TileWidth
        && info.Height <= (long)level.Height * level.TileHeight;

    /// <summary>A validated tile on its way to the client, written with Home's own type.</summary>
    internal sealed class TileResult : IActionResult
    {
        private readonly ReadOnlyMemory<byte> _bytes;

        public TileResult(ReadOnlyMemory<byte> bytes) => _bytes = bytes;

        public async Task ExecuteResultAsync(ActionContext context)
        {
            var response = context.HttpContext.Response;
            response.StatusCode = StatusCodes.Status200OK;
            ResponseWriter.Apply(response, ResponseKind.TrickplayTile);
            response.ContentLength = _bytes.Length;
            await response.Body.WriteAsync(_bytes, context.HttpContext.RequestAborted).ConfigureAwait(false);
        }
    }
}
