using System;
using System.Linq;
using Jellyfin.Plugin.Shoal.Security;
using Jellyfin.Plugin.Shoal.State;

namespace Jellyfin.Plugin.Shoal.Peers;

/// <summary>
/// Finds the endpoint (address, pins and token) of a peer.
/// </summary>
public interface IPeerEndpoints
{
    /// <summary>
    /// Looks the peer up.
    /// </summary>
    /// <param name="peerKey">The peer key.</param>
    /// <returns>The endpoint, or null when the peer is unknown, disabled or has no credential.</returns>
    PeerEndpoint? Find(string peerKey);
}

/// <summary>
/// A directory whose token can be replaced in memory before the secret file holds it (token rotation, DESIGN v2.6.1).
/// </summary>
public interface IActiveTokenSource : IPeerEndpoints
{
    /// <summary>
    /// Makes a token the active one in memory.
    /// </summary>
    /// <param name="peerKey">The peer key.</param>
    /// <param name="token">The new token; kept in memory only.</param>
    void SetActiveToken(string peerKey, string token);

    /// <summary>
    /// Drops the in-memory token once the secret store holds it (or the rotation is given up).
    /// </summary>
    /// <param name="peerKey">The peer key.</param>
    void ClearActiveToken(string peerKey);
}

/// <summary>
/// What is needed to talk to one peer. Built per call from the state and the secret store; never cached, so a removed peer or a
/// rotated token takes effect at once. The token is internal: it leaves this assembly's peer client only in the
/// <c>Authorization</c> header.
/// </summary>
public sealed class PeerEndpoint
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PeerEndpoint"/> class.
    /// </summary>
    /// <param name="peerKey">The peer key.</param>
    /// <param name="displayName">The display name.</param>
    /// <param name="baseUri">The base URL.</param>
    /// <param name="deviceId">The device id Home presents.</param>
    /// <param name="token">The peer token (empty for a call before the pairing has produced one).</param>
    /// <param name="check">The 4-character check that is part of the device name (only a server paired the old way has one), or null.</param>
    internal PeerEndpoint(string peerKey, string displayName, Uri baseUri, string deviceId, string token, string? check = null)
    {
        PeerKey = peerKey;
        DisplayName = displayName;
        BaseUri = baseUri;
        DeviceId = deviceId;
        Token = token;
        Check = check;
    }

    /// <summary>Gets the peer key.</summary>
    public string PeerKey { get; }

    /// <summary>Gets the display name.</summary>
    public string DisplayName { get; }

    /// <summary>Gets the base URL (scheme, authority and an optional base path, no trailing slash).</summary>
    public Uri BaseUri { get; }

    /// <summary>Gets the device id Home presents to the peer.</summary>
    public string DeviceId { get; }

    /// <summary>Gets the check that is part of the device name, or null (invite pairing).</summary>
    public string? Check { get; }

    /// <summary>Gets a value indicating whether this endpoint holds a token. Authenticated calls need one.</summary>
    public bool HasToken => Token.Length > 0;

    /// <summary>Gets the network type of the peer; decides which addresses are acceptable.</summary>
    public PeerNetwork Network { get; init; } = PeerNetwork.Tailnet;

    /// <summary>Gets the optional loopback SOCKS5 proxy (Tailnet peers only), or null.</summary>
    public string? ProxyUrl { get; init; }

    /// <summary>Gets the optional TLS public-key pin (base64 SHA-256 of the SubjectPublicKeyInfo), or null.</summary>
    public string? TlsSpkiPin { get; init; }

    /// <summary>Gets a value indicating whether the admin accepted plain http on the LAN.</summary>
    public bool LanCleartextAck { get; init; }

    /// <summary>Gets the server id pinned at pairing (32 hex characters), or null before the first pairing.</summary>
    public string? PinnedServerId { get; init; }

    /// <summary>Gets the peer token. Internal on purpose.</summary>
    internal string Token { get; }

    /// <inheritdoc />
    public override string ToString() => "Peer " + DisplayName;

    /// <summary>
    /// An endpoint for calls that carry no token: the unauthenticated identity check and the steps of a pairing that has not
    /// produced a token yet.
    /// </summary>
    /// <param name="peerKey">The peer key.</param>
    /// <param name="displayName">The display name.</param>
    /// <param name="baseUri">The base URL.</param>
    /// <param name="deviceId">The device id Home presents.</param>
    /// <param name="check">The check for the device name, or null.</param>
    /// <returns>The endpoint.</returns>
    internal static PeerEndpoint WithoutToken(string peerKey, string displayName, Uri baseUri, string deviceId, string? check) =>
        new(peerKey, displayName, baseUri, deviceId, string.Empty, check);

    /// <summary>
    /// Returns a copy with the network settings of a peer that is not in the directory yet (a probe, a pairing in progress).
    /// </summary>
    /// <param name="network">The network type.</param>
    /// <param name="proxyUrl">The loopback proxy, or null.</param>
    /// <param name="lanCleartextAck">Whether plain http on the LAN was acknowledged.</param>
    /// <param name="pinnedServerId">The pinned server id, or null to keep this endpoint's.</param>
    /// <param name="tlsSpkiPin">The stored certificate pin of a re-paired peer, or null to keep this endpoint's.</param>
    /// <returns>The copy.</returns>
    internal PeerEndpoint Configured(PeerNetwork network, string? proxyUrl, bool lanCleartextAck, string? pinnedServerId = null, string? tlsSpkiPin = null) =>
        new(PeerKey, DisplayName, BaseUri, DeviceId, Token, Check)
        {
            Network = network,
            ProxyUrl = proxyUrl,
            TlsSpkiPin = tlsSpkiPin ?? TlsSpkiPin,
            LanCleartextAck = lanCleartextAck,
            PinnedServerId = pinnedServerId ?? PinnedServerId
        };
}

/// <summary>
/// Finds the endpoint for a peer key from <see cref="IStateStore"/> and <see cref="ISecretStore"/>. Fails closed: in recovery
/// mode, with an unreadable secret store, for a disabled or unknown peer, or without a credential, there is no endpoint.
/// </summary>
public sealed class PeerDirectory : IActiveTokenSource
{
    private readonly IStateStore _state;
    private readonly ISecretStore _secrets;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (string Token, DateTime PairedUtc)> _activeTokens = new(StringComparer.Ordinal);

    /// <summary>
    /// Initializes a new instance of the <see cref="PeerDirectory"/> class.
    /// </summary>
    /// <param name="state">The state store.</param>
    /// <param name="secrets">The secret store.</param>
    public PeerDirectory(IStateStore state, ISecretStore secrets)
    {
        _state = state;
        _secrets = secrets;
    }

    /// <summary>
    /// Finds an enabled, usable peer.
    /// </summary>
    /// <param name="peerKey">The peer key (the first part of a stub's address).</param>
    /// <returns>The endpoint, or null.</returns>
    public PeerEndpoint? Find(string peerKey)
    {
        if (_state.Status != StateStatus.Ready || _secrets.Status != SecretStoreStatus.Ready)
        {
            return null;
        }

        var peer = _state.Current.Home.Peers.FirstOrDefault(p => p.Enabled && string.Equals(p.PeerKey, peerKey, StringComparison.Ordinal));
        var credential = peer is null ? null : _secrets.TryGet(peer.PeerKey);
        if (peer is null || credential is null)
        {
            return null;
        }

        // The address is checked again from its text on every lookup: a state.json that was edited by hand, or restored from an old copy,
        // cannot point the token at a loopback, metadata or wrong-network address. Anything doubtful means "no endpoint".
        var check = AddressPolicy.Validate(peer.BaseUrl, peer.Network, peer.LanCleartextAck, peer.ProxyUrl);
        if (!check.Ok || (peer.TlsSpkiPin is not null && !TlsPin.IsValid(peer.TlsSpkiPin)))
        {
            return null;
        }

        // The in-memory token of a rotation belongs to the pairing it was made for. A pairing, a re-pair or a removal writes a credential with another
        // PairedUtc, and then the override is dropped: it must not outlive the pairing and shadow the fresh token (Warden 10 L4).
        var token = credential.Token;
        if (_activeTokens.TryGetValue(peer.PeerKey, out var active))
        {
            if (active.PairedUtc == credential.PairedUtc)
            {
                token = active.Token;
            }
            else
            {
                _activeTokens.TryRemove(peer.PeerKey, out _);
            }
        }

        return new PeerEndpoint(peer.PeerKey, peer.DisplayName, check.Address!.BaseUri, credential.DeviceId, token)
        {
            Network = peer.Network,
            ProxyUrl = peer.ProxyUrl,
            TlsSpkiPin = peer.TlsSpkiPin,
            LanCleartextAck = peer.LanCleartextAck,
            PinnedServerId = peer.PinnedServerId
        };
    }

    /// <inheritdoc />
    public void SetActiveToken(string peerKey, string token) => _activeTokens[peerKey] = (token, _secrets.TryGet(peerKey)?.PairedUtc ?? default);

    /// <inheritdoc />
    public void ClearActiveToken(string peerKey) => _activeTokens.TryRemove(peerKey, out _);
}
