#pragma warning disable SA1600 // internal plumbing; the public surface is documented

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
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Shoal.Playback;

/// <summary>
/// Answers subtitle files of federated items (DESIGN 6.6a): <c>Shoal/Subtitles/{item}/{index}/Stream.{fmt}</c> and the intercepted core routes.
/// </summary>
public sealed class SubtitleHandler : ISubtitleHandler
{
    private static readonly string[] TextCodecs = ["subrip", "srt", "ass", "ssa", "webvtt", "vtt", "mov_text"];
    private static readonly string[] Formats = ["vtt", "srt", "ass", "ssa", "js", "json"];

    private readonly GrantStore _grants;
    private readonly StubCatalog _catalog;
    private readonly IPeerEndpoints _peers;
    private readonly PeerClient _client;
    private readonly PeerTrust _trust;
    private readonly PeerRegistry _registry;
    private readonly IStateStore _state;
    private readonly IOfflineNotice _notice;
    private readonly PeerExtractionGate _broker;
    private readonly RateLimiter _limiter;
    private readonly TimeProvider _time;
    private readonly ILogger<SubtitleHandler> _logger;
    private readonly IVersionResolver? _versions;

    /// <summary>
    /// Initializes a new instance of the <see cref="SubtitleHandler"/> class.
    /// </summary>
    /// <param name="grants">The grants.</param>
    /// <param name="catalog">The catalog lookup.</param>
    /// <param name="peers">The peers with their credentials.</param>
    /// <param name="client">The peer client.</param>
    /// <param name="trust">The trust window.</param>
    /// <param name="registry">The runtime peer table.</param>
    /// <param name="state">The state store.</param>
    /// <param name="notice">The offline notice.</param>
    /// <param name="broker">The coalescing and gating of Peer calls.</param>
    /// <param name="limiter">The per-user rate (30 per minute).</param>
    /// <param name="time">The clock.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="versions">Maps the media source of the route to a version of the title (DESIGN v2.6.4); none means the title has one version.</param>
    public SubtitleHandler(
        GrantStore grants,
        StubCatalog catalog,
        IPeerEndpoints peers,
        PeerClient client,
        PeerTrust trust,
        PeerRegistry registry,
        IStateStore state,
        IOfflineNotice notice,
        PeerExtractionGate broker,
        RateLimiter limiter,
        TimeProvider time,
        ILogger<SubtitleHandler> logger,
        IVersionResolver? versions = null)
    {
        _versions = versions;
        _grants = grants;
        _catalog = catalog;
        _peers = peers;
        _client = client;
        _trust = trust;
        _registry = registry;
        _state = state;
        _notice = notice;
        _broker = broker;
        _limiter = limiter;
        _time = time;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IActionResult> HandleAsync(FederatedRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            return await HandleCoreAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031 // The boundary rule (6.1): the exception type is logged, nothing reaches core's exception middleware.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _logger.LogWarning("The subtitle handler failed unexpectedly ({Type})", ex.GetType().Name);
            return new ShoalResult(StatusCodes.Status500InternalServerError);
        }
    }

    /// <summary>
    /// Serves one HLS subtitle segment (<c>subs/{index}/stream.vtt</c>, DESIGN 6.4): the same gate, lanes and rebuild as a subtitle file, but without
    /// the one-open-lane-per-user rule.
    /// </summary>
    /// <param name="grant">The grant.</param>
    /// <param name="caller">The caller.</param>
    /// <param name="peer">The peer.</param>
    /// <param name="index">The text subtitle stream (already checked against the grant).</param>
    /// <param name="start">The validated start in ticks.</param>
    /// <param name="end">The validated end in ticks.</param>
    /// <param name="cancellationToken">The request's abort token.</param>
    /// <returns>The segment.</returns>
    public async Task<IActionResult> HlsSegmentAsync(Grant grant, Caller caller, PeerEndpoint peer, int index, long start, long end, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(grant);
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(peer);
        if (!_limiter.TryTake(caller.UserId))
        {
            return new ShoalResult(StatusCodes.Status429TooManyRequests, null, 5);
        }

        var path = $"/Videos/{grant.PeerItemId:N}/{grant.PeerMediaSourceId:N}/Subtitles/{index.ToString(CultureInfo.InvariantCulture)}/stream.vtt?CopyTimestamps=true&AddVttTimeMap=true&StartPositionTicks={start.ToString(CultureInfo.InvariantCulture)}&EndPositionTicks={end.ToString(CultureInfo.InvariantCulture)}";
        return await BrokeredAsync(grant, peer, caller.UserId, path, "vtt", exemptFromUserLane: true, cancellationToken).ConfigureAwait(false);
    }

    private static string? RouteValue(HttpContext http, params string[] names)
    {
        foreach (var name in names)
        {
            if (http.Request.RouteValues.TryGetValue(name, out var value) && value is not null)
            {
                return Convert.ToString(value, CultureInfo.InvariantCulture);
            }
        }

        return null;
    }

    private static ResponseKind KindOf(string format) => format switch
    {
        "vtt" => ResponseKind.SubtitleVtt,
        "srt" => ResponseKind.SubtitleSrt,
        "ass" or "ssa" => ResponseKind.SubtitleAss,
        _ => ResponseKind.SubtitleJson,
    };

    private static bool TryBool(HttpContext http, string key, out bool? value)
    {
        value = null;
        var values = ShoalRequest.Values(http.Request, key);
        if (values.Count == 0)
        {
            return true;
        }

        if (values.Count > 1 || !bool.TryParse(values[0], out var parsed))
        {
            return false;
        }

        value = parsed;
        return true;
    }

    private async Task<IActionResult> HandleCoreAsync(FederatedRequest request, CancellationToken cancellationToken)
    {
        var http = request.Http;
        var caller = request.Caller;

        // 2. route values only; {ms} must be a member of the title's version set when the route has one (the item itself when there is one version)
        Guid? routeVersion = null;
        if (RouteValue(http, "routeMediaSourceId") is { } routeSource)
        {
            if (!ShoalRequest.TryParseGuid(routeSource, out var named) || (named != request.ItemId && (_versions is null || _versions.Resolve(request.ItemId, routeSource) is null)))
            {
                return new ShoalResult(StatusCodes.Status404NotFound);
            }

            routeVersion = named;
        }

        // 3. the format
        var format = (RouteValue(http, "routeFormat", "fmt") ?? string.Empty).ToLowerInvariant();
        if (!Formats.Contains(format, StringComparer.Ordinal) || !int.TryParse(RouteValue(http, "routeIndex", "index"), NumberStyles.None, CultureInfo.InvariantCulture, out var index))
        {
            return new ShoalResult(StatusCodes.Status404NotFound);
        }

        if (!_limiter.TryTake(caller.UserId))
        {
            return new ShoalResult(StatusCodes.Status429TooManyRequests, null, 5);
        }

        // 4. the newest live grant of (caller, item)
        var grant = _grants.ForUser(caller.UserId).Where(g => g.LocalItemId == request.ItemId).OrderByDescending(static g => g.CreatedUtc).FirstOrDefault();
        if (grant is null)
        {
            return new ShoalResult(StatusCodes.Status404NotFound);
        }

        if (routeVersion is { } wanted && wanted != grant.EffectiveVersionItemId)
        {
            return new ShoalResult(StatusCodes.Status404NotFound); // the grant is for another version: its tracks are not this version's
        }

        grant.LastAccessUtc = _time.GetUtcNow();

        // 5. a text stream of the grant, by an allowlist of codecs
        var stream = grant.Streams.FirstOrDefault(s => s.Index == index && string.Equals(s.Type, "Subtitle", StringComparison.Ordinal));
        if (stream is null || stream.Codec is null || !TextCodecs.Contains(stream.Codec.ToLowerInvariant(), StringComparer.Ordinal))
        {
            return new ShoalResult(StatusCodes.Status404NotFound);
        }

        // 6. mapped client values: anything invalid or repeated is a 400
        var limit = grant.RuntimeTicks + 36_000_000_000L;
        long ticks = 0;
        if (RouteValue(http, "routeStartPositionTicks", "ticks") is { } rawTicks
            && (!long.TryParse(rawTicks, NumberStyles.None, CultureInfo.InvariantCulture, out ticks) || ticks > grant.RuntimeTicks))
        {
            return new ShoalResult(StatusCodes.Status400BadRequest);
        }

        long? end = null;
        var ends = ShoalRequest.Values(http.Request, "endPositionTicks");
        if (ends.Count > 1 || (ends.Count == 1 && (!long.TryParse(ends[0], NumberStyles.None, CultureInfo.InvariantCulture, out var parsedEnd) || parsedEnd < ticks || parsedEnd > limit)))
        {
            return new ShoalResult(StatusCodes.Status400BadRequest);
        }

        if (ends.Count == 1)
        {
            end = long.Parse(ends[0], NumberStyles.None, CultureInfo.InvariantCulture);
        }

        if (!TryBool(http, "copyTimestamps", out var copy) || !TryBool(http, "addVttTimeMap", out var map))
        {
            return new ShoalResult(StatusCodes.Status400BadRequest);
        }

        // 7. the Peer request
        // The server is the grant's: the stub's own, or for a grouped title the member its PlaybackInfo picked (PLAN v2.9 K42).
        var entry = _catalog.Find(request.Stub.PeerKey, request.Stub.FolderName);
        var peer = _peers.Find(grant.PeerKey);
        if (entry is null || peer is null || _state.Status != StateStatus.Ready
            || _registry.StatusOf(grant.PeerKey) is PeerStatus.AuthFailed or PeerStatus.IdentityMismatch or PeerStatus.TlsKeyChanged or PeerStatus.Disabled)
        {
            return new ShoalResult(StatusCodes.Status503ServiceUnavailable);
        }

        var sentFormat = format == "json" ? "js" : format;
        var path = $"/Videos/{grant.PeerItemId:N}/{grant.PeerMediaSourceId:N}/Subtitles/{index.ToString(CultureInfo.InvariantCulture)}/{ticks.ToString(CultureInfo.InvariantCulture)}/Stream.{sentFormat}";
        var query = new List<string>();
        if (end is { } e)
        {
            query.Add("endPositionTicks=" + e.ToString(CultureInfo.InvariantCulture));
        }

        if (copy is { } c)
        {
            query.Add("copyTimestamps=" + (c ? "true" : "false"));
        }

        if (sentFormat == "vtt" && map is { } m)
        {
            query.Add("addVttTimeMap=" + (m ? "true" : "false"));
        }

        var pathAndQuery = query.Count == 0 ? path : path + "?" + string.Join('&', query);
        return await BrokeredAsync(grant, peer, caller.UserId, pathAndQuery, format, exemptFromUserLane: false, cancellationToken).ConfigureAwait(false);
    }

    private async Task<IActionResult> BrokeredAsync(Grant grant, PeerEndpoint peer, Guid userId, string pathAndQuery, string format, bool exemptFromUserLane, CancellationToken cancellationToken)
    {
        var itemKey = $"{grant.PeerKey}|{grant.PeerItemId:N}|{grant.PeerMediaSourceId:N}";
        var requestKey = itemKey + "|" + pathAndQuery;
        var result = await _broker.RunAsync(
            grant.PeerKey,
            itemKey,
            requestKey,
            userId,
            exemptFromUserLane,
            token => FetchAsync(peer, pathAndQuery, format, grant, userId, token),
            cancellationToken).ConfigureAwait(false);

        switch (result.Status)
        {
            case BrokerStatus.Busy:
            case BrokerStatus.UserLane:
                return new ShoalResult(StatusCodes.Status429TooManyRequests, null, result.RetryAfterSeconds);
            default:
                return result.Fetch is { Status: StatusCodes.Status200OK } ok
                    ? new SubtitleBodyResult(KindOf(format), ok.Body)
                    : new ShoalResult(result.Fetch?.Status ?? StatusCodes.Status502BadGateway);
        }
    }

    // One Peer call, validated and rebuilt (steps 8, 9, 9a). It never throws except for cancellation; the outcome is shared by every joiner.
    private async Task<SubtitleFetch> FetchAsync(PeerEndpoint peer, string pathAndQuery, string format, Grant grant, Guid userId, CancellationToken cancellationToken)
    {
        try
        {
            if (!await _trust.EnsureAsync(peer, TrustRequirement.PinFresh, cancellationToken).ConfigureAwait(false))
            {
                _notice.PeerOffline(userId, grant.PeerKey, peer.DisplayName);
                return new SubtitleFetch(StatusCodes.Status503ServiceUnavailable, ReadOnlyMemory<byte>.Empty);
            }

            using var response = await _client.SendAsync(peer, PeerRequest.GetMedia(pathAndQuery).WithTrust(TrustRequirement.PinFresh), PeerCallProfile.SubtitleFile, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return new SubtitleFetch(StatusCodes.Status404NotFound, ReadOnlyMemory<byte>.Empty);
            }

            var kind = KindOf(format);
            if (response.StatusCode != HttpStatusCode.OK || !response.HasBody || !ResponseWriter.IsAcceptedUpstream(kind, response.MediaType))
            {
                return new SubtitleFetch(StatusCodes.Status502BadGateway, ReadOnlyMemory<byte>.Empty);
            }

            var bytes = await response.ReadBytesAsync(cancellationToken).ConfigureAwait(false);
            return Rebuild(format, kind, bytes, grant, peer);
        }
        catch (PeerCallException ex)
        {
            _logger.LogInformation("A subtitle call to peer {Peer} failed ({Kind})", Redact.Id(grant.PeerKey), ex.Kind);
            return ex.Kind switch
            {
                PeerFailureKind.BadResponse or PeerFailureKind.TooLarge or PeerFailureKind.Redirect => new SubtitleFetch(StatusCodes.Status502BadGateway, ReadOnlyMemory<byte>.Empty),
                _ => new SubtitleFetch(StatusCodes.Status503ServiceUnavailable, ReadOnlyMemory<byte>.Empty),
            };
        }
    }

    private static SubtitleFetch Rebuild(string format, ResponseKind kind, byte[] bytes, Grant grant, PeerEndpoint peer)
    {
        SubtitleResult rebuilt;
        switch (format)
        {
            case "vtt":
                rebuilt = SubtitleRebuilder.Vtt(bytes, grant.RuntimeTicks);
                break;
            case "srt":
                rebuilt = SubtitleRebuilder.Srt(bytes, grant.RuntimeTicks);
                break;
            case "js":
            case "json":
                rebuilt = SubtitleRebuilder.Json(bytes, grant.RuntimeTicks);
                break;
            default:
                if (!SubtitleBodyCheck.IsAcceptable(kind, bytes))
                {
                    return new SubtitleFetch(StatusCodes.Status502BadGateway, ReadOnlyMemory<byte>.Empty);
                }

                rebuilt = new SubtitleResult(SubtitleFault.None, bytes, 0, 0);
                break;
        }

        if (!rebuilt.Ok)
        {
            return new SubtitleFetch(StatusCodes.Status502BadGateway, ReadOnlyMemory<byte>.Empty);
        }

        var text = System.Text.Encoding.UTF8.GetString(rebuilt.Body);
        return PlaybackInfoBuilder.CanaryHit(text, peer.Token, [grant.PeerItemId, grant.PeerMediaSourceId], grant.PeerPlaySessionId, peer.BaseUri.Host)
            ? new SubtitleFetch(StatusCodes.Status502BadGateway, ReadOnlyMemory<byte>.Empty)
            : new SubtitleFetch(StatusCodes.Status200OK, rebuilt.Body);
    }
}

/// <summary>
/// A rebuilt subtitle on its way to the client, written with Home's own type for the format.
/// </summary>
internal sealed class SubtitleBodyResult : IActionResult
{
    private readonly ResponseKind _kind;
    private readonly ReadOnlyMemory<byte> _body;

    public SubtitleBodyResult(ResponseKind kind, ReadOnlyMemory<byte> body)
    {
        _kind = kind;
        _body = body;
    }

    public async Task ExecuteResultAsync(ActionContext context)
    {
        var response = context.HttpContext.Response;
        response.StatusCode = StatusCodes.Status200OK;
        ResponseWriter.Apply(response, _kind);
        response.ContentLength = _body.Length;
        await response.Body.WriteAsync(_body, context.HttpContext.RequestAborted).ConfigureAwait(false);
    }
}
