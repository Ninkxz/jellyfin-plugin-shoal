using System;
using System.Collections.Concurrent;

namespace Jellyfin.Plugin.Shoal.Peers;

/// <summary>
/// The runtime state of a peer (DESIGN 2 #6). It is derived and never stored: a restart starts every peer at <see cref="Offline"/>
/// until the health check has looked.
/// </summary>
public enum PeerStatus
{
    /// <summary>Not reached yet, or unreachable. The starting state.</summary>
    Offline,

    /// <summary>Reachable and authenticated.</summary>
    Online,

    /// <summary>Reachable but slow, or with failures in a row that did not open the breaker yet.</summary>
    Degraded,

    /// <summary>The peer refused the token (401 or 403): no retries, no re-login loop; the admin re-pairs (DESIGN 4.4).</summary>
    AuthFailed,

    /// <summary>The server id is not the pinned one: a reset server, a reused name or address. Zero calls until the admin acts.</summary>
    IdentityMismatch,

    /// <summary>The TLS public key is not the pinned one. Zero calls until the admin re-trusts it.</summary>
    TlsKeyChanged,

    /// <summary>The admin disabled the peer.</summary>
    Disabled,

    /// <summary>A pairing is in progress; no token exists yet.</summary>
    Pairing,

    /// <summary>A pairing was stopped; the peer keeps no token (DESIGN 4.2a).</summary>
    PairingAborted
}

/// <summary>
/// What an authenticated call to a peer needs to be true before it is sent (DESIGN 4.4, "Trust window").
/// </summary>
public enum TrustRequirement
{
    /// <summary>Nothing: only the calls that refresh the window (the identity check, the authenticated health call) and pairing steps.</summary>
    None,

    /// <summary>The server id was confirmed <b>unauthenticated</b> within the window (120 s). The default for every token-bearing call.</summary>
    PinFresh,

    /// <summary>Additionally the token was confirmed within the window. Static proxying needs this, so a revoked token stops streaming within 120 s.</summary>
    PinAndAuthFresh
}

/// <summary>
/// Why a call was not allowed by the trust window.
/// </summary>
public enum TrustDenial
{
    /// <summary>Allowed.</summary>
    None,

    /// <summary>The status says stop: <see cref="PeerStatus.IdentityMismatch"/> or <see cref="PeerStatus.TlsKeyChanged"/> (everything), <see cref="PeerStatus.AuthFailed"/> (token-bearing calls).</summary>
    Blocked,

    /// <summary>The identity check is older than the window, or was never done.</summary>
    PinStale,

    /// <summary>The token check is older than the window, or was never done.</summary>
    AuthStale,

    /// <summary>A token rotation is running (DESIGN v2.6.1): token-bearing calls wait as if the circuit were open.</summary>
    Rotating
}

/// <summary>
/// The result of <see cref="PeerRegistry.BeginRotation"/>.
/// </summary>
public enum RotationStart
{
    /// <summary>The rotation mark is set.</summary>
    Started,

    /// <summary>Another rotation of this peer is running.</summary>
    AlreadyRotating,

    /// <summary>A stream or conversion slot is held for the peer.</summary>
    StreamsActive
}

/// <summary>
/// The in-memory runtime table of peers: status and trust window (DESIGN 4.4). "No authenticated request (other than the health call that
/// refreshes them) is sent unless the pin was confirmed unauthenticated within the last 120 s" is enforced by
/// <see cref="PeerClient"/> asking <see cref="Check"/> before it sends a token-bearing request.
/// </summary>
public sealed class PeerRegistry
{
    /// <summary>How long a confirmed pin or token check stays valid.</summary>
    public static readonly TimeSpan TrustWindow = TimeSpan.FromSeconds(120);

    /// <summary>The longest a rotation mark stands (DESIGN v2.6.1).</summary>
    public static readonly TimeSpan RotationWindow = TimeSpan.FromSeconds(15);

    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<string, Entry> _peers = new(StringComparer.Ordinal);

    /// <summary>
    /// Initializes a new instance of the <see cref="PeerRegistry"/> class.
    /// </summary>
    /// <param name="time">The clock.</param>
    public PeerRegistry(TimeProvider time) => _time = time;

    /// <summary>
    /// Initializes a new instance of the <see cref="PeerRegistry"/> class.
    /// </summary>
    public PeerRegistry()
        : this(TimeProvider.System)
    {
    }

    /// <summary>
    /// Starts a token rotation of a peer if nothing else is rotating it and <paramref name="noStreamsHeld"/> says no stream or conversion slot is held.
    /// The check and the mark are one step under the peer's lock; the mark ends by itself after <see cref="RotationWindow"/>.
    /// </summary>
    /// <param name="peerKey">The peer key.</param>
    /// <param name="noStreamsHeld">Tells that no stream or conversion slot is held for the peer.</param>
    /// <returns>Started, or why not.</returns>
    public RotationStart BeginRotation(string peerKey, Func<bool> noStreamsHeld)
    {
        ArgumentNullException.ThrowIfNull(noStreamsHeld);
        var entry = _peers.GetOrAdd(peerKey, _ => new Entry());
        lock (entry)
        {
            var now = _time.GetUtcNow();
            if (entry.RotatingUntil is { } until && now < until)
            {
                return RotationStart.AlreadyRotating;
            }

            if (!noStreamsHeld())
            {
                return RotationStart.StreamsActive;
            }

            entry.RotatingUntil = now + RotationWindow;
            return RotationStart.Started;
        }
    }

    /// <summary>
    /// Ends the rotation mark of a peer.
    /// </summary>
    /// <param name="peerKey">The peer key.</param>
    public void EndRotation(string peerKey)
    {
        if (_peers.TryGetValue(peerKey, out var entry))
        {
            lock (entry)
            {
                entry.RotatingUntil = null;
            }
        }
    }

    /// <summary>
    /// Whether a token rotation of the peer is running (at most <see cref="RotationWindow"/> long).
    /// </summary>
    /// <param name="peerKey">The peer key.</param>
    /// <returns>True while the mark stands.</returns>
    public bool IsRotating(string peerKey) => _peers.TryGetValue(peerKey, out var entry) && entry.RotatingUntil is { } until && _time.GetUtcNow() < until;

    /// <summary>
    /// Gets the status of a peer.
    /// </summary>
    /// <param name="peerKey">The peer key.</param>
    /// <returns>The status; <see cref="PeerStatus.Offline"/> for a peer nobody has looked at.</returns>
    public PeerStatus StatusOf(string peerKey) => _peers.TryGetValue(peerKey, out var entry) ? entry.Status : PeerStatus.Offline;

    /// <summary>
    /// Sets the status.
    /// </summary>
    /// <param name="peerKey">The peer key.</param>
    /// <param name="status">The new status.</param>
    public void SetStatus(string peerKey, PeerStatus status)
    {
        var entry = _peers.GetOrAdd(peerKey, _ => new Entry());
        lock (entry)
        {
            entry.Status = status;
            if (status is PeerStatus.IdentityMismatch or PeerStatus.TlsKeyChanged or PeerStatus.AuthFailed or PeerStatus.Pairing or PeerStatus.PairingAborted or PeerStatus.Disabled)
            {
                entry.PinVerifiedUtc = null; // whatever was true before no longer counts
                entry.AuthVerifiedUtc = null;
            }
        }
    }

    /// <summary>
    /// Lifts a TLS stop after the admin trusted a new key, but keeps every token-bearing call blocked until the next <b>identity check</b>
    /// (the unauthenticated server id call, <see cref="RecordPinVerified"/>) has passed on the new key (Warden 11 M3). Without this, a plain
    /// <see cref="SetStatus"/> to <see cref="PeerStatus.Offline"/> would let calls that need no trust window (sign-out, rotation) carry the token to the new key first.
    /// </summary>
    /// <param name="peerKey">The peer key.</param>
    public void BeginRetrust(string peerKey)
    {
        var entry = _peers.GetOrAdd(peerKey, _ => new Entry());
        lock (entry)
        {
            entry.Status = PeerStatus.Offline;
            entry.PinVerifiedUtc = null;
            entry.AuthVerifiedUtc = null;
            entry.IdentityRequired = true;
        }
    }

    /// <summary>
    /// Records that the server id was confirmed by an unauthenticated call just now. Ignored while the status blocks the peer.
    /// </summary>
    /// <param name="peerKey">The peer key.</param>
    public void RecordPinVerified(string peerKey)
    {
        var entry = _peers.GetOrAdd(peerKey, _ => new Entry());
        lock (entry)
        {
            if (entry.Status is not (PeerStatus.IdentityMismatch or PeerStatus.TlsKeyChanged))
            {
                entry.PinVerifiedUtc = _time.GetUtcNow();
                entry.IdentityRequired = false;
            }
        }
    }

    /// <summary>
    /// Records that the token was confirmed by an authenticated call just now.
    /// </summary>
    /// <param name="peerKey">The peer key.</param>
    public void RecordAuthVerified(string peerKey)
    {
        var entry = _peers.GetOrAdd(peerKey, _ => new Entry());
        lock (entry)
        {
            if (entry.Status is not (PeerStatus.IdentityMismatch or PeerStatus.TlsKeyChanged or PeerStatus.AuthFailed))
            {
                entry.AuthVerifiedUtc = _time.GetUtcNow();
            }
        }
    }

    /// <summary>
    /// Forgets the trust window of a peer (the pin, the token) without changing its status, for example when its address changes.
    /// </summary>
    /// <param name="peerKey">The peer key.</param>
    public void Invalidate(string peerKey)
    {
        if (_peers.TryGetValue(peerKey, out var entry))
        {
            lock (entry)
            {
                entry.PinVerifiedUtc = null;
                entry.AuthVerifiedUtc = null;
            }
        }
    }

    /// <summary>
    /// Forgets a peer completely.
    /// </summary>
    /// <param name="peerKey">The peer key.</param>
    public void Forget(string peerKey) => _peers.TryRemove(peerKey, out _);

    /// <summary>
    /// When the server id was last confirmed, or null.
    /// </summary>
    /// <param name="peerKey">The peer key.</param>
    /// <returns>The time, or null.</returns>
    public DateTimeOffset? PinVerifiedUtc(string peerKey) => _peers.TryGetValue(peerKey, out var entry) ? entry.PinVerifiedUtc : null;

    /// <summary>
    /// When the token was last confirmed, or null.
    /// </summary>
    /// <param name="peerKey">The peer key.</param>
    /// <returns>The time, or null.</returns>
    public DateTimeOffset? AuthVerifiedUtc(string peerKey) => _peers.TryGetValue(peerKey, out var entry) ? entry.AuthVerifiedUtc : null;

    /// <summary>
    /// Decides whether a call may be sent now.
    /// </summary>
    /// <param name="peerKey">The peer key.</param>
    /// <param name="carriesToken">Whether the request carries the peer token.</param>
    /// <param name="requirement">What the call needs.</param>
    /// <returns>None when the call may go, otherwise why not.</returns>
    public TrustDenial Check(string peerKey, bool carriesToken, TrustRequirement requirement)
    {
        var status = StatusOf(peerKey);
        if (status is PeerStatus.IdentityMismatch or PeerStatus.TlsKeyChanged)
        {
            return TrustDenial.Blocked; // zero further calls until the admin acts
        }

        if (!carriesToken)
        {
            return TrustDenial.None;
        }

        if (_peers.TryGetValue(peerKey, out var retrust) && retrust.IdentityRequired)
        {
            return TrustDenial.Blocked; // a new key was trusted: no token goes to it before the server id is confirmed
        }

        if (status == PeerStatus.AuthFailed)
        {
            return TrustDenial.Blocked;
        }

        if (requirement == TrustRequirement.None)
        {
            return TrustDenial.None;
        }

        if (IsRotating(peerKey))
        {
            return TrustDenial.Rotating;
        }

        var now = _time.GetUtcNow();
        if (PinVerifiedUtc(peerKey) is not { } pin || now - pin > TrustWindow)
        {
            return TrustDenial.PinStale;
        }

        if (requirement == TrustRequirement.PinAndAuthFresh && (AuthVerifiedUtc(peerKey) is not { } auth || now - auth > TrustWindow))
        {
            return TrustDenial.AuthStale;
        }

        return TrustDenial.None;
    }

    private sealed class Entry
    {
        public PeerStatus Status { get; set; } = PeerStatus.Offline;

        public DateTimeOffset? PinVerifiedUtc { get; set; }

        public DateTimeOffset? AuthVerifiedUtc { get; set; }

        public DateTimeOffset? RotatingUntil { get; set; }

        public bool IdentityRequired { get; set; }
    }
}
