using System;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.Http;
using Jellyfin.Plugin.Shoal.Pairing;
using Jellyfin.Plugin.Shoal.State;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Shoal.Api;

/// <summary>
/// The body of <c>POST Shoal/Admin/Pairing</c>.
/// </summary>
public sealed class StartPairingRequest
{
    /// <summary>Gets or sets the single-use handle from the probe.</summary>
    [JsonPropertyName("probeHandle")]
    public string? ProbeHandle { get; set; }

    /// <summary>Gets or sets the name the admin gives the server.</summary>
    [JsonPropertyName("displayName")]
    public string? DisplayName { get; set; }

    /// <summary>Gets or sets the method. Ignored: every pairing is by invite (D17). The value <c>qc</c> is refused with 409 <c>methodUnavailable</c>.</summary>
    [JsonPropertyName("method")]
    public string? Method { get; set; }
}

/// <summary>
/// The Home-side pairing endpoints of the admin API (DESIGN 9.6): probe, the watch-side pairing session, dismissing a stopped pairing, and
/// confirming a restore. Every action requires an administrator (<c>RequiresElevation</c>); nothing here is anonymous. No response contains a
/// token, the invite secret or a peer GUID: the session shows the safety code, which the friend types.
/// </summary>
[ApiController]
[Route("Shoal/Admin")]
[Authorize(Policy = Policies.RequiresElevation)]
[Produces("application/json")]
public class AdminPairingController : ControllerBase
{
    private const string UserIdClaim = "Jellyfin-UserId"; // core's InternalClaimTypes.UserId

    private readonly PairingService _pairing;
    private readonly IStateStore _state;

    /// <summary>
    /// Initializes a new instance of the <see cref="AdminPairingController"/> class.
    /// </summary>
    /// <param name="pairing">The pairing service.</param>
    /// <param name="state">The state store.</param>
    public AdminPairingController(PairingService pairing, IStateStore state)
    {
        _pairing = pairing;
        _state = state;
    }

    /// <summary>
    /// Looks at a server before pairing (steps 1 and 2 of the wizard). Sends no token; a probe without an invite can be checked but cannot start a pairing.
    /// </summary>
    /// <param name="request">The address, network type and optional proxy.</param>
    /// <param name="cancellationToken">Cancelled when the client disconnects (call 1).</param>
    /// <response code="200">The result; <c>ok:false</c> says what was wrong.</response>
    /// <returns>The probe result.</returns>
    [HttpPost("Peers/Probe")]
    public async Task<IActionResult> Probe([FromBody] ProbeRequest request, CancellationToken cancellationToken) =>
        new ShoalResult(200, await _pairing.ProbeAsync(request, cancellationToken).ConfigureAwait(false));

    /// <summary>
    /// Starts a pairing: the admin's "Start pairing" click.
    /// </summary>
    /// <param name="request">The probe handle and the name (the method is ignored; <c>qc</c> is refused).</param>
    /// <param name="cancellationToken">Cancelled when the client disconnects (call 2).</param>
    /// <response code="200">The session.</response>
    /// <response code="409"><c>sessionActive</c>, <c>probeExpired</c>, <c>secretsUnreadable</c> or <c>methodUnavailable</c>.</response>
    /// <response code="429">Too many pairings were started for this server; <c>Retry-After</c> says when to try again.</response>
    /// <returns>The session that was started.</returns>
    [HttpPost("Pairing")]
    public async Task<IActionResult> Start([FromBody] StartPairingRequest request, CancellationToken cancellationToken)
    {
        if (!TryReadUserId(out var adminId) || request.ProbeHandle is null || request.DisplayName is null)
        {
            return new ShoalResult(400, new { error = "invalidRequest" });
        }

        if (string.Equals(request.Method, "qc", StringComparison.OrdinalIgnoreCase))
        {
            return new ShoalResult(409, new { error = "methodUnavailable" });
        }

        try
        {
            return new ShoalResult(200, await _pairing.BeginAsync(adminId, request.ProbeHandle, request.DisplayName, cancellationToken).ConfigureAwait(false));
        }
        catch (PairingConflictException ex)
        {
            return ConflictResult(ex);
        }
    }

    /// <summary>
    /// Gets the current pairing session, which keeps running without any page open.
    /// </summary>
    /// <response code="200">The session.</response>
    /// <response code="204">There is none (not an error: the wizard asks every time it opens, and a 404 would show as an error in the browser console).</response>
    /// <returns>The session as it stands.</returns>
    [HttpGet("Pairing")]
    public IActionResult Get() => _pairing.GetSession() is { } session ? new ShoalResult(200, session) : new ShoalResult(204);

    /// <summary>
    /// Cancels the live session.
    /// </summary>
    /// <param name="cancellationToken">Cancelled when the client disconnects (call 4).</param>
    /// <response code="204">Cancelled, or there was no live session (no session is never an error here).</response>
    /// <returns>Nothing.</returns>
    [HttpDelete("Pairing")]
    public async Task<IActionResult> Cancel(CancellationToken cancellationToken)
    {
        // Nothing to cancel is the same answer as cancelled: the page asks without knowing, and a 404 shows as an error in the browser console.
        await _pairing.CancelAsync(cancellationToken).ConfigureAwait(false);
        return new ShoalResult(204);
    }

    /// <summary>
    /// Clears a stopped pairing (the card's Dismiss).
    /// </summary>
    /// <param name="handle">The peer handle.</param>
    /// <param name="cancellationToken">Cancelled when the client disconnects (call 5).</param>
    /// <response code="200"><c>{removed:true}</c> for a never-paired peer, <c>{restored:true, status}</c> for a re-paired one.</response>
    /// <response code="404">No such peer.</response>
    /// <response code="409">No stopped pairing is stored, or a pairing is live for the peer.</response>
    /// <returns>What happened.</returns>
    [HttpPost("Peers/{handle}/DismissPairing")]
    public async Task<IActionResult> DismissPairing([FromRoute] string handle, CancellationToken cancellationToken)
    {
        try
        {
            return new ShoalResult(200, await _pairing.DismissPairingAsync(handle, cancellationToken).ConfigureAwait(false));
        }
        catch (PairingConflictException ex)
        {
            return ConflictResult(ex);
        }
    }

    /// <summary>
    /// "Confirm" on the restore banner: the admin has reviewed who can see each library and which friends can connect.
    /// </summary>
    /// <response code="204">Confirmed.</response>
    /// <response code="409">Nothing awaits confirmation.</response>
    /// <returns>Nothing.</returns>
    [HttpPost("State/ConfirmRestore")]
    public IActionResult ConfirmRestore() => _state.ConfirmRestore() ? new ShoalResult(204) : new ShoalResult(409, new { error = "nothingToConfirm" });

    private static string CodeOf(PairingConflict conflict) => conflict switch
    {
        PairingConflict.SessionActive => "sessionActive",
        PairingConflict.ProbeExpired => "probeExpired",
        PairingConflict.NoSession => "noSession",
        PairingConflict.SecretsUnreadable => "secretsUnreadable",
        PairingConflict.MethodUnavailable => "methodUnavailable",
        PairingConflict.NothingToDismiss => "nothingToDismiss",
        PairingConflict.UnknownPeer => "unknownPeer",
        PairingConflict.RateLimited => "rateLimited",
        _ => "invalidRequest"
    };

    private bool TryReadUserId(out Guid userId)
    {
        userId = Guid.Empty;
        return User.FindFirst(UserIdClaim)?.Value is { } value && Guid.TryParse(value, out userId);
    }

    private static ShoalResult ConflictResult(PairingConflictException ex)
    {
        return ex.Conflict switch
        {
            PairingConflict.RateLimited => new ShoalResult(StatusCodes.Status429TooManyRequests, new { error = CodeOf(ex.Conflict), retryAfterSeconds = ex.RetryAfterSeconds }, ex.RetryAfterSeconds),
            PairingConflict.UnknownPeer => new ShoalResult(404, new { error = CodeOf(ex.Conflict) }),
            _ => new ShoalResult(409, new { error = CodeOf(ex.Conflict) })
        };
    }
}
