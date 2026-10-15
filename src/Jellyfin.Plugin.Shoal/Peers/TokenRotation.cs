using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.Pairing;
using Jellyfin.Plugin.Shoal.Playback;
using Jellyfin.Plugin.Shoal.Security;
using Jellyfin.Plugin.Shoal.State;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Shoal.Peers;

/// <summary>Who started a rotation.</summary>
public enum RotationTrigger
{
    /// <summary>The admin pressed the button.</summary>
    Manual,

    /// <summary>The schedule (<c>rotationDays</c>) found it due.</summary>
    Scheduled,
}

/// <summary>How a rotation ended, in the words of the admin API.</summary>
public enum RotateError
{
    /// <summary>Nothing went wrong.</summary>
    None,

    /// <summary>The peer is unknown.</summary>
    UnknownPeer,

    /// <summary>The peer was not paired by invite (a Quick Connect pairing cannot rotate; re-pairing is its rotation).</summary>
    NotSupported,

    /// <summary>The peer is not online (status, trust window or circuit), or could not be reached; nothing changed.</summary>
    NotOnline,

    /// <summary>A stream or conversion is running through the peer.</summary>
    StreamsActive,

    /// <summary>A pairing session or another rotation is running.</summary>
    Busy,

    /// <summary>The Peer refused (404, 401, 403 or another answer that carries no token).</summary>
    Rejected,

    /// <summary>The Peer may have rotated but Home did not get the answer; Home's token is probably dead and the peer is marked AuthFailed.</summary>
    LostResponse,

    /// <summary>The Peer answered 200 with something Home does not accept (a malformed token, another DeviceId, an administrator account).</summary>
    BadResponse,
}

/// <summary>
/// The result of <see cref="TokenRotation.RotateAsync"/>.
/// </summary>
/// <param name="Error">How it ended.</param>
/// <param name="RotatedUtc">When the Peer issued the token (only when <paramref name="Error"/> is <see cref="RotateError.None"/>).</param>
public sealed record RotateResult(RotateError Error, DateTime? RotatedUtc = null);

/// <summary>
/// What the admin pages show about a peer's rotation (DESIGN v2.6.1). Never a token.
/// </summary>
/// <param name="Supported">True for an invite pairing.</param>
/// <param name="Days">The schedule: 0 (off), 30, 60 or 90.</param>
/// <param name="LastRotatedUtc">When the token was last rotated, or null.</param>
/// <param name="NextDueUtc">When the schedule rotates next, or null when it is off.</param>
/// <param name="Error">The word for the last failure (rejected, lostResponse, badResponse, storeFailed), or null.</param>
/// <param name="Overdue">True when the schedule has been blocked for 24 hours.</param>
public sealed record RotationInfo(bool Supported, int Days, DateTime? LastRotatedUtc, DateTime? NextDueUtc, string? Error, bool Overdue);

/// <summary>
/// Home's side of token rotation (DESIGN v2.6.1, K26): asks a Shoal Peer for a new token for Home's own sharing account, makes it the active
/// credential, checks it, and stores it. It runs only when no stream goes through the peer, marks the peer as <i>rotating</i> for at most 15 seconds
/// (token-bearing calls then wait as if the circuit were open), and never retries the call: a retry would carry a dead token.
/// </summary>
public sealed partial class TokenRotation
{
    /// <summary>The shortest time between two manual rotations of one peer.</summary>
    public static readonly TimeSpan ManualInterval = TimeSpan.FromMinutes(10);

    /// <summary>How long a scheduled rotation may stay blocked before it is reported as overdue.</summary>
    public static readonly TimeSpan OverdueAfter = TimeSpan.FromHours(24);

    private const int StoreAttempts = 4;

    private static readonly byte[] Body = Encoding.ASCII.GetBytes("{\"v\":1}");

    private readonly IStateStore _state;
    private readonly ISecretStore _secrets;
    private readonly IActiveTokenSource _directory;
    private readonly PeerClient _client;
    private readonly PeerRegistry _registry;
    private readonly StreamSlots _slots;
    private readonly IPairingGate _pairing;
    private readonly PeerHealthStore _health;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _manualUntil = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _blockedSince = new(StringComparer.Ordinal);

    /// <summary>
    /// Initializes a new instance of the <see cref="TokenRotation"/> class.
    /// </summary>
    /// <param name="state">The state store.</param>
    /// <param name="secrets">The secret store.</param>
    /// <param name="directory">The peer directory (the endpoint and the in-memory token).</param>
    /// <param name="client">The peer client.</param>
    /// <param name="registry">The runtime table.</param>
    /// <param name="slots">The stream slots.</param>
    /// <param name="pairing">Tells whether a pairing session is live.</param>
    /// <param name="health">The per-peer health files.</param>
    /// <param name="time">The clock.</param>
    /// <param name="logger">The logger.</param>
    public TokenRotation(IStateStore state, ISecretStore secrets, IActiveTokenSource directory, PeerClient client, PeerRegistry registry, StreamSlots slots, IPairingGate pairing, PeerHealthStore health, TimeProvider time, ILogger<TokenRotation> logger)
        : this(state, secrets, directory, client, registry, slots, pairing, health, time, (ILogger)logger)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="TokenRotation"/> class (tests: any logger).
    /// </summary>
    /// <param name="state">The state store.</param>
    /// <param name="secrets">The secret store.</param>
    /// <param name="directory">The peer directory.</param>
    /// <param name="client">The peer client.</param>
    /// <param name="registry">The runtime table.</param>
    /// <param name="slots">The stream slots.</param>
    /// <param name="pairing">Tells whether a pairing session is live.</param>
    /// <param name="health">The per-peer health files.</param>
    /// <param name="time">The clock.</param>
    /// <param name="logger">The logger.</param>
    internal TokenRotation(IStateStore state, ISecretStore secrets, IActiveTokenSource directory, PeerClient client, PeerRegistry registry, StreamSlots slots, IPairingGate pairing, PeerHealthStore health, TimeProvider time, ILogger logger)
    {
        _state = state;
        _secrets = secrets;
        _directory = directory;
        _client = client;
        _registry = registry;
        _slots = slots;
        _pairing = pairing;
        _health = health;
        _time = time;
        _logger = logger;
    }

    /// <summary>Gets or sets the pause between two tries to write the new token to disk (30 seconds in all over three retries).</summary>
    internal TimeSpan StoreRetryDelay { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Reserves the manual cooldown of a peer (one rotation per ten minutes).
    /// </summary>
    /// <param name="peerKey">The peer key.</param>
    /// <param name="retryAfter">When it is refused, how long to wait.</param>
    /// <returns>True when the admin may start a rotation now.</returns>
    public bool TryReserveManual(string peerKey, out TimeSpan retryAfter)
    {
        var now = _time.GetUtcNow();
        lock (_manualUntil)
        {
            if (_manualUntil.TryGetValue(peerKey, out var until) && until > now)
            {
                retryAfter = until - now;
                return false;
            }

            _manualUntil[peerKey] = now + ManualInterval;
            retryAfter = TimeSpan.Zero;
            return true;
        }
    }

    /// <summary>
    /// Describes a peer's rotation for the admin pages.
    /// </summary>
    /// <param name="peer">The peer record.</param>
    /// <returns>The description (never a token).</returns>
    public RotationInfo Describe(PeerState peer)
    {
        ArgumentNullException.ThrowIfNull(peer);
        var supported = peer.Pairing?.Method == PairingMethod.Invite;
        var anchor = peer.LastRotatedUtc ?? peer.Pairing?.PairedUtc;
        DateTime? next = supported && peer.RotationDays > 0 && anchor is { } at ? at.AddDays(peer.RotationDays) : null;
        return new RotationInfo(supported, peer.RotationDays, peer.LastRotatedUtc, next, _health.GetRotationError(peer.PeerKey)?.Kind, _blockedSince.TryGetValue(peer.PeerKey, out var since) && _time.GetUtcNow() - since >= OverdueAfter);
    }

    /// <summary>
    /// Whether a peer's scheduled rotation is due now.
    /// </summary>
    /// <param name="peer">The peer record.</param>
    /// <returns>True when the schedule is on, the pairing can rotate and the time has come.</returns>
    public bool IsDue(PeerState peer)
    {
        var info = Describe(peer);
        return peer.Enabled && info.Supported && info.NextDueUtc is { } due && due <= _time.GetUtcNow().UtcDateTime;
    }

    /// <summary>
    /// Records that a scheduled rotation could not run (or did), for the overdue alert.
    /// </summary>
    /// <param name="peerKey">The peer key.</param>
    /// <param name="blocked">True when it was blocked; false clears the mark.</param>
    public void NoteScheduled(string peerKey, bool blocked)
    {
        if (blocked)
        {
            _blockedSince.TryAdd(peerKey, _time.GetUtcNow());
        }
        else
        {
            _blockedSince.TryRemove(peerKey, out _);
        }
    }

    /// <summary>
    /// Rotates the token Home holds for a peer.
    /// </summary>
    /// <param name="peerKey">The peer key.</param>
    /// <param name="trigger">Who asked.</param>
    /// <param name="cancellationToken">Cancellation of the wait for the answer.</param>
    /// <returns>How it ended.</returns>
    public async Task<RotateResult> RotateAsync(string peerKey, RotationTrigger trigger, CancellationToken cancellationToken)
    {
        if (_state.Status != StateStatus.Ready || _state.Current.Home.Peers.FirstOrDefault(p => string.Equals(p.PeerKey, peerKey, StringComparison.Ordinal)) is not { } peer)
        {
            return new RotateResult(RotateError.UnknownPeer);
        }

        if (peer.Pairing?.Method != PairingMethod.Invite)
        {
            return new RotateResult(RotateError.NotSupported);
        }

        if (_pairing.IsLive(peerKey))
        {
            return new RotateResult(RotateError.Busy);
        }

        if (!IsOnline(peer) || _directory.Find(peerKey) is not { HasToken: true } endpoint || _client.BreakerOf(endpoint).IsOpen)
        {
            return new RotateResult(RotateError.NotOnline);
        }

        switch (_registry.BeginRotation(peerKey, () => _slots.ActiveSlotsOnPeer(peerKey) == 0))
        {
            case RotationStart.AlreadyRotating:
                return new RotateResult(RotateError.Busy);
            case RotationStart.StreamsActive:
                return new RotateResult(RotateError.StreamsActive);
        }

        try
        {
            return await RunAsync(peer, endpoint, trigger, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _registry.EndRotation(peerKey);
        }
    }

    [GeneratedRegex(@"\A[0-9a-f]{32}\z", RegexOptions.CultureInvariant)]
    private static partial Regex TokenRegex();

    private bool IsOnline(PeerState peer)
    {
        var now = _time.GetUtcNow();
        return _registry.StatusOf(peer.PeerKey) == PeerStatus.Online
            && _registry.PinVerifiedUtc(peer.PeerKey) is { } pin && now - pin <= PeerRegistry.TrustWindow
            && _registry.AuthVerifiedUtc(peer.PeerKey) is { } auth && now - auth <= PeerRegistry.TrustWindow;
    }

    private async Task<RotateResult> RunAsync(PeerState peer, PeerEndpoint endpoint, RotationTrigger trigger, CancellationToken cancellationToken)
    {
        var peerKey = peer.PeerKey;
        RotateReply reply;
        try
        {
            using var response = await _client.SendAsync(endpoint, PeerRequest.Post("/Shoal/Share/Rotate", Body).WithTrust(TrustRequirement.None).WithoutBreaker(), PeerCallProfile.Rotate, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                // A 500 means the Peer signed the old token out and could not mint a new one. Anything else did not touch the token.
                return response.StatusCode == HttpStatusCode.InternalServerError
                    ? await LostAsync(peerKey).ConfigureAwait(false)
                    : Failed(peerKey, RotateError.Rejected, "rejected");
            }

            try
            {
                reply = await response.ReadJsonAsync<RotateReply>(cancellationToken).ConfigureAwait(false);
            }
            catch (PeerCallException ex) when (ex.Kind is PeerFailureKind.BadResponse or PeerFailureKind.TooLarge)
            {
                return await Bad(peerKey).ConfigureAwait(false); // a 200 that cannot be read: the Peer most likely rotated
            }
        }
        catch (PeerCallException ex) when (ex.Kind is PeerFailureKind.Unreachable or PeerFailureKind.Timeout or PeerFailureKind.BadResponse or PeerFailureKind.TooLarge or PeerFailureKind.Redirect)
        {
            // The request may have been sent. If the old token still works, nothing happened; if not, Home's token is gone.
            return await StillAliveAsync(endpoint, cancellationToken).ConfigureAwait(false) ? new RotateResult(RotateError.NotOnline) : await LostAsync(peerKey).ConfigureAwait(false);
        }
        catch (PeerCallException)
        {
            return new RotateResult(RotateError.NotOnline); // nothing was sent
        }

        if (reply.V != 1 || reply.Token is not { } token || !TokenRegex().IsMatch(token) || !string.Equals(reply.DeviceId, endpoint.DeviceId, StringComparison.Ordinal))
        {
            // The Peer says it rotated but the answer is not usable: the old token is probably dead.
            return await Bad(peerKey).ConfigureAwait(false);
        }

        // The new token is the active credential in memory at once; the file follows.
        _directory.SetActiveToken(peerKey, token);
        var fresh = _directory.Find(peerKey);
        if (fresh is null || !await CheckNewTokenAsync(fresh, cancellationToken).ConfigureAwait(false))
        {
            await SignOutAsync(fresh ?? endpoint).ConfigureAwait(false);
            _directory.ClearActiveToken(peerKey);
            return await Bad(peerKey).ConfigureAwait(false);
        }

        var now = _time.GetUtcNow().UtcDateTime;
        await _state.UpdateAsync(s => s.Home.Peers.FirstOrDefault(p => p.PeerKey == peerKey)?.LastRotatedUtc = now, CancellationToken.None).ConfigureAwait(false);
        _health.SetRotationError(peerKey, null);
        NoteScheduled(peerKey, blocked: false);
        _registry.RecordPinVerified(peerKey);
        _registry.RecordAuthVerified(peerKey);
        _logger.LogInformation("Rotated the token of server {Server} ({Trigger})", peer.DisplayName, trigger);
        _ = StoreAsync(peerKey, endpoint.DeviceId, token);
        return new RotateResult(RotateError.None, reply.RotatedUtc is { } at && at > DateTime.UnixEpoch ? at.ToUniversalTime() : now);
    }

    // 4.2 step 4 with the new token: the account must still be a plain user (never an administrator), and the server must be the pinned one.
    private async Task<bool> CheckNewTokenAsync(PeerEndpoint fresh, CancellationToken cancellationToken)
    {
        try
        {
            using (var me = await _client.SendAsync(fresh, PeerRequest.Get("/Users/Me").WithTrust(TrustRequirement.None), PeerCallProfile.Info, cancellationToken).ConfigureAwait(false))
            {
                if (me.StatusCode != HttpStatusCode.OK)
                {
                    return false;
                }

                var user = await me.ReadJsonAsync<UserWire>(cancellationToken).ConfigureAwait(false);
                if (user.Policy?.IsAdministrator is not false)
                {
                    return false; // an administrator account, or no policy to judge
                }
            }

            using var info = await _client.SendAsync(fresh, PeerRequest.Get("/System/Info").WithTrust(TrustRequirement.None), PeerCallProfile.Info, cancellationToken).ConfigureAwait(false);
            if (info.StatusCode != HttpStatusCode.OK)
            {
                return false;
            }

            var system = await info.ReadJsonAsync<SystemInfoWire>(cancellationToken).ConfigureAwait(false);
            return PeerTrust.TryNormalizeServerId(system.Id, out var serverId) && PeerTrust.TryNormalizeServerId(fresh.PinnedServerId, out var pinned) && string.Equals(serverId, pinned, StringComparison.Ordinal);
        }
        catch (PeerCallException)
        {
            return false;
        }
    }

    private async Task<bool> StillAliveAsync(PeerEndpoint endpoint, CancellationToken cancellationToken)
    {
        try
        {
            using var me = await _client.SendAsync(endpoint, PeerRequest.Get("/Users/Me").WithTrust(TrustRequirement.None), PeerCallProfile.Info, cancellationToken).ConfigureAwait(false);
            return me.StatusCode == HttpStatusCode.OK;
        }
        catch (PeerCallException)
        {
            return false;
        }
    }

    private async Task SignOutAsync(PeerEndpoint endpoint)
    {
        try
        {
            using var response = await _client.SendAsync(endpoint, PeerRequest.Post("/Sessions/Logout", null).WithTrust(TrustRequirement.None), PeerCallProfile.SessionReport, CancellationToken.None).ConfigureAwait(false);
        }
        catch (PeerCallException)
        {
            // best effort: the account is a plain user and the Peer's own idle rules apply
        }
    }

    private RotateResult Failed(string peerKey, RotateError error, string word)
    {
        _health.SetRotationError(peerKey, new RotationErrorRecord { Kind = word, AtUtc = _time.GetUtcNow().UtcDateTime });
        _logger.LogWarning("A token rotation was refused by the server ({Kind})", word);
        return new RotateResult(error);
    }

    private async Task<RotateResult> LostAsync(string peerKey)
    {
        _registry.SetStatus(peerKey, PeerStatus.AuthFailed);
        _health.SetRotationError(peerKey, new RotationErrorRecord { Kind = "lostResponse", AtUtc = _time.GetUtcNow().UtcDateTime });
        _logger.LogWarning("A token rotation did not come back; the server is marked AuthFailed and must be paired again");
        await Task.CompletedTask.ConfigureAwait(false);
        return new RotateResult(RotateError.LostResponse);
    }

    private async Task<RotateResult> Bad(string peerKey)
    {
        _registry.SetStatus(peerKey, PeerStatus.AuthFailed);
        _health.SetRotationError(peerKey, new RotationErrorRecord { Kind = "badResponse", AtUtc = _time.GetUtcNow().UtcDateTime });
        _logger.LogWarning("A token rotation answer was not acceptable; the server is marked AuthFailed and must be paired again");
        await Task.CompletedTask.ConfigureAwait(false);
        return new RotateResult(RotateError.BadResponse);
    }

    // The file is written after the answer: while it fails the in-memory token keeps working, and a restart in that window needs a new pairing.
    private async Task StoreAsync(string peerKey, string deviceId, string token)
    {
        for (var attempt = 1; attempt <= StoreAttempts; attempt++)
        {
            try
            {
                var current = _secrets.TryGet(peerKey);
                await _secrets.PutAsync(peerKey, new PeerCredential(token, deviceId, current?.PairedUtc ?? _time.GetUtcNow().UtcDateTime, current?.Method ?? PairingMethod.Invite), CancellationToken.None).ConfigureAwait(false);
                _directory.ClearActiveToken(peerKey);
                return;
            }
#pragma warning disable CA1031 // A failed write is retried; the last failure is reported as an alert, never thrown into the request.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                _logger.LogWarning("The new token could not be written to the secret store (try {Attempt} of {Max}, {Error})", attempt, StoreAttempts, ex.GetType().Name);
                if (attempt < StoreAttempts)
                {
                    await Task.Delay(StoreRetryDelay, CancellationToken.None).ConfigureAwait(false);
                }
            }
        }

        _health.SetRotationError(peerKey, new RotationErrorRecord { Kind = "storeFailed", AtUtc = _time.GetUtcNow().UtcDateTime });
    }

    private sealed class RotateReply
    {
        public int V { get; set; }

        public string? Token { get; set; }

        public string? DeviceId { get; set; }

        public DateTime? RotatedUtc { get; set; }
    }
}
