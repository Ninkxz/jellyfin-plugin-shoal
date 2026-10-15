using System;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Jellyfin.Plugin.Shoal.Peers;

/// <summary>
/// The optional TLS public-key pin of a peer (DESIGN 4.4, 8.3 T13): the base64 of the SHA-256 of the certificate's
/// SubjectPublicKeyInfo. It is opt-in because ACME clients usually make a new key at every renewal; a pinned peer whose key changes goes
/// to <see cref="PeerStatus.TlsKeyChanged"/> and stays there until the admin chooses "Re-trust". A pin also lets a self-signed
/// certificate be trusted: with a pin the key is the trust anchor and chain errors are ignored.
/// </summary>
public static class TlsPin
{
    private const int HashBytes = 32;

    /// <summary>
    /// Computes the pin of a certificate.
    /// </summary>
    /// <param name="certificate">The certificate.</param>
    /// <returns>The pin (44 characters of base64).</returns>
    public static string Compute(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        return Convert.ToBase64String(SHA256.HashData(certificate.PublicKey.ExportSubjectPublicKeyInfo()));
    }

    /// <summary>
    /// Whether <paramref name="value"/> is a well-formed pin: base64 of exactly 32 bytes.
    /// </summary>
    /// <param name="value">The candidate.</param>
    /// <returns>True when it is.</returns>
    public static bool IsValid(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length != 44)
        {
            return false;
        }

        Span<byte> bytes = stackalloc byte[HashBytes + 3];
        return Convert.TryFromBase64String(value, bytes, out var written) && written == HashBytes && Convert.ToBase64String(bytes[..written]) == value;
    }

    /// <summary>
    /// Compares a certificate with a stored pin in constant time.
    /// </summary>
    /// <param name="certificate">The certificate the server presented.</param>
    /// <param name="pin">The stored pin.</param>
    /// <returns>True when the certificate's key is the pinned one.</returns>
    public static bool Matches(X509Certificate2 certificate, string pin)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        if (!IsValid(pin))
        {
            return false;
        }

        var actual = SHA256.HashData(certificate.PublicKey.ExportSubjectPublicKeyInfo());
        return CryptographicOperations.FixedTimeEquals(actual, Convert.FromBase64String(pin));
    }
}
