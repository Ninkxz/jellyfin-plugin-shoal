using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.Authorization;
using Jellyfin.Plugin.Shoal.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Shoal.Playback;

/// <summary>
/// Handles <c>Items/{itemId}/PlaybackInfo</c> for a federated item (DESIGN 6.2).
/// </summary>
public interface IPlaybackInfoHandler
{
    /// <summary>
    /// Answers the request.
    /// </summary>
    /// <param name="request">The checked request.</param>
    /// <param name="cancellationToken">Cancellation (the client's abort).</param>
    /// <returns>The result to send; core's action never runs.</returns>
    Task<IActionResult> HandleAsync(FederatedRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// Handles a static (direct play) stream of a federated item (DESIGN 6.3).
/// </summary>
public interface IStaticStreamHandler
{
    /// <summary>
    /// Answers the request.
    /// </summary>
    /// <param name="request">The checked request.</param>
    /// <param name="cancellationToken">Cancellation (the client's abort).</param>
    /// <returns>The result to send; core's action never runs.</returns>
    Task<IActionResult> HandleAsync(FederatedRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// Handles the core subtitle route for a federated item (DESIGN 6.6).
/// </summary>
public interface ISubtitleHandler
{
    /// <summary>
    /// Answers the request.
    /// </summary>
    /// <param name="request">The checked request, for the route item.</param>
    /// <param name="cancellationToken">Cancellation (the client's abort).</param>
    /// <returns>The result to send; core's action never runs.</returns>
    Task<IActionResult> HandleAsync(FederatedRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// Handles <c>DELETE Videos/ActiveEncodings</c> when the play session belongs to one of the caller's grants (DESIGN 6.1, W4-4).
/// </summary>
public interface IActiveEncodingsHandler
{
    /// <summary>
    /// Answers the request if the play session is one of the caller's grants.
    /// </summary>
    /// <param name="http">The request.</param>
    /// <param name="userId">The authenticated user, or null when the request carries no user.</param>
    /// <param name="deviceId">The <c>deviceId</c> query value, or null.</param>
    /// <param name="playSessionId">The <c>playSessionId</c> query value, or null.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The result to send, or null when no grant matches and core should handle the request.</returns>
    Task<IActionResult?> HandleAsync(HttpContext http, Guid? userId, string? deviceId, string? playSessionId, CancellationToken cancellationToken);
}

/// <summary>
/// A federated request that passed CallerContext.
/// </summary>
/// <param name="Http">The request.</param>
/// <param name="ItemId">The local item id (the route id).</param>
/// <param name="Stub">The stub the item stands for.</param>
/// <param name="Caller">The checked caller.</param>
/// <param name="Template">The route template, for logs.</param>
public sealed record FederatedRequest(HttpContext Http, Guid ItemId, IndexedStub Stub, Caller Caller, string Template);

/// <summary>
/// The handlers that later tasks replace (K14 PlaybackInfo, K15 static stream, K18 subtitles, K17 ActiveEncodings). Until then a federated request
/// that passes every check is answered 501 by Shoal: never by core, which would try to read the stub.
/// </summary>
public sealed class NotYetImplementedHandlers : IPlaybackInfoHandler, IStaticStreamHandler, ISubtitleHandler, IActiveEncodingsHandler
{
    /// <inheritdoc />
    Task<IActionResult> IPlaybackInfoHandler.HandleAsync(FederatedRequest request, CancellationToken cancellationToken) => NotImplemented();

    /// <inheritdoc />
    Task<IActionResult> IStaticStreamHandler.HandleAsync(FederatedRequest request, CancellationToken cancellationToken) => NotImplemented();

    /// <inheritdoc />
    Task<IActionResult> ISubtitleHandler.HandleAsync(FederatedRequest request, CancellationToken cancellationToken) => NotImplemented();

    /// <inheritdoc />
    Task<IActionResult?> IActiveEncodingsHandler.HandleAsync(HttpContext http, Guid? userId, string? deviceId, string? playSessionId, CancellationToken cancellationToken) =>
        Task.FromResult<IActionResult?>(null); // no grants exist yet: core handles it

    private static Task<IActionResult> NotImplemented() => Task.FromResult<IActionResult>(new ShoalResult(StatusCodes.Status501NotImplemented));
}

/// <summary>
/// The counters Pilot reads (DESIGN 6.1): how often a federated item was asked for on a route Shoal does not serve.
/// </summary>
public sealed class InterceptCounters
{
    /// <summary>The counter for a non-static <c>stream</c> request (a progressive transcode or remux; DESIGN v2.6.3, v2.6.8).</summary>
    public const string NonStaticStream = "nonstatic-stream";

    /// <summary>The counter for the <c>Audio/...</c> routes.</summary>
    public const string AudioRoute = "audio-route";

    /// <summary>The counter for <c>Trickplay/{width}/tiles.m3u8</c>.</summary>
    public const string TrickplayPlaylist = "trickplay-m3u8";

    /// <summary>The counter for a federated request that arrived without credentials.</summary>
    public const string NoCredentials = "no-credentials";

    /// <summary>The counter for a core HLS, attachment or trickplay route.</summary>
    public const string CoreHls = "core-hls-route";

    /// <summary>The counter for <c>Download</c> and <c>File</c>.</summary>
    public const string Forbidden = "forbidden-route";

    /// <summary>The counter for requests with a query string over the limit.</summary>
    public const string LongQuery = "long-query";

    private static readonly string[] Known = [NonStaticStream, AudioRoute, CoreHls, Forbidden, LongQuery, TrickplayPlaylist, NoCredentials];

    private readonly ConcurrentDictionary<string, long> _counts = new(StringComparer.Ordinal);

    /// <summary>
    /// Initializes a new instance of the <see cref="InterceptCounters"/> class.
    /// </summary>
    /// <param name="time">The clock (default: the system clock).</param>
    public InterceptCounters(TimeProvider? time = null) => SinceUtc = (time ?? TimeProvider.System).GetUtcNow().UtcDateTime;

    /// <summary>Gets when counting started (UTC; the counters are memory only and start again with the server).</summary>
    public DateTime SinceUtc { get; }

    /// <summary>Gets the names of the counters, in the order they are listed.</summary>
    public static IReadOnlyList<string> Names => Known;

    /// <summary>
    /// Adds one to a counter.
    /// </summary>
    /// <param name="name">The counter.</param>
    public void Increment(string name) => _counts.AddOrUpdate(name, 1, static (_, value) => value + 1);

    /// <summary>
    /// Reads a counter.
    /// </summary>
    /// <param name="name">The counter.</param>
    /// <returns>The count.</returns>
    public long Get(string name) => _counts.GetValueOrDefault(name);

    /// <summary>
    /// Copies all counters.
    /// </summary>
    /// <returns>The counters by name.</returns>
    public IReadOnlyDictionary<string, long> Snapshot() => new Dictionary<string, long>(_counts, StringComparer.Ordinal);
}
