using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Jellyfin.Plugin.Shoal.Sharing;

/// <summary>
/// The pieces of the invite protocol both sides compute (DESIGN 4.1b "POST /Shoal/Pair"): the secret's text form, the nonce commitment and the
/// safety code. One implementation, so Home and the Peer cannot disagree.
/// </summary>
public static class InviteCrypto
{
    /// <summary>The Crockford base32 alphabet (no I, L, O, U).</summary>
    public const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    /// <summary>The length of an invite secret in characters (128 bits).</summary>
    public const int SecretLength = 26;

    /// <summary>
    /// Makes a new secret from 16 random bytes: 26 Crockford characters.
    /// </summary>
    /// <returns>The normalized secret.</returns>
    public static string NewSecret() => Encode(RandomNumberGenerator.GetBytes(16), SecretLength);

    /// <summary>
    /// Writes a secret the way the admin sees it: <c>shoal1-&lt;8 hex of the server id&gt;-K7QM-2XA4-...</c>.
    /// </summary>
    /// <param name="serverId">The Peer's server id (32 hex).</param>
    /// <param name="secret">The normalized secret.</param>
    /// <returns>The invite text.</returns>
    public static string Format(string serverId, string secret) =>
        "shoal1-" + serverId[..8].ToLowerInvariant() + "-" + string.Join('-', Enumerable.Range(0, (secret.Length + 3) / 4).Select(i => secret.Substring(i * 4, Math.Min(4, secret.Length - (i * 4)))));

    /// <summary>
    /// Reads an invite the admin pasted: ignores case, spaces and hyphens, maps I and L to 1 and O to 0.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <param name="serverIdPrefix">The 8 hex characters of the server it was made for.</param>
    /// <param name="secret">The normalized secret.</param>
    /// <returns>True when it has the right shape.</returns>
    public static bool TryParse(string? text, out string serverIdPrefix, out string secret)
    {
        serverIdPrefix = string.Empty;
        secret = string.Empty;
        if (text is null || text.Length > 128)
        {
            return false;
        }

        var raw = new string([.. text.Where(static c => c is not ('-' or ' '))]).ToLowerInvariant();
        if (!raw.StartsWith("shoal1", StringComparison.Ordinal) || raw.Length != 6 + 8 + SecretLength)
        {
            return false;
        }

        var prefix = raw.Substring(6, 8);
        if (!prefix.All(static c => c is >= '0' and <= '9' or >= 'a' and <= 'f'))
        {
            return false;
        }

        var normalized = NormalizeCode(raw[14..]);
        if (normalized.Length != SecretLength || !normalized.All(static c => Alphabet.Contains(c, StringComparison.Ordinal)))
        {
            return false;
        }

        serverIdPrefix = prefix;
        secret = normalized;
        return true;
    }

    /// <summary>
    /// Normalizes a typed code or secret: capitals, no spaces or hyphens, I and L are 1, O is 0.
    /// </summary>
    /// <param name="text">What was typed.</param>
    /// <returns>The normalized text.</returns>
    public static string NormalizeCode(string text) =>
        new string([.. text.Where(static c => c is not ('-' or ' ' or '\t' or '\r' or '\n'))]).ToUpperInvariant().Replace('I', '1').Replace('L', '1').Replace('O', '0');

    /// <summary>
    /// The commitment to a nonce: base64url of SHA-256 over the nonce bytes.
    /// </summary>
    /// <param name="nonce">The nonce bytes.</param>
    /// <returns>The commitment.</returns>
    public static string Commit(byte[] nonce) => Base64Url(SHA256.HashData(nonce));

    /// <summary>
    /// The safety code: the first 40 bits of <c>SHA-256("shoal-sas-v1" ‖ secret ‖ homeNonce ‖ peerNonce ‖ homeServerId ‖ peerServerId)</c> as 8 Crockford characters.
    /// </summary>
    /// <param name="secret">The normalized secret.</param>
    /// <param name="homeNonce">Home's nonce.</param>
    /// <param name="peerNonce">The Peer's nonce.</param>
    /// <param name="homeServerId">Home's server id (32 hex, lower case).</param>
    /// <param name="peerServerId">The Peer's server id (32 hex, lower case).</param>
    /// <returns>For example <c>K7QM2XA4</c>.</returns>
    public static string Sas(string secret, byte[] homeNonce, byte[] peerNonce, string homeServerId, string peerServerId)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes("shoal-sas-v1"));
        hash.AppendData(Encoding.UTF8.GetBytes(secret));
        hash.AppendData(homeNonce);
        hash.AppendData(peerNonce);
        hash.AppendData(Encoding.UTF8.GetBytes(homeServerId.ToLowerInvariant()));
        hash.AppendData(Encoding.UTF8.GetBytes(peerServerId.ToLowerInvariant()));
        return Encode(hash.GetHashAndReset()[..5], 8);
    }

    /// <summary>Encodes bytes as unpadded base64url.</summary>
    /// <param name="bytes">The bytes.</param>
    /// <returns>The text.</returns>
    public static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>Decodes unpadded base64url.</summary>
    /// <param name="text">The text.</param>
    /// <param name="bytes">The bytes.</param>
    /// <returns>True when it is valid base64url.</returns>
    public static bool TryBase64Url(string? text, out byte[] bytes)
    {
        bytes = [];
        if (string.IsNullOrEmpty(text) || text.Length > 64 || text.Any(static c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_')))
        {
            return false;
        }

        var padded = text.Replace('-', '+').Replace('_', '/');
        padded = padded.PadRight(padded.Length + ((4 - (padded.Length % 4)) % 4), '=');
        try
        {
            bytes = Convert.FromBase64String(padded);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static string Encode(byte[] bytes, int chars)
    {
        var builder = new StringBuilder(chars);
        var buffer = 0;
        var bits = 0;
        foreach (var b in bytes)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5 && builder.Length < chars)
            {
                builder.Append(Alphabet[(buffer >> (bits - 5)) & 31]);
                bits -= 5;
            }
        }

        if (builder.Length < chars && bits > 0)
        {
            builder.Append(Alphabet[(buffer << (5 - bits)) & 31]);
        }

        return builder.ToString();
    }
}
