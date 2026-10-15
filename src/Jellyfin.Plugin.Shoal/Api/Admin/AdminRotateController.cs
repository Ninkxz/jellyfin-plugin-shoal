using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.Peers;
using Jellyfin.Plugin.Shoal.State;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Shoal.Api.Admin;

/// <summary>
/// Token rotation for the admin (DESIGN v2.6.1): "Rotate now", the schedule and what the server detail shows. Answers carry handles and words, never a token.
/// </summary>
[ApiController]
[Authorize(Policy = Policies.RequiresElevation)]
[Route("Shoal/Admin")]
public class AdminRotateController : ControllerBase
{
    private readonly IStateStore _state;
    private readonly TokenRotation _rotation;

    /// <summary>
    /// Initializes a new instance of the <see cref="AdminRotateController"/> class.
    /// </summary>
    /// <param name="state">The state store.</param>
    /// <param name="rotation">The rotation service.</param>
    public AdminRotateController(IStateStore state, TokenRotation rotation)
    {
        _state = state;
        _rotation = rotation;
    }

    /// <summary>
    /// Rotates the token of a server now. One per ten minutes per server.
    /// </summary>
    /// <param name="handle">The handle of the server.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>200 with the time, 404, 409 with the reason, 429 with Retry-After, or 502 with what went wrong at the server.</returns>
    [HttpPost("Peers/{handle}/Rotate")]
    [Produces("application/json")]
    public async Task<IActionResult> Rotate(string handle, CancellationToken cancellationToken)
    {
        if (Find(handle) is null)
        {
            return AdminReply.Error(StatusCodes.Status404NotFound, "notFound");
        }

        if (!_rotation.TryReserveManual(handle, out var retryAfter))
        {
            return AdminReply.TooMany(retryAfter);
        }

        var result = await _rotation.RotateAsync(handle, RotationTrigger.Manual, cancellationToken).ConfigureAwait(false);
        return result.Error switch
        {
            RotateError.None => AdminReply.Json(StatusCodes.Status200OK, new { rotatedUtc = result.RotatedUtc }),
            RotateError.NotSupported => AdminReply.Error(StatusCodes.Status409Conflict, "notSupported"),
            RotateError.NotOnline => AdminReply.Error(StatusCodes.Status409Conflict, "notOnline"),
            RotateError.StreamsActive => AdminReply.Error(StatusCodes.Status409Conflict, "streamsActive"),
            RotateError.Busy => AdminReply.Error(StatusCodes.Status409Conflict, "busy"),
            RotateError.Rejected => AdminReply.Error(StatusCodes.Status502BadGateway, "rejected"),
            RotateError.LostResponse => AdminReply.Error(StatusCodes.Status502BadGateway, "lostResponse"),
            RotateError.BadResponse => AdminReply.Error(StatusCodes.Status502BadGateway, "badResponse"),
            _ => AdminReply.Error(StatusCodes.Status404NotFound, "notFound"),
        };
    }

    /// <summary>
    /// What the server detail shows about rotation: whether it is supported, the schedule, the last time, the next due time and the last failure.
    /// </summary>
    /// <param name="handle">The handle of the server.</param>
    /// <returns>The block, or 404.</returns>
    [HttpGet("Peers/{handle}/Rotation")]
    [Produces("application/json")]
    public IActionResult GetRotation(string handle) =>
        Find(handle) is { } peer ? AdminReply.Json(StatusCodes.Status200OK, _rotation.Describe(peer)) : AdminReply.Error(StatusCodes.Status404NotFound, "notFound");

    /// <summary>
    /// Sets the schedule: 0 (off, the default), 30, 60 or 90 days.
    /// </summary>
    /// <param name="handle">The handle of the server.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>200 with the block, 400 for another number, 404, or 409 when the pairing cannot rotate.</returns>
    [HttpPut("Peers/{handle}/Rotation")]
    [Produces("application/json")]
    public async Task<IActionResult> SetRotation(string handle, CancellationToken cancellationToken)
    {
        if (Find(handle) is not { } peer)
        {
            return AdminReply.Error(StatusCodes.Status404NotFound, "notFound");
        }

        var body = await AdminReply.ReadAsync<RotationDaysRequest>(Request, cancellationToken).ConfigureAwait(false);
        if (body?.Days is not { } days || days is not (0 or 30 or 60 or 90))
        {
            return AdminReply.Error(StatusCodes.Status400BadRequest, "invalidDays");
        }

        if (days > 0 && peer.Pairing?.Method != PairingMethod.Invite)
        {
            return AdminReply.Error(StatusCodes.Status409Conflict, "notSupported");
        }

        await _state.UpdateAsync(s => s.Home.Peers.FirstOrDefault(p => p.PeerKey == handle)?.RotationDays = days, cancellationToken).ConfigureAwait(false);
        return AdminReply.Json(StatusCodes.Status200OK, _rotation.Describe(Find(handle) ?? peer));
    }

    private PeerState? Find(string handle) =>
        _state.Status == StateStatus.Recovery ? null : _state.Current.Home.Peers.FirstOrDefault(p => string.Equals(p.PeerKey, handle, StringComparison.Ordinal));

    private sealed class RotationDaysRequest
    {
        public int? Days { get; set; }
    }
}
