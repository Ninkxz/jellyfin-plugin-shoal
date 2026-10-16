using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.Api.Admin;
using Jellyfin.Plugin.Shoal.Pairing;
using Jellyfin.Plugin.Shoal.Security;
using Jellyfin.Plugin.Shoal.State;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Shoal.Sharing;

/// <summary>What <c>POST /Shoal/Pair</c> answers to Home (DESIGN 4.1b). A <see cref="Failed"/> result is always the same 404; the rest carry the protocol's own words.</summary>
/// <param name="Failed">True for every failure (unknown, expired, delivered, mismatch, malformed): one answer, no oracle.</param>
/// <param name="State">pending, approved, denied or codeMismatch; for a valid secret of an invite that is over: expired, replaced, cancelled, uncollected or used.</param>
/// <param name="PeerServerId">This server's id (32 hex).</param>
/// <param name="PeerNonce">This server's nonce.</param>
/// <param name="Token">The access token, in the first approved answer only.</param>
/// <param name="ExpiresInSeconds">How long the invite has left, in seconds (not a time of day: the servers' clocks need not agree), while it is pending.</param>
public sealed record PairAnswer(bool Failed, string State, string? PeerServerId, string? PeerNonce, string? Token, int? ExpiresInSeconds = null)
{
    /// <summary>Gets the one failure answer.</summary>
    public static PairAnswer Failure { get; } = new(true, string.Empty, null, null, null);
}

/// <summary>What Home sends to <c>POST /Shoal/Pair</c>.</summary>
/// <param name="Invite">The 26 character secret (normalized).</param>
/// <param name="HomeNonceCommit">The commitment to Home's nonce.</param>
/// <param name="HomeNonce">Home's nonce, absent on the first request.</param>
/// <param name="HomeServerId">Home's server id.</param>
/// <param name="HomeName">Home's name (text only).</param>
/// <param name="HomeDeviceId">The device id Home will use.</param>
/// <param name="RemoteAddress">The caller's address (for the failure buckets and the address class).</param>
public sealed record PairRequestData(string Invite, string HomeNonceCommit, string? HomeNonce, string HomeServerId, string HomeName, string HomeDeviceId, string RemoteAddress);

/// <summary>
/// The invites and approvals of the share service (K22; DESIGN 4.1b steps 2 and 3, <c>POST /Shoal/Pair</c>). Everything is in memory:
/// an invite is only the SHA-256 of a secret, valid 15 minutes, single use and bound to one share. A restart cancels every invite.
/// </summary>
public sealed class InviteBook : IShareInvites
{
    /// <summary>How many wrong codes end an approval.</summary>
    public const int Tries = 3;

    /// <summary>How long an invite lives.</summary>
    public static readonly TimeSpan InviteLife = TimeSpan.FromMinutes(15);

    /// <summary>How long an approved token waits for Home before it is signed out again.</summary>
    public static readonly TimeSpan CollectWindow = TimeSpan.FromMinutes(2);

    /// <summary>
    /// How long a bound attempt may go unheard before it counts as abandoned: Home polls every second and gives up after 30 seconds of silence, so a card
    /// nobody has polled for this long is dead (the admin sees no card, and cannot approve it). The same Home can then start again with the same invite.
    /// </summary>
    public static readonly TimeSpan AttemptStale = TimeSpan.FromSeconds(30);

    private const int PerIpFailures = 10;
    private const int GlobalFailures = 100;

    private readonly object _lock = new();
    private readonly IStateStore _state;
    private readonly IShareUserSource _users;
    private readonly IHomeIdentity _home;
    private readonly TimeProvider _time;
    private readonly ShareLocks _locks;
    private readonly ILogger _logger;
    private readonly Dictionary<string, Invite> _invites = new(StringComparer.Ordinal);
    private readonly List<EndedInvite> _ended = [];
    private readonly FailureLimiter _failures = new(PerIpFailures, GlobalFailures);

    /// <summary>
    /// Initializes a new instance of the <see cref="InviteBook"/> class.
    /// </summary>
    /// <param name="state">The state store.</param>
    /// <param name="users">Jellyfin's accounts, devices and sessions.</param>
    /// <param name="home">This server's identity.</param>
    /// <param name="time">The clock.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="locks">The per-share locks shared with rotation, approval, pause and revoke (a private set when none is given).</param>
    public InviteBook(IStateStore state, IShareUserSource users, IHomeIdentity home, TimeProvider time, ILogger<InviteBook> logger, ShareLocks? locks = null)
        : this(state, users, home, time, (ILogger)logger, locks)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="InviteBook"/> class (tests: any logger).
    /// </summary>
    /// <param name="state">The state store.</param>
    /// <param name="users">Jellyfin's accounts, devices and sessions.</param>
    /// <param name="home">This server's identity.</param>
    /// <param name="time">The clock.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="locks">The per-share locks shared with rotation, approval, pause and revoke (a private set when none is given).</param>
    internal InviteBook(IStateStore state, IShareUserSource users, IHomeIdentity home, TimeProvider time, ILogger logger, ShareLocks? locks = null)
    {
        _locks = locks ?? new ShareLocks();
        _state = state;
        _users = users;
        _home = home;
        _time = time;
        _logger = logger;
    }

    private enum Phase
    {
        Active,
        Bound,
        Approving,
        Approved,
        Delivered,
        Denied,
        WrongCode,
        Uncollected
    }

    /// <summary>Gets how many wrong invites were tried in the last hour (the calm notice on the Overview).</summary>
    public int FailuresLastHour
    {
        get
        {
            return _failures.LastHour(_time.GetUtcNow());
        }
    }

    /// <inheritdoc />
    public object InviteState(string shareId)
    {
        lock (_lock)
        {
            if (!_invites.TryGetValue(shareId, out var invite))
            {
                return new { state = "none" };
            }

            return _time.GetUtcNow() >= invite.ExpiresUtc ? new { state = "expired" } : new { state = "active", expiresUtc = (DateTime?)invite.ExpiresUtc.UtcDateTime };
        }
    }

    /// <inheritdoc />
    public Task<(ShareOutcome Outcome, object? Body)> InviteAsync(ShareState share, bool create, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            // An approval in flight (or a token waiting to be collected) is not thrown away by a new or cancelled invite (Warden L3).
            if (_invites.TryGetValue(share.ShareId, out var existing) && existing.Phase is Phase.Approving or Phase.Approved && _time.GetUtcNow() < existing.ExpiresUtc + CollectWindow)
            {
                return Task.FromResult<(ShareOutcome, object?)>((ShareOutcome.Conflict, null));
            }

            if (!create)
            {
                End(share.ShareId, "cancelled");
                return Task.FromResult<(ShareOutcome, object?)>((ShareOutcome.Ok, new { }));
            }

            End(share.ShareId, "replaced"); // the old secret still gets a clear answer from now on
            var secret = InviteCrypto.NewSecret();
            var invite = new Invite(share.ShareId, SHA256.HashData(Encoding.UTF8.GetBytes(secret)), _time.GetUtcNow() + InviteLife);
            _invites[share.ShareId] = invite;
            _logger.LogInformation("An invite was created");
            return Task.FromResult<(ShareOutcome, object?)>((ShareOutcome.Ok, new { invite = InviteCrypto.Format(_home.ServerId, secret), expiresUtc = invite.ExpiresUtc.UtcDateTime }));
        }
    }

    /// <inheritdoc />
    public void Forget(string shareId)
    {
        lock (_lock)
        {
            End(shareId, "cancelled");
        }
    }

    /// <inheritdoc />
    public object? Approval(string shareId)
    {
        lock (_lock)
        {
            if (!_invites.TryGetValue(shareId, out var invite) || invite.Sas is null)
            {
                return null;
            }

            if (invite.Phase == Phase.Bound && (_time.GetUtcNow() >= invite.ExpiresUtc || Stale(invite, _time.GetUtcNow())))
            {
                return null; // over, or abandoned: a dead card is never offered for approval
            }

            var prefix = invite.HomeServerId[..8];
            return invite.Phase switch
            {
                Phase.Bound or Phase.Approving => new { state = "pending", homeName = invite.HomeName, homeServerIdPrefix = prefix, addressClass = invite.AddressClass, triesLeft = invite.TriesLeft, restarted = invite.Restarts > 0 },
                Phase.Approved => new { state = "approved", homeName = invite.HomeName, homeServerIdPrefix = prefix, addressClass = invite.AddressClass, triesLeft = invite.TriesLeft },
                Phase.Delivered => new { state = "collected", homeName = invite.HomeName, homeServerIdPrefix = prefix, addressClass = invite.AddressClass, triesLeft = invite.TriesLeft },
                Phase.Uncollected => new { state = "uncollected", homeName = invite.HomeName, homeServerIdPrefix = prefix, addressClass = invite.AddressClass, triesLeft = invite.TriesLeft },
                Phase.WrongCode => new { state = "cancelledWrongCode", homeName = invite.HomeName, homeServerIdPrefix = prefix, addressClass = invite.AddressClass, triesLeft = 0 },
                Phase.Denied => new { state = "denied", homeName = invite.HomeName, homeServerIdPrefix = prefix, addressClass = invite.AddressClass, triesLeft = invite.TriesLeft },
                _ => null
            };
        }
    }

    /// <inheritdoc />
    public async Task<(ShareOutcome Outcome, object? Body)> DecideAsync(ShareState share, ApprovalRequest request, CancellationToken cancellationToken)
    {
        Invite invite;
        lock (_lock)
        {
            if (!_invites.TryGetValue(share.ShareId, out var found) || found.Phase != Phase.Bound || found.Sas is null || _time.GetUtcNow() >= found.ExpiresUtc || Stale(found, _time.GetUtcNow()))
            {
                return (ShareOutcome.Conflict, null);
            }

            invite = found;
            if (!request.Approve)
            {
                invite.Phase = Phase.Denied;
                _logger.LogInformation("An approval was denied by the admin (server {Home})", Redact.Id(invite.HomeServerId));
                return (ShareOutcome.Ok, new { state = "denied" });
            }

            // The server judges the code: normalized again here, compared in constant time; a malformed value is a miss like any other.
            var typed = InviteCrypto.NormalizeCode(request.Sas ?? string.Empty);
            var hit = typed.Length == 8 && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(typed), Encoding.UTF8.GetBytes(invite.Sas));
            if (!hit)
            {
                invite.TriesLeft--;
                if (invite.TriesLeft <= 0)
                {
                    invite.Phase = Phase.WrongCode;
                    _logger.LogWarning("An approval was cancelled after three wrong safety codes (server {Home}): codeMismatch", Redact.Id(invite.HomeServerId));
                    return (ShareOutcome.Ok, new { match = false, triesLeft = 0, state = "cancelledWrongCode" });
                }

                return (ShareOutcome.Ok, new { match = false, triesLeft = invite.TriesLeft });
            }

            invite.Phase = Phase.Approving; // the effects below must not run twice; Home still reads "pending" until the token exists
        }

        try
        {
            // The work is not cancelled halfway by a client that went away: a half-done approval is worse than a finished one.
            using (await _locks.HoldAsync(share.ShareId, CancellationToken.None).ConfigureAwait(false))
            {
                await ApproveAsync(share, invite, CancellationToken.None).ConfigureAwait(false);
            }
        }
#pragma warning disable CA1031 // Whatever failed, the approval is rolled back to "pending" and nothing stays signed in.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _logger.LogWarning("The approval could not be completed: {Error}", ex.GetType().Name);
            await SignOutAllAsync(share).ConfigureAwait(false);
            lock (_lock)
            {
                if (invite.Phase == Phase.Approving)
                {
                    invite.Phase = Phase.Bound;
                    invite.Token = null;
                }
            }

            return (ShareOutcome.Invalid, null);
        }

        _logger.LogInformation("An approval succeeded for server {Home}; the token waits for it", Redact.Id(invite.HomeServerId));
        return (ShareOutcome.Ok, new { match = true, state = "approved" });
    }

    /// <summary>
    /// Answers one request to <c>POST /Shoal/Pair</c>. The same work happens for every request, so neither the answer nor its timing says
    /// whether an invite is active.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <returns>The answer, or <see cref="PairAnswer.Failure"/>.</returns>
    public PairAnswer Redeem(PairRequestData request)
    {
        lock (_lock)
        {
            var now = _time.GetUtcNow();
            var presented = SHA256.HashData(Encoding.UTF8.GetBytes(request.Invite));

            // Compare with every invite Shoal still remembers (live or not) and with the ended ones, or with a fixed dummy when there are none: the same work
            // either way. A secret that matches an invite that is over gets its reason; one that matches nothing gets the one uniform 404.
            Invite? match = null;
            string? endedReason = null;
            var pool = _invites.Values.Select(static i => (i.Hash, (Invite?)i, (string?)null)).Concat(_ended.Select(static e => (e.Hash, (Invite?)null, (string?)e.Reason))).ToList();
            foreach (var candidate in pool.Count == 0 ? [(Invite.Dummy.Hash, (Invite?)null, (string?)null)] : pool)
            {
                if (CryptographicOperations.FixedTimeEquals(presented, candidate.Hash) && !ReferenceEquals(candidate.Item2, Invite.Dummy) && (candidate.Item2 is not null || candidate.Item3 is not null))
                {
                    match = candidate.Item2;
                    endedReason = candidate.Item3;
                }
            }

            if (match is null && endedReason is null)
            {
                return Fail(request.RemoteAddress, now, "unknownInvite");
            }

            var ownId = _home.ServerId.ToLowerInvariant();
            if (endedReason is not null)
            {
                _logger.LogInformation("A pairing request used an invite that is over: {Reason}", endedReason);
                return new PairAnswer(false, endedReason, ownId, null, null);
            }

            if (match is null)
            {
                return Fail(request.RemoteAddress, now, "unknownInvite");
            }

            if (DeadReason(match, now) is { } dead)
            {
                _logger.LogInformation("A pairing request used an invite that is over: {Reason}", dead);
                return new PairAnswer(false, dead, ownId, null, null);
            }

            if (!InviteCrypto.TryBase64Url(request.HomeNonceCommit, out var commitBytes) || commitBytes.Length != 32)
            {
                return Fail(request.RemoteAddress, now);
            }

            if (match.Phase == Phase.Active)
            {
                if (request.HomeNonce is not null)
                {
                    return Fail(request.RemoteAddress, now); // the first request carries the commitment only
                }

                match.Commit = request.HomeNonceCommit;
                match.HomeServerId = request.HomeServerId.ToLowerInvariant();
                match.HomeName = request.HomeName;
                match.HomeDeviceId = request.HomeDeviceId;
                match.AddressClass = AddressClassOf(request.RemoteAddress);
                match.PeerNonce = RandomNumberGenerator.GetBytes(16);
                match.LastSeen = now;
                match.Phase = Phase.Bound;
                _logger.LogInformation("An invite was redeemed by server {Home} ({AddressClass}); waiting for the safety code", Redact.Id(match.HomeServerId), match.AddressClass);
                return new PairAnswer(false, "pending", _home.ServerId.ToLowerInvariant(), InviteCrypto.Base64Url(match.PeerNonce), null, Remaining(match, now));
            }

            // The same Home starting again (it backed out, reloaded or lost its connection): the first request of a new attempt carries a new commitment and no
            // nonce. While nothing was approved, the old attempt is dropped and the new one takes its place, with the tries already used still counted. The
            // invite is only used up by an approval, so an interrupted attempt does not burn it, and the old attempt can no longer be approved.
            if (match.Phase == Phase.Bound && request.HomeNonce is null && !string.Equals(match.Commit, request.HomeNonceCommit, StringComparison.Ordinal)
                && string.Equals(request.HomeServerId.ToLowerInvariant(), match.HomeServerId, StringComparison.Ordinal)
                && string.Equals(request.HomeName, match.HomeName, StringComparison.Ordinal))
            {
                match.Commit = request.HomeNonceCommit;
                match.HomeDeviceId = request.HomeDeviceId; // Home's new attempt may use a new device id; the name and server id are what must stay the same
                match.Restarts++;
                match.AddressClass = AddressClassOf(request.RemoteAddress);
                match.PeerNonce = RandomNumberGenerator.GetBytes(16);
                match.HomeNonce = null;
                match.Sas = null;
                match.LastSeen = now;
                _logger.LogInformation("An invite was started again by server {Home}; the earlier attempt is dropped", Redact.Id(match.HomeServerId));
                return new PairAnswer(false, "pending", _home.ServerId.ToLowerInvariant(), InviteCrypto.Base64Url(match.PeerNonce), null, Remaining(match, now));
            }

            // A bound invite: the same commitment, and from the first poll on the nonce that hashes to it.
            if (!string.Equals(match.Commit, request.HomeNonceCommit, StringComparison.Ordinal) || match.PeerNonce is null)
            {
                return Fail(request.RemoteAddress, now);
            }

            if (match.Sas is null)
            {
                if (!InviteCrypto.TryBase64Url(request.HomeNonce, out var nonce) || nonce.Length != 16 || !string.Equals(InviteCrypto.Commit(nonce), match.Commit, StringComparison.Ordinal))
                {
                    return Fail(request.RemoteAddress, now);
                }

                match.HomeNonce = nonce;
                match.Sas = InviteCrypto.Sas(request.Invite, nonce, match.PeerNonce, match.HomeServerId, _home.ServerId);
            }
            else if (!InviteCrypto.TryBase64Url(request.HomeNonce, out var again) || !CryptographicOperations.FixedTimeEquals(again, match.HomeNonce!))
            {
                return Fail(request.RemoteAddress, now);
            }

            if (match.Phase == Phase.Bound)
            {
                match.LastSeen = now;
            }

            var peerNonce = InviteCrypto.Base64Url(match.PeerNonce);
            var peerId = _home.ServerId.ToLowerInvariant();
            switch (match.Phase)
            {
                case Phase.Approved:
                    var token = match.Token;
                    match.Token = null;
                    match.Phase = Phase.Delivered;
                    _logger.LogInformation("An invite's token was delivered");
                    return new PairAnswer(false, "approved", peerId, peerNonce, token);
                case Phase.Denied:
                    return new PairAnswer(false, "denied", peerId, peerNonce, null);
                case Phase.WrongCode:
                    return new PairAnswer(false, "codeMismatch", peerId, peerNonce, null);
                case Phase.Bound:
                case Phase.Approving:
                    return new PairAnswer(false, "pending", peerId, peerNonce, null, Remaining(match, now));
                default:
                    return Fail(request.RemoteAddress, now);
            }
        }
    }

    /// <summary>
    /// Signs out a token Home never collected (2 minutes) and forgets expired invites. Run from the share sweep.
    /// </summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>A task that completes when the pass is done.</returns>
    public async Task SweepAsync(CancellationToken cancellationToken)
    {
        List<Invite> stale;
        lock (_lock)
        {
            var now = _time.GetUtcNow();
            stale = [.. _invites.Values.Where(i => i.Phase == Phase.Approved && now - i.ApprovedUtc >= CollectWindow)];
            foreach (var invite in stale)
            {
                invite.Phase = Phase.Uncollected;
                invite.Token = null;
            }

            foreach (var key in _invites.Where(kv => now >= kv.Value.ExpiresUtc + TimeSpan.FromHours(6)).Select(kv => kv.Key).ToList())
            {
                _invites.Remove(key);
            }
        }

        foreach (var invite in stale)
        {
            var share = _state.Current.PeerRole.Shares.FirstOrDefault(s => s.ShareId == invite.ShareId);
            if (share is not null && _users.Exists(share.ShareUserId))
            {
                foreach (var device in _users.Devices(share.ShareUserId))
                {
                    await _users.RemoveDeviceAsync(share.ShareUserId, device.DeviceId, cancellationToken).ConfigureAwait(false);
                }
            }

            _logger.LogWarning("A token nobody collected within two minutes was signed out");
        }
    }

    /// <summary>
    /// Whether this caller has used up its failure bucket (10 per minute per address, 100 per minute overall). Valid secrets are never counted.
    /// </summary>
    /// <param name="ip">The caller's address.</param>
    /// <returns>True when a failure should be answered 429.</returns>
    public bool Throttled(string ip)
    {
        return _failures.Throttled(AddressKey.Of(ip), _time.GetUtcNow());
    }

    // Why an invite can no longer be redeemed, or null while it can. An approved key can be collected for two minutes after the approval, even past the
    // invite's own 15 minutes: the friend approved in time, and Home only has to fetch it.
    private static string? DeadReason(Invite invite, DateTimeOffset now) => invite.Phase switch
    {
        Phase.Delivered => "used",
        Phase.Uncollected => "uncollected",
        Phase.Approved => now - invite.ApprovedUtc >= CollectWindow ? "uncollected" : null,
        _ => now >= invite.ExpiresUtc ? "expired" : null,
    };

    // Seconds left, not a time of day: the two servers' clocks need not agree.
    private static bool Stale(Invite invite, DateTimeOffset now) => invite.Phase == Phase.Bound && now - invite.LastSeen > AttemptStale;

    private static int Remaining(Invite invite, DateTimeOffset now) => (int)Math.Clamp((invite.ExpiresUtc - now).TotalSeconds, 0, InviteLife.TotalSeconds);

    private static string AddressClassOf(string address)
    {
        if (!System.Net.IPAddress.TryParse(address, out var ip))
        {
            return "public";
        }

        if (ip.IsIPv4MappedToIPv6)
        {
            ip = ip.MapToIPv4();
        }

        var bytes = ip.GetAddressBytes();
        if (bytes.Length == 4)
        {
            if (bytes[0] == 100 && bytes[1] is >= 64 and <= 127)
            {
                return "tailnet";
            }

            return System.Net.IPAddress.IsLoopback(ip) || bytes[0] == 10 || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) || (bytes[0] == 192 && bytes[1] == 168) ? "lan" : "public";
        }

        return bytes[0] == 0xfd && bytes[1] == 0x7a ? "tailnet" : System.Net.IPAddress.IsLoopback(ip) || (bytes[0] & 0xfe) == 0xfc ? "lan" : "public";
    }

    // Takes an invite out of the book and remembers its secret's hash with the reason, so a friend or Home holding the old secret learns what happened.
    // Called with the lock held.
    private void End(string shareId, string reason)
    {
        if (!_invites.Remove(shareId, out var gone))
        {
            return;
        }

        var why = gone.Phase switch { Phase.Delivered => "used", Phase.Uncollected => "uncollected", _ => reason };
        var now = _time.GetUtcNow();
        _ended.RemoveAll(e => now - e.At > TimeSpan.FromHours(6));
        _ended.Add(new EndedInvite(gone.Hash, why, now));
        if (_ended.Count > 16)
        {
            _ended.RemoveAt(0);
        }
    }

    private PairAnswer Fail(string ip, DateTimeOffset now, string reason = "badRequest")
    {
        if (!_failures.Throttled(AddressKey.Of(ip), now))
        {
            _logger.LogInformation("A pairing request was refused: {Reason}", reason); // no address, no invite, no code
        }

        _failures.Record(AddressKey.Of(ip), now);
        return PairAnswer.Failure;
    }

    // On approval, in this order (DESIGN 4.1b step 3): sign out every existing device, lock the account to Home's device, mint the token.
    private async Task ApproveAsync(ShareState share, Invite invite, CancellationToken cancellationToken)
    {
        // Home's server id was checked as 32 hex characters at the first request; its first 12 are kept for the display-only friend link (DESIGN v2.6.2).
        var prefix = invite.HomeServerId.Length >= 12 && invite.HomeServerId[..12].All(static c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f')) ? invite.HomeServerId[..12] : null;
        var token = await ShareTokenIssuer.IssueAsync(_state, _users, share.ShareId, invite.HomeDeviceId, "Shoal: " + invite.HomeName, replaceExpectedDevices: true, prefix, mintAttempts: 1, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The share was paused.");
        lock (_lock)
        {
            // A revoked share (or a replaced invite) drops the approval: the minted token is signed out by the caller.
            if (!_invites.TryGetValue(share.ShareId, out var registered) || !ReferenceEquals(registered, invite))
            {
                throw new InvalidOperationException("The invite is gone.");
            }

            // The token and the Approved phase appear together, so Home can never be handed a null token.
            invite.Token = token;
            invite.ApprovedUtc = _time.GetUtcNow();
            invite.Phase = Phase.Approved;
        }
    }

    // A failed approval may have minted a token already: leave nothing signed in.
    private async Task SignOutAllAsync(ShareState share)
    {
        try
        {
            foreach (var device in _users.Devices(share.ShareUserId))
            {
                await _users.RemoveDeviceAsync(share.ShareUserId, device.DeviceId, CancellationToken.None).ConfigureAwait(false);
            }
        }
#pragma warning disable CA1031 // A clean-up after a failure must not throw over the original failure.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _logger.LogWarning("The leftover devices of a failed approval could not be signed out: {Error}", ex.GetType().Name);
        }
    }

    private sealed record EndedInvite(byte[] Hash, string Reason, DateTimeOffset At);

    private sealed class Invite
    {
        public Invite(string shareId, byte[] hash, DateTimeOffset expiresUtc)
        {
            ShareId = shareId;
            Hash = hash;
            ExpiresUtc = expiresUtc;
        }

        public static Invite Dummy { get; } = new(string.Empty, SHA256.HashData(RandomNumberGenerator.GetBytes(16)), DateTimeOffset.MinValue);

        public string ShareId { get; }

        public byte[] Hash { get; }

        public DateTimeOffset ExpiresUtc { get; }

        public Phase Phase { get; set; }

        public string Commit { get; set; } = string.Empty;

        public string HomeServerId { get; set; } = string.Empty;

        public string HomeName { get; set; } = string.Empty;

        public string HomeDeviceId { get; set; } = string.Empty;

        public string AddressClass { get; set; } = "public";

        public byte[]? PeerNonce { get; set; }

        public byte[]? HomeNonce { get; set; }

        public string? Sas { get; set; }

        public int TriesLeft { get; set; } = Tries;

        public string? Token { get; set; }

        public DateTimeOffset ApprovedUtc { get; set; }

        public DateTimeOffset LastSeen { get; set; }

        public int Restarts { get; set; }
    }
}
