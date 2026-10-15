using System;
using System.Collections.Concurrent;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Shoal.Peers;

/// <summary>
/// The result of an identity check.
/// </summary>
public enum PinOutcome
{
    /// <summary>The peer's server id equals the pinned one; the trust window is open.</summary>
    Verified,

    /// <summary>The peer answered with another server id. The peer is now <see cref="PeerStatus.IdentityMismatch"/>.</summary>
    Mismatch,

    /// <summary>No server id is pinned yet (a pairing in progress), so there is nothing to compare.</summary>
    NotPinned,

    /// <summary>The peer could not be asked, or did not answer like Jellyfin. Nothing changed; the trust window is not opened.</summary>
    Unavailable
}

/// <summary>
/// The result of a token check.
/// </summary>
public enum AuthOutcome
{
    /// <summary>The peer accepted the token.</summary>
    Verified,

    /// <summary>The peer refused the token (401 or 403). The peer is now <see cref="PeerStatus.AuthFailed"/>.</summary>
    Refused,

    /// <summary>The peer could not be asked, or answered something else. Nothing changed.</summary>
    Unavailable
}

/// <summary>
/// Opens and refreshes the trust window of a peer (DESIGN 4.4): the unauthenticated identity check against the pinned server id and the
/// authenticated check that the token is still accepted. <see cref="PeerClient"/> sends no other token-bearing request unless these
/// succeeded within <see cref="PeerRegistry.TrustWindow"/>. The server id comes from the anonymous <c>/System/Info/Public</c>, so anyone
/// can replay it: the pin is accident detection (a reset server, a reused name), and the network layer authenticates the peer.
/// </summary>
public sealed class PeerTrust
{
    /// <summary>The shortest time between two on-demand refreshes of one peer.</summary>
    public static readonly TimeSpan OnDemandInterval = TimeSpan.FromSeconds(10);

    private readonly PeerClient _client;
    private readonly PeerRegistry _registry;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastAttempt = new(StringComparer.Ordinal);

    /// <summary>
    /// Initializes a new instance of the <see cref="PeerTrust"/> class.
    /// </summary>
    /// <param name="client">The peer client.</param>
    /// <param name="registry">The runtime table.</param>
    /// <param name="time">The clock.</param>
    public PeerTrust(PeerClient client, PeerRegistry registry, TimeProvider time)
    {
        _client = client;
        _registry = registry;
        _time = time;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="PeerTrust"/> class.
    /// </summary>
    /// <param name="client">The peer client.</param>
    /// <param name="registry">The runtime table.</param>
    public PeerTrust(PeerClient client, PeerRegistry registry)
        : this(client, registry, TimeProvider.System)
    {
    }

    /// <summary>
    /// Asks the peer for its server id without a token and compares it with the pinned one.
    /// </summary>
    /// <param name="peer">The peer.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The outcome.</returns>
    public async Task<PinOutcome> VerifyPinAsync(PeerEndpoint peer, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(peer);
        if (!TryNormalizeServerId(peer.PinnedServerId, out var pinned))
        {
            return PinOutcome.NotPinned;
        }

        try
        {
            using var response = await _client.SendAsync(peer, PeerRequest.GetAnonymous("/System/Info/Public"), PeerCallProfile.Info, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                return PinOutcome.Unavailable;
            }

            var info = await response.ReadJsonAsync<PublicInfo>(cancellationToken).ConfigureAwait(false);
            if (!TryNormalizeServerId(info.Id, out var actual))
            {
                return PinOutcome.Unavailable; // not a Jellyfin answer
            }

            if (!string.Equals(actual, pinned, StringComparison.Ordinal))
            {
                _registry.SetStatus(peer.PeerKey, PeerStatus.IdentityMismatch);
                return PinOutcome.Mismatch;
            }

            _registry.RecordPinVerified(peer.PeerKey);
            return PinOutcome.Verified;
        }
        catch (PeerCallException ex) when (ex.Kind == PeerFailureKind.Blocked)
        {
            return _registry.StatusOf(peer.PeerKey) == PeerStatus.IdentityMismatch ? PinOutcome.Mismatch : PinOutcome.Unavailable;
        }
        catch (PeerCallException)
        {
            return PinOutcome.Unavailable;
        }
    }

    /// <summary>
    /// Asks the peer whether it still accepts the token (<c>GET /Users/Me</c>, which is the health call and so exempt from the window).
    /// </summary>
    /// <param name="peer">The peer.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The outcome.</returns>
    public async Task<AuthOutcome> VerifyAuthAsync(PeerEndpoint peer, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(peer);
        try
        {
            using var response = await _client.SendAsync(peer, PeerRequest.Get("/Users/Me").WithTrust(TrustRequirement.None), PeerCallProfile.Info, cancellationToken).ConfigureAwait(false);
            switch (response.StatusCode)
            {
                case HttpStatusCode.OK:
                    _registry.RecordAuthVerified(peer.PeerKey);
                    return AuthOutcome.Verified;
                case HttpStatusCode.Unauthorized:
                case HttpStatusCode.Forbidden:
                    if (_registry.IsRotating(peer.PeerKey))
                    {
                        return AuthOutcome.Unavailable; // a 401 while the token is being replaced is not a verdict (DESIGN v2.6.1)
                    }

                    _registry.SetStatus(peer.PeerKey, PeerStatus.AuthFailed);
                    return AuthOutcome.Refused;
                default:
                    return AuthOutcome.Unavailable;
            }
        }
        catch (PeerCallException)
        {
            return AuthOutcome.Unavailable;
        }
    }

    /// <summary>
    /// Makes sure the trust window covers <paramref name="requirement"/>, refreshing it when needed. A peer is refreshed at most once per
    /// <see cref="OnDemandInterval"/> this way, so a flood of requests cannot turn into a flood of identity checks.
    /// </summary>
    /// <param name="peer">The peer.</param>
    /// <param name="requirement">What the caller is about to need.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>True when the call may be sent.</returns>
    public async Task<bool> EnsureAsync(PeerEndpoint peer, TrustRequirement requirement, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(peer);
        if (_registry.Check(peer.PeerKey, carriesToken: true, requirement) == TrustDenial.None)
        {
            return true;
        }

        var now = _time.GetUtcNow();
        if (_lastAttempt.TryGetValue(peer.PeerKey, out var last) && now - last < OnDemandInterval)
        {
            return false;
        }

        _lastAttempt[peer.PeerKey] = now;
        if (_registry.Check(peer.PeerKey, carriesToken: true, TrustRequirement.PinFresh) != TrustDenial.None)
        {
            if (await VerifyPinAsync(peer, cancellationToken).ConfigureAwait(false) != PinOutcome.Verified)
            {
                return false;
            }
        }

        if (requirement == TrustRequirement.PinAndAuthFresh && _registry.Check(peer.PeerKey, carriesToken: true, requirement) == TrustDenial.AuthStale)
        {
            if (await VerifyAuthAsync(peer, cancellationToken).ConfigureAwait(false) != AuthOutcome.Verified)
            {
                return false;
            }
        }

        return _registry.Check(peer.PeerKey, carriesToken: true, requirement) == TrustDenial.None;
    }

    /// <summary>
    /// Brings a server id to its canonical form: 32 lower-case hex characters. A GUID with dashes is accepted.
    /// </summary>
    /// <param name="value">The id as text.</param>
    /// <param name="normalized">The canonical form when this returns true.</param>
    /// <returns>True when <paramref name="value"/> is a GUID.</returns>
    public static bool TryNormalizeServerId(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (value is null || value.Length is not (32 or 36) || !Guid.TryParse(value, out var guid))
        {
            return false;
        }

        normalized = guid.ToString("N");
        return true;
    }

    private sealed class PublicInfo
    {
        public string? Id { get; set; }
    }
}
