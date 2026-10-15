using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.Playback;
using Jellyfin.Plugin.Shoal.State;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Shoal.Sharing;

/// <summary>How a rotation request ended.</summary>
public enum RotationOutcome
{
    /// <summary>A new token was issued.</summary>
    Ok,

    /// <summary>The caller is not an enabled share user holding the live token of an expected device (one uniform answer).</summary>
    NotFound,

    /// <summary>Another rotation of this share is running.</summary>
    Busy,

    /// <summary>A rate limit applies; <see cref="RotationResult.RetryAfterSeconds"/> says when to come back.</summary>
    TooMany,

    /// <summary>Issuing failed twice: the share has no device and Home must pair again.</summary>
    Failed,
}

/// <summary>
/// The result of <see cref="ShareRotation.RotateAsync"/>.
/// </summary>
/// <param name="Outcome">How it ended.</param>
/// <param name="Token">The new token (only for <see cref="RotationOutcome.Ok"/>); never stored or logged.</param>
/// <param name="DeviceId">Home's device id (unchanged by a rotation).</param>
/// <param name="RotatedUtc">When the token was issued.</param>
/// <param name="RetryAfterSeconds">For <see cref="RotationOutcome.TooMany"/>, the seconds to wait.</param>
public sealed record RotationResult(RotationOutcome Outcome, string? Token = null, string? DeviceId = null, DateTime? RotatedUtc = null, int RetryAfterSeconds = 0)
{
    /// <summary>
    /// Names the outcome and nothing else: a generated <c>ToString</c> would print the token if a result were ever logged (Warden 10 L7).
    /// </summary>
    /// <returns>The outcome as text.</returns>
    public override string ToString() => "RotationResult { Outcome = " + Outcome + " }";
}

/// <summary>
/// <c>POST /Shoal/Share/Rotate</c> on a Shoal Peer (DESIGN v2.6.1, K25): Home asks for a new token for its own sharing account. The
/// sequence is the approval's (<see cref="ShareTokenIssuer"/>) with Home's existing DeviceId, so the old token dies at once. Rotation limits
/// the life of an old leaked token; it does not stop a thief who holds the current one.
/// </summary>
public sealed class ShareRotation
{
    /// <summary>The most rotations of one share in 24 hours.</summary>
    public const int PerDay = 6;

    /// <summary>The shortest time between two rotations of one share.</summary>
    public static readonly TimeSpan MinInterval = TimeSpan.FromMinutes(10);

    private readonly IStateStore _state;
    private readonly IShareUserSource _users;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly RateLimiter _route;
    private readonly RateLimiter _perUser;
    private readonly ShareLocks _locks;
    private readonly Dictionary<string, List<DateTimeOffset>> _history = new(StringComparer.Ordinal);
    private readonly object _historyLock = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="ShareRotation"/> class.
    /// </summary>
    /// <param name="state">The state store.</param>
    /// <param name="users">Jellyfin's accounts, devices and sessions.</param>
    /// <param name="time">The clock.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="locks">The per-share locks shared with rotation, approval, pause and revoke (a private set when none is given).</param>
    public ShareRotation(IStateStore state, IShareUserSource users, TimeProvider time, ILogger<ShareRotation> logger, ShareLocks? locks = null)
        : this(state, users, time, (ILogger)logger, locks)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ShareRotation"/> class (tests: any logger).
    /// </summary>
    /// <param name="state">The state store.</param>
    /// <param name="users">Jellyfin's accounts, devices and sessions.</param>
    /// <param name="time">The clock.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="locks">The per-share locks shared with rotation, approval, pause and revoke (a private set when none is given).</param>
    internal ShareRotation(IStateStore state, IShareUserSource users, TimeProvider time, ILogger logger, ShareLocks? locks = null)
    {
        _locks = locks ?? new ShareLocks();
        _state = state;
        _users = users;
        _time = time;
        _logger = logger;
        _route = new RateLimiter(time, perMinute: 30, burst: 30);
        _perUser = new RateLimiter(time, perMinute: 30, burst: 30);
    }

    /// <summary>
    /// Rotates the token of the share that belongs to the caller.
    /// </summary>
    /// <param name="userId">The authenticated user (from core's claims, never from the request).</param>
    /// <param name="token">The caller's own access token (from core's claims).</param>
    /// <param name="cancellationToken">Cancellation (the client's abort); the issuing itself is not cancelled halfway.</param>
    /// <returns>The result.</returns>
    public async Task<RotationResult> RotateAsync(Guid userId, string token, CancellationToken cancellationToken)
    {
        // Before the caller is known only a bucket of its own is charged, and a caller that overdraws it gets the same 404 as any stranger: an account
        // that is not a share can neither starve Home's rotation nor tell 429 from 404 (Warden 10 M1).
        if (!_perUser.TryTake(userId) || _state.Status != StateStatus.Ready)
        {
            return new RotationResult(RotationOutcome.NotFound);
        }

        // 1. who is asking: an enabled share user whose own token is the live token of one of its expected devices. The DeviceId claim is the
        // client's word and is never used.
        var share = _state.Current.PeerRole.Shares.FirstOrDefault(s => s.ShareUserId == userId);
        if (share is null || !share.Enabled || !_users.Exists(userId) || _users.DeviceIdOfToken(userId, token) is not { } deviceId
            || !share.ExpectedDeviceIds.Contains(deviceId, StringComparer.Ordinal))
        {
            return new RotationResult(RotationOutcome.NotFound);
        }

        // Step 1 passed, so the caller is a share user holding its live token: only now the route-wide limit (30 a minute for every share together) counts.
        if (!_route.TryTake(Guid.Empty))
        {
            return new RotationResult(RotationOutcome.TooMany, RetryAfterSeconds: 2);
        }

        using var held = _locks.TryHold(share.ShareId);
        if (held is null)
        {
            return new RotationResult(RotationOutcome.Busy);
        }

        {
            if (TooSoon(share.ShareId) is { } wait)
            {
                return new RotationResult(RotationOutcome.TooMany, RetryAfterSeconds: wait);
            }

            return await IssueAsync(share, deviceId).ConfigureAwait(false);
        }
    }

    private async Task<RotationResult> IssueAsync(ShareState share, string deviceId)
    {
        var name = _users.Devices(share.ShareUserId).FirstOrDefault(d => string.Equals(d.DeviceId, deviceId, StringComparison.Ordinal))?.Name;
        string? newToken;
        try
        {
            // Not cancelled halfway: a half-done rotation is worse than a finished one.
            newToken = await ShareTokenIssuer.IssueAsync(_state, _users, share.ShareId, deviceId, string.IsNullOrEmpty(name) ? "Shoal" : name, replaceExpectedDevices: false, homeServerIdPrefix: null, mintAttempts: 2, CancellationToken.None).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Whatever failed, the share is left without a device and marked; the caller gets one 500.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            var failedAt = _time.GetUtcNow().UtcDateTime;
            await _state.UpdateAsync(s => s.PeerRole.Shares.FirstOrDefault(x => x.ShareId == share.ShareId)?.RotationFailedUtc = failedAt, CancellationToken.None).ConfigureAwait(false);
            _logger.LogWarning("The token of share {Friend} could not be rotated ({Error}); Home must pair again", share.FriendName, ex.GetType().Name);
            return new RotationResult(RotationOutcome.Failed);
        }

        if (newToken is null)
        {
            return new RotationResult(RotationOutcome.NotFound); // paused meanwhile: the old token is dead, the share stays paused, nothing to report as a failure
        }

        var now = _time.GetUtcNow().UtcDateTime;
        await _state.UpdateAsync(
            s =>
            {
                if (s.PeerRole.Shares.FirstOrDefault(x => x.ShareId == share.ShareId) is { } target)
                {
                    target.LastRotatedUtc = now;
                    target.RotationCount++;
                    target.RotationFailedUtc = null;
                }
            },
            CancellationToken.None).ConfigureAwait(false);
        _logger.LogInformation("Rotated the token of share {Friend}", share.FriendName);
        return new RotationResult(RotationOutcome.Ok, newToken, deviceId, now);
    }

    // One per 10 minutes and six per day, counted per share once the caller is known; the attempt is recorded when it is allowed.
    private int? TooSoon(string shareId)
    {
        lock (_historyLock)
        {
            var now = _time.GetUtcNow();
            if (!_history.TryGetValue(shareId, out var times))
            {
                times = [];
                _history[shareId] = times;
            }

            times.RemoveAll(t => now - t >= TimeSpan.FromDays(1));
            if (times.Count > 0 && now - times[^1] < MinInterval)
            {
                return Math.Max(1, (int)Math.Ceiling((MinInterval - (now - times[^1])).TotalSeconds));
            }

            if (times.Count >= PerDay)
            {
                return Math.Max(1, (int)Math.Ceiling((TimeSpan.FromDays(1) - (now - times[0])).TotalSeconds));
            }

            times.Add(now);
            return null;
        }
    }
}
