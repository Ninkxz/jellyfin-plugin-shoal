using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.State;
using MediaBrowser.Common.Extensions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Shoal.Authorization;

/// <summary>
/// Finds a user by id and describes it for CallerContext.
/// </summary>
public interface ICallerUsers
{
    /// <summary>
    /// Looks a user up.
    /// </summary>
    /// <param name="userId">The id.</param>
    /// <returns>The user, or null when there is none (deleted).</returns>
    CallerUser? Find(Guid userId);
}

/// <summary>
/// Asks Jellyfin whether an item is visible to a user, through the user's own view (library access, parental rating, tags).
/// </summary>
public interface IVisibleItems
{
    /// <summary>
    /// Whether <paramref name="itemId"/> is visible to <paramref name="userId"/>.
    /// </summary>
    /// <param name="userId">A user that exists.</param>
    /// <param name="itemId">The local item id.</param>
    /// <returns>True when core's visibility rules show the item to this user.</returns>
    bool IsVisible(Guid userId, Guid itemId);
}

/// <summary>
/// Tells whether a local item is a federated one and which library it belongs to.
/// </summary>
public interface IFederatedItems
{
    /// <summary>
    /// Looks an item up. This is classification, never authorization.
    /// </summary>
    /// <param name="localItemId">The local item id.</param>
    /// <returns>The peer and mapping, or null when the item is not federated.</returns>
    FederatedItem? Find(Guid localItemId);
}

/// <summary>
/// The only way a federated request is authorized (DESIGN 8.5, Warden A-4, B-6). Core has no <c>[Authorize]</c> on its stream, subtitle,
/// attachment and legacy HLS routes, so for federated items this class is all that stands between the network and a peer's bytes.
/// <para>
/// Identity comes from <c>HttpContext.User</c> (which core's authentication already filled), never from a token Shoal parses itself; the
/// steps run in the order of 8.5 and each refusal says which step refused:
/// 1 authenticated (401), 2 not an API key (403), 3 core's default policy (403; a vanished user is 401), 4 the user exists and is not
/// disabled (401), 5 the item is visible to the user's own view (404), 6 the Shoal access mode admits the user (404, the same answer as for an
/// unknown item), 7 playback is permitted (403 <c>NotAllowed</c>), 8 return a <see cref="Caller"/>.
/// </para>
/// </summary>
public sealed class CallerContext
{
    /// <summary>The claim core puts the user id in (N format).</summary>
    public const string UserIdClaim = "Jellyfin-UserId";

    /// <summary>The claim core puts the device id in.</summary>
    public const string DeviceIdClaim = "Jellyfin-DeviceId";

    /// <summary>The claim core puts the token in.</summary>
    public const string TokenClaim = "Jellyfin-Token";

    /// <summary>The claim that is <c>True</c> for an API key.</summary>
    public const string IsApiKeyClaim = "Jellyfin-IsApiKey";

    private readonly IAuthorizationService _authorization;
    private readonly IAuthorizationPolicyProvider _policies;
    private readonly ICallerUsers _users;
    private readonly IVisibleItems _items;
    private readonly IFederatedItems _federated;
    private readonly IStateStore _state;
    private readonly Func<HttpContext, bool> _isLocalNetwork;
    private readonly ILogger _logger;
    private readonly int _skippedStep;

    /// <summary>
    /// Initializes a new instance of the <see cref="CallerContext"/> class.
    /// </summary>
    /// <param name="authorization">ASP.NET Core's authorization service.</param>
    /// <param name="policies">The policy provider (the default policy is core's).</param>
    /// <param name="users">The users.</param>
    /// <param name="items">Item visibility.</param>
    /// <param name="federated">The federated items.</param>
    /// <param name="state">The state store (the access modes and the share users).</param>
    /// <param name="logger">The logger.</param>
    public CallerContext(
        IAuthorizationService authorization,
        IAuthorizationPolicyProvider policies,
        ICallerUsers users,
        IVisibleItems items,
        IFederatedItems federated,
        IStateStore state,
        ILogger<CallerContext> logger)
        : this(authorization, policies, users, items, federated, state, DefaultIsLocalNetwork, logger)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="CallerContext"/> class (tests: own local-network rule).
    /// </summary>
    /// <param name="authorization">ASP.NET Core's authorization service.</param>
    /// <param name="policies">The policy provider.</param>
    /// <param name="users">The users.</param>
    /// <param name="items">Item visibility.</param>
    /// <param name="federated">The federated items.</param>
    /// <param name="state">The state store.</param>
    /// <param name="isLocalNetwork">Tells whether a request came from the local network.</param>
    /// <param name="skippedStep">Mutation check of the test harness (DESIGN 11.2 K6): the number of one step to leave out, 0 in production.</param>
    /// <param name="logger">The logger.</param>
    internal CallerContext(
        IAuthorizationService authorization,
        IAuthorizationPolicyProvider policies,
        ICallerUsers users,
        IVisibleItems items,
        IFederatedItems federated,
        IStateStore state,
        Func<HttpContext, bool> isLocalNetwork,
        ILogger logger,
        int skippedStep = 0)
    {
        _authorization = authorization;
        _policies = policies;
        _users = users;
        _items = items;
        _federated = federated;
        _state = state;
        _isLocalNetwork = isLocalNetwork;
        _logger = logger;
        _skippedStep = skippedStep;
    }

    /// <summary>
    /// Authorizes a federated request for one local item.
    /// </summary>
    /// <param name="http">The request.</param>
    /// <param name="localItemId">The local item id the route names.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The caller, or a refusal.</returns>
    public Task<CallerDecision> AuthorizeAsync(HttpContext http, Guid localItemId, CancellationToken cancellationToken = default) =>
        RunAsync(http, localItemId, null, full: true, cancellationToken);

    /// <summary>
    /// Authorizes a request on a grant (DESIGN 6.4): steps 1 to 4 always, and the item's steps 5 to 7 only when <paramref name="full"/> is set (the caller re-runs them
    /// when the grant was last authorized more than five minutes ago). A caller who is not the grant's owner gets the same 404 as an unknown grant.
    /// </summary>
    /// <param name="http">The request.</param>
    /// <param name="owner">The local user who owns the grant.</param>
    /// <param name="localItemId">The grant's local item id.</param>
    /// <param name="full">Whether steps 5 to 7 run.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The caller, or a refusal.</returns>
    public Task<CallerDecision> AuthorizeGrantAsync(HttpContext http, Guid owner, Guid localItemId, bool full, CancellationToken cancellationToken = default) =>
        RunAsync(http, localItemId, owner, full, cancellationToken);

    private async Task<CallerDecision> RunAsync(HttpContext http, Guid localItemId, Guid? expectedUser, bool full, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(http);
        cancellationToken.ThrowIfCancellationRequested();
        var principal = http.User;

        // 1. Identity from HttpContext.User. Invalid or logged-out tokens and disabled users have already become an anonymous principal.
        if (principal.Identity?.IsAuthenticated != true)
        {
            return CallerDecision.Deny(401, 1);
        }

        // 2. API keys never act as users.
        if (principal.HasClaim(c => c.Type == IsApiKeyClaim && string.Equals(c.Value, "True", StringComparison.OrdinalIgnoreCase)))
        {
            return CallerDecision.Deny(403, 2);
        }

        // 3. Core's default policy: remote access and the parental access schedule.
        try
        {
            var policy = await _policies.GetDefaultPolicyAsync().ConfigureAwait(false);
            var result = await _authorization.AuthorizeAsync(principal, null, policy).ConfigureAwait(false);
            if (!result.Succeeded)
            {
                return CallerDecision.Deny(403, 3);
            }
        }
        catch (ResourceNotFoundException)
        {
            return CallerDecision.Deny(401, 3); // the handler throws this for a user that no longer exists
        }

        if (_skippedStep != 4 && (!Guid.TryParse(principal.FindFirst(UserIdClaim)?.Value, out var userId) || userId == Guid.Empty))
        {
            return CallerDecision.Deny(401, 4);
        }

        _ = Guid.TryParse(principal.FindFirst(UserIdClaim)?.Value, out userId);
        var user = _users.Find(userId);
        if (_skippedStep != 4 && (user is null || user.IsDisabled))
        {
            return CallerDecision.Deny(401, 4);
        }

        user ??= new CallerUser(userId, false, false, true, true, true, false, null); // only reachable when a test removes step 4

        if (expectedUser is { } owner && owner != user.Id)
        {
            return CallerDecision.Deny(404, 4); // another user's grant is the same 404 as no grant
        }

        if (!full)
        {
            var quickToken = principal.FindFirst(TokenClaim)?.Value ?? string.Empty;
            var quickDevice = DeviceKey.Cap(principal.FindFirst(DeviceIdClaim)?.Value);
            return CallerDecision.Allow(new Caller(user, quickToken, quickDevice, _isLocalNetwork(http)));
        }

        // 5. Visibility through the user's own view; an unknown or hidden item is the same 404.
        if (!_items.IsVisible(user.Id, localItemId))
        {
            return CallerDecision.Deny(404, 5);
        }

        // 6. The Shoal access mode, read from the state at request time. Nothing is materialized per user.
        if (_skippedStep != 6 && _state.Status != StateStatus.Ready)
        {
            return CallerDecision.Deny(403, 6, PlaybackDenial.NotAllowed); // recovery mode: every peer counts as disabled
        }

        if (_skippedStep != 6 && !IsAdmitted(user, localItemId))
        {
            return CallerDecision.Deny(404, 6);
        }

        // 7. Permission to play.
        if (!user.CanPlay)
        {
            return CallerDecision.Deny(403, 7, PlaybackDenial.NotAllowed);
        }

        // 8. The caller.
        var token = principal.FindFirst(TokenClaim)?.Value ?? string.Empty;
        var deviceId = DeviceKey.Cap(principal.FindFirst(DeviceIdClaim)?.Value);
        return CallerDecision.Allow(new Caller(user, token, deviceId, _isLocalNetwork(http)));
    }

    private static bool DefaultIsLocalNetwork(HttpContext http) =>
        http.Connection.RemoteIpAddress is { } address && (System.Net.IPAddress.IsLoopback(address) || IsPrivate(address));

    private static bool IsPrivate(System.Net.IPAddress address)
    {
        var bytes = address.MapToIPv4().GetAddressBytes();
        return bytes[0] == 10 || (bytes[0] == 172 && (bytes[1] & 0xf0) == 0x10) || (bytes[0] == 192 && bytes[1] == 168);
    }

    private bool IsAdmitted(CallerUser user, Guid localItemId)
    {
        var item = _federated.Find(localItemId);
        if (item is null)
        {
            return false; // not a federated item: this class is for federated requests only
        }

        if (item.PeerKey.Length == 0)
        {
            return false; // a title folder in a shared library whose owner is not known (yet): fail closed
        }

        var state = _state.Current;
        var peer = state.Home.Peers.FirstOrDefault(p => string.Equals(p.PeerKey, item.PeerKey, StringComparison.Ordinal));
        var mapping = peer?.Libraries.FirstOrDefault(l => string.Equals(l.MappingKey, item.MappingKey, StringComparison.Ordinal));
        if (peer is { Enabled: false })
        {
            return false; // a paused or disabled peer is unknown to every request (W6 L-4), whatever a handler later remembers to check
        }

        if (peer is null || mapping is null)
        {
            _logger.LogWarning("A federated item belongs to a library that is not in the state ({Peer})", Security.Redact.Id(item.PeerKey));
            return false;
        }

        // "Hide friends' titles" (K45): the BlockedTags entry hides the titles from lists; playback refuses them as well, whatever core's tags say right now.
        if (state.Home.Visibility.HiddenFriendTitles.Contains(user.Id))
        {
            return false;
        }

        // Admitted(user, L), where L is the library the item's file is in (K45): a shared library's own access, a legacy folder's mapping access.
        var access = LibraryFolders.AccessOfItem(state, item.PeerKey, item.MappingKey, item.LibKey, item.IsCopy);
        if (access is null)
        {
            return false;
        }

        var shareUsers = state.PeerRole.Shares.Select(s => s.ShareUserId).ToHashSet();
        return AccessPolicy.Admitted(user, access, shareUsers);
    }
}
