using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.Pairing;
using Jellyfin.Plugin.Shoal.Security;
using Jellyfin.Plugin.Shoal.State;

namespace Jellyfin.Plugin.Shoal.Peers;

/// <summary>
/// Why a peer address change was refused.
/// </summary>
public enum PeerAddressChangeProblem
{
    /// <summary>No peer has that key.</summary>
    UnknownPeer,

    /// <summary>The new address is not an absolute http or https URL.</summary>
    InvalidAddress,

    /// <summary>The host changes and the secret store cannot be used, so the old token cannot be dropped first (DESIGN 4.4, Warden N-8).</summary>
    SecretsUnreadable,

    /// <summary>A pairing session is live for this peer; its address, network type and proxy cannot change meanwhile (DESIGN 4.2a).</summary>
    PairingActive
}

/// <summary>
/// A peer address change was refused. The message is a fixed text; it never contains the address.
/// </summary>
public sealed class PeerAddressChangeException : InvalidOperationException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PeerAddressChangeException"/> class.
    /// </summary>
    public PeerAddressChangeException()
        : this(PeerAddressChangeProblem.InvalidAddress)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="PeerAddressChangeException"/> class.
    /// </summary>
    /// <param name="message">The message.</param>
    public PeerAddressChangeException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="PeerAddressChangeException"/> class.
    /// </summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The inner exception.</param>
    public PeerAddressChangeException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="PeerAddressChangeException"/> class.
    /// </summary>
    /// <param name="problem">Why the change was refused.</param>
    /// <param name="detail">What the address policy found wrong.</param>
    public PeerAddressChangeException(PeerAddressChangeProblem problem, AddressProblem detail)
        : base("The peer address was not changed (" + problem + ", " + detail + ").")
    {
        Problem = problem;
        Detail = detail;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="PeerAddressChangeException"/> class.
    /// </summary>
    /// <param name="problem">Why the change was refused.</param>
    public PeerAddressChangeException(PeerAddressChangeProblem problem)
        : base("The peer address was not changed (" + problem + ").")
    {
        Problem = problem;
    }

    /// <summary>Gets why the change was refused.</summary>
    public PeerAddressChangeProblem Problem { get; }

    /// <summary>Gets what the address policy found wrong, when <see cref="Problem"/> is <see cref="PeerAddressChangeProblem.InvalidAddress"/>.</summary>
    public AddressProblem Detail { get; }
}

/// <summary>
/// The one way a peer's address is changed (DESIGN 4.4, Warden N-8). A new <b>host</b> drops the stored token <b>first</b>: the secret
/// entry is deleted, and only then is the new address written to <c>state.json</c>, so no request carrying the old token can ever be
/// sent to the new host (an admin mis-click, a phished admin, XSS in the admin page). A scheme, port or path change keeps the token.
/// While the secret store is unreadable a host change is refused outright: the old token could not be dropped.
/// </summary>
public sealed class PeerAddressService
{
    private readonly IStateStore _state;
    private readonly ISecretStore _secrets;
    private readonly PeerRegistry? _registry;
    private readonly IPairingGate? _gate;

    /// <summary>
    /// Initializes a new instance of the <see cref="PeerAddressService"/> class.
    /// </summary>
    /// <param name="state">The state store.</param>
    /// <param name="secrets">The secret store.</param>
    /// <param name="registry">The runtime table; a host change puts the peer in <see cref="PeerStatus.AuthFailed"/> (re-pair), or null.</param>
    /// <param name="gate">Tells whether a pairing is live for a peer (then nothing about its address may change), or null.</param>
    public PeerAddressService(IStateStore state, ISecretStore secrets, PeerRegistry? registry = null, IPairingGate? gate = null)
    {
        _state = state;
        _secrets = secrets;
        _registry = registry;
        _gate = gate;
    }

    /// <summary>
    /// Whether two addresses name the same host (case-insensitive, punycode, normalized IP forms). Scheme, port and path are not compared.
    /// </summary>
    /// <param name="first">The first address.</param>
    /// <param name="second">The second address.</param>
    /// <returns>True when the hosts are the same; false when either is not an absolute URL.</returns>
    public static bool SameHost(string? first, string? second) =>
        Uri.TryCreate(first, UriKind.Absolute, out var a)
        && Uri.TryCreate(second, UriKind.Absolute, out var b)
        && string.Equals(a.IdnHost, b.IdnHost, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Changes the base URL of a peer.
    /// </summary>
    /// <param name="peerKey">The peer.</param>
    /// <param name="newBaseUrl">The new base URL; it is checked by <see cref="AddressPolicy"/> for the peer's network type and stored in its normalized form.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>True when the host changed and the stored token was dropped (the peer needs a new pairing).</returns>
    /// <exception cref="PeerAddressChangeException">The change was refused; nothing was changed.</exception>
    public Task<bool> ChangeBaseUrlAsync(string peerKey, string newBaseUrl, CancellationToken cancellationToken = default) =>
        ChangeAddressAsync(peerKey, newBaseUrl, null, useProxy: false, alsoApply: null, cancellationToken);

    /// <summary>
    /// Changes the base URL and the proxy of a peer, and whatever else the caller edits at the same time, in <b>one</b> state write (Warden 11 L4):
    /// either all of it happens or none of it. A new host still drops the stored token first, before that write.
    /// </summary>
    /// <param name="peerKey">The peer.</param>
    /// <param name="newBaseUrl">The new base URL; it is checked by <see cref="AddressPolicy"/> for the peer's network type together with the proxy.</param>
    /// <param name="proxyUrl">The proxy to store, or null for none; used only when <paramref name="useProxy"/> is true.</param>
    /// <param name="useProxy">Whether <paramref name="proxyUrl"/> replaces the stored proxy.</param>
    /// <param name="alsoApply">More edits to the peer record, applied in the same write after the address (and after a new host cleared the pin), or null.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>True when the host changed and the stored token was dropped (the peer needs a new pairing).</returns>
    /// <exception cref="PeerAddressChangeException">The change was refused; nothing was changed.</exception>
    public async Task<bool> ChangeAddressAsync(string peerKey, string newBaseUrl, string? proxyUrl, bool useProxy, Action<PeerState>? alsoApply, CancellationToken cancellationToken = default)
    {
        var peer = _state.Current.Home.Peers.FirstOrDefault(p => string.Equals(p.PeerKey, peerKey, StringComparison.Ordinal))
            ?? throw new PeerAddressChangeException(PeerAddressChangeProblem.UnknownPeer);
        if (_gate?.IsLive(peerKey) == true)
        {
            throw new PeerAddressChangeException(PeerAddressChangeProblem.PairingActive);
        }

        var proxy = useProxy ? proxyUrl : peer.ProxyUrl;
        var check = AddressPolicy.Validate(newBaseUrl, peer.Network, peer.LanCleartextAck, proxy);
        if (!check.Ok)
        {
            throw new PeerAddressChangeException(PeerAddressChangeProblem.InvalidAddress, check.Problem);
        }

        var normalized = check.Address!.BaseUri.AbsoluteUri.TrimEnd('/');
        var hostChanged = !SameHost(peer.BaseUrl, normalized);
        if (hostChanged)
        {
            if (_secrets.Status != SecretStoreStatus.Ready)
            {
                throw new PeerAddressChangeException(PeerAddressChangeProblem.SecretsUnreadable);
            }

            // The token goes first. If the process dies between the two steps, the peer still has the old address and no token: AuthFailed, re-pair.
            await _secrets.RemoveAsync(peerKey, cancellationToken).ConfigureAwait(false);
        }

        await _state.UpdateAsync(
            state =>
            {
                var target = state.Home.Peers.First(p => string.Equals(p.PeerKey, peerKey, StringComparison.Ordinal));
                target.BaseUrl = normalized;
                if (useProxy)
                {
                    target.ProxyUrl = proxy;
                }

                if (hostChanged)
                {
                    target.TlsSpkiPin = null;
                }

                alsoApply?.Invoke(target);
            },
            cancellationToken).ConfigureAwait(false);
        if (hostChanged)
        {
            _registry?.SetStatus(peerKey, PeerStatus.AuthFailed);
        }

        return hostChanged;
    }
}
