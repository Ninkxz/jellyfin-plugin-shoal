using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.Authorization;
using Jellyfin.Plugin.Shoal.Http;
using Jellyfin.Plugin.Shoal.Sync;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Shoal.Playback;

/// <summary>
/// Serves <c>GET Videos/{itemId}/Trickplay/{width}/{index}.jpg</c> for a federated item (DESIGN v2.6.5, K30).
/// </summary>
public interface ITrickplayHandler
{
    /// <summary>
    /// Answers the request.
    /// </summary>
    /// <param name="route">The classified request.</param>
    /// <param name="cancellationToken">Cancellation (the client's abort).</param>
    /// <returns>The result to send; core's action never runs.</returns>
    Task<IActionResult> HandleAsync(FederatedRoute route, CancellationToken cancellationToken);
}

/// <summary>
/// Serves <c>GET Videos/{videoId}/{mediaSourceId}/Attachments/{index}</c> (a font) for a federated item (DESIGN v2.6.6, K31).
/// </summary>
public interface IAttachmentHandler
{
    /// <summary>
    /// Answers the request.
    /// </summary>
    /// <param name="route">The classified request.</param>
    /// <param name="cancellationToken">Cancellation (the client's abort).</param>
    /// <returns>The result to send; core's action never runs.</returns>
    Task<IActionResult> HandleAsync(FederatedRoute route, CancellationToken cancellationToken);
}

/// <summary>
/// Maps a route item and an optional requested <c>mediaSourceId</c> to the version that is served (DESIGN v2.6.4, K28/K29). Until the version
/// sets exist the answer is the route item's own source, which is what v1 does.
/// </summary>
public interface IVersionResolver
{
    /// <summary>
    /// Resolves the version.
    /// </summary>
    /// <param name="routeItemId">The local item id of the route.</param>
    /// <param name="requestedMediaSourceId">The <c>mediaSourceId</c> the client sent, or null.</param>
    /// <returns>The version, or null when the item is unknown, the request does not parse or it names an item outside the route item's set (a 404 with no Peer call).</returns>
    ResolvedVersion? Resolve(Guid routeItemId, string? requestedMediaSourceId);
}

/// <summary>
/// Rebuilds <c>MediaAttachments</c> from the Peer's source (DESIGN v2.6.6, K31). The default returns no attachments, as v1 does.
/// </summary>
public interface IAttachmentRebuilder
{
    /// <summary>
    /// Rebuilds the list.
    /// </summary>
    /// <param name="peerSource">The Peer's source (hostile input).</param>
    /// <param name="localItemId">The local item id.</param>
    /// <param name="localMediaSourceId">The local media source id (the version's item id).</param>
    /// <param name="callerToken">The caller's own token, for delivery URLs that go back to the caller.</param>
    /// <returns>The attachments and the kept indexes.</returns>
    AttachmentRebuild Rebuild(MediaSourceInfo peerSource, Guid localItemId, Guid localMediaSourceId, string callerToken);
}

/// <summary>
/// A request on a core route that Shoal will serve for a federated item in phase 6 (trickplay tiles, attachments). The intercept filter has only
/// classified it (the item is federated and the query is short); the handler runs <see cref="CallerContext"/> itself, so a handler that does not
/// exist yet changes nothing: the default answers 404 for every caller, exactly as the table did before.
/// </summary>
/// <param name="Http">The request.</param>
/// <param name="ItemId">The local item id from the route.</param>
/// <param name="Template">The route template, for logs.</param>
public sealed record FederatedRoute(HttpContext Http, Guid ItemId, string Template);

/// <summary>
/// The version a request means (DESIGN v2.6.4).
/// </summary>
/// <param name="VersionItemId">The local item id of the version (the route item itself until K28).</param>
/// <param name="StubKey">The stub's key.</param>
/// <param name="PeerKey">The peer.</param>
/// <param name="PeerItemId">The peer's item id.</param>
/// <param name="PeerMediaSourceId">The peer's media source id.</param>
/// <param name="SetId">The version set (the primary's stub key).</param>
public sealed record ResolvedVersion(Guid VersionItemId, string StubKey, string PeerKey, Guid PeerItemId, Guid PeerMediaSourceId, string SetId);

/// <summary>
/// The attachments of a rebuilt PlaybackInfo answer and the indexes that may be fetched later (DESIGN v2.6.6, K31).
/// </summary>
/// <param name="Attachments">The attachments for the client.</param>
/// <param name="KeptIndexes">The attachment indexes the grant allows.</param>
public sealed record AttachmentRebuild(IReadOnlyList<MediaAttachment> Attachments, IReadOnlyList<int> KeptIndexes);

/// <summary>
/// The default <see cref="IAttachmentRebuilder"/>: no attachments (v1).
/// </summary>
public sealed class NoAttachments : IAttachmentRebuilder
{
    /// <summary>Gets the only instance.</summary>
    public static NoAttachments Instance { get; } = new();

    /// <inheritdoc />
    public AttachmentRebuild Rebuild(MediaSourceInfo peerSource, Guid localItemId, Guid localMediaSourceId, string callerToken) => new([], []);
}

/// <summary>
/// The default trickplay and attachment handlers: 404 for every caller, counted as a core HLS route, as the table did before phase 6.
/// </summary>
public sealed class NotServedHandlers : ITrickplayHandler, IAttachmentHandler
{
    private readonly InterceptCounters _counters;

    /// <summary>
    /// Initializes a new instance of the <see cref="NotServedHandlers"/> class.
    /// </summary>
    /// <param name="counters">The counters.</param>
    public NotServedHandlers(InterceptCounters counters) => _counters = counters;

    /// <inheritdoc />
    Task<IActionResult> ITrickplayHandler.HandleAsync(FederatedRoute route, CancellationToken cancellationToken) => NotFound();

    /// <inheritdoc />
    Task<IActionResult> IAttachmentHandler.HandleAsync(FederatedRoute route, CancellationToken cancellationToken) => NotFound();

    private Task<IActionResult> NotFound()
    {
        _counters.Increment(InterceptCounters.CoreHls);
        return Task.FromResult<IActionResult>(new ShoalResult(StatusCodes.Status404NotFound));
    }
}

/// <summary>
/// The default <see cref="IVersionResolver"/>: every item is a set of one, its own source.
/// </summary>
public sealed class OwnSourceVersionResolver : IVersionResolver
{
    private readonly ShoalIndex _index;
    private readonly StubCatalog _catalog;

    /// <summary>
    /// Initializes a new instance of the <see cref="OwnSourceVersionResolver"/> class.
    /// </summary>
    /// <param name="index">The federated items.</param>
    /// <param name="catalog">The catalogs.</param>
    public OwnSourceVersionResolver(ShoalIndex index, StubCatalog catalog)
    {
        _index = index;
        _catalog = catalog;
    }

    /// <inheritdoc />
    public ResolvedVersion? Resolve(Guid routeItemId, string? requestedMediaSourceId)
    {
        if (requestedMediaSourceId is not null && (!Http.ShoalRequest.TryParseGuid(requestedMediaSourceId, out var named) || named != routeItemId))
        {
            return null; // a set of one: any other id is outside it
        }

        if (_index.Get(routeItemId) is not { } stub || _catalog.Find(stub.PeerKey, stub.FolderName) is not { } entry)
        {
            return null;
        }

        return MetadataSanitizer.TryParseId(entry.PeerItemId, out var peerItem) && MetadataSanitizer.TryParseId(entry.PeerMediaSourceId, out var peerSource)
            ? new ResolvedVersion(routeItemId, entry.StubKey, stub.PeerKey, peerItem, peerSource, entry.StubKey)
            : null;
    }
}
