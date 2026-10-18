using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.Http;
using Jellyfin.Plugin.Shoal.Security;
using Jellyfin.Plugin.Shoal.State;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Shoal.Api.Admin;

/// <summary>
/// Quality limits and the play order (PLAN v2.9 K51-K53): the quality panel of a connection, changing its limit (with the limit the admin saw), dismissing the
/// speed-test card, "Which server plays first" and Playing now. Admin only (<c>RequiresElevation</c>); every name and text in the answers is set as text by the page.
/// </summary>
[ApiController]
[Authorize(Policy = Policies.RequiresElevation)]
[Route("Shoal/Admin")]
public class AdminQualityController : ControllerBase
{
    private const string UserIdClaim = "Jellyfin-UserId"; // core's InternalClaimTypes.UserId

    private readonly IStateStore _state;
    private readonly QualityAdmin _quality;
    private readonly ILogger<AdminQualityController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="AdminQualityController"/> class.
    /// </summary>
    /// <param name="state">The state store.</param>
    /// <param name="quality">The quality service.</param>
    /// <param name="logger">The logger.</param>
    public AdminQualityController(IStateStore state, QualityAdmin quality, ILogger<AdminQualityController> logger)
    {
        _state = state;
        _quality = quality;
        _logger = logger;
    }

    /// <summary>
    /// The quality panel of a connection: <c>{ limit, effective, costText?, recommendation?, dismissed[], rank? }</c>.
    /// </summary>
    /// <param name="peerKey">The connection.</param>
    /// <returns>200, 404 or 409 (<c>recovery</c>).</returns>
    [HttpGet("Peers/{peerKey}/Quality")]
    [Produces("application/json")]
    public IActionResult Panel(string peerKey)
    {
        if (_state.Status != StateStatus.Ready)
        {
            return AdminReply.Error(StatusCodes.Status409Conflict, "recovery");
        }

        return _quality.Panel(peerKey) is { } panel ? AdminReply.Json(StatusCodes.Status200OK, panel) : AdminReply.Error(StatusCodes.Status404NotFound, "notFound");
    }

    /// <summary>
    /// Sets the quality limit of a connection. Body <c>{ maxClass, maxMbps?, fromRecommendation?, expectedCurrent: { maxClass, maxMbps? } }</c>; the Mbps is clamped
    /// to 1..1000. When the stored limit is not <c>expectedCurrent</c> nothing changes and the answer is 409 <c>{ error: "changed", current }</c>.
    /// </summary>
    /// <param name="peerKey">The connection.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>200 with the new limit, 400 (<c>invalid</c> with <c>detail</c>), 404 or 409 (<c>recovery</c>, <c>changed</c>).</returns>
    [HttpPut("Peers/{peerKey}/QualityLimit")]
    [Produces("application/json")]
    public async Task<IActionResult> SetLimit(string peerKey, CancellationToken cancellationToken)
    {
        if (_state.Status != StateStatus.Ready)
        {
            return AdminReply.Error(StatusCodes.Status409Conflict, "recovery");
        }

        if (!TryAdmin(out var admin))
        {
            return AdminReply.Error(StatusCodes.Status400BadRequest, "adminUserRequired"); // an API key has no admin user
        }

        var body = await AdminReply.ReadAsync<QualityLimitRequest>(Request, cancellationToken).ConfigureAwait(false);
        if (body is null)
        {
            return AdminReply.Error(StatusCodes.Status400BadRequest, "invalid");
        }

        var change = await _quality.SetLimitAsync(peerKey, body, admin, cancellationToken).ConfigureAwait(false);
        switch (change.Outcome)
        {
            case QualityChangeOutcome.Saved:
                _logger.LogInformation("Admin set the quality limit of peer {Peer} to {Class} ({Origin})", Redact.Id(peerKey), change.Limit!.MaxClass, change.Limit.Origin);
                return AdminReply.Json(StatusCodes.Status200OK, new { limit = change.Limit, effective = QualityAdmin.Effective(new QualityLimit { MaxClass = change.Limit.MaxClass, MaxMbps = change.Limit.MaxMbps }) });
            case QualityChangeOutcome.Changed:
                return new ShoalResult(StatusCodes.Status409Conflict, new { error = "changed", current = change.Limit });
            case QualityChangeOutcome.Invalid:
                return new ShoalResult(StatusCodes.Status400BadRequest, new { error = "invalid", detail = change.Problem });
            default:
                return AdminReply.Error(StatusCodes.Status404NotFound, "notFound");
        }
    }

    /// <summary>
    /// Sets which tier a connection plays from (Always prefer, Normal, Backup only). Body <c>{ tier, expectedCurrent }</c>; the order is evaluated again at once.
    /// Answers <c>{ tier, rank? { position, count, tier, reason, label } }</c>; 409 <c>{ error: "changed", current }</c> when the stored tier is not the one the admin saw.
    /// Also served as <c>.../PriorityTier</c> (the name the admin page was first built against).
    /// </summary>
    /// <param name="peerKey">The connection.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>200, 400 (<c>invalid</c>, <c>adminUserRequired</c>), 404 or 409 (<c>recovery</c>, <c>changed</c>).</returns>
    [HttpPut("Peers/{peerKey}/Priority")]
    [HttpPut("Peers/{peerKey}/PriorityTier")]
    [Produces("application/json")]
    public async Task<IActionResult> SetPriority(string peerKey, CancellationToken cancellationToken)
    {
        if (_state.Status != StateStatus.Ready)
        {
            return AdminReply.Error(StatusCodes.Status409Conflict, "recovery");
        }

        if (!TryAdmin(out _))
        {
            return AdminReply.Error(StatusCodes.Status400BadRequest, "adminUserRequired"); // an API key has no admin user
        }

        var body = await AdminReply.ReadAsync<PriorityTierRequest>(Request, cancellationToken).ConfigureAwait(false);
        if (body is null)
        {
            return AdminReply.Error(StatusCodes.Status400BadRequest, "invalid");
        }

        var (outcome, tier, rank) = await _quality.SetTierAsync(peerKey, body, cancellationToken).ConfigureAwait(false);
        switch (outcome)
        {
            case QualityChangeOutcome.Saved:
                _logger.LogInformation("Admin set the priority tier of peer {Peer} to {Tier}", Redact.Id(peerKey), tier);
                return AdminReply.Json(StatusCodes.Status200OK, new { tier, rank });
            case QualityChangeOutcome.Changed:
                return new ShoalResult(StatusCodes.Status409Conflict, new { error = "changed", current = tier });
            case QualityChangeOutcome.Invalid:
                return AdminReply.Error(StatusCodes.Status400BadRequest, "invalid");
            default:
                return AdminReply.Error(StatusCodes.Status404NotFound, "notFound");
        }
    }

    /// <summary>
    /// Dismisses the speed-test card. Body <c>{ suggestedClass }</c>: the class on the card, which must be the one suggested now (409 <c>stale</c> otherwise: the
    /// page reloads the panel). The card shows again only when the suggested class drops, the link gets more than 30 percent slower, or after 30 days.
    /// </summary>
    /// <param name="peerKey">The connection.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>200, 400 (<c>invalid</c>), 404 or 409 (<c>recovery</c>, <c>stale</c>).</returns>
    [HttpPost("Peers/{peerKey}/Recommendations/Dismiss")]
    [Produces("application/json")]
    public async Task<IActionResult> Dismiss(string peerKey, CancellationToken cancellationToken)
    {
        if (_state.Status != StateStatus.Ready)
        {
            return AdminReply.Error(StatusCodes.Status409Conflict, "recovery");
        }

        if (!TryAdmin(out var admin))
        {
            return AdminReply.Error(StatusCodes.Status400BadRequest, "adminUserRequired"); // an API key has no admin user
        }

        var body = await AdminReply.ReadAsync<DismissRecommendationRequest>(Request, cancellationToken).ConfigureAwait(false);
        var outcome = await _quality.DismissAsync(peerKey, body?.SuggestedClass, admin, cancellationToken).ConfigureAwait(false);
        return outcome switch
        {
            QualityChangeOutcome.Saved => AdminReply.Json(StatusCodes.Status200OK, new { dismissed = true }),
            QualityChangeOutcome.Invalid => AdminReply.Error(StatusCodes.Status400BadRequest, "invalid"),
            QualityChangeOutcome.Stale => AdminReply.Error(StatusCodes.Status409Conflict, "stale"),
            _ => AdminReply.Error(StatusCodes.Status404NotFound, "notFound"),
        };
    }

    /// <summary>
    /// "Which server plays first": <c>{ playOrder: [{ peerKey, name, tier, position, count, reason?, label?, effective }] }</c>.
    /// </summary>
    /// <returns>200 or 409 (<c>recovery</c>).</returns>
    [HttpGet("PlayOrder")]
    [Produces("application/json")]
    public IActionResult PlayOrder() =>
        _state.Status != StateStatus.Ready ? AdminReply.Error(StatusCodes.Status409Conflict, "recovery") : AdminReply.Json(StatusCodes.Status200OK, new { playOrder = _quality.PlayOrder() });

    /// <summary>
    /// Playing now from friends' servers: <c>{ friends: [{ peerKey, name, streams: [{ itemId, title, userId, method, sourceQuality?, sourceMbps?, heldByLimit, limitText?, startedUtc }] }] }</c>.
    /// </summary>
    /// <returns>200 or 409 (<c>recovery</c>).</returns>
    [HttpGet("PlayingNow")]
    [Produces("application/json")]
    public IActionResult PlayingNow() =>
        _state.Status != StateStatus.Ready ? AdminReply.Error(StatusCodes.Status409Conflict, "recovery") : AdminReply.Json(StatusCodes.Status200OK, new { friends = _quality.PlayingNow() });

    private bool TryAdmin(out Guid admin)
    {
        admin = Guid.Empty;
        return User.FindFirst(UserIdClaim)?.Value is { } value && Guid.TryParse(value, out admin) && admin != Guid.Empty;
    }
}
