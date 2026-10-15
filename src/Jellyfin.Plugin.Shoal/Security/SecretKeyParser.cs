using System;
using System.Buffers.Text;
using System.Security.Cryptography;

namespace Jellyfin.Plugin.Shoal.Security;

/// <summary>
/// Reads the master key a person supplies in <c>JELLYFIN_SHOAL_KEY</c> or in the file named by <c>JELLYFIN_SHOAL_KEY_FILE</c>
/// (DESIGN 8.2, Warden N-7.1). HKDF is not a password hash, so a human-chosen string would be a brute-forceable key: the value
/// must be exactly 32 random bytes, written as base64url (43 characters, one trailing <c>=</c> allowed) or hex (64 characters,
/// either case). Nothing else is accepted, and 32 identical bytes (an obvious placeholder such as all zeros) are refused too.
/// </summary>
internal static class SecretKeyParser
{
    /// <summary>The key length in bytes.</summary>
    public const int KeyBytes = 32;

    /// <summary>A message that tells the person how to make a good key (never contains the rejected value).</summary>
    public const string HowToMakeAKey = "The key must be exactly 32 random bytes as 64 hex characters or 43 base64url characters; make one with: openssl rand -hex 32";

    /// <summary>
    /// Parses a key.
    /// </summary>
    /// <param name="value">The text, without a trailing line break.</param>
    /// <param name="key">The 32 key bytes when this returns true.</param>
    /// <returns>True when <paramref name="value"/> is a valid key.</returns>
    public static bool TryParse(string? value, out byte[] key)
    {
        key = [];
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        byte[]? decoded = null;
        if (value.Length == 64)
        {
            decoded = TryDecodeHex(value);
        }
        else if (value.Length is 43 or 44)
        {
            decoded = TryDecodeBase64Url(value);
        }

        if (decoded is null || decoded.Length != KeyBytes || IsPlaceholder(decoded))
        {
            if (decoded is not null)
            {
                CryptographicOperations.ZeroMemory(decoded);
            }

            return false;
        }

        key = decoded;
        return true;
    }

    private static byte[]? TryDecodeHex(string value)
    {
        foreach (var c in value)
        {
            if (!char.IsAsciiHexDigit(c))
            {
                return null;
            }
        }

        return Convert.FromHexString(value);
    }

    private static byte[]? TryDecodeBase64Url(string value)
    {
        var text = value;
        if (text.Length == 44)
        {
            if (text[43] != '=')
            {
                return null;
            }

            text = text[..43];
        }

        foreach (var c in text)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
            {
                return null;
            }
        }

        var buffer = new byte[KeyBytes];
        if (!Base64Url.TryDecodeFromChars(text, buffer, out var written) || written != KeyBytes)
        {
            return null;
        }

        // Canonical form only: 43 characters carry 258 bits for 256, and the two spare bits must be zero.
        return string.Equals(Base64Url.EncodeToString(buffer), text, StringComparison.Ordinal) ? buffer : null;
    }

    private static bool IsPlaceholder(byte[] bytes)
    {
        for (var i = 1; i < bytes.Length; i++)
        {
            if (bytes[i] != bytes[0])
            {
                return false;
            }
        }

        return true;
    }
}
