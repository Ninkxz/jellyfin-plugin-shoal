using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.Authorization;
using Jellyfin.Plugin.Shoal.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Shoal.Playback;

/// <summary>
/// The global MVC resource filter that takes over every core media route for federated items (DESIGN 6.1). Core leaves several of these routes
/// anonymous and would read the stub, so for a federated item nothing falls through to core: the filter answers, or hands the request to the
/// handler after <see cref="CallerContext"/> allowed it, and never calls the rest of the pipeline.
/// <para>
/// A request that is not for a federated item costs one table lookup and one dictionary hit, with no I/O and no body read. The query is read
/// only through <c>Request.Query</c> (the collection MVC binds from), GUIDs are parsed with <c>Guid.TryParse</c>, and a repeated <c>itemId</c>
/// is judged value by value (W4-12). No log line names a path or a query.
/// </para>
/// </summary>
public sealed class ShoalInterceptFilter : IAsyncResourceFilter
{
    private const string StaticKey = "Static";
    private const string ItemIdKey = "itemId";

    private readonly ShoalIndex _index;
    private readonly CallerContext _callerContext;
    private readonly IPlaybackInfoHandler _playbackInfo;
    private readonly IStaticStreamHandler _staticStream;
    private readonly ISubtitleHandler _subtitles;
    private readonly IActiveEncodingsHandler _activeEncodings;
    private readonly InterceptCounters _counters;
    private readonly ILogger<ShoalInterceptFilter> _logger;
    private readonly ITrickplayHandler? _trickplay;
    private readonly IAttachmentHandler? _attachments;
    private readonly IVersionResolver? _versions;

    /// <summary>
    /// Initializes a new instance of the <see cref="ShoalInterceptFilter"/> class.
    /// </summary>
    /// <param name="index">The federated items.</param>
    /// <param name="callerContext">The only authorization of federated requests (DESIGN 8.5).</param>
    /// <param name="playbackInfo">The PlaybackInfo handler.</param>
    /// <param name="staticStream">The static stream handler.</param>
    /// <param name="subtitles">The subtitle handler.</param>
    /// <param name="activeEncodings">The ActiveEncodings handler.</param>
    /// <param name="counters">The counters.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="trickplay">The trickplay tile handler; none means 404 (counted as a core HLS route).</param>
    /// <param name="attachments">The attachment handler; none means 404 (counted as a core HLS route).</param>
    /// <param name="versions">Tells whether a media source belongs to the same title as the route item (DESIGN v2.6.4); none means only the item itself does.</param>
    public ShoalInterceptFilter(
        ShoalIndex index,
        CallerContext callerContext,
        IPlaybackInfoHandler playbackInfo,
        IStaticStreamHandler staticStream,
        ISubtitleHandler subtitles,
        IActiveEncodingsHandler activeEncodings,
        InterceptCounters counters,
        ILogger<ShoalInterceptFilter> logger,
        ITrickplayHandler? trickplay = null,
        IAttachmentHandler? attachments = null,
        IVersionResolver? versions = null)
    {
        _versions = versions;
        _trickplay = trickplay;
        _attachments = attachments;
        _index = index;
        _callerContext = callerContext;
        _playbackInfo = playbackInfo;
        _staticStream = staticStream;
        _subtitles = subtitles;
        _activeEncodings = activeEncodings;
        _counters = counters;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task OnResourceExecutionAsync(ResourceExecutingContext context, ResourceExecutionDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);
        var template = context.ActionDescriptor.AttributeRouteInfo?.Template;
        var route = InterceptTable.Find(template);
        if (route is null)
        {
            await next().ConfigureAwait(false);
            return;
        }

        var http = context.HttpContext;
        IActionResult? result;
        try
        {
            result = await DecideAsync(http, route, template!).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (http.RequestAborted.IsCancellationRequested)
        {
            context.Result = new ShoalResult(499);
            return;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Boundary rule (W B-11): nothing a handler throws reaches core's ExceptionMiddleware, which logs socket messages with the peer host.
            _logger.LogWarning("A federated request on {Template} failed ({Error})", template, ex.GetType().Name);
            context.Result = new ShoalResult(StatusCodes.Status500InternalServerError);
            return;
        }

        if (result is null)
        {
            await next().ConfigureAwait(false);
            return;
        }

        context.Result = result;
    }

    private static bool IsStatic(HttpRequest request) =>
        ShoalRequest.Values(request, StaticKey) is [var first, ..] && string.Equals(first.Trim(), "true", StringComparison.OrdinalIgnoreCase);

    private static bool TryRouteId(HttpContext http, InterceptRoute route, out Guid id)
    {
        id = Guid.Empty;
        return route.IdName is not null
            && http.Request.RouteValues.TryGetValue(route.IdName, out var raw)
            && ShoalRequest.TryParseGuid(raw?.ToString(), out id);
    }

    private static ShoalResult Status(int status) => new(status);

    // The media source an attachment route names is a route value; a trickplay route takes it from the query.
    private static IEnumerable<string> NamedSources(HttpContext http, RouteKind kind) =>
        kind == RouteKind.Attachment
            ? (http.Request.RouteValues.TryGetValue("mediaSourceId", out var routed) && routed?.ToString() is { } text ? [text] : [])
            : ShoalRequest.Values(http.Request, "mediaSourceId");

    // A federated media source other than the route item is acceptable only when it is another version of the same title (DESIGN v2.6.4).
    private bool InSet(Guid routeItem, string? mediaSource) => _versions is not null && mediaSource is not null && _versions.Resolve(routeItem, mediaSource) is not null;

    private bool IsFederated(Guid id, bool readsStub) => readsStub || !_index.IsReady ? _index.ContainsOrClassify(id) : _index.Contains(id);

    /// <summary>Returns the result to send, or null to let core handle the request.</summary>
    private async Task<IActionResult?> DecideAsync(HttpContext http, InterceptRoute route, string template)
    {
        switch (route.Kind)
        {
            case RouteKind.LiveStreamsOpen:
                return ShoalRequest.Values(http.Request, ItemIdKey).Any(v => ShoalRequest.TryParseGuid(v, out var id) && IsFederated(id, readsStub: false))
                    ? Answer(template, StatusCodes.Status400BadRequest)
                    : null;

            case RouteKind.ActiveEncodings:
                return await _activeEncodings.HandleAsync(
                    http,
                    TryUserId(http),
                    ShoalRequest.Values(http.Request, "deviceId") is [var device, ..] ? device : null,
                    ShoalRequest.Values(http.Request, "playSessionId") is [var play, ..] ? play : null,
                    http.RequestAborted).ConfigureAwait(false);

            case RouteKind.Subtitle:
                return await DecideSubtitleAsync(http, route, template).ConfigureAwait(false);
        }

        // SR 4.2: core resolves MediaSourceId among the route item's sources, linked alternates included, so a local route id naming a stub as its
        // media source would reach the stub's sentinel. A federated mediaSourceId that is not the route id is a 404 before anything else.
        if (route.Kind == RouteKind.StaticStream && TryRouteId(http, route, out var staticRouteId)
            && ShoalRequest.Values(http.Request, "mediaSourceId").Any(v => ShoalRequest.TryParseGuid(v, out var named) && named != staticRouteId && IsFederated(named, readsStub: true) && !InSet(staticRouteId, v)))
        {
            return Answer(template, StatusCodes.Status404NotFound);
        }

        // The same rule for the attachment and trickplay routes (Warden 10 L5): a local route id must not name a stub as its media source either.
        if (route.Kind is RouteKind.Attachment or RouteKind.Trickplay && TryRouteId(http, route, out var mediaRouteId)
            && NamedSources(http, route.Kind).Any(v => ShoalRequest.TryParseGuid(v, out var named) && named != mediaRouteId && IsFederated(named, readsStub: true) && !InSet(mediaRouteId, v)))
        {
            return Answer(template, StatusCodes.Status404NotFound);
        }

        if (!TryRouteId(http, route, out var itemId) || !IsFederated(itemId, route.ReadsStub))
        {
            return null; // not ours: no I/O, no body read
        }

        if (ShoalRequest.QueryTooLong(http.Request))
        {
            _counters.Increment(InterceptCounters.LongQuery);
            return Answer(template, StatusCodes.Status414UriTooLong);
        }

        switch (route.Kind)
        {
            case RouteKind.Forbidden:
                _counters.Increment(InterceptCounters.Forbidden);
                return Answer(template, StatusCodes.Status403Forbidden);

            case RouteKind.CoreHls:
                _counters.Increment(InterceptCounters.CoreHls);
                return Answer(template, StatusCodes.Status404NotFound);

            case RouteKind.TrickplayPlaylist:
                _counters.Increment(InterceptCounters.TrickplayPlaylist);
                return Answer(template, StatusCodes.Status404NotFound);

            case RouteKind.Trickplay:
                return await ServeAsync(_trickplay, http, itemId, template).ConfigureAwait(false);

            case RouteKind.Attachment:
                return await ServeAsync(_attachments, http, itemId, template).ConfigureAwait(false);

            case RouteKind.Audio:
                _counters.Increment(InterceptCounters.AudioRoute);
                return Answer(template, StatusCodes.Status404NotFound);

            case RouteKind.StaticStream when !IsStatic(http.Request):
                _counters.Increment(InterceptCounters.NonStaticStream); // a progressive transcode or remux: not built (DESIGN v2.6.8)
                return Answer(template, StatusCodes.Status404NotFound);

            case RouteKind.StaticStream:
                return await AuthorizedAsync(http, itemId, template, route.Kind, NamedVersion(http, itemId)).ConfigureAwait(false);

            case RouteKind.PlaybackInfo:
                return await AuthorizedAsync(http, itemId, template, route.Kind).ConfigureAwait(false);

            default:
                return null;
        }
    }

    /// <summary>
    /// The core subtitle route: its obsolete <c>itemId</c> query value overrides the route id, and the route is anonymous, so this decision is the
    /// only gate. The id that is authorized is the id that is served, and that is the route id (DESIGN 6.1, W4-3, W4-12).
    /// </summary>
    private async Task<IActionResult?> DecideSubtitleAsync(HttpContext http, InterceptRoute route, string template)
    {
        var haveRouteId = TryRouteId(http, route, out var routeId);
        var queryValues = ShoalRequest.Values(http.Request, ItemIdKey);
        var federated = (haveRouteId && IsFederated(routeId, readsStub: true))
            || queryValues.Any(v => ShoalRequest.TryParseGuid(v, out var id) && IsFederated(id, readsStub: true))
            || (http.Request.RouteValues.TryGetValue("routeMediaSourceId", out var routeSource) && ShoalRequest.TryParseGuid(routeSource?.ToString(), out var sourceId) && IsFederated(sourceId, readsStub: true));
        if (!federated)
        {
            return null;
        }

        if (ShoalRequest.QueryTooLong(http.Request))
        {
            _counters.Increment(InterceptCounters.LongQuery);
            return Answer(template, StatusCodes.Status414UriTooLong);
        }

        // A federated media source in the route that is not the route item (a merged-version trick) is the same 404 (SR 4.2).
        var foreignSource = http.Request.RouteValues.TryGetValue("routeMediaSourceId", out var source) && ShoalRequest.TryParseGuid(source?.ToString(), out var sourceGuid) && haveRouteId && sourceGuid != routeId && IsFederated(sourceGuid, readsStub: true) && !InSet(routeId, source?.ToString());

        // Any itemId value that is not the route id (an unparseable one counts as not equal) is a 404 before anything else happens.
        if (!haveRouteId || foreignSource || queryValues.Any(v => !ShoalRequest.TryParseGuid(v, out var id) || id != routeId))
        {
            return Answer(template, StatusCodes.Status404NotFound);
        }

        return await AuthorizedAsync(http, routeId, template, RouteKind.Subtitle, http.Request.RouteValues.TryGetValue("routeMediaSourceId", out var named) && ShoalRequest.TryParseGuid(named?.ToString(), out var namedGuid) && namedGuid != routeId && InSet(routeId, named?.ToString()) ? namedGuid : null).ConfigureAwait(false);
    }

    // The other version a request names, when it is a member of the route item's set (DESIGN v2.6.4): it is authorized too, which repeats the same library check.
    private Guid? NamedVersion(HttpContext http, Guid routeItem) =>
        ShoalRequest.Values(http.Request, "mediaSourceId") is [var first, ..] && ShoalRequest.TryParseGuid(first, out var named) && named != routeItem && InSet(routeItem, first) ? named : null;

    private async Task<IActionResult?> AuthorizedAsync(HttpContext http, Guid itemId, string template, RouteKind kind, Guid? alsoVersion = null)
    {
        var decision = await _callerContext.AuthorizeAsync(http, itemId, http.RequestAborted).ConfigureAwait(false);
        if (decision.Allowed && alsoVersion is { } version)
        {
            decision = await _callerContext.AuthorizeAsync(http, version, http.RequestAborted).ConfigureAwait(false);
        }

        if (!decision.Allowed)
        {
            _logger.LogDebug("Federated request on {Template} refused at step {Step} with {Status}", template, decision.DeniedAtStep, decision.Status);
            if (decision.Status == StatusCodes.Status401Unauthorized)
            {
                _counters.Increment(InterceptCounters.NoCredentials);
            }

            return kind == RouteKind.PlaybackInfo && decision.PlaybackError == PlaybackDenial.NotAllowed
                ? new ShoalResult(decision.Status, new Dictionary<string, string> { ["ErrorCode"] = "NotAllowed" })
                : new ShoalResult(decision.Status);
        }

        var stub = _index.Get(itemId);
        if (stub is null)
        {
            return Answer(template, StatusCodes.Status404NotFound); // the item vanished between the check and now
        }

        var request = new FederatedRequest(http, itemId, stub, decision.Caller!, template);
        return kind switch
        {
            RouteKind.PlaybackInfo => await _playbackInfo.HandleAsync(request, http.RequestAborted).ConfigureAwait(false),
            RouteKind.StaticStream => await _staticStream.HandleAsync(request, http.RequestAborted).ConfigureAwait(false),
            RouteKind.Subtitle => await _subtitles.HandleAsync(request, http.RequestAborted).ConfigureAwait(false),
            _ => Answer(template, StatusCodes.Status404NotFound),
        };
    }

    private async Task<IActionResult> ServeAsync(object? handler, HttpContext http, Guid itemId, string template)
    {
        var route = new FederatedRoute(http, itemId, template);
        switch (handler)
        {
            case ITrickplayHandler trickplay:
                return await trickplay.HandleAsync(route, http.RequestAborted).ConfigureAwait(false);
            case IAttachmentHandler attachments:
                return await attachments.HandleAsync(route, http.RequestAborted).ConfigureAwait(false);
            default:
                _counters.Increment(InterceptCounters.CoreHls);
                return Answer(template, StatusCodes.Status404NotFound);
        }
    }

    private static Guid? TryUserId(HttpContext http) =>
        http.User.Identity?.IsAuthenticated == true && Guid.TryParse(http.User.FindFirst(CallerContext.UserIdClaim)?.Value, out var id) ? id : null;

    private ShoalResult Answer(string template, int status)
    {
        _logger.LogDebug("Federated request on {Template} answered {Status}", template, status);
        return Status(status);
    }
}
