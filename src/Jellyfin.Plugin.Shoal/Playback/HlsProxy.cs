#pragma warning disable SA1600 // internal plumbing; the public surface is documented

using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.Authorization;
using Jellyfin.Plugin.Shoal.Http;
using Jellyfin.Plugin.Shoal.Peers;
using Jellyfin.Plugin.Shoal.Security;
using Jellyfin.Plugin.Shoal.State;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Shoal.Playback;

/// <summary>
/// The routes of the HLS proxy (DESIGN 6.4).
/// </summary>
public enum HlsRoute
{
    /// <summary><c>master.m3u8</c>.</summary>
    Master,

    /// <summary><c>main.m3u8</c>.</summary>
    Main,

    /// <summary><c>hls1/main/{segmentId}.{ext}</c>.</summary>
    Segment,

    /// <summary><c>subs/{index}/subtitles.m3u8</c>.</summary>
    SubtitlePlaylist,

    /// <summary><c>subs/{index}/stream.vtt</c>.</summary>
    SubtitleSegment,
}

/// <summary>
/// One request on <c>/Shoal/Hls/{grantId}/...</c>.
/// </summary>
/// <param name="Route">Which route.</param>
/// <param name="GrantId">The grant id from the path.</param>
/// <param name="SegmentId">The segment number (-1 to 999999) of a segment route.</param>
/// <param name="Extension">The segment extension, <c>ts</c> or <c>mp4</c>.</param>
/// <param name="SubtitleIndex">The subtitle stream index of a subtitle route.</param>
/// <param name="Head">Whether the request is a HEAD: everything is checked, nothing is started and no body is sent.</param>
public sealed record HlsRequest(HlsRoute Route, string GrantId, int SegmentId = 0, string Extension = "", int SubtitleIndex = 0, bool Head = false);

/// <summary>
/// The exact requests that can end a Peer transcode (DESIGN 6.11). Built here so one unit test can pin the hard rule of 6.8: a kill request never
/// carries an empty PlaySessionId, because the Peer would then stop every job of Home's shared DeviceId.
/// </summary>
public static class PeerJobRequests
{
    /// <summary>
    /// Builds <c>DELETE /Videos/ActiveEncodings?deviceId=..&amp;playSessionId=..</c>.
    /// </summary>
    /// <param name="deviceId">Home's pairing DeviceId at the Peer.</param>
    /// <param name="peerPlaySessionId">The grant's PlaySessionId at the Peer; never empty.</param>
    /// <returns>The request.</returns>
    /// <exception cref="ArgumentException">A value is empty.</exception>
    public static PeerRequest ScopedKill(string deviceId, string peerPlaySessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(peerPlaySessionId);
        return PeerRequest.Delete("/Videos/ActiveEncodings?deviceId=" + Uri.EscapeDataString(deviceId) + "&playSessionId=" + Uri.EscapeDataString(peerPlaySessionId))
            .WithTrust(TrustRequirement.PinAndAuthFresh);
    }
}

/// <summary>
/// Stops the Peer job behind a grant: the scoped <c>ActiveEncodings</c> delete (DESIGN 6.1, 6.8), at most once per started job.
/// </summary>
public sealed class HlsJobControl
{
    private readonly IPeerEndpoints _peers;
    private readonly PeerClient _client;
    private readonly ILogger<HlsJobControl> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="HlsJobControl"/> class.
    /// </summary>
    /// <param name="peers">The peers with their credentials.</param>
    /// <param name="client">The peer client.</param>
    /// <param name="logger">The logger.</param>
    public HlsJobControl(IPeerEndpoints peers, PeerClient client, ILogger<HlsJobControl> logger)
    {
        _peers = peers;
        _client = client;
        _logger = logger;
    }

    /// <summary>
    /// Sends the scoped delete for a <c>Transcode</c> grant whose Peer job was started, and clears the flag when the Peer answered.
    /// </summary>
    /// <param name="grant">The grant.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>True when the delete was sent and answered (the slots may then be released).</returns>
    public async Task<bool> SendScopedKillAsync(Grant grant, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(grant);
        if (grant.Method != PlayMethod.Transcode || !grant.PeerJobStarted || string.IsNullOrEmpty(grant.PeerPlaySessionId))
        {
            return false;
        }

        var peer = _peers.Find(grant.PeerKey);
        if (peer is null)
        {
            return false;
        }

        try
        {
            using var response = await _client.SendAsync(peer, PeerJobRequests.ScopedKill(peer.DeviceId, grant.PeerPlaySessionId), PeerCallProfile.SessionReport, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccess)
            {
                _logger.LogInformation("Peer {Peer} answered the job stop with HTTP {Status}", Redact.Id(grant.PeerKey), (int)response.StatusCode);
                return false;
            }

            grant.PeerJobStarted = false;
            return true;
        }
        catch (PeerCallException ex)
        {
            _logger.LogInformation("The job stop for peer {Peer} failed ({Kind})", Redact.Id(grant.PeerKey), ex.Kind);
            return false;
        }
    }
}

/// <summary>
/// Answers the client's <c>DELETE Videos/ActiveEncodings</c> for a grant it owns (DESIGN 6.1, W4-4): a started conversion gets the scoped delete and
/// only then gives up its slots; any other grant is left alone. The answer is 204 in every matched case.
/// </summary>
public sealed class ActiveEncodingsHandler : IActiveEncodingsHandler
{
    private readonly GrantStore _grants;
    private readonly HlsJobControl _jobs;
    private readonly StreamSlots _slots;
    private readonly RateLimiter _limiter;

    /// <summary>
    /// Initializes a new instance of the <see cref="ActiveEncodingsHandler"/> class.
    /// </summary>
    /// <param name="grants">The grants.</param>
    /// <param name="jobs">The job control.</param>
    /// <param name="slots">The stream slots.</param>
    /// <param name="limiter">The per-user limit (10 per minute).</param>
    public ActiveEncodingsHandler(GrantStore grants, HlsJobControl jobs, StreamSlots slots, RateLimiter limiter)
    {
        _grants = grants;
        _jobs = jobs;
        _slots = slots;
        _limiter = limiter;
    }

    /// <inheritdoc />
    public async Task<IActionResult?> HandleAsync(HttpContext http, Guid? userId, string? deviceId, string? playSessionId, CancellationToken cancellationToken)
    {
        if (userId is not { } user || string.IsNullOrEmpty(playSessionId))
        {
            return null; // core's own handling (and its own authorization)
        }

        var grant = _grants.FindByPlaySession(user, playSessionId);
        if (grant is null)
        {
            return null;
        }

        if (!_limiter.TryTake(user))
        {
            return new ShoalResult(StatusCodes.Status429TooManyRequests, null, 6);
        }

        if (grant.Method == PlayMethod.Transcode && grant.PeerJobStarted && await _jobs.SendScopedKillAsync(grant, cancellationToken).ConfigureAwait(false))
        {
            _slots.ReleaseHls(grant.GrantId);
        }

        return new ShoalResult(StatusCodes.Status204NoContent);
    }
}

/// <summary>
/// The HLS proxy (DESIGN 6.4): every request names a grant; the Peer request is built from the grant and the validated integers only; playlists go
/// through <see cref="PlaylistGrammar"/>; segments are copied under the byte and idle limits and hold a stream slot and a conversion slot.
/// </summary>
public sealed class HlsProxyHandler
{
    private const int MaxSegmentsPerGrant = 3;
    private const int SegmentGateSlots = 8;

    private static readonly TimeSpan Reauthorize = TimeSpan.FromMinutes(5);
    private static readonly Regex SegmentLine = new("^hls1/main/(\\d+)\\.(ts|mp4)\\?ApiKey=[^&\\n]*&runtimeTicks=(\\d+)&actualSegmentLengthTicks=(\\d+)$", RegexOptions.CultureInvariant | RegexOptions.Multiline, TimeSpan.FromMilliseconds(100));
    private static readonly Regex GrantIdShape = new("^[A-Za-z0-9_-]{22}\\z", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(100));

    private readonly GrantStore _grants;
    private readonly IPeerEndpoints _peers;
    private readonly PeerClient _client;
    private readonly PeerTrust _trust;
    private readonly PeerRegistry _registry;
    private readonly StreamSlots _slots;
    private readonly IStateStore _state;
    private readonly IOfflineNotice _notice;
    private readonly SubtitleHandler _subtitles;
    private readonly RateLimiter _limiter;
    private readonly TimeProvider _time;
    private readonly ILogger<HlsProxyHandler> _logger;
    private readonly Jellyfin.Plugin.Shoal.Diagnostics.ISegmentTelemetry? _telemetry;
    private readonly HlsReadAhead? _readAhead;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _segmentGates = new(StringComparer.Ordinal);

    /// <summary>
    /// Initializes a new instance of the <see cref="HlsProxyHandler"/> class.
    /// </summary>
    /// <param name="grants">The grants.</param>
    /// <param name="peers">The peers with their credentials.</param>
    /// <param name="client">The peer client.</param>
    /// <param name="trust">The trust window.</param>
    /// <param name="registry">The runtime peer table.</param>
    /// <param name="slots">The stream slots.</param>
    /// <param name="state">The state store.</param>
    /// <param name="notice">The offline notice.</param>
    /// <param name="subtitles">The subtitle handler (HLS subtitle segments).</param>
    /// <param name="limiter">The per-grant request limit (10 per second, burst 40).</param>
    /// <param name="time">The clock.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="telemetry">Watches the segments for the speed diagnostics, or null.</param>
    /// <param name="readAhead">Reads the next segments of a stream ahead, or null (none).</param>
    public HlsProxyHandler(
        GrantStore grants,
        IPeerEndpoints peers,
        PeerClient client,
        PeerTrust trust,
        PeerRegistry registry,
        StreamSlots slots,
        IStateStore state,
        IOfflineNotice notice,
        SubtitleHandler subtitles,
        RateLimiter limiter,
        TimeProvider time,
        ILogger<HlsProxyHandler> logger,
        Jellyfin.Plugin.Shoal.Diagnostics.ISegmentTelemetry? telemetry = null,
        HlsReadAhead? readAhead = null)
    {
        _readAhead = readAhead;
        _telemetry = telemetry;
        _grants = grants;
        _peers = peers;
        _client = client;
        _trust = trust;
        _registry = registry;
        _slots = slots;
        _state = state;
        _notice = notice;
        _subtitles = subtitles;
        _limiter = limiter;
        _time = time;
        _logger = logger;
    }

    /// <summary>
    /// Handles one request.
    /// </summary>
    /// <param name="callerContext">The request's authorization.</param>
    /// <param name="http">The request.</param>
    /// <param name="request">The route and its path values.</param>
    /// <param name="cancellationToken">The request's abort token.</param>
    /// <returns>The result to send.</returns>
    public async Task<IActionResult> HandleAsync(CallerContext callerContext, HttpContext http, HlsRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(callerContext);
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            return await HandleCoreAsync(callerContext, http, request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031 // The boundary rule (6.1): the exception type is logged, nothing reaches core's exception middleware.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _logger.LogWarning("The HLS proxy failed unexpectedly ({Type})", ex.GetType().Name);
            return new ShoalResult(StatusCodes.Status500InternalServerError);
        }
    }

    private static bool TryTicks(Microsoft.AspNetCore.Http.HttpRequest request, string key, long max, out long value)
    {
        value = 0;
        var values = ShoalRequest.Values(request, key);
        return values.Count == 1 && values[0].Length is >= 1 and <= 19 && values[0].All(static c => c is >= '0' and <= '9')
            && long.TryParse(values[0], NumberStyles.None, CultureInfo.InvariantCulture, out value) && value <= max;
    }

    private static Guid KeyOf(string grantId) => new(SHA256.HashData(Encoding.UTF8.GetBytes(grantId))[..16]);

    private static string QueryOf(System.Collections.Generic.IEnumerable<System.Collections.Generic.KeyValuePair<string, string>> pairs) =>
        string.Join('&', pairs.Select(static p => Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(p.Value)));

    private static bool IsQ(string value) => value.All(static c => c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '.' or '_' or '~' or '%' or '=' or '&' or ',' or '+' or '-');

    private async Task<IActionResult> HandleCoreAsync(CallerContext callerContext, HttpContext http, HlsRequest request, CancellationToken cancellationToken)
    {
        if (ShoalRequest.QueryTooLong(http.Request))
        {
            return new ShoalResult(StatusCodes.Status414UriTooLong);
        }

        var grant = GrantIdShape.IsMatch(request.GrantId) ? _grants.Get(request.GrantId) : null;
        var now = _time.GetUtcNow();
        var full = grant is not null && now - grant.LastAuthorizedUtc >= Reauthorize;

        // CallerContext steps 1 to 4 on every request; steps 5 to 7 again when the last full check is older than five minutes (6.4, W§A-19).
        var decision = await callerContext.AuthorizeGrantAsync(http, grant?.LocalUserId ?? Guid.Empty, grant?.LocalItemId ?? Guid.Empty, full, cancellationToken).ConfigureAwait(false);
        if (decision.Caller is not { } caller)
        {
            if (grant is not null && decision.DeniedAtStep >= 5)
            {
                _slots.ReleaseHls(grant.GrantId);
                _grants.Remove(grant.GrantId); // access was taken away: the grant dies with it (R13, R14)
                return new ShoalResult(StatusCodes.Status404NotFound);
            }

            return new ShoalResult(decision.Status);
        }

        if (grant is null || grant.Method != PlayMethod.Transcode)
        {
            return new ShoalResult(StatusCodes.Status404NotFound);
        }

        if (full)
        {
            grant.LastAuthorizedUtc = now;
        }

        if (!_limiter.TryTake(KeyOf(grant.GrantId)))
        {
            return new ShoalResult(StatusCodes.Status429TooManyRequests, null, 1);
        }

        grant.LastAccessUtc = now;
        var peer = _peers.Find(grant.PeerKey);
        var peerState = _state.Current.Home.Peers.FirstOrDefault(p => p.PeerKey == grant.PeerKey);
        if (peer is null || peerState is null || _state.Status != StateStatus.Ready
            || _registry.StatusOf(grant.PeerKey) is PeerStatus.AuthFailed or PeerStatus.IdentityMismatch or PeerStatus.TlsKeyChanged or PeerStatus.Disabled)
        {
            return new ShoalResult(StatusCodes.Status503ServiceUnavailable);
        }

        var context = new PlaylistContext(
            caller.Token,
            grant.PeerMediaSourceId,
            grant.PeerPlaySessionId,
            peer.DeviceId,
            grant.RuntimeTicks,
            grant.Streams,
            text => PlaybackInfoBuilder.CanaryHit(text, peer.Token, [grant.PeerItemId, grant.PeerMediaSourceId], grant.PeerPlaySessionId, peer.BaseUri.Host));
        try
        {
            var outcome = request.Route switch
            {
                HlsRoute.Master => await MasterAsync(http, grant, peer, context, caller, cancellationToken).ConfigureAwait(false),
                HlsRoute.Main => await MainAsync(grant, peer, context, caller, cancellationToken).ConfigureAwait(false),
                HlsRoute.Segment => await SegmentAsync(http, grant, peer, peerState, request, caller, cancellationToken).ConfigureAwait(false),
                HlsRoute.SubtitlePlaylist => await SubtitlePlaylistAsync(grant, peer, context, caller, request.SubtitleIndex, cancellationToken).ConfigureAwait(false),
                HlsRoute.SubtitleSegment => await SubtitleSegmentAsync(http, grant, peer, caller, request.SubtitleIndex, request.Head, cancellationToken).ConfigureAwait(false),
                _ => new ShoalResult(StatusCodes.Status404NotFound),
            };
            return request.Head && outcome is HlsPlaylistResult playlist ? playlist.AsHead() : outcome;
        }
        catch (PeerCallException ex)
        {
            _logger.LogInformation("An HLS call to peer {Peer} failed ({Kind})", Redact.Id(grant.PeerKey), ex.Kind);
            return ex.Kind switch
            {
                PeerFailureKind.Blocked => new ShoalResult(StatusCodes.Status503ServiceUnavailable),
                PeerFailureKind.BadResponse or PeerFailureKind.TooLarge or PeerFailureKind.Redirect => new ShoalResult(StatusCodes.Status502BadGateway),
                _ => Offline(caller.UserId, grant.PeerKey, peer.DisplayName),
            };
        }
    }

    private SemaphoreSlim SegmentGateOf(string peerKey) => _segmentGates.GetOrAdd(peerKey, static _ => new SemaphoreSlim(SegmentGateSlots));

    private ShoalResult Offline(Guid userId, string peerKey, string peerName)
    {
        _notice.PeerOffline(userId, peerKey, peerName);
        return new ShoalResult(StatusCodes.Status503ServiceUnavailable);
    }

    private async Task<IActionResult> MasterAsync(HttpContext http, Grant grant, PeerEndpoint peer, PlaylistContext context, Caller caller, CancellationToken cancellationToken)
    {
        CountMismatch(http, grant);
        var pairs = grant.PeerMasterQuery.Where(static p => !p.Key.Equals("EnableAdaptiveBitrateStreaming", StringComparison.OrdinalIgnoreCase)).ToList();
        pairs.Add(new("EnableAdaptiveBitrateStreaming", "false"));
        var query = QueryOf(pairs);
        if (!IsQ(query))
        {
            return new ShoalResult(StatusCodes.Status502BadGateway);
        }

        var result = await PlaylistAsync(peer, $"/Videos/{grant.PeerItemId:N}/master.m3u8?{query}", PeerCallProfile.HlsMaster, PlaylistKind.Master, context, cancellationToken).ConfigureAwait(false);
        if (result.Outcome == PlaylistOutcome.Ok)
        {
            grant.PeerVariantQuery = result.VariantQuery;
            StartMainPrefetch(grant, peer);
        }

        return Playlist(result, caller);
    }

    private async Task<IActionResult> MainAsync(Grant grant, PeerEndpoint peer, PlaylistContext context, Caller caller, CancellationToken cancellationToken)
    {
        if (grant.PeerVariantQuery.Count == 0)
        {
            return new ShoalResult(StatusCodes.Status404NotFound); // the master playlist comes first; there is no variant query yet
        }

        if (MainPath(grant) is not { } path)
        {
            return new ShoalResult(StatusCodes.Status502BadGateway);
        }

        var clock = System.Diagnostics.Stopwatch.StartNew();
        var prefetched = false;
        PlaylistResult? ready = null;
        var started = grant.MainPrefetch;
        if (started is not null)
        {
            // A client that cancels leaves the fetch running (it is not tied to the request), so its retry finds the answer instead of starting over.
            var bytes = await started.WaitAsync(cancellationToken).ConfigureAwait(false);
            if (bytes is null)
            {
                grant.MainPrefetch = null; // it failed: ask again below, with this request's own deadlines and errors
            }
            else
            {
                prefetched = true;
                ready = PlaylistGrammar.Process(PlaylistKind.Variant, bytes, context);
            }
        }

        var result = ready ?? await PlaylistAsync(peer, path, PeerCallProfile.HlsMain, PlaylistKind.Variant, context, cancellationToken).ConfigureAwait(false);
        grant.MainPrefetch = null; // a later request reads the Peer's current playlist
        _logger.LogInformation("The main playlist of a transcode was ready after {Ms} ms (started at the master playlist: {Prefetched}, outcome {Outcome})", clock.ElapsedMilliseconds, prefetched, result.Outcome);
        if (result.Outcome == PlaylistOutcome.Ok)
        {
            grant.SegmentCount = result.Segments; // the only ids the segment route will serve (H-1)
            grant.HasInitSegment = result.Text.Contains("#EXT-X-MAP:", StringComparison.Ordinal);
            grant.SegmentPlan = PlanOf(result.Text, result.Segments);
        }

        return Playlist(result, caller);
    }

    // The segment lines of the playlist Home itself wrote (rebuilt from parsed values, so every number is one Home produced): what the read-ahead asks the Peer for.
    private static SegmentPlanItem[] PlanOf(string text, int count)
    {
        var plan = new SegmentPlanItem?[count];
        foreach (Match match in SegmentLine.Matches(text))
        {
            if (int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id >= 0 && id < count
                && long.TryParse(match.Groups[3].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var runtime) && long.TryParse(match.Groups[4].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var length))
            {
                plan[id] = new SegmentPlanItem(runtime, length, match.Groups[2].Value);
            }
        }

        return [.. plan.Select(static p => p ?? new SegmentPlanItem(0, 0, string.Empty))];
    }

    private async Task<IActionResult> SubtitlePlaylistAsync(Grant grant, PeerEndpoint peer, PlaylistContext context, Caller caller, int index, CancellationToken cancellationToken)
    {
        if (!grant.Streams.Any(s => s.Index == index && string.Equals(s.Type, "Subtitle", StringComparison.Ordinal) && s.IsText))
        {
            return new ShoalResult(StatusCodes.Status404NotFound);
        }

        var path = $"/Videos/{grant.PeerItemId:N}/{grant.PeerMediaSourceId:N}/Subtitles/{index.ToString(CultureInfo.InvariantCulture)}/subtitles.m3u8?SegmentLength=30";
        var result = await PlaylistAsync(peer, path, PeerCallProfile.SubtitlePlaylist, PlaylistKind.Subtitle, context, cancellationToken).ConfigureAwait(false);
        return Playlist(result, caller);
    }

    private async Task<IActionResult> SubtitleSegmentAsync(HttpContext http, Grant grant, PeerEndpoint peer, Caller caller, int index, bool head, CancellationToken cancellationToken)
    {
        // Rule U-S on the way in: exactly one value of each, digits only, start <= end <= runtime + 1 h; anything else is a 400.
        var limit = grant.RuntimeTicks + 36_000_000_000L;
        if (!TryTicks(http.Request, "StartPositionTicks", limit, out var start) || !TryTicks(http.Request, "EndPositionTicks", limit, out var end) || start > end)
        {
            return new ShoalResult(StatusCodes.Status400BadRequest);
        }

        if (!grant.Streams.Any(s => s.Index == index && string.Equals(s.Type, "Subtitle", StringComparison.Ordinal) && s.IsText))
        {
            return new ShoalResult(StatusCodes.Status404NotFound);
        }

        if (head)
        {
            return new HlsHeadResult(ResponseKind.SubtitleVtt, null); // checked, not fetched: a HEAD starts no extraction
        }

        return await _subtitles.HlsSegmentAsync(grant, caller, peer, index, start, end, cancellationToken).ConfigureAwait(false);
    }

    private static string? MainPath(Grant grant)
    {
        if (grant.PeerVariantQuery.Count == 0)
        {
            return null;
        }

        var query = QueryOf(grant.PeerVariantQuery);
        return IsQ(query) ? $"/Videos/{grant.PeerItemId:N}/main.m3u8?{query}" : null;
    }

    // Asks for the main playlist now, on Home's own deadline and not the client's (Pilot: the client cancelled after 3 s on a relay link while the Peer was still starting its conversion).
    private void StartMainPrefetch(Grant grant, PeerEndpoint peer)
    {
        if (grant.MainPrefetch is not null || MainPath(grant) is not { } path)
        {
            return;
        }

        grant.PeerJobStarted = true; // the Peer's conversion begins with this request: it must be stopped with the session even if the client never gets further
        grant.MainPrefetch = Task.Run(async () =>
        {
            using var deadline = new CancellationTokenSource((PeerCallProfile.HlsMain.Total ?? TimeSpan.FromSeconds(40)) + TimeSpan.FromSeconds(5));
            try
            {
                return await FetchPlaylistBytesAsync(peer, path, PeerCallProfile.HlsMain, deadline.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is PeerCallException or OperationCanceledException)
            {
                return null;
            }
        });
    }

    private async Task<byte[]?> FetchPlaylistBytesAsync(PeerEndpoint peer, string path, PeerCallProfile profile, CancellationToken cancellationToken)
    {
        if (!await _trust.EnsureAsync(peer, TrustRequirement.PinFresh, cancellationToken).ConfigureAwait(false))
        {
            throw new PeerCallException(PeerFailureKind.TrustLapsed);
        }

        using var response = await _client.SendAsync(peer, PeerRequest.GetMedia(path).WithTrust(TrustRequirement.PinFresh), profile, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.OK || !response.HasBody || !ResponseWriter.IsAcceptedUpstream(ResponseKind.HlsPlaylist, response.MediaType))
        {
            return null;
        }

        return await response.ReadBytesAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<PlaylistResult> PlaylistAsync(PeerEndpoint peer, string path, PeerCallProfile profile, PlaylistKind kind, PlaylistContext context, CancellationToken cancellationToken)
    {
        var bytes = await FetchPlaylistBytesAsync(peer, path, profile, cancellationToken).ConfigureAwait(false);
        return bytes is null ? PlaylistResult.Reject(PlaylistFault.Structure) : PlaylistGrammar.Process(kind, bytes, context);
    }

    private IActionResult Playlist(PlaylistResult result, Caller caller)
    {
        switch (result.Outcome)
        {
            case PlaylistOutcome.Ok:
                return new HlsPlaylistResult(result.Text);
            case PlaylistOutcome.Forbidden:
                return new ShoalResult(StatusCodes.Status403Forbidden);
            default:
                _logger.LogWarning("An HLS playlist was refused ({Fault}) for user {User}", result.Fault, Redact.Id(caller.UserId));
                return new ShoalResult(StatusCodes.Status502BadGateway);
        }
    }

    // The client's AudioStreamIndex and SubtitleStreamIndex are compared with the grant's and never forwarded (6.4); a difference is only counted.
    private static void CountMismatch(HttpContext http, Grant grant)
    {
        foreach (var (key, expected) in new[] { ("AudioStreamIndex", grant.AudioStreamIndex), ("SubtitleStreamIndex", grant.SubtitleStreamIndex) })
        {
            var values = ShoalRequest.Values(http.Request, key);
            if (values.Count == 1 && int.TryParse(values[0], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var asked) && expected is { } have && asked != have)
            {
                grant.ClientIndexMismatch++;
            }
        }
    }

    private async Task<IActionResult> SegmentAsync(HttpContext http, Grant grant, PeerEndpoint peer, PeerState peerState, HlsRequest request, Caller caller, CancellationToken cancellationToken)
    {
        // The only client-supplied values: the segment id, the extension and two integers (U-V).
        if (request.SegmentId is < -1 or > 999_999 || request.Extension is not ("ts" or "mp4")
            || !TryTicks(http.Request, "runtimeTicks", grant.RuntimeTicks + 36_000_000_000L, out var runtime)
            || !TryTicks(http.Request, "actualSegmentLengthTicks", 6_000_000_000L, out var length)
            || grant.PeerVariantQuery.Count == 0)
        {
            return new ShoalResult(grant.PeerVariantQuery.Count == 0 ? StatusCodes.Status404NotFound : StatusCodes.Status400BadRequest);
        }

        if (grant.SegmentCount is not { } count || (request.SegmentId >= count) || (request.SegmentId == -1 && !grant.HasInitSegment) || (request.SegmentId == -1 && request.Extension != "mp4"))
        {
            return new ShoalResult(StatusCodes.Status404NotFound); // not in the playlist Home approved
        }

        if (request.Head)
        {
            return new HlsHeadResult(ResponseKind.HlsSegment, request.Extension); // checked, not fetched: a HEAD never starts a Peer job or takes a slot
        }

        var variant = QueryOf(grant.PeerVariantQuery);
        if (!IsQ(variant))
        {
            return new ShoalResult(StatusCodes.Status502BadGateway);
        }

        // A segment Home already read ahead (or is reading now) is served from memory: the client reads it from Home's fast side.
        if (_readAhead is not null && request.SegmentId >= 0 && _registry.Check(grant.PeerKey, true, TrustRequirement.PinAndAuthFresh) == TrustDenial.None && _readAhead.Find(grant.GrantId, request.SegmentId) is { } held)
        {
            byte[]? bytes = null;
            try
            {
                bytes = await held.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // dropped while waiting: ask the Peer below
            }

            if (bytes is not null && _slots.TryTakeHls(grant, peerState.MaxConcurrentStreams, peerState.MaxConcurrentTranscodes, out _))
            {
                _slots.TouchHls(grant.GrantId);
                return new HlsBufferedSegmentResult(bytes, request.Extension, _slots, grant.GrantId, () => ReadAhead(grant, peer, peerState, request.SegmentId, count));
            }
        }

        // At most 3 segment calls per grant and 8 per peer at once, so Peer restarts caused by far seeks can never pin the pool (H-1).
        if (!grant.TryEnterSegment(MaxSegmentsPerGrant))
        {
            return new ShoalResult(StatusCodes.Status429TooManyRequests, null, 1);
        }

        var gate = SegmentGateOf(grant.PeerKey);
        if (!await gate.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false))
        {
            grant.ExitSegment();
            return new ShoalResult(StatusCodes.Status429TooManyRequests, null, 2);
        }

        var handedOff = false;
        try
        {
            if (!_slots.TryTakeHls(grant, peerState.MaxConcurrentStreams, peerState.MaxConcurrentTranscodes, out var denial))
            {
                _logger.LogInformation("An HLS segment was refused for lack of a slot ({Denial})", denial);
                return new ShoalResult(StatusCodes.Status429TooManyRequests, null, 5);
            }

            void Done()
            {
                gate.Release();
                grant.ExitSegment();
                ReadAhead(grant, peer, peerState, request.SegmentId, count); // the segment is through: read the window after it
            }

            var result = await FetchSegmentAsync(grant, peer, request, caller, variant, runtime, length, Done, cancellationToken).ConfigureAwait(false);
            handedOff = result is HlsSegmentResult;
            return result;
        }
        finally
        {
            if (!handedOff)
            {
                gate.Release();
                grant.ExitSegment();
            }
        }
    }

    // Keeps reading the window after the segment the client is at. A segment goes through the same limits as the client's own: the grant's three calls, the peer's
    // eight, the stream slot (which it never takes anew and never touches: only the client's own requests keep a stream looking active); one read at a time per stream.
    private void ReadAhead(Grant grant, PeerEndpoint peer, PeerState peerState, int segment, int count)
    {
        if (_readAhead is null || grant.SegmentPlan is null)
        {
            return;
        }

        _readAhead.Advance(grant.GrantId, grant.PeerKey, segment, count, (next, reservation, token) => ReadSegmentAsync(grant, peer, next, reservation, token));
    }

    private async Task<byte[]?> ReadSegmentAsync(Grant grant, PeerEndpoint peer, int id, ReadAheadReservation reservation, CancellationToken cancellationToken)
    {
        if (grant.SegmentPlan is not { } plan || id < 0 || id >= plan.Count || plan[id].Extension.Length == 0 || grant.PeerVariantQuery.Count == 0)
        {
            return null;
        }

        var variant = QueryOf(grant.PeerVariantQuery);
        if (!IsQ(variant) || !_slots.HoldsOwnSlot(grant) || !grant.TryEnterSegment(MaxSegmentsPerGrant))
        {
            return null; // a stream that holds no slot (stopped, freed, replaced) is read for no more
        }

        var gate = SegmentGateOf(grant.PeerKey);
        if (!await gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            grant.ExitSegment();
            return null; // the peer already has its eight: reading ahead never takes a place a client's own request could use
        }

        PeerResponse? upstream = null;
        try
        {
            var item = plan[id];
            var path = $"/Videos/{grant.PeerItemId:N}/hls1/main/{id.ToString(CultureInfo.InvariantCulture)}.{item.Extension}?{variant}&runtimeTicks={item.RuntimeTicks.ToString(CultureInfo.InvariantCulture)}&actualSegmentLengthTicks={item.LengthTicks.ToString(CultureInfo.InvariantCulture)}";
            upstream = await _client.SendAsync(peer, PeerRequest.GetMedia(path).WithTrust(TrustRequirement.PinAndAuthFresh), PeerCallProfile.HlsSegment, cancellationToken).ConfigureAwait(false);
            if (upstream.StatusCode != HttpStatusCode.OK || !upstream.HasBody || !ResponseWriter.IsAcceptedUpstream(ResponseKind.HlsSegment, upstream.MediaType) || upstream.ContentLength > HlsReadAhead.MaxSegmentBytes)
            {
                return null;
            }

            // The bytes are reserved before they are read: the segment's own length when the Peer declares it, else the most one can be. Nothing is read without the room.
            var declared = upstream.ContentLength;
            if (!reservation.TryReserve(declared ?? HlsReadAhead.MaxSegmentBytes))
            {
                return null;
            }

            var body = upstream.OpenBody();
            var copying = System.Diagnostics.Stopwatch.StartNew();
            if (declared is { } length)
            {
                var exact = new byte[length]; // exactly the size reserved, no copy
                var filled = 0;
                while (filled < exact.Length)
                {
                    var read = await body.ReadAsync(exact.AsMemory(filled), cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                    {
                        return null; // shorter than declared
                    }

                    filled += read;
                }

                if (await body.ReadAsync(new byte[1].AsMemory(), cancellationToken).ConfigureAwait(false) != 0)
                {
                    return null; // longer than declared
                }

                _telemetry?.ObserveCopy(grant.PeerKey, grant.GrantId, item.LengthTicks / 1e7, exact.Length, copying.Elapsed); // a segment read ahead tells about the link too
                return exact;
            }

            // No declared length: read into a buffer capped at half the reservation, so the copy to an exact array stays inside what was reserved.
            const long Half = HlsReadAhead.MaxSegmentBytes / 2;
            using var buffer = new MemoryStream();
            var chunk = new byte[81 * 1024];
            int got;
            while ((got = await body.ReadAsync(chunk.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + got > Half)
                {
                    return null; // too big to hold: the client streams it itself
                }

                await buffer.WriteAsync(chunk.AsMemory(0, got), cancellationToken).ConfigureAwait(false);
            }

            _telemetry?.ObserveCopy(grant.PeerKey, grant.GrantId, item.LengthTicks / 1e7, buffer.Length, copying.Elapsed);
            return buffer.ToArray();
        }
        catch (PeerCallException)
        {
            return null; // the client's own request will meet the failure and say what it is
        }
        finally
        {
            if (upstream is not null)
            {
                await upstream.DisposeAsync().ConfigureAwait(false);
            }

            gate.Release();
            grant.ExitSegment();
        }
    }

    private async Task<IActionResult> FetchSegmentAsync(Grant grant, PeerEndpoint peer, HlsRequest request, Caller caller, string variant, long runtime, long length, Action onDone, CancellationToken cancellationToken)
    {
        _slots.TouchHls(grant.GrantId);
        var path = $"/Videos/{grant.PeerItemId:N}/hls1/main/{request.SegmentId.ToString(CultureInfo.InvariantCulture)}.{request.Extension}?{variant}&runtimeTicks={runtime.ToString(CultureInfo.InvariantCulture)}&actualSegmentLengthTicks={length.ToString(CultureInfo.InvariantCulture)}";
        grant.PeerJobStarted = true;
        PeerResponse? upstream = null;
        var measure = _telemetry?.Begin(grant, length / 1e7, request.SegmentId);
        try
        {
            upstream = await _client.SendAsync(peer, PeerRequest.GetMedia(path).WithTrust(TrustRequirement.PinAndAuthFresh), PeerCallProfile.HlsSegment, cancellationToken).ConfigureAwait(false);
            measure?.GotHeaders();
            if (upstream.StatusCode != HttpStatusCode.OK || !upstream.HasBody || !ResponseWriter.IsAcceptedUpstream(ResponseKind.HlsSegment, upstream.MediaType))
            {
                var missing = upstream.StatusCode == HttpStatusCode.NotFound;
                await upstream.DisposeAsync().ConfigureAwait(false);
                return new ShoalResult(missing ? StatusCodes.Status404NotFound : StatusCodes.Status502BadGateway);
            }

            return new HlsSegmentResult(upstream, _slots, grant.GrantId, request.Extension, _logger, onDone, measure);
        }
        catch (PeerCallException ex)
        {
            if (upstream is not null)
            {
                await upstream.DisposeAsync().ConfigureAwait(false);
            }

            _logger.LogInformation("An HLS segment call to peer {Peer} failed ({Kind})", Redact.Id(grant.PeerKey), ex.Kind);
            return ex.Kind switch
            {
                PeerFailureKind.Blocked => new ShoalResult(StatusCodes.Status503ServiceUnavailable),
                PeerFailureKind.BadResponse or PeerFailureKind.TooLarge or PeerFailureKind.Redirect => new ShoalResult(StatusCodes.Status502BadGateway),
                _ => Offline(caller.UserId, grant.PeerKey, peer.DisplayName),
            };
        }
    }
}

/// <summary>
/// A rebuilt playlist on its way to the client, written with Home's own type and headers.
/// </summary>
internal sealed class HlsPlaylistResult : IActionResult
{
    private readonly byte[] _body;
    private readonly bool _head;

    public HlsPlaylistResult(string text, bool head = false)
    {
        Text = text;
        _head = head;
        _body = Encoding.UTF8.GetBytes(text);
    }

    public string Text { get; }

    public HlsPlaylistResult AsHead() => new(Text, head: true);

    public async Task ExecuteResultAsync(ActionContext context)
    {
        var response = context.HttpContext.Response;
        response.StatusCode = StatusCodes.Status200OK;
        ResponseWriter.Apply(response, ResponseKind.HlsPlaylist);
        response.ContentLength = _body.Length;
        if (_head)
        {
            return;
        }

        await response.Body.WriteAsync(_body, context.HttpContext.RequestAborted).ConfigureAwait(false);
    }
}

/// <summary>
/// A segment copied from the Peer under the byte and idle limits; every chunk keeps the grant's slot alive.
/// </summary>
internal sealed class HlsSegmentResult : IActionResult
{
    private const int BufferSize = 81 * 1024;

    private readonly PeerResponse _upstream;
    private readonly StreamSlots _slots;
    private readonly string _grantId;
    private readonly string _extension;
    private readonly ILogger _logger;
    private readonly Action _onDone;
    private readonly Jellyfin.Plugin.Shoal.Diagnostics.SegmentMeasure? _measure;

    public HlsSegmentResult(PeerResponse upstream, StreamSlots slots, string grantId, string extension, ILogger logger, Action onDone, Jellyfin.Plugin.Shoal.Diagnostics.SegmentMeasure? measure = null)
    {
        _measure = measure;
        _onDone = onDone;
        _upstream = upstream;
        _slots = slots;
        _grantId = grantId;
        _extension = extension;
        _logger = logger;
    }

    public async Task ExecuteResultAsync(ActionContext context)
    {
        var http = context.HttpContext;
        var response = http.Response;
        long copied = 0;
        long upstreamTicks = 0;
        var complete = false;
        try
        {
            response.StatusCode = StatusCodes.Status200OK;
            ResponseWriter.Apply(response, ResponseKind.HlsSegment, _extension);
            if (_upstream.ContentLength is { } length and >= 0)
            {
                response.ContentLength = length;
            }

            var body = _upstream.OpenBody();
            var buffer = new byte[BufferSize];
            int read;
            while (true)
            {
                var waited = System.Diagnostics.Stopwatch.GetTimestamp();
                read = await body.ReadAsync(buffer.AsMemory(), http.RequestAborted).ConfigureAwait(false);
                upstreamTicks += System.Diagnostics.Stopwatch.GetTimestamp() - waited; // only the reads from the peer are timed: the client's own speed says nothing about the link
                if (read <= 0)
                {
                    break;
                }

                await response.Body.WriteAsync(buffer.AsMemory(0, read), http.RequestAborted).ConfigureAwait(false);
                copied += read;
                _slots.TouchHls(_grantId);
            }

            complete = true;
        }
        catch (OperationCanceledException)
        {
            // the client went away
        }
        catch (PeerCallException ex)
        {
            _logger.LogInformation("An HLS segment was cut ({Kind})", ex.Kind);
            http.Abort(); // a short or stalled body is an error, never a clean end
        }
        finally
        {
            if (complete)
            {
                _measure?.Done(copied, System.Diagnostics.Stopwatch.GetElapsedTime(0, upstreamTicks));
            }

            await _upstream.DisposeAsync().ConfigureAwait(false);
            _onDone();
        }
    }
}

/// <summary>
/// A segment Home read ahead, written from memory with the same headers as a segment copied from the Peer.
/// </summary>
internal sealed class HlsBufferedSegmentResult : IActionResult
{
    private readonly byte[] _bytes;
    private readonly string _extension;
    private readonly StreamSlots _slots;
    private readonly string _grantId;
    private readonly Action _onDone;

    public HlsBufferedSegmentResult(byte[] bytes, string extension, StreamSlots slots, string grantId, Action onDone)
    {
        _bytes = bytes;
        _extension = extension;
        _slots = slots;
        _grantId = grantId;
        _onDone = onDone;
    }

    public async Task ExecuteResultAsync(ActionContext context)
    {
        var http = context.HttpContext;
        try
        {
            http.Response.StatusCode = StatusCodes.Status200OK;
            ResponseWriter.Apply(http.Response, ResponseKind.HlsSegment, _extension);
            http.Response.ContentLength = _bytes.Length;
            await http.Response.Body.WriteAsync(_bytes, http.RequestAborted).ConfigureAwait(false);
            _slots.TouchHls(_grantId);
        }
        catch (OperationCanceledException)
        {
            // the client went away
        }
        finally
        {
            _onDone();
        }
    }
}

/// <summary>
/// The answer to a HEAD on a segment or a subtitle segment: the request was authorized and checked, the type is Home's own, and nothing was asked of the Peer.
/// </summary>
internal sealed class HlsHeadResult : IActionResult
{
    private readonly ResponseKind _kind;
    private readonly string? _format;

    public HlsHeadResult(ResponseKind kind, string? format)
    {
        _kind = kind;
        _format = format;
    }

    public Task ExecuteResultAsync(ActionContext context)
    {
        var response = context.HttpContext.Response;
        response.StatusCode = StatusCodes.Status200OK;
        ResponseWriter.Apply(response, _kind, _format);
        return Task.CompletedTask;
    }
}
