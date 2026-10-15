using System;
using System.Text;
using Jellyfin.Plugin.Shoal.Text;

namespace Jellyfin.Plugin.Shoal.Peers;

/// <summary>
/// Builds the one <c>Authorization</c> header Home sends to a peer (DESIGN 6.11, 4.2a "Header encoding"). Every value is
/// percent-encoded UTF-8: each byte outside <c>A-Z a-z 0-9 - . _ ~</c> becomes <c>%XX</c>, so <c>%</c> is <c>%25</c>, <c>+</c> is
/// <c>%2B</c> and a space is <c>%20</c>. Core decodes with <c>WebUtility.UrlDecode</c> (<c>AuthorizationContext.cs:276-313</c>), which
/// turns a bare <c>+</c> into a space and mangles an unescaped <c>%</c>.
/// </summary>
internal static class PeerAuthorization
{
    private const int MaxNameChars = 64;
    private const string DefaultServerName = "Jellyfin";

    /// <summary>
    /// Makes a Home server name safe to put in a device name: control and direction characters, quotes, commas and backslashes are
    /// removed and the result is cut to 64 characters.
    /// </summary>
    /// <param name="serverName">The server's friendly name.</param>
    /// <returns>A non-empty name.</returns>
    public static string SanitizeServerName(string? serverName)
    {
        var cleaned = TextClean.Display(serverName, MaxNameChars * 2).Replace("\"", string.Empty, StringComparison.Ordinal)
            .Replace(",", string.Empty, StringComparison.Ordinal)
            .Replace("\\", string.Empty, StringComparison.Ordinal);
        var cut = TextClean.Display(cleaned, MaxNameChars);
        return cut.Length == 0 ? DefaultServerName : cut;
    }

    /// <summary>
    /// The device name the friend sees in the Devices list: <c>Shoal: &lt;Home name&gt; (&lt;check&gt;)</c>, or without the check
    /// when the peer was paired by invite.
    /// </summary>
    /// <param name="serverName">The server's friendly name.</param>
    /// <param name="check">The 4-character check, or null.</param>
    /// <returns>The device name (not yet encoded).</returns>
    public static string DeviceName(string? serverName, string? check) =>
        string.IsNullOrEmpty(check) ? "Shoal: " + SanitizeServerName(serverName) : "Shoal: " + SanitizeServerName(serverName) + " (" + check + ")";

    /// <summary>
    /// Percent-encodes a header value.
    /// </summary>
    /// <param name="value">The value.</param>
    /// <returns>The encoded value.</returns>
    public static string Encode(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var b in Encoding.UTF8.GetBytes(value))
        {
            var c = (char)b;
            if (b < 0x80 && (char.IsAsciiLetterOrDigit(c) || c is '-' or '.' or '_' or '~'))
            {
                builder.Append(c);
            }
            else
            {
                builder.Append('%').Append(HexUpper(b >> 4)).Append(HexUpper(b & 0xF));
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// Builds the header value.
    /// </summary>
    /// <param name="deviceName">The device name (see <see cref="DeviceName"/>).</param>
    /// <param name="deviceId">The pairing's device id.</param>
    /// <param name="version">The plugin version.</param>
    /// <param name="token">The peer token, or null for an unauthenticated call.</param>
    /// <returns>The header value.</returns>
    public static string Build(string deviceName, string deviceId, string version, string? token)
    {
        var builder = new StringBuilder("MediaBrowser Client=\"Shoal\", Device=\"", 160);
        builder.Append(Encode(deviceName)).Append("\", DeviceId=\"").Append(Encode(deviceId)).Append("\", Version=\"").Append(Encode(version)).Append('"');
        if (!string.IsNullOrEmpty(token))
        {
            builder.Append(", Token=\"").Append(Encode(token)).Append('"');
        }

        return builder.ToString();
    }

    private static char HexUpper(int nibble) => (char)(nibble < 10 ? '0' + nibble : 'A' + nibble - 10);
}
