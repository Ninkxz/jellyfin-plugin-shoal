using System;
using System.Globalization;
using System.IO;
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

#pragma warning disable SA1600 // internal plumbing; the public surface is documented

namespace Jellyfin.Plugin.Shoal.Playback;

/// <summary>
/// A single byte range a client asked for, re-serialized by Home.
/// </summary>
/// <param name="Start">The first byte, or null for a suffix range (<c>-n</c>).</param>
/// <param name="End">The last byte, or null for an open range (<c>a-</c>); for a suffix range the number of bytes asked for.</param>
public sealed record StaticRange(long? Start, long? End)
{
    /// <summary>Gets the header sent to the Peer: <c>bytes=a-b</c>, <c>bytes=a-</c> or <c>bytes=-n</c>.</summary>
    public string Header => Start is { } s ? $"bytes={s.ToString(CultureInfo.InvariantCulture)}-{(End is { } e ? e.ToString(CultureInfo.InvariantCulture) : string.Empty)}" : $"bytes=-{End!.Value.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>
    /// Reads the client's <c>Range</c> header. Only one <c>bytes=a-b</c>, <c>a-</c> or <c>-n</c> is accepted (DESIGN 6.3 step 4).
    /// </summary>
    /// <param name="values">The header values (none, one, or several).</param>
    /// <param name="range">The range, or null when the client sent none.</param>
    /// <returns>False when the header is malformed or asks for several ranges (answer 416).</returns>
    public static bool TryParse(Microsoft.Extensions.Primitives.StringValues values, out StaticRange? range)
    {
        range = null;
        if (values.Count == 0)
        {
            return true;
        }

        if (values.Count > 1 || values[0] is not { } text)
        {
            return false;
        }

        const string Prefix = "bytes=";
        if (!text.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var spec = text[Prefix.Length..].Trim();
        var dash = spec.IndexOf('-', StringComparison.Ordinal);
        if (dash < 0 || spec.Contains(',', StringComparison.Ordinal) || spec.IndexOf('-', dash + 1) >= 0)
        {
            return false;
        }

        var left = spec[..dash].Trim();
        var right = spec[(dash + 1)..].Trim();
        if (left.Length == 0)
        {
            if (!TryNumber(right, out var suffix) || suffix == 0)
            {
                return false;
            }

            range = new StaticRange(null, suffix);
            return true;
        }

        if (!TryNumber(left, out var start))
        {
            return false;
        }

        if (right.Length == 0)
        {
            range = new StaticRange(start, null);
            return true;
        }

        if (!TryNumber(right, out var end) || end < start)
        {
            return false;
        }

        range = new StaticRange(start, end);
        return true;
    }

    private static bool TryNumber(string text, out long value)
    {
        value = 0;
        return text.Length is >= 1 and <= 18 && text.All(static c => c is >= '0' and <= '9') && long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }
}

/// <summary>
/// Answers a static (direct play) stream of a federated item (DESIGN 6.3): one validated range goes to the Peer, the answer is validated, Home's
/// own type and headers are written, and the body is copied under the byte and idle limits. A stream slot (6.9) is held while it runs.
/// </summary>
public sealed class StaticStreamHandler : IStaticStreamHandler
{
    private const double SizeTolerance = 1.01;
    private const long MaxUnknownSize = 1L << 40;

    private readonly StubCatalog _catalog;
    private readonly IPeerEndpoints _peers;
    private readonly PeerClient _client;
    private readonly PeerTrust _trust;
    private readonly PeerRegistry _registry;
    private readonly StreamSlots _slots;
    private readonly RateLimiter _limiter;
    private readonly IStateStore _state;
    private readonly IOfflineNotice _notice;
    private readonly ILogger<StaticStreamHandler> _logger;
    private readonly IVersionResolver? _versions;
    private readonly OfflineCards? _cards;
    private readonly ReadAheadBudget? _readAhead;
    private readonly IGroupPlayback? _groups;
    private readonly GrantStore? _grants;

    /// <summary>
    /// Initializes a new instance of the <see cref="StaticStreamHandler"/> class.
    /// </summary>
    /// <param name="catalog">The catalog lookup.</param>
    /// <param name="peers">The peers with their credentials.</param>
    /// <param name="client">The peer client.</param>
    /// <param name="trust">The trust window.</param>
    /// <param name="registry">The runtime peer table.</param>
    /// <param name="slots">The stream slots.</param>
    /// <param name="limiter">The request rate limit (20 per second, burst 100, per user).</param>
    /// <param name="state">The state store (the peer's stream limit).</param>
    /// <param name="notice">The offline notice.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="versions">Maps the media source the client names to a version of the title (DESIGN v2.6.4); none means the title has one version.</param>
    /// <param name="cards">The offline card; none means its media source is not served.</param>
    /// <param name="readAhead">The byte budget for the read-ahead buffer of each stream, or null for none.</param>
    /// <param name="groups">Follows the server a grouped title's PlaybackInfo picked (PLAN v2.9 K42); none means every title streams from its stub's server.</param>
    /// <param name="grants">The grants, where the picked server is recorded.</param>
    public StaticStreamHandler(
        StubCatalog catalog,
        IPeerEndpoints peers,
        PeerClient client,
        PeerTrust trust,
        PeerRegistry registry,
        StreamSlots slots,
        RateLimiter limiter,
        IStateStore state,
        IOfflineNotice notice,
        ILogger<StaticStreamHandler> logger,
        IVersionResolver? versions = null,
        OfflineCards? cards = null,
        ReadAheadBudget? readAhead = null,
        IGroupPlayback? groups = null,
        GrantStore? grants = null)
    {
        _groups = groups;
        _grants = grants;
        _readAhead = readAhead;
        _cards = cards;
        _versions = versions;
        _catalog = catalog;
        _peers = peers;
        _client = client;
        _trust = trust;
        _registry = registry;
        _slots = slots;
        _limiter = limiter;
        _state = state;
        _notice = notice;
        _logger = logger;
    }

#pragma warning disable SA1202 // Next to the one place that calls it.
    private async Task<IActionResult> ServeCardAsync(OfflineCards cards, FederatedRequest request, CancellationToken cancellationToken)
    {
        if (!_limiter.TryTake(request.Caller.UserId))
        {
            return new ShoalResult(StatusCodes.Status429TooManyRequests, null, 1);
        }

        var name = _state.Current.Home.Peers.FirstOrDefault(p => p.PeerKey == request.Stub.PeerKey)?.ShownName() ?? string.Empty;
        return cards.Enabled && await cards.GetAsync(name, cancellationToken).ConfigureAwait(false) is { } clip
            ? new OfflineCardResult(clip)
            : new ShoalResult(StatusCodes.Status404NotFound);
    }

    /// <inheritdoc />
    public async Task<IActionResult> HandleAsync(FederatedRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var http = request.Http;
        var caller = request.Caller;
        var peerKey = request.Stub.PeerKey;

        // The offline card's own media source (owner request 2026-10-16): served from Home's cache, no peer call, no slot.
        if (_cards is { } cards && ShoalRequest.Values(http.Request, "mediaSourceId") is [var cardSource]
            && ShoalRequest.TryParseGuid(cardSource, out var cardId) && cardId == OfflineCards.SourceIdOf(request.ItemId))
        {
            return await ServeCardAsync(cards, request, cancellationToken).ConfigureAwait(false);
        }

        // A media source the client names must be a version of this title: this item itself, or another member of its set (DESIGN v2.6.4).
        string? namedSource = null;
        foreach (var value in ShoalRequest.Values(http.Request, "mediaSourceId"))
        {
            if (!ShoalRequest.TryParseGuid(value, out var named) || (_versions is null && named != request.ItemId) || (namedSource is not null && !string.Equals(namedSource, named.ToString("N"), StringComparison.Ordinal)))
            {
                return new ShoalResult(StatusCodes.Status400BadRequest);
            }

            namedSource = named.ToString("N");
        }

        ResolvedVersion? version = null;
        if (_versions is not null)
        {
            version = _versions.Resolve(request.ItemId, namedSource);
            if (version is null)
            {
                return new ShoalResult(namedSource is null ? StatusCodes.Status404NotFound : StatusCodes.Status400BadRequest);
            }
        }

        if (!_limiter.TryTake(caller.UserId))
        {
            return new ShoalResult(StatusCodes.Status429TooManyRequests, null, 1);
        }

        if (!StaticRange.TryParse(http.Request.Headers.Range, out var range))
        {
            return new ShoalResult(StatusCodes.Status416RangeNotSatisfiable);
        }

        var entry = _catalog.Find(peerKey, request.Stub.FolderName);
        if (entry is null || entry.State is not (EntryState.Active or EntryState.HeldRemoval)
            || !Guid.TryParseExact(entry.PeerItemId, "N", out var peerItemId))
        {
            return new ShoalResult(StatusCodes.Status404NotFound);
        }

        var peerSourceId = Guid.TryParseExact(entry.PeerMediaSourceId, "N", out var sourceId) ? sourceId : peerItemId;
        var slotItem = request.ItemId;
        var versionSource = entry.Sources.Count > 0 ? entry.Sources[0] : null;
        if (version is not null)
        {
            peerItemId = version.PeerItemId;
            peerSourceId = version.PeerMediaSourceId;
            slotItem = SlotKey.Of(version.SetId);
            versionSource = entry.Sources.FirstOrDefault(s => s.StubKey == version.StubKey) ?? versionSource;
        }

        // K42: a grouped title streams from the server its PlaybackInfo picked (the caller's grant for this item and device), so the stream never switches;
        // with no grant, from the best server that is up now. The extra versions are hidden, so only the item itself is named.
        if (_groups is not null && (namedSource is null || namedSource == request.ItemId.ToString("N")))
        {
            var grant = _grants?.ForUser(caller.UserId).Where(g => g.LocalItemId == request.ItemId && g.CallerDeviceId == caller.DeviceId).OrderByDescending(static g => g.CreatedUtc).FirstOrDefault();
            var member = grant is not null
                ? _groups.Member(peerKey, entry, grant.PeerKey, grant.PeerItemId)
                : (request.Stub.IsCopy ? _groups.CandidatesForCopy(peerKey, entry, caller.User, request.Stub.LibKey) : _groups.Candidates(peerKey, entry, caller.User)) is { Count: > 0 } candidates ? candidates[0] : null;

            // Warden 10 L2: a grant's member is checked again against the caller's libraries on every request (access may have been taken away since).
            if (grant is not null && member is not null && !_groups.Admits(peerKey, entry, member, caller.User, request.Stub.IsCopy ? request.Stub.LibKey : null))
            {
                return new ShoalResult(StatusCodes.Status404NotFound);
            }

            if (request.Stub.IsCopy && member is null)
            {
                return new ShoalResult(StatusCodes.Status404NotFound); // K45: an extra copy never falls back to the owner's server
            }

            if (member is not null)
            {
                peerKey = member.PeerKey;
                entry = member.Entry;
                peerItemId = member.PeerItemId;
                peerSourceId = member.PeerMediaSourceId;
                versionSource = entry.Sources.Count > 0 ? entry.Sources[0] : null;
            }
        }

        var peer = _peers.Find(peerKey);
        var peerState = _state.Current.Home.Peers.FirstOrDefault(p => p.PeerKey == peerKey);
        if (peer is null || peerState is null || _registry.StatusOf(peerKey) is PeerStatus.AuthFailed or PeerStatus.IdentityMismatch or PeerStatus.TlsKeyChanged or PeerStatus.Disabled)
        {
            return new ShoalResult(StatusCodes.Status503ServiceUnavailable);
        }

        // K51: a file over the connection's quality limit (the one stored now, and the one the caller's grant holds) is never streamed as it is.
        if (OverLimit(caller.UserId, caller.DeviceId, request.ItemId, peerState, entry, versionSource))
        {
            _logger.LogInformation("A static stream of item {Item} was refused: over the quality limit of peer {Peer}", Redact.Id(request.ItemId), Redact.Id(peerKey));
            return new ShoalResult(StatusCodes.Status403Forbidden);
        }

        // 2. the trust window must cover the pin and the token (4.4)
        try
        {
            if (!await _trust.EnsureAsync(peer, TrustRequirement.PinAndAuthFresh, cancellationToken).ConfigureAwait(false))
            {
                return Offline(caller.UserId, peerKey, peer.DisplayName);
            }
        }
        catch (PeerCallException)
        {
            return Offline(caller.UserId, peerKey, peer.DisplayName);
        }

        // 3. the slot (6.9)
        var lease = _slots.TryOpenStatic(caller.UserId, slotItem, peerKey, peerState.MaxConcurrentStreams, out var denial, new SlotOrigin(entry.Kind == EntryKind.Episode ? entry.PeerSeriesId : string.Empty, caller.DeviceId));
        if (lease is null)
        {
            _logger.LogInformation("A static stream was refused for lack of a slot ({Denial})", denial);
            return new ShoalResult(StatusCodes.Status429TooManyRequests, null, 5);
        }

        var isHead = HttpMethods.IsHead(http.Request.Method);
        if (isHead)
        {
            range = null; // a HEAD carries no body, so no range is forwarded
        }

        var path = $"/Videos/{peerItemId:N}/stream?static=true&mediaSourceId={peerSourceId:N}";
        var peerRequest = (isHead ? PeerRequest.HeadMedia(path) : PeerRequest.GetMedia(path, range?.Header)).WithTrust(TrustRequirement.PinAndAuthFresh);
        PeerResponse? upstream = null;
        try
        {
            upstream = await _client.SendAsync(peer, peerRequest, PeerCallProfile.StaticStream, cancellationToken).ConfigureAwait(false);
            var verdict = Validate(upstream, range, isHead, versionSource is null || ReferenceEquals(versionSource, entry.Sources.FirstOrDefault()) ? entry : EntryOf(entry, versionSource));
            if (verdict.Failure is { } failure)
            {
                await upstream.DisposeAsync().ConfigureAwait(false);
                lease.Abandon();
                lease.Dispose();
                return failure;
            }

            return new StaticStreamResult(upstream, lease, verdict, ContainerToken(version is null || versionSource is null ? entry.Container : versionSource.Container ?? entry.Container), isHead, _logger, _readAhead);
        }
        catch (PeerCallException ex)
        {
            if (upstream is not null)
            {
                await upstream.DisposeAsync().ConfigureAwait(false);
            }

            lease.Abandon();
            lease.Dispose();
            _logger.LogInformation("A static stream from peer {Peer} failed ({Kind})", Redact.Id(peerKey), ex.Kind);
            return ex.Kind switch
            {
                PeerFailureKind.CircuitOpen or PeerFailureKind.TrustLapsed or PeerFailureKind.Blocked or PeerFailureKind.Timeout or PeerFailureKind.Unreachable =>
                    Offline(caller.UserId, peerKey, peer.DisplayName),
                _ => new ShoalResult(StatusCodes.Status502BadGateway),
            };
        }
        catch
        {
            if (upstream is not null)
            {
                await upstream.DisposeAsync().ConfigureAwait(false);
            }

            lease.Abandon();
            lease.Dispose();
            throw;
        }
    }

    private static string? ContainerToken(string? container) => container?.Split(',')[0] is { Length: > 0 } first ? first : null;

    private ShoalResult Offline(Guid userId, string peerKey, string peerName)
    {
        _notice.PeerOffline(userId, peerKey, peerName);
        return new ShoalResult(StatusCodes.Status503ServiceUnavailable);
    }

    /// <summary>Validates the Peer's answer (DESIGN 6.3 step 5) and works out what Home may say about it.</summary>
    // K51: the size of what is streamed comes from the caller's grant (the Peer's answer at PlaybackInfo) where it has one, else from the catalog. An unknown size is over
    // any box, so with a limit and nothing known the file is refused, never assumed small.
    private bool OverLimit(Guid userId, string deviceId, Guid itemId, PeerState peerState, CatalogEntry entry, CatalogSource? source)
    {
        var grant = _grants?.ForUser(userId).Where(g => g.LocalItemId == itemId && g.CallerDeviceId == deviceId && g.PeerKey == peerState.PeerKey).OrderByDescending(static g => g.CreatedUtc).FirstOrDefault();
        QualityCap?[] caps = [QualityCap.From(peerState.QualityLimit), grant?.Cap];
        if (caps.All(static c => c is null))
        {
            return false;
        }

        var (width, height) = grant is { SourceWidth: > 0, SourceHeight: > 0 } ? (grant.SourceWidth, grant.SourceHeight)
            : source is { Width: > 0, Height: > 0 } ? (source.Width, source.Height)
            : (entry.Width, entry.Height);
        long bitrate = grant is { SourceBitrate: > 0 } ? grant.SourceBitrate : source is { Bitrate: > 0 } ? source.Bitrate : entry.Bitrate;
        return caps.Any(c => c is not null && !c.Allows(width, height, bitrate));
    }

    // The size bound of a version is its own source's, not the primary's (DESIGN v2.6.4).
    private static CatalogEntry EntryOf(CatalogEntry entry, CatalogSource source) => new() { SizeBytes = source.SizeBytes, Container = source.Container ?? entry.Container };

    private static StaticVerdict Validate(PeerResponse upstream, StaticRange? range, bool isHead, CatalogEntry entry)
    {
        var status = upstream.StatusCode;
        if (status == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            return new StaticVerdict(null, StatusCodes.Status416RangeNotSatisfiable, null, null, null, TotalOf(upstream.ContentRange));
        }

        if (status is not (HttpStatusCode.OK or HttpStatusCode.PartialContent))
        {
            return Fail(status == HttpStatusCode.NotFound ? StatusCodes.Status404NotFound : StatusCodes.Status502BadGateway);
        }

        if (!ResponseWriter.IsAcceptedUpstream(ResponseKind.StaticStream, upstream.MediaType))
        {
            return Fail(StatusCodes.Status502BadGateway);
        }

        var length = upstream.ContentLength;
        if (length is null or < 0)
        {
            return Fail(StatusCodes.Status502BadGateway);
        }

        var bound = entry.SizeBytes > 0 ? (long)(entry.SizeBytes * SizeTolerance) : MaxUnknownSize;
        if (length > bound)
        {
            return Fail(StatusCodes.Status502BadGateway);
        }

        if (status == HttpStatusCode.OK)
        {
            if (range is { Start: > 0 } || range is { Start: null })
            {
                return Fail(StatusCodes.Status502BadGateway); // the Peer ignored a range that mattered: the bytes would be the wrong ones
            }

            if (range is { Start: 0, End: { } wanted } && length > wanted + 1 && !isHead)
            {
                return Fail(StatusCodes.Status502BadGateway);
            }

            return new StaticVerdict(null, StatusCodes.Status200OK, length, null, upstream.LastModified, null);
        }

        // 206: Content-Range must be well formed, inside the bound, and what was asked for
        if (range is null || !TryContentRange(upstream.ContentRange, out var first, out var last, out var total) || total > bound || last >= total
            || last - first + 1 != length || !Consistent(range, first, last, total))
        {
            return Fail(StatusCodes.Status502BadGateway);
        }

        return new StaticVerdict(null, StatusCodes.Status206PartialContent, length, $"bytes {first.ToString(CultureInfo.InvariantCulture)}-{last.ToString(CultureInfo.InvariantCulture)}/{total.ToString(CultureInfo.InvariantCulture)}", upstream.LastModified, total);
    }

    private static StaticVerdict Fail(int status) => new(new ShoalResult(status), status, null, null, null, null);

    private static bool Consistent(StaticRange range, long first, long last, long total) => range switch
    {
        { Start: { } s, End: { } e } => first == s && last <= e,
        { Start: { } s, End: null } => first == s && last == total - 1,
        { Start: null, End: { } n } => last == total - 1 && last - first + 1 == Math.Min(n, total),
        _ => false,
    };

    private static long? TotalOf(string? contentRange) =>
        contentRange is { } text && text.StartsWith("bytes */", StringComparison.Ordinal) && long.TryParse(text["bytes */".Length..], NumberStyles.None, CultureInfo.InvariantCulture, out var total) ? total : null;

    private static bool TryContentRange(string? text, out long first, out long last, out long total)
    {
        first = last = total = 0;
        if (text is null || !text.StartsWith("bytes ", StringComparison.Ordinal))
        {
            return false;
        }

        var spec = text["bytes ".Length..];
        var slash = spec.IndexOf('/', StringComparison.Ordinal);
        var dash = spec.IndexOf('-', StringComparison.Ordinal);
        return slash > dash && dash > 0
            && long.TryParse(spec[..dash], NumberStyles.None, CultureInfo.InvariantCulture, out first)
            && long.TryParse(spec[(dash + 1)..slash], NumberStyles.None, CultureInfo.InvariantCulture, out last)
            && long.TryParse(spec[(slash + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out total)
            && first <= last;
    }

    /// <summary>What the validation decided.</summary>
    internal sealed record StaticVerdict(ShoalResult? Failure, int Status, long? Length, string? ContentRange, DateTimeOffset? LastModified, long? Total);
}

/// <summary>
/// Writes a validated static answer: Home's type from the catalog container, Home's re-serialized length, range and date, and the body copied
/// through the bounded stream while the slot is touched. Disposes the upstream response and the lease when it ends.
/// </summary>
internal sealed class StaticStreamResult : IActionResult
{
    private const int BufferSize = 81 * 1024;

    /// <summary>The read-ahead buffer of one direct-play stream: a few MB that Home keeps filled from the friend's server while the client reads.</summary>
    internal const int ReadAheadBytes = 4 * 1024 * 1024;

    private readonly ReadAheadBudget? _readAhead;
    private readonly PeerResponse _upstream;
    private readonly StaticLease _lease;
    private readonly StaticStreamHandler.StaticVerdict _verdict;
    private readonly string? _container;
    private readonly bool _head;
    private readonly ILogger _logger;

    public StaticStreamResult(PeerResponse upstream, StaticLease lease, StaticStreamHandler.StaticVerdict verdict, string? container, bool head, ILogger logger, ReadAheadBudget? readAhead = null)
    {
        _readAhead = readAhead;
        _upstream = upstream;
        _lease = lease;
        _verdict = verdict;
        _container = container;
        _head = head;
        _logger = logger;
    }

    public async Task ExecuteResultAsync(ActionContext context)
    {
        var http = context.HttpContext;
        var response = http.Response;
        Stream? buffered = null;
        try
        {
            response.StatusCode = _verdict.Status;
            if (_verdict.Status == StatusCodes.Status416RangeNotSatisfiable)
            {
                ResponseWriter.Apply(response, ResponseKind.Json);
                if (_verdict.Total is { } total)
                {
                    ResponseWriter.SetValidatedMediaHeaders(response, $"bytes */{total.ToString(CultureInfo.InvariantCulture)}", "bytes", null);
                }

                return;
            }

            ResponseWriter.Apply(response, ResponseKind.StaticStream, _container);
            ResponseWriter.SetValidatedMediaHeaders(response, _verdict.ContentRange, "bytes", _verdict.LastModified);
            response.ContentLength = _verdict.Length;
            if (_head || !_upstream.HasBody)
            {
                return;
            }

            var body = ReadAheadBody.Wrap(_upstream.OpenBody(), ReadAheadBytes, _readAhead); // the upstream body is owned by the upstream response
            buffered = body;
            var buffer = new byte[BufferSize];
            int read;
            while ((read = await body.ReadAsync(buffer.AsMemory(), http.RequestAborted).ConfigureAwait(false)) > 0)
            {
                await response.Body.WriteAsync(buffer.AsMemory(0, read), http.RequestAborted).ConfigureAwait(false);
                _lease.Touch();
            }
        }
        catch (OperationCanceledException)
        {
            // the client went away; the slot lingers for the grace period
        }
        catch (PeerCallException ex)
        {
            // Headers are out: the only honest answer is to cut the connection (a short or stalled body is an error, never a clean end).
            _logger.LogInformation("A static stream was cut ({Kind})", ex.Kind);
            http.Abort();
        }
        finally
        {
            if (buffered is ReadAheadBody readAheadBody)
            {
                await readAheadBody.DisposeAsync().ConfigureAwait(false); // stops the background read before the upstream response goes
            }

            await _upstream.DisposeAsync().ConfigureAwait(false);
            _lease.Dispose();
        }
    }
}
