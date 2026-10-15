using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.Authorization;
using Jellyfin.Plugin.Shoal.Http;
using Jellyfin.Plugin.Shoal.Sharing;
using Jellyfin.Plugin.Shoal.State;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Shoal.Guard;

/// <summary>
/// The Peer-side guard (DESIGN 8.4): one global resource filter, inert unless a share exists, with four jobs. (a) sign-in for shared media
/// (<c>LogOnly</c>, <c>Enforce</c>, <c>Off</c>), (b) the Quick Connect initiate shield, (c) a best-effort per-share transcode limit, (d) no
/// self-minting by sharing accounts. Jobs (a) to (c) fail open (a bug here must never break the friend's own server); job (d) fails closed.
/// It only adds requirements and never relaxes core's authorization.
/// </summary>
public sealed partial class PeerGuardFilter : IAsyncResourceFilter
{
    private const int MaxTrackedSessions = 64;
    private static readonly TimeSpan SessionLife = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ReserveGrace = TimeSpan.FromSeconds(20);

    private readonly GuardShares _shares;
    private readonly IStateStore _state;
    private readonly IGuardItems _items;
    private readonly ITranscodeProbe _transcodes;
    private readonly GuardCounters _counters;
    private readonly InitiateShield _shield;
    private readonly SharedItemCache _cache;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<string, DateTimeOffset>> _sessions = new();
    private DateTimeOffset _lastWarning = DateTimeOffset.MinValue;
    private GuardSnapshot? _lastSnapshot;

    /// <summary>
    /// Initializes a new instance of the <see cref="PeerGuardFilter"/> class.
    /// </summary>
    /// <param name="shares">The share list.</param>
    /// <param name="state">The state store (the hardening mode).</param>
    /// <param name="items">Item questions.</param>
    /// <param name="transcodes">Transcode jobs.</param>
    /// <param name="counters">The counters.</param>
    /// <param name="shield">The Quick Connect shield.</param>
    /// <param name="cache">The shared-item cache.</param>
    /// <param name="time">The clock.</param>
    /// <param name="logger">The logger.</param>
    public PeerGuardFilter(GuardShares shares, IStateStore state, IGuardItems items, ITranscodeProbe transcodes, GuardCounters counters, InitiateShield shield, SharedItemCache cache, TimeProvider time, ILogger<PeerGuardFilter> logger)
        : this(shares, state, items, transcodes, counters, shield, cache, time, (ILogger)logger)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="PeerGuardFilter"/> class (tests: any logger).
    /// </summary>
    /// <param name="shares">The share list.</param>
    /// <param name="state">The state store.</param>
    /// <param name="items">Item questions.</param>
    /// <param name="transcodes">Transcode jobs.</param>
    /// <param name="counters">The counters.</param>
    /// <param name="shield">The shield.</param>
    /// <param name="cache">The cache.</param>
    /// <param name="time">The clock.</param>
    /// <param name="logger">The logger.</param>
    internal PeerGuardFilter(GuardShares shares, IStateStore state, IGuardItems items, ITranscodeProbe transcodes, GuardCounters counters, InitiateShield shield, SharedItemCache cache, TimeProvider time, ILogger logger)
    {
        _shares = shares;
        _state = state;
        _items = items;
        _transcodes = transcodes;
        _counters = counters;
        _shield = shield;
        _cache = cache;
        _time = time;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task OnResourceExecutionAsync(ResourceExecutingContext context, ResourceExecutionDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);
        var http = context.HttpContext;
        GuardSnapshot snapshot;
        try
        {
            snapshot = _shares.Current;
        }
#pragma warning disable CA1031 // The share list failing must not break the server; job d still works from the last list that was built.
        catch (Exception)
#pragma warning restore CA1031
        {
            _counters.Error();
            snapshot = _shares.LastKnown;
        }

        // New or changed shares: the cached "is it shared" answers are dropped at once, not after 60 seconds (Warden L5).
        if (!ReferenceEquals(Interlocked.Exchange(ref _lastSnapshot, snapshot), snapshot))
        {
            _cache.Clear();
        }

        if (!snapshot.Any)
        {
            await next().ConfigureAwait(false);
            return;
        }

        // The route template, never the path (DESIGN 8.8).
        var path = (context.ActionDescriptor.AttributeRouteInfo?.Template ?? string.Empty).Trim('/');
        var user = UserOf(http);
        var isShareUser = user is { } id && snapshot.ShareUserIds.Contains(id);

        // (d) fails closed, and only looks at share users' own calls.
        try
        {
            if (isShareUser && HttpMethods.IsPost(http.Request.Method) && Banned(path))
            {
                context.Result = new ShoalResult(StatusCodes.Status403Forbidden, new { error = "forbidden" });
                return;
            }
        }
#pragma warning disable CA1031 // Job (d) refuses when it cannot tell: it only touches a few share-user routes.
        catch (Exception)
#pragma warning restore CA1031
        {
            context.Result = new ShoalResult(StatusCodes.Status403Forbidden, new { error = "forbidden" });
            return;
        }

        try
        {
            if (HttpMethods.IsPost(http.Request.Method) && path.Equals("QuickConnect/Initiate", StringComparison.OrdinalIgnoreCase))
            {
                var ip = AddressKey.Of(http.Connection.RemoteIpAddress); // an IPv6 network counts as one caller
                if (!_shield.TryAllow(ip))
                {
                    _counters.Shield();
                    context.Result = new ShoalResult(StatusCodes.Status429TooManyRequests, new { error = "rateLimited" }, 60);
                    return;
                }
            }

            if (isShareUser && user is { } shareUser && TranscodeStart(http, path) && TranscodeGate(shareUser, snapshot, http) is { } gate)
            {
                context.Result = gate;
                return;
            }

            if (AnonymousItemRoute(context) is { } itemId)
            {
                var refusal = Anonymous(http, path, itemId, snapshot, user);
                if (refusal is not null)
                {
                    context.Result = refusal;
                    return;
                }
            }
        }
#pragma warning disable CA1031 // Jobs (a) to (c) fail open: one request goes through, a counter goes up, one warning an hour.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _counters.Error();
            var now = _time.GetUtcNow();
            if (now - _lastWarning >= TimeSpan.FromHours(1))
            {
                _lastWarning = now;
                _logger.LogWarning("The guard failed on a request and let it through: {Error}", ex.GetType().Name);
            }
        }

        await next().ConfigureAwait(false);
    }

    private static Guid? UserOf(HttpContext http) =>
        http.User.FindFirst(CallerContext.UserIdClaim)?.Value is { } value && Guid.TryParse(value, out var id) ? id : null;

    private static bool IsApiKey(HttpContext http) =>
        http.User.HasClaim(c => c.Type == CallerContext.IsApiKeyClaim && string.Equals(c.Value, "True", StringComparison.OrdinalIgnoreCase));

    // The routes a share user may never call (DESIGN 8.4 job d).
    private static bool Banned(string path) =>
        path.Equals("QuickConnect/Authorize", StringComparison.OrdinalIgnoreCase)
        || UserPassword().IsMatch(path)
        || path.Equals("Sessions/Capabilities", StringComparison.OrdinalIgnoreCase)
        || path.Equals("Sessions/Capabilities/Full", StringComparison.OrdinalIgnoreCase);

    private static bool TranscodeStart(HttpContext http, string path)
    {
        if (!VideoTranscodeRoute().IsMatch(path))
        {
            return false;
        }

        // Direct play (static=true) is not counted.
        return !(http.Request.Query.TryGetValue("static", out var s) && s.Any(v => string.Equals(v, "true", StringComparison.OrdinalIgnoreCase)));
    }

    // A share user's transcode needs a play session id (400 without one); a NEW id while the share already has its limit of active transcodes is
    // refused (429). An accepted id is reserved at once and counts as active until its job exists or the grace time is over, so parallel fresh
    // ids cannot all pass before the first job starts (Warden M8). Best effort: it is keyed on the client-chosen id.
    private ShoalResult? TranscodeGate(Guid shareUser, GuardSnapshot snapshot, HttpContext http)
    {
        var playSession = http.Request.Query["PlaySessionId"].FirstOrDefault();
        if (string.IsNullOrEmpty(playSession) || playSession.Length > 128)
        {
            return new ShoalResult(StatusCodes.Status400BadRequest, new { error = "playSessionRequired" });
        }

        var now = _time.GetUtcNow();
        var mine = _sessions.GetOrAdd(shareUser, static _ => new ConcurrentDictionary<string, DateTimeOffset>(StringComparer.Ordinal));
        lock (mine)
        {
            foreach (var stale in mine.Where(kv => now - kv.Value > SessionLife).Select(kv => kv.Key).ToList())
            {
                mine.TryRemove(stale, out _);
            }

            if (mine.ContainsKey(playSession))
            {
                mine[playSession] = now;
                return null;
            }

            var limit = snapshot.TranscodeLimits.GetValueOrDefault(shareUser, 4);
            var active = mine.Count(kv => now - kv.Value < ReserveGrace || _transcodes.IsTranscoding(kv.Key));
            if (active >= limit || mine.Count >= MaxTrackedSessions)
            {
                _counters.TranscodeCap();
                return new ShoalResult(StatusCodes.Status429TooManyRequests, new { error = "rateLimited" }, 10);
            }

            mine[playSession] = now;
            return null;
        }
    }

    private static Guid? AnonymousItemRoute(ResourceExecutingContext context)
    {
        // No authorization attribute and an item-id route value: the routes core serves without a sign-in (DESIGN 8.4 job a).
        if (!GuardRoutes.IsAnonymous(context.ActionDescriptor.EndpointMetadata))
        {
            return null;
        }

        foreach (var key in new[] { "itemId", "routeItemId", "videoId" })
        {
            if (context.RouteData.Values.TryGetValue(key, out var raw) && raw is not null && Guid.TryParse(raw.ToString(), out var id))
            {
                return id;
            }
        }

        return null;
    }

    private ShoalResult? Anonymous(HttpContext http, string path, Guid itemId, GuardSnapshot snapshot, Guid? user)
    {
        var mode = _shares.Mode;
        if (mode == HardeningMode.Off)
        {
            return null;
        }

        if (!_cache.TryGet(itemId, out var shared))
        {
            var answer = _items.IsShared(itemId, snapshot.SharedLibraryIds);
            if (answer is null)
            {
                return null; // no such item: nothing to protect, and nothing is cached
            }

            shared = answer.Value;
            _cache.Put(itemId, shared);
        }

        if (!shared)
        {
            return null;
        }

        var ip = http.Connection.RemoteIpAddress;
        var network = NetworkOf(ip);
        _counters.Seen(network, ip?.ToString() ?? "unknown");
        if (user is { } id)
        {
            if (IsApiKey(http) || _items.UserCanSee(itemId, id))
            {
                return null;
            }
        }

        var kind = GuardRoutes.KindOf(path);
        if (mode == HardeningMode.LogOnly)
        {
            _counters.Blocked(kind, http.Request.Headers.UserAgent.ToString(), network);
            return null;
        }

        // Enforce: posters stay open (every client loads them with a plain <img>).
        if (kind == "image")
        {
            return null;
        }

        _counters.Blocked(kind, http.Request.Headers.UserAgent.ToString(), network);
        return user is null
            ? new ShoalResult(StatusCodes.Status401Unauthorized, new { error = "unauthorized" })
            : new ShoalResult(StatusCodes.Status404NotFound, new { error = "notFound" });
    }

    private static string NetworkOf(System.Net.IPAddress? ip)
    {
        if (ip is null)
        {
            return "public";
        }

        if (ip.IsIPv4MappedToIPv6)
        {
            ip = ip.MapToIPv4();
        }

        if (System.Net.IPAddress.IsLoopback(ip))
        {
            return "loopback";
        }

        var b = ip.GetAddressBytes();
        if (b.Length == 4)
        {
            return b[0] == 100 && b[1] is >= 64 and <= 127 ? "tailnet" : b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168) ? "lan" : "public";
        }

        return b[0] == 0xfd && b[1] == 0x7a ? "tailnet" : (b[0] & 0xfe) == 0xfc ? "lan" : "public";
    }

    [GeneratedRegex(@"^Users/(\{[^}]+\}/)?Password$", RegexOptions.IgnoreCase)]
    private static partial Regex UserPassword();

    [GeneratedRegex(@"^Videos/\{[^}]+\}/(master\.m3u8|main\.m3u8|hls1/.+|stream(\..+)?)$", RegexOptions.IgnoreCase)]
    private static partial Regex VideoTranscodeRoute();
}
