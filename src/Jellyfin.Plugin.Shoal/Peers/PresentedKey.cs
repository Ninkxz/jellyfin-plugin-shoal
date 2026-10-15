namespace Jellyfin.Plugin.Shoal.Peers;

/// <summary>
/// The public key a server presented on a connection that accepted any certificate (see <see cref="PeerClient.ReadPresentedKeyAsync"/>).
/// </summary>
/// <param name="Pin">The pin: base64 of the SHA-256 of the SubjectPublicKeyInfo.</param>
/// <param name="ChainValid">Whether the certificate also passes the normal chain and host name checks.</param>
public sealed record PresentedKey(string Pin, bool ChainValid);
