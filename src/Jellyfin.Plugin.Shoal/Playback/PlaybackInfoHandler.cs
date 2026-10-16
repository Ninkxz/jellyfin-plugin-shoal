using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.Authorization;
using Jellyfin.Plugin.Shoal.Http;
using Jellyfin.Plugin.Shoal.Pairing;
using Jellyfin.Plugin.Shoal.Peers;
using Jellyfin.Plugin.Shoal.Security;
using Jellyfin.Plugin.Shoal.State;
using Jellyfin.Plugin.Shoal.Sync;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.MediaInfo;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Shoal.Playback;

/// <summary>
/// Answers <c>Items/{itemId}/PlaybackInfo</c> for a federated item (DESIGN 6.2): the client's request is merged and validated, a new body is built
/// from validated values only, the Peer decides between direct play and transcode, and the answer is rebuilt from scratch with a grant behind it.
/// Nothing the Peer sent is passed through, and a canary check on the finished answer stops anything that belongs to the Peer from leaving.
/// </summary>
public sealed class PlaybackInfoHandler : IPlaybackInfoHandler
{
    private static readonly TimeSpan CanaryAlertInterval = TimeSpan.FromMinutes(5);

    private readonly GrantStore _grants;
    private readonly RateLimiter _limiter;
    private readonly IPeerEndpoints _peers;
    private readonly PeerClient _client;
    private readonly PeerTrust _trust;
    private readonly PeerRegistry _registry;
    private readonly StubCatalog _catalog;
    private readonly IStateStore _state;
    private readonly IDeviceCapabilities _devices;
    private readonly ISystemLimits _limits;
    private readonly IOfflineNotice _notice;
    private readonly IAdminNotifier _admins;
    private readonly ISlotLedger _slots;
    private readonly Func<bool> _hlsSubtitles;
    private readonly TimeProvider _time;
    private readonly IAttachmentRebuilder? _attachments;
    private readonly IVersionResolver? _versions;
    private readonly OfflineCards? _cards;
    private readonly ILogger<PlaybackInfoHandler> _logger;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTimeOffset> _lastCanaryAlert = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="PlaybackInfoHandler"/> class.
    /// </summary>
    /// <param name="grants">The grants.</param>
    /// <param name="limiter">The PlaybackInfo rate limit (30 per minute, burst 15, per user).</param>
    /// <param name="peers">The peers with their credentials.</param>
    /// <param name="client">The peer client.</param>
    /// <param name="trust">The trust window.</param>
    /// <param name="registry">The runtime peer table.</param>
    /// <param name="catalog">The catalog lookup.</param>
    /// <param name="state">The state store.</param>
    /// <param name="devices">The registered device profiles.</param>
    /// <param name="limits">The server-wide limits.</param>
    /// <param name="notice">The offline notice.</param>
    /// <param name="admins">The admin notifier (canary alerts).</param>
    /// <param name="slots">The stream slots (the pre-checks of 6.2 steps 1 and 6).</param>
    /// <param name="hlsSubtitles">Whether HLS subtitles are on (the kill switch).</param>
    /// <param name="time">The clock.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="attachments">Rebuilds the attachments (DESIGN v2.6.6); none means no attachments.</param>
    /// <param name="versions">Maps the media source the client names to a version of the title (DESIGN v2.6.4); none means the title has one version.</param>
    /// <param name="cards">The offline card (owner request 2026-10-16); none means an offline friend gets the plain refusal.</param>
    public PlaybackInfoHandler(
        GrantStore grants,
        RateLimiter limiter,
        IPeerEndpoints peers,
        PeerClient client,
        PeerTrust trust,
        PeerRegistry registry,
        StubCatalog catalog,
        IStateStore state,
        IDeviceCapabilities devices,
        ISystemLimits limits,
        IOfflineNotice notice,
        IAdminNotifier admins,
        ISlotLedger slots,
        Func<bool> hlsSubtitles,
        TimeProvider time,
        ILogger<PlaybackInfoHandler> logger,
        IAttachmentRebuilder? attachments = null,
        IVersionResolver? versions = null,
        OfflineCards? cards = null)
    {
        _cards = cards;
        _versions = versions;
        _grants = grants;
        _limiter = limiter;
        _peers = peers;
        _client = client;
        _trust = trust;
        _registry = registry;
        _catalog = catalog;
        _state = state;
        _devices = devices;
        _limits = limits;
        _notice = notice;
        _admins = admins;
        _slots = slots;
        _hlsSubtitles = hlsSubtitles;
        _time = time;
        _attachments = attachments;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IActionResult> HandleAsync(FederatedRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var caller = request.Caller;
        var peerKey = request.Stub.PeerKey;

        // 1. limits and peer state
        if (!_limiter.TryTake(caller.UserId))
        {
            return Error(PlaybackErrorCode.RateLimitExceeded);
        }

        if (_state.Status != StateStatus.Ready || _registry.StatusOf(peerKey) is PeerStatus.AuthFailed or PeerStatus.IdentityMismatch or PeerStatus.TlsKeyChanged or PeerStatus.Disabled)
        {
            return Error(PlaybackErrorCode.NotAllowed);
        }

        if (!_grants.HasRoom(caller.UserId, request.ItemId, caller.DeviceId))
        {
            return Error(PlaybackErrorCode.RateLimitExceeded);
        }

        // 2. read the request
        var parsed = await PlaybackInfoParser.ParseAsync(request.Http.Request, request.ItemId, cancellationToken).ConfigureAwait(false);
        if (!parsed.Ok)
        {
            return new ShoalResult(parsed.FailureStatus);
        }

        var entry = _catalog.Find(peerKey, request.Stub.FolderName);
        if (entry is null || entry.State is not (EntryState.Active or EntryState.HeldRemoval)
            || !Guid.TryParseExact(entry.PeerItemId, "N", out var peerItemId))
        {
            return new ShoalResult(StatusCodes.Status404NotFound);
        }

        var peerMediaSourceId = Guid.TryParseExact(entry.PeerMediaSourceId, "N", out var sourceId) ? sourceId : peerItemId;

        // The version the client means (DESIGN v2.6.4): its own source unless it names another member of the title's set; any other id is a 400.
        Guid? versionItemId = null;
        var setId = string.Empty;
        var runtimeTicks = entry.RuntimeTicks;
        var named = parsed.Request!.MediaSourceId;
        if (_versions is not null)
        {
            var version = _versions.Resolve(request.ItemId, named?.ToString("N"));
            if (version is null)
            {
                return new ShoalResult(named is null ? StatusCodes.Status404NotFound : StatusCodes.Status400BadRequest);
            }

            versionItemId = version.VersionItemId;
            setId = version.SetId;
            peerItemId = version.PeerItemId;
            peerMediaSourceId = version.PeerMediaSourceId;
            runtimeTicks = entry.Sources.FirstOrDefault(s => s.StubKey == version.StubKey) is { RuntimeTicks: > 0 } own ? own.RuntimeTicks : entry.RuntimeTicks;
        }
        else if (named is not null && named != request.ItemId)
        {
            return new ShoalResult(StatusCodes.Status400BadRequest);
        }

        // Slot pre-check (6.2 step 1): nothing is reserved; the first byte or segment stays the authoritative check (6.9). One slot per version set.
        var peerState = _state.Current.Home.Peers.FirstOrDefault(p => p.PeerKey == peerKey);
        if (peerState is not null && !_slots.CanStart(caller.UserId, setId.Length == 0 ? request.ItemId : SlotKey.Of(setId), peerKey, peerState.MaxConcurrentStreams, new SlotOrigin(entry.Kind == EntryKind.Episode ? entry.PeerSeriesId : string.Empty, caller.DeviceId)))
        {
            return Error(PlaybackErrorCode.RateLimitExceeded);
        }

        var peer = _peers.Find(peerKey);
        if (peer is null)
        {
            return Error(PlaybackErrorCode.NotAllowed);
        }

        // 3. build the body (6.2.1) from validated values only
        var client = parsed.Request!;
        var maxBitrate = PlaybackInfoBuilder.ComputeMaxStreamingBitrate(client.MaxStreamingBitrate, caller, _limits.ForPeer(peerKey));
        var clientProfile = client.DeviceProfile ?? _devices.ProfileFor(caller.UserId, caller.DeviceId);
        var profile = clientProfile is null ? null : DeviceProfileRebuilder.Rebuild(clientProfile, maxBitrate, _hlsSubtitles());
        var body = PlaybackInfoBuilder.BuildPeerBody(client, caller.User, peerMediaSourceId, runtimeTicks, profile, maxBitrate);

        // 4. ask the Peer
        PlaybackInfoResponse? answer;
        try
        {
            if (!await _trust.EnsureAsync(peer, TrustRequirement.PinFresh, cancellationToken).ConfigureAwait(false))
            {
                return await OfflineAsync(request, caller.UserId, peerKey, peer.DisplayName, cancellationToken).ConfigureAwait(false);
            }

            using var response = await _client.SendAsync(peer, PeerRequest.Post($"/Items/{peerItemId:N}/PlaybackInfo", body), PeerCallProfile.PlaybackInfo, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                _logger.LogWarning("Peer {Peer} answered PlaybackInfo with HTTP {Status}", Redact.Id(peerKey), (int)response.StatusCode);
                return response.StatusCode switch
                {
                    HttpStatusCode.NotFound => new ShoalResult(StatusCodes.Status404NotFound),
                    HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => Error(PlaybackErrorCode.NotAllowed),
                    _ => new ShoalResult(StatusCodes.Status502BadGateway),
                };
            }

            answer = PlaybackInfoBuilder.ParsePeerAnswer(await response.ReadBytesAsync(cancellationToken).ConfigureAwait(false));
        }
        catch (PeerCallException ex)
        {
            _logger.LogInformation("The PlaybackInfo call to peer {Peer} failed ({Kind})", Redact.Id(peerKey), ex.Kind);
            return ex.Kind switch
            {
                PeerFailureKind.Blocked => Error(PlaybackErrorCode.NotAllowed),
                PeerFailureKind.BadResponse or PeerFailureKind.TooLarge or PeerFailureKind.Redirect => new ShoalResult(StatusCodes.Status502BadGateway),
                _ => await OfflineAsync(request, caller.UserId, peerKey, peer.DisplayName, cancellationToken).ConfigureAwait(false),
            };
        }

        if (answer is null)
        {
            return new ShoalResult(StatusCodes.Status502BadGateway);
        }

        if (answer.ErrorCode is { } code)
        {
            return Error(code); // the enum only; a Peer error body is never relayed
        }

        // 5. rebuild from scratch (6.2.3) and remember what was agreed
        var grantId = GrantStore.NewGrantId();
        var now = _time.GetUtcNow();
        var built = PlaybackInfoBuilder.Rebuild(
            answer,
            new RebuildContext(grantId, GrantStore.NewPlaySessionId(), caller.UserId, caller.DeviceId, caller.Token, request.ItemId, peerKey, peerItemId, peerMediaSourceId, entry, now, _attachments, versionItemId, setId),
            out var failure);
        if (built is null)
        {
            return failure == BuildFailure.NoStream ? Error(PlaybackErrorCode.NoCompatibleStream) : new ShoalResult(StatusCodes.Status502BadGateway);
        }

        // Conversion pre-check (6.2 step 6): a source that can only be converted needs a free conversion slot at the peer.
        if (built.Grant.Method == PlayMethod.Transcode && peerState is not null && _slots.ConversionsFull(peerKey, peerState.MaxConcurrentTranscodes))
        {
            return Error(PlaybackErrorCode.RateLimitExceeded);
        }

        // 6. canary: nothing that belongs to the Peer may be in what goes back
        var json = PlaybackInfoBuilder.Serialize(built.Response);
        if (PlaybackInfoBuilder.CanaryHit(Encoding.UTF8.GetString(json), peer.Token, CanaryIds(entry, peerItemId, peerMediaSourceId), answer.PlaySessionId, peer.BaseUri.Host))
        {
            _logger.LogWarning("Redacted canary hit in PlaybackInfo (peer {Peer})", Redact.Id(peerKey));
            if (ShouldAlert(peerKey))
            {
                _ = AlertAsync(peer.DisplayName);
            }

            return new ShoalResult(StatusCodes.Status502BadGateway);
        }

        if (_grants.TryAdd(built.Grant) is GrantOutcome.RefusedUser or GrantOutcome.RefusedGlobal)
        {
            return Error(PlaybackErrorCode.RateLimitExceeded); // lost a race for the last place
        }

        _logger.LogInformation("Served Shoal PlaybackInfo for item {Item} from peer {Peer}", Redact.Id(request.ItemId), Redact.Id(peerKey));
        return new ShoalRawJsonResult(StatusCodes.Status200OK, json);
    }

    // The canary covers the Peer ids of every version of the title, not only the one played (DESIGN v2.6.4).
    private static List<Guid> CanaryIds(CatalogEntry entry, Guid peerItemId, Guid peerMediaSourceId)
    {
        var ids = new List<Guid> { peerItemId, peerMediaSourceId };
        ids.AddRange(entry.Sources.Select(static s => s.PeerMediaSourceId).Where(static s => s.Length > 0).Select(static s => Guid.TryParseExact(s, "N", out var id) ? id : Guid.Empty).Where(static g => g != Guid.Empty));
        return ids;
    }

    private static ShoalRawJsonResult Error(PlaybackErrorCode code) => new(StatusCodes.Status200OK, PlaybackInfoBuilder.ErrorBody(code));

    private async Task<IActionResult> OfflineAsync(FederatedRequest request, Guid userId, string peerKey, string peerName, CancellationToken cancellationToken)
    {
        // The alias the admin gave the server, if any, is what Home's users read; the name the friend chose for its server is not (Warden 15 L3).
        peerName = _state.Current.Home.Peers.FirstOrDefault(p => p.PeerKey == peerKey)?.ShownName() ?? peerName;
        _notice.PeerOffline(userId, peerKey, peerName);

        // The offline card (owner request 2026-10-16): a normal-looking direct-play source for a short clip that says the friend is down. No peer is called, no grant or
        // stream slot is made, and the play session is Home's own; the reports for it are swallowed by the intercept filter. Off, or no clip: the plain refusal below.
        if (_cards is { Enabled: true } cards && await cards.GetAsync(peerName, cancellationToken).ConfigureAwait(false) is { } clip && cards.NewSession(userId, request.ItemId) is { } session)
        {
            return new ShoalRawJsonResult(StatusCodes.Status200OK, PlaybackInfoBuilder.Serialize(OfflineCards.BuildResponse(request.ItemId, clip, session)));
        }

        // NotAllowed, not RateLimitExceeded: jellyfin-web words the latter "cannot be played due to rate limits", which is wrong for a server that is down. No error code
        // can say "offline"; the message the notice sends to the user's sessions does, and NotAllowed ("Playback of this media is not allowed") is at least not a false reason.
        return Error(PlaybackErrorCode.NotAllowed);
    }

    // K14-L1: at most one pop-up per peer per five minutes; a hostile peer cannot flood the admins by being played again and again.
    private bool ShouldAlert(string peerKey)
    {
        var now = _time.GetUtcNow();
        var due = false;
        _lastCanaryAlert.AddOrUpdate(
            peerKey,
            _ =>
            {
                due = true;
                return now;
            },
            (_, last) =>
            {
                if (now - last < CanaryAlertInterval)
                {
                    return last;
                }

                due = true;
                return now;
            });
        return due;
    }

    private async Task AlertAsync(string peerName)
    {
        try
        {
            await _admins.NotifyAdminsAsync("Shoal", $"An answer from {MetadataSanitizer.DisplayName(peerName)} was blocked because it held data that belongs to that server.").ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogDebug("The canary alert could not be shown ({Error})", ex.GetType().Name);
        }
    }
}
