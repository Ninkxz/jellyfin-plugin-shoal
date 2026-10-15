using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.Http;
using Jellyfin.Plugin.Shoal.State;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Shoal.Api.Admin;

/// <summary>What the share service answers to a change: the page maps each to a status and one fixed word.</summary>
public enum ShareOutcome
{
    /// <summary>Done.</summary>
    Ok,

    /// <summary>No such share (or device).</summary>
    NotFound,

    /// <summary>The request was not valid.</summary>
    Invalid,

    /// <summary>The typed name did not match.</summary>
    ConfirmName,

    /// <summary>Nothing is waiting for this (no pending approval).</summary>
    Conflict,

    /// <summary>The share service (K21) and invite pairing (K22) are not built yet.</summary>
    NotBuilt
}

/// <summary>
/// The Peer side of the admin API (DESIGN 4.1b, 4.5, 9.5, 9.6): the friends who watch from this server. K21 (ShareService: the sharing
/// account, its lock, the device sweep, revocation) and K22 (invites and the approval with the safety code) implement it.
/// </summary>
public interface IShareService
{
    /// <summary>Lists the shares and the libraries a share may include.</summary>
    /// <returns>The listing.</returns>
    object List();

    /// <summary>Reads one share.</summary>
    /// <param name="shareId">The share.</param>
    /// <returns>The detail, or null.</returns>
    object? Read(string shareId);

    /// <summary>Creates a share.</summary>
    /// <param name="request">What to share and the limits.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The outcome and the new share's detail.</returns>
    Task<(ShareOutcome Outcome, object? Body)> CreateAsync(ShareRequest request, CancellationToken cancellationToken);

    /// <summary>Changes a share's libraries and limits.</summary>
    /// <param name="shareId">The share.</param>
    /// <param name="request">The new values.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The outcome.</returns>
    Task<ShareOutcome> UpdateAsync(string shareId, ShareRequest request, CancellationToken cancellationToken);

    /// <summary>Pauses or resumes a share.</summary>
    /// <param name="shareId">The share.</param>
    /// <param name="enabled">True to resume.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The outcome.</returns>
    Task<ShareOutcome> SetEnabledAsync(string shareId, bool enabled, CancellationToken cancellationToken);

    /// <summary>Stops sharing: disables the account, signs out every device, checks none are left, deletes the account.</summary>
    /// <param name="shareId">The share.</param>
    /// <param name="confirmName">The typed name, compared with the stored friend name before anything happens.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The outcome and the per-step result.</returns>
    Task<(ShareOutcome Outcome, object? Body)> RevokeAsync(string shareId, string? confirmName, CancellationToken cancellationToken);

    /// <summary>Creates an invite (shown once) or cancels the current one.</summary>
    /// <param name="shareId">The share.</param>
    /// <param name="create">True to create, false to cancel.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The outcome and, for a new invite, the one-time body.</returns>
    Task<(ShareOutcome Outcome, object? Body)> InviteAsync(string shareId, bool create, CancellationToken cancellationToken);

    /// <summary>Reads the pending approval of a share (never the code).</summary>
    /// <param name="shareId">The share.</param>
    /// <returns>The approval, or null.</returns>
    object? Approval(string shareId);

    /// <summary>Approves or denies the pending request; the safety code is judged here, never by the page.</summary>
    /// <param name="shareId">The share.</param>
    /// <param name="request">The decision.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The outcome and the answer (match, triesLeft, state).</returns>
    Task<(ShareOutcome Outcome, object? Body)> DecideAsync(string shareId, ApprovalRequest request, CancellationToken cancellationToken);

    /// <summary>Removes one device from the sharing account.</summary>
    /// <param name="shareId">The share.</param>
    /// <param name="deviceHandle">The device.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The outcome.</returns>
    Task<ShareOutcome> RemoveDeviceAsync(string shareId, string deviceHandle, CancellationToken cancellationToken);
}

/// <summary>The body of POST and PUT Shoal/Admin/Shares.</summary>
public sealed record ShareRequest
{
    /// <summary>Gets the friend's name.</summary>
    [JsonPropertyName("friendName")]
    public string? FriendName { get; init; }

    /// <summary>Gets the libraries to share.</summary>
    [JsonPropertyName("libraryIds")]
    public List<Guid>? LibraryIds { get; init; }

    /// <summary>Gets the highest quality per stream in Mbps, or null for no limit.</summary>
    [JsonPropertyName("maxBitrateMbps")]
    public int? MaxBitrateMbps { get; init; }

    /// <summary>Gets how many transcodes may run at once (1 to 10, default 4, owner D15).</summary>
    [JsonPropertyName("maxConcurrentTranscodes")]
    public int? MaxConcurrentTranscodes { get; init; }
}

/// <summary>The body of the destructive Shares calls (the typed name).</summary>
public sealed record ShareConfirmRequest
{
    /// <summary>Gets what the admin typed.</summary>
    [JsonPropertyName("confirmName")]
    public string? ConfirmName { get; init; }
}

/// <summary>The body of POST Shares/{id}/Approval: <c>{approve, sas}</c>. The page sends the code normalised; the server normalises again.</summary>
public sealed record ApprovalRequest
{
    /// <summary>Gets a value indicating whether the admin approves.</summary>
    [JsonPropertyName("approve")]
    public bool Approve { get; init; }

    /// <summary>Gets the safety code as typed.</summary>
    [JsonPropertyName("sas")]
    public string? Sas { get; init; }
}

/// <summary>
/// The share service until K21 and K22 exist: the shares and their limits are read from the state, and every change answers
/// "not built". Nothing here creates an account or an invite.
/// </summary>
public sealed class PendingShareService : IShareService
{
    private static readonly object NotBuilt = new { };

    private readonly IStateStore _state;

    /// <summary>
    /// Initializes a new instance of the <see cref="PendingShareService"/> class.
    /// </summary>
    /// <param name="state">The state store.</param>
    public PendingShareService(IStateStore state) => _state = state;

    /// <inheritdoc />
    public object List() => new
    {
        shares = Shares().Select(s => new
        {
            handle = s.ShareId,
            friendName = s.FriendName,
            status = s.Enabled ? "Active" : "Paused",
            libraryIds = s.LibraryIds,
            maxBitrateMbps = s.MaxBitrateMbps,
            maxConcurrentTranscodes = s.MaxConcurrentTranscodes
        }).ToList(),
        availableLibraries = Array.Empty<object>()
    };

    /// <inheritdoc />
    public object? Read(string shareId) => Shares().FirstOrDefault(s => string.Equals(s.ShareId, shareId, StringComparison.Ordinal)) is { } s
        ? new
        {
            handle = s.ShareId,
            friendName = s.FriendName,
            status = s.Enabled ? "Active" : "Paused",
            libraryIds = s.LibraryIds,
            maxBitrateMbps = s.MaxBitrateMbps,
            maxConcurrentTranscodes = s.MaxConcurrentTranscodes,
            deviceCount = s.ExpectedDeviceIds.Count,
            devices = Array.Empty<object>(),
            invite = new { state = "none" },
            availableLibraries = Array.Empty<object>(),
            hardeningMode = _state.Current.PeerRole.HardeningMode.ToString()
        }
        : null;

    /// <inheritdoc />
    public Task<(ShareOutcome Outcome, object? Body)> CreateAsync(ShareRequest request, CancellationToken cancellationToken) => Task.FromResult<(ShareOutcome, object?)>((ShareOutcome.NotBuilt, null));

    /// <inheritdoc />
    public Task<ShareOutcome> UpdateAsync(string shareId, ShareRequest request, CancellationToken cancellationToken) => Task.FromResult(Known(shareId) ? ShareOutcome.NotBuilt : ShareOutcome.NotFound);

    /// <inheritdoc />
    public Task<ShareOutcome> SetEnabledAsync(string shareId, bool enabled, CancellationToken cancellationToken) => Task.FromResult(Known(shareId) ? ShareOutcome.NotBuilt : ShareOutcome.NotFound);

    /// <inheritdoc />
    public Task<(ShareOutcome Outcome, object? Body)> RevokeAsync(string shareId, string? confirmName, CancellationToken cancellationToken)
    {
        var share = Shares().FirstOrDefault(s => string.Equals(s.ShareId, shareId, StringComparison.Ordinal));
        if (share is null)
        {
            return Task.FromResult<(ShareOutcome, object?)>((ShareOutcome.NotFound, null));
        }

        // The typed name is judged against the stored friend name before anything else, as the real service will.
        return Task.FromResult<(ShareOutcome, object?)>((string.Equals(confirmName, share.FriendName, StringComparison.Ordinal) ? ShareOutcome.NotBuilt : ShareOutcome.ConfirmName, null));
    }

    /// <inheritdoc />
    public Task<(ShareOutcome Outcome, object? Body)> InviteAsync(string shareId, bool create, CancellationToken cancellationToken) =>
        Task.FromResult<(ShareOutcome, object?)>((Known(shareId) ? ShareOutcome.NotBuilt : ShareOutcome.NotFound, null));

    /// <inheritdoc />
    public object? Approval(string shareId) => Known(shareId) ? NotBuilt : null;

    /// <inheritdoc />
    public Task<(ShareOutcome Outcome, object? Body)> DecideAsync(string shareId, ApprovalRequest request, CancellationToken cancellationToken) =>
        Task.FromResult<(ShareOutcome, object?)>((Known(shareId) ? ShareOutcome.Conflict : ShareOutcome.NotFound, null));

    /// <inheritdoc />
    public Task<ShareOutcome> RemoveDeviceAsync(string shareId, string deviceHandle, CancellationToken cancellationToken) => Task.FromResult(Known(shareId) ? ShareOutcome.NotFound : ShareOutcome.NotFound);

    private List<ShareState> Shares() => _state.Status == StateStatus.Ready ? _state.Current.PeerRole.Shares : [];

    private bool Known(string shareId) => Shares().Any(s => string.Equals(s.ShareId, shareId, StringComparison.Ordinal));
}

/// <summary>
/// The friends who watch from this server (ui-spec 8.2.4, 8.7). Admin only, answers through <see cref="ShoalResult"/>. The work is the
/// <see cref="IShareService"/>'s; this class only checks the request and maps the outcome to a status and one fixed word.
/// </summary>
[ApiController]
[Authorize(Policy = Policies.RequiresElevation)]
[Route("Shoal/Admin")]
public class AdminSharesController : ControllerBase
{
    private readonly IShareService _shares;
    private readonly IStateStore _state;

    /// <summary>
    /// Initializes a new instance of the <see cref="AdminSharesController"/> class.
    /// </summary>
    /// <param name="shares">The share service.</param>
    /// <param name="state">The state store.</param>
    public AdminSharesController(IShareService shares, IStateStore state)
    {
        _shares = shares;
        _state = state;
    }

    /// <summary>Lists the shares.</summary>
    /// <returns>The shares and the libraries that may be shared.</returns>
    [HttpGet("Shares")]
    [Produces("application/json")]
    public IActionResult List() => AdminReply.Json(StatusCodes.Status200OK, _shares.List());

    /// <summary>Creates a share.</summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>201, 400, 409 or 501.</returns>
    [HttpPost("Shares")]
    public async Task<IActionResult> Create(CancellationToken cancellationToken)
    {
        var body = await AdminReply.ReadAsync<ShareRequest>(Request, cancellationToken).ConfigureAwait(false);
        if (Guard(body) is { } refused)
        {
            return refused;
        }

        var (outcome, result) = await _shares.CreateAsync(body!, cancellationToken).ConfigureAwait(false);
        return Map(outcome, StatusCodes.Status201Created, result);
    }

    /// <summary>Reads one share.</summary>
    /// <param name="id">The share.</param>
    /// <returns>The detail or 404.</returns>
    [HttpGet("Shares/{id}")]
    [Produces("application/json")]
    public IActionResult Get(string id) => _shares.Read(id) is { } detail ? AdminReply.Json(StatusCodes.Status200OK, detail) : AdminReply.Error(StatusCodes.Status404NotFound, "notFound");

    /// <summary>Changes a share's libraries and limits.</summary>
    /// <param name="id">The share.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>200, 400, 404 or 501.</returns>
    [HttpPut("Shares/{id}")]
    public async Task<IActionResult> Update(string id, CancellationToken cancellationToken)
    {
        var body = await AdminReply.ReadAsync<ShareRequest>(Request, cancellationToken).ConfigureAwait(false);
        if (Guard(body) is { } refused)
        {
            return refused;
        }

        return Map(await _shares.UpdateAsync(id, body!, cancellationToken).ConfigureAwait(false), StatusCodes.Status200OK, new { });
    }

    /// <summary>Stops sharing with a friend; the typed name is checked on the server.</summary>
    /// <param name="id">The share.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The per-step result, 400 without the name, 404 or 501.</returns>
    [HttpDelete("Shares/{id}")]
    public async Task<IActionResult> Delete(string id, CancellationToken cancellationToken)
    {
        if (_state.Status != StateStatus.Ready)
        {
            return AdminReply.Error(StatusCodes.Status409Conflict, "recovery");
        }

        var body = await AdminReply.ReadAsync<ShareConfirmRequest>(Request, cancellationToken).ConfigureAwait(false);
        var (outcome, result) = await _shares.RevokeAsync(id, body?.ConfirmName, cancellationToken).ConfigureAwait(false);
        return Map(outcome, StatusCodes.Status200OK, result);
    }

    /// <summary>Pauses a share: signs its server out at once.</summary>
    /// <param name="id">The share.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>200, 404 or 501.</returns>
    [HttpPost("Shares/{id}/Pause")]
    public async Task<IActionResult> Pause(string id, CancellationToken cancellationToken) =>
        Map(await _shares.SetEnabledAsync(id, false, cancellationToken).ConfigureAwait(false), StatusCodes.Status200OK, new { });

    /// <summary>Resumes a paused share.</summary>
    /// <param name="id">The share.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>200, 404 or 501.</returns>
    [HttpPost("Shares/{id}/Resume")]
    public async Task<IActionResult> Resume(string id, CancellationToken cancellationToken) =>
        Map(await _shares.SetEnabledAsync(id, true, cancellationToken).ConfigureAwait(false), StatusCodes.Status200OK, new { });

    /// <summary>Creates an invite; it is in the answer once and never again.</summary>
    /// <param name="id">The share.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>201, 404 or 501.</returns>
    [HttpPost("Shares/{id}/Invites")]
    public async Task<IActionResult> CreateInvite(string id, CancellationToken cancellationToken)
    {
        var (outcome, result) = await _shares.InviteAsync(id, true, cancellationToken).ConfigureAwait(false);
        return Map(outcome, StatusCodes.Status201Created, result);
    }

    /// <summary>Cancels the current invite.</summary>
    /// <param name="id">The share.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>200, 404 or 501.</returns>
    [HttpDelete("Shares/{id}/Invites/current")]
    public async Task<IActionResult> CancelInvite(string id, CancellationToken cancellationToken)
    {
        var (outcome, result) = await _shares.InviteAsync(id, false, cancellationToken).ConfigureAwait(false);
        return Map(outcome, StatusCodes.Status200OK, result ?? new { });
    }

    /// <summary>Reads the pending approval. It never carries the safety code.</summary>
    /// <param name="id">The share.</param>
    /// <returns>The approval or 404.</returns>
    [HttpGet("Shares/{id}/Approval")]
    [Produces("application/json")]
    public IActionResult GetApproval(string id) => _shares.Approval(id) is { } approval ? AdminReply.Json(StatusCodes.Status200OK, approval) : AdminReply.Error(StatusCodes.Status404NotFound, "notFound");

    /// <summary>Approves with the safety code, or denies.</summary>
    /// <param name="id">The share.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The answer, 400, 404 or 409.</returns>
    [HttpPost("Shares/{id}/Approval")]
    public async Task<IActionResult> Decide(string id, CancellationToken cancellationToken)
    {
        var body = await AdminReply.ReadAsync<ApprovalRequest>(Request, cancellationToken).ConfigureAwait(false);
        if (body is null || (body.Approve && (body.Sas is null || body.Sas.Length > 32)))
        {
            return AdminReply.Error(StatusCodes.Status400BadRequest, "invalid");
        }

        var (outcome, result) = await _shares.DecideAsync(id, body, cancellationToken).ConfigureAwait(false);
        return Map(outcome, StatusCodes.Status200OK, result);
    }

    /// <summary>Removes one device from the sharing account.</summary>
    /// <param name="id">The share.</param>
    /// <param name="deviceHandle">The device.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>200 or 404.</returns>
    [HttpDelete("Shares/{id}/Devices/{deviceHandle}")]
    public async Task<IActionResult> RemoveDevice(string id, string deviceHandle, CancellationToken cancellationToken) =>
        Map(await _shares.RemoveDeviceAsync(id, deviceHandle, cancellationToken).ConfigureAwait(false), StatusCodes.Status200OK, new { });

    private static ShoalResult Map(ShareOutcome outcome, int okStatus, object? body) => outcome switch
    {
        ShareOutcome.Ok => AdminReply.Json(okStatus, body ?? new { }),
        ShareOutcome.NotFound => AdminReply.Error(StatusCodes.Status404NotFound, "notFound"),
        ShareOutcome.Invalid => AdminReply.Error(StatusCodes.Status400BadRequest, "invalid"),
        ShareOutcome.ConfirmName => AdminReply.Error(StatusCodes.Status400BadRequest, "confirmName"),
        ShareOutcome.Conflict => AdminReply.Error(StatusCodes.Status409Conflict, "nothingPending"),
        _ => AdminReply.Error(StatusCodes.Status501NotImplemented, "notImplemented")
    };

    private ShoalResult? Guard(ShareRequest? body)
    {
        if (_state.Status != StateStatus.Ready)
        {
            return AdminReply.Error(StatusCodes.Status409Conflict, "recovery");
        }

        if (body is null
            || string.IsNullOrWhiteSpace(body.FriendName) || body.FriendName.Length > 64
            || (body.MaxConcurrentTranscodes is { } t && (t < 1 || t > 10))
            || (body.MaxBitrateMbps is { } m && (m < 1 || m > 1000)))
        {
            return AdminReply.Error(StatusCodes.Status400BadRequest, "invalid");
        }

        return null;
    }
}
