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
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

#pragma warning disable SA1600 // internal plumbing; the public surface is documented

namespace Jellyfin.Plugin.Shoal.Playback;

/// <summary>
/// Serves <c>GET Videos/{videoId}/{mediaSourceId}/Attachments/{index}</c> (a font) for a federated item (DESIGN v2.6.6, K31). Only an index that the
/// grant's rebuilt PlaybackInfo listed can be fetched; the Peer call goes through the shared extraction gate (the Peer's extractor runs ffmpeg over the
/// whole source on first use, exactly like subtitle extraction); the answer must be a font by its magic bytes, and Home emits its own type from the magic,
/// never the Peer's. Home never parses a font. Nothing is cached: a font is fetched again on each play.
/// </summary>
public sealed class AttachmentHandler : IAttachmentHandler
{
    /// <summary>The most font requests of one grant that may wait in Home's own queue.</summary>
    public const int MaxQueuedPerGrant = 32;

    private readonly GrantStore _grants;
    private readonly IVersionResolver _resolver;
    private readonly IPeerEndpoints _peers;
    private readonly PeerClient _client;
    private readonly PeerTrust _trust;
    private readonly PeerRegistry _registry;
    private readonly IStateStore _state;
    private readonly PeerExtractionGate _gate;
    private readonly InterceptCounters _counters;
    private readonly RateLimiter _limiter;
    private readonly Func<bool> _enabled;
    private readonly TimeProvider _time;
    private readonly ILogger<AttachmentHandler> _logger;
    private readonly object _lock = new();
    private readonly Dictionary<string, Task<SubtitleFetch>> _flights = new(StringComparer.Ordinal);
    private readonly Dictionary<string, GrantQueue> _queues = new(StringComparer.Ordinal);

    /// <summary>
    /// Initializes a new instance of the <see cref="AttachmentHandler"/> class.
    /// </summary>
    /// <param name="grants">The grants.</param>
    /// <param name="resolver">Maps a route item and a requested media source to a version.</param>
    /// <param name="peers">The peers with their credentials.</param>
    /// <param name="client">The peer client.</param>
    /// <param name="trust">The trust window.</param>
    /// <param name="registry">The runtime peer table.</param>
    /// <param name="state">The state store.</param>
    /// <param name="gate">The shared extraction gate (item lanes, two calls per peer).</param>
    /// <param name="counters">The intercept counters.</param>
    /// <param name="limiter">The per-user rate (60 per minute, its own bucket).</param>
    /// <param name="enabled">Reads the <c>AttachmentsEnabled</c> switch.</param>
    /// <param name="time">The clock.</param>
    /// <param name="logger">The logger.</param>
    public AttachmentHandler(
        GrantStore grants,
        IVersionResolver resolver,
        IPeerEndpoints peers,
        PeerClient client,
        PeerTrust trust,
        PeerRegistry registry,
        IStateStore state,
        PeerExtractionGate gate,
        InterceptCounters counters,
        RateLimiter limiter,
        Func<bool> enabled,
        TimeProvider time,
        ILogger<AttachmentHandler> logger)
    {
        _grants = grants;
        _resolver = resolver;
        _peers = peers;
        _client = client;
        _trust = trust;
        _registry = registry;
        _state = state;
        _gate = gate;
        _counters = counters;
        _limiter = limiter;
        _enabled = enabled;
        _time = time;
        _logger = logger;
    }

    /// <summary>
    /// The font type of a file by its first bytes (DESIGN v2.6.6 step 3).
    /// </summary>
    /// <param name="head">The start of the file.</param>
    /// <returns><c>ttf</c>, <c>otf</c>, <c>collection</c>, <c>woff</c> or <c>woff2</c>, or null when it is not one of the six magics.</returns>
    public static string? FontFormatOf(ReadOnlySpan<byte> head)
    {
        if (head.Length < 4)
        {
            return null;
        }

        if (head[..4].SequenceEqual("\0\u0001\0\0"u8) || head[..4].SequenceEqual("true"u8))
        {
            return "ttf";
        }

        if (head[..4].SequenceEqual("OTTO"u8))
        {
            return "otf";
        }

        if (head[..4].SequenceEqual("ttcf"u8))
        {
            return "collection";
        }

        if (head[..4].SequenceEqual("wOFF"u8))
        {
            return "woff";
        }

        return head[..4].SequenceEqual("wOF2"u8) ? "woff2" : null;
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

        // 1. credentials, the route item, the version and the grant: nothing below runs for a caller who may not see the item
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
        if (!int.TryParse(RouteValue(http, "index"), NumberStyles.None, CultureInfo.InvariantCulture, out var index) || index > AttachmentRebuilder.MaxIndex
            || _resolver.Resolve(route.ItemId, RouteValue(http, "mediaSourceId")) is not { } version)
        {
            return new ShoalResult(StatusCodes.Status404NotFound);
        }

        // 4. the rate comes before any Peer work
        if (!_limiter.TryTake(caller.UserId))
        {
            return new ShoalResult(StatusCodes.Status429TooManyRequests, null, 5);
        }

        var grant = _grants.ForUser(caller.UserId).Where(g => g.LocalItemId == route.ItemId && g.EffectiveVersionItemId == version.VersionItemId).OrderByDescending(static g => g.CreatedUtc).FirstOrDefault();
        if (grant is null || !grant.AttachmentIndexes.Contains(index))
        {
            return new ShoalResult(StatusCodes.Status404NotFound); // no grant, or an index the rebuilt PlaybackInfo never listed: zero Peer calls
        }

        grant.LastAccessUtc = _time.GetUtcNow();
        var peer = _peers.Find(grant.PeerKey);
        if (peer is null || _state.Status != StateStatus.Ready
            || _registry.StatusOf(grant.PeerKey) is PeerStatus.AuthFailed or PeerStatus.IdentityMismatch or PeerStatus.TlsKeyChanged or PeerStatus.Disabled)
        {
            return new ShoalResult(StatusCodes.Status503ServiceUnavailable);
        }

        // 2. the Peer call: identical requests share one, the grant's requests go one at a time into the gate (a client asks for many fonts at once)
        var fetched = await FetchSharedAsync(grant, peer, index, caller.UserId, cancellationToken).ConfigureAwait(false);
        if (fetched is null)
        {
            return new ShoalResult(StatusCodes.Status429TooManyRequests, null, 5);
        }

        if (fetched.Status != StatusCodes.Status200OK)
        {
            return new ShoalResult(fetched.Status, null, fetched.Status == StatusCodes.Status429TooManyRequests ? 30 : null);
        }

        var format = FontFormatOf(fetched.Body.Span);
        if (format is null)
        {
            return new ShoalResult(StatusCodes.Status502BadGateway);
        }

        return grant.TryChargeAttachmentBytes(fetched.Body.Length)
            ? new FontResult(format, fetched.Body)
            : new ShoalResult(StatusCodes.Status404NotFound); // the grant has served its 128 MB
    }

    private static string? RouteValue(HttpContext http, string name) =>
        http.Request.RouteValues.TryGetValue(name, out var value) && value is not null ? Convert.ToString(value, CultureInfo.InvariantCulture) : null;

    private async Task<SubtitleFetch?> FetchSharedAsync(Grant grant, PeerEndpoint peer, int index, Guid userId, CancellationToken cancellationToken)
    {
        var key = grant.GrantId + "|" + index.ToString(CultureInfo.InvariantCulture);
        Task<SubtitleFetch> flight;
        GrantQueue? queue = null;
        lock (_lock)
        {
            if (!_flights.TryGetValue(key, out flight!))
            {
                if (!_queues.TryGetValue(grant.GrantId, out queue))
                {
                    queue = new GrantQueue();
                    _queues[grant.GrantId] = queue;
                }

                if (queue.Waiting >= MaxQueuedPerGrant)
                {
                    return null;
                }

                queue.Waiting++;
                queue.Refs++;
                flight = Task.Run(() => RunFlightAsync(key, grant, peer, index, userId, queue));
                _flights[key] = flight;
            }
        }

        return await flight.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<SubtitleFetch> RunFlightAsync(string key, Grant grant, PeerEndpoint peer, int index, Guid userId, GrantQueue queue)
    {
        var holding = false;
        try
        {
            try
            {
                holding = await queue.Turn.WaitAsync(PeerExtractionGate.Budget).ConfigureAwait(false);
                if (!holding)
                {
                    return new SubtitleFetch(StatusCodes.Status429TooManyRequests, ReadOnlyMemory<byte>.Empty);
                }
            }
            finally
            {
                lock (_lock)
                {
                    queue.Waiting--;
                }
            }

            var path = string.Create(CultureInfo.InvariantCulture, $"/Videos/{grant.PeerItemId:N}/{grant.PeerMediaSourceId:N}/Attachments/{index}");
            var itemKey = $"{grant.PeerKey}|{grant.PeerItemId:N}|{grant.PeerMediaSourceId:N}";
            var result = await _gate.RunAsync(grant.PeerKey, itemKey, itemKey + "|" + path, userId, exemptFromUserLane: false, token => CallPeerAsync(peer, path, grant.PeerKey, token), CancellationToken.None).ConfigureAwait(false);
            return result.Status switch
            {
                BrokerStatus.Done when result.Fetch is { } done => done,
                _ => new SubtitleFetch(StatusCodes.Status429TooManyRequests, ReadOnlyMemory<byte>.Empty),
            };
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            return new SubtitleFetch(StatusCodes.Status429TooManyRequests, ReadOnlyMemory<byte>.Empty);
        }
        finally
        {
            if (holding)
            {
                queue.Turn.Release();
            }

            lock (_lock)
            {
                _flights.Remove(key);
                if (--queue.Refs == 0)
                {
                    _queues.Remove(grant.GrantId);
                }
            }
        }
    }

    // One Peer call, validated (steps 2 and 3). It never throws except for cancellation; the outcome is shared by every joiner.
    private async Task<SubtitleFetch> CallPeerAsync(PeerEndpoint peer, string path, string peerKey, CancellationToken cancellationToken)
    {
        try
        {
            if (!await _trust.EnsureAsync(peer, TrustRequirement.PinFresh, cancellationToken).ConfigureAwait(false))
            {
                return new SubtitleFetch(StatusCodes.Status503ServiceUnavailable, ReadOnlyMemory<byte>.Empty);
            }

            using var response = await _client.SendAsync(peer, PeerRequest.GetMedia(path).WithTrust(TrustRequirement.PinFresh), PeerCallProfile.AttachmentFile, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return new SubtitleFetch(StatusCodes.Status404NotFound, ReadOnlyMemory<byte>.Empty);
            }

            if (response.StatusCode != HttpStatusCode.OK || !response.HasBody)
            {
                return new SubtitleFetch(StatusCodes.Status502BadGateway, ReadOnlyMemory<byte>.Empty); // the declared type is never looked at: the magic decides
            }

            var bytes = await response.ReadBytesAsync(cancellationToken).ConfigureAwait(false);
            return bytes.Length > 0 && FontFormatOf(bytes) is not null
                ? new SubtitleFetch(StatusCodes.Status200OK, bytes)
                : new SubtitleFetch(StatusCodes.Status502BadGateway, ReadOnlyMemory<byte>.Empty);
        }
        catch (PeerCallException ex)
        {
            _logger.LogInformation("An attachment call to peer {Peer} failed ({Kind})", Redact.Id(peerKey), ex.Kind);
            return ex.Kind switch
            {
                PeerFailureKind.BadResponse or PeerFailureKind.TooLarge or PeerFailureKind.Redirect => new SubtitleFetch(StatusCodes.Status502BadGateway, ReadOnlyMemory<byte>.Empty),
                _ => new SubtitleFetch(StatusCodes.Status503ServiceUnavailable, ReadOnlyMemory<byte>.Empty),
            };
        }
    }

    /// <summary>A validated font on its way to the client, written with Home's own type from the magic.</summary>
    internal sealed class FontResult : IActionResult
    {
        private readonly string _format;
        private readonly ReadOnlyMemory<byte> _bytes;

        public FontResult(string format, ReadOnlyMemory<byte> bytes)
        {
            _format = format;
            _bytes = bytes;
        }

        public async Task ExecuteResultAsync(ActionContext context)
        {
            var response = context.HttpContext.Response;
            response.StatusCode = StatusCodes.Status200OK;
            if (!ResponseWriter.Apply(response, ResponseKind.Font, _format))
            {
                response.StatusCode = StatusCodes.Status502BadGateway;
                return;
            }

            response.ContentLength = _bytes.Length;
            await response.Body.WriteAsync(_bytes, context.HttpContext.RequestAborted).ConfigureAwait(false);
        }
    }

    private sealed class GrantQueue
    {
        public SemaphoreSlim Turn { get; } = new(1, 1);

        public int Waiting { get; set; }

        public int Refs { get; set; }
    }
}
