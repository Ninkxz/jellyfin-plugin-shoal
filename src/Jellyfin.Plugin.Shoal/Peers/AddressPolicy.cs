using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.Shoal.State;

namespace Jellyfin.Plugin.Shoal.Peers;

/// <summary>
/// What kind of address an IP is (DESIGN 8.3 T11).
/// </summary>
public enum AddressClass
{
    /// <summary>Never acceptable: loopback, link-local, metadata services, multicast, reserved, unspecified.</summary>
    Blocked,

    /// <summary>A Tailscale address: <c>100.64.0.0/10</c> or <c>fd7a:115c:a1e0::/48</c>.</summary>
    Tailnet,

    /// <summary>A private network address: RFC1918 or <c>fc00::/7</c>.</summary>
    Lan,

    /// <summary>A global unicast address.</summary>
    Public
}

/// <summary>
/// Why an address was refused. The values are fixed codes for the admin UI; they never carry the address.
/// </summary>
public enum AddressProblem
{
    /// <summary>Nothing is wrong.</summary>
    None,

    /// <summary>Empty, too long, or not an http(s) URL with an authority.</summary>
    NotAUrl,

    /// <summary>The scheme is not http or https.</summary>
    BadScheme,

    /// <summary>The URL has user info (<c>user:password@</c>).</summary>
    UserInfo,

    /// <summary>The URL has a query string.</summary>
    Query,

    /// <summary>The URL has a fragment.</summary>
    Fragment,

    /// <summary>The URL contains a backslash, a space or a control character.</summary>
    IllegalCharacter,

    /// <summary>The host is not a valid DNS name, IPv4 address or IPv6 address (for example a non-punycode internationalized name).</summary>
    BadHost,

    /// <summary>The host is an IPv4 address in decimal, octal, hex or short form (only canonical dotted decimal is accepted).</summary>
    NonCanonicalIp,

    /// <summary>The port is not 1 to 65535.</summary>
    BadPort,

    /// <summary>The path has an empty, dot or encoded-separator segment, or is too long.</summary>
    BadPath,

    /// <summary>The address is in a range that is never acceptable (loopback, link-local, metadata, multicast, reserved).</summary>
    Blocked,

    /// <summary>The address does not belong to the chosen network type.</summary>
    WrongNetwork,

    /// <summary>A public server must be reached over https.</summary>
    HttpsRequired,

    /// <summary>Plain http on a LAN needs the admin's acknowledgement.</summary>
    CleartextNotAcknowledged,

    /// <summary>A proxy is only allowed for Tailnet servers.</summary>
    ProxyNotAllowed,

    /// <summary>The proxy URL is not a loopback <c>socks5://</c> URL.</summary>
    ProxyInvalid,

    /// <summary>A name resolved to an address that is not acceptable for the network type (checked at connect time).</summary>
    ResolvedAddressRefused
}

/// <summary>
/// A peer address that passed <see cref="AddressPolicy.Validate"/>.
/// </summary>
/// <param name="BaseUri">The normalized base URL: lower-case scheme and host, no default port, no trailing slash.</param>
/// <param name="Host">The normalized host (IPv6 without brackets).</param>
/// <param name="Network">The network type it was validated for.</param>
/// <param name="HostIsLiteral">True when the host is an IP address rather than a name.</param>
/// <param name="IsCleartext">True when the scheme is http.</param>
/// <param name="Proxy">The loopback SOCKS5 proxy (Tailnet only), or null for a direct connection.</param>
public sealed record PeerAddress(Uri BaseUri, string Host, PeerNetwork Network, bool HostIsLiteral, bool IsCleartext, Uri? Proxy);

/// <summary>
/// The outcome of <see cref="AddressPolicy.Validate"/>.
/// </summary>
/// <param name="Problem">What is wrong, or <see cref="AddressProblem.None"/>.</param>
/// <param name="Address">The normalized address when nothing is wrong, otherwise null.</param>
public sealed record AddressCheck(AddressProblem Problem, PeerAddress? Address)
{
    /// <summary>Gets a value indicating whether the address is acceptable.</summary>
    public bool Ok => Problem == AddressProblem.None;
}

/// <summary>
/// The SSRF guard for admin-entered peer URLs and for the addresses they resolve to (DESIGN 2 #8, 8.3 T11, Warden A-8).
/// <para>
/// <see cref="Validate"/> works on the <b>text</b> of the URL, before any URL class normalizes it, because the .NET <c>Uri</c> quietly
/// turns <c>http://2130706433/</c> and <c>http://0x7f.1/</c> into <c>127.0.0.1</c>: only canonical dotted-decimal IPv4 is accepted, no
/// user info, query, fragment, backslash or non-punycode name. <see cref="Classify"/> maps an IP to a class after unwrapping IPv4-mapped,
/// NAT64, 6to4 and Teredo forms. <see cref="CheckResolved"/> runs at connect time for every address a name resolved to, so DNS
/// rebinding cannot swap a blocked address in after validation.
/// </para>
/// </summary>
public static partial class AddressPolicy
{
    private const int MaxUrlChars = 512;
    private const int MaxPathChars = 200;
    private const int MaxHostChars = 253;

    /// <summary>
    /// Validates the URL (and proxy) the admin typed.
    /// </summary>
    /// <param name="url">The base URL of the peer.</param>
    /// <param name="network">The network type the admin chose.</param>
    /// <param name="lanCleartextAck">Whether the admin acknowledged that the token travels unencrypted on the LAN.</param>
    /// <param name="proxyUrl">The optional SOCKS5 proxy, or null.</param>
    /// <returns>The result.</returns>
    public static AddressCheck Validate(string? url, PeerNetwork network, bool lanCleartextAck, string? proxyUrl = null)
    {
        if (string.IsNullOrEmpty(url) || url.Length > MaxUrlChars)
        {
            return Fail(AddressProblem.NotAUrl);
        }

        foreach (var c in url)
        {
            if (c == '\\' || c <= ' ' || c == 0x7f)
            {
                return Fail(AddressProblem.IllegalCharacter);
            }

            if (c > '~')
            {
                return Fail(AddressProblem.BadHost); // non-ASCII anywhere: names must already be punycode, and the path is ASCII
            }
        }

        var match = UrlShape().Match(url);
        if (!match.Success)
        {
            return DiagnoseShape(url);
        }

        var scheme = match.Groups["scheme"].Value.ToLowerInvariant();
        var authority = match.Groups["authority"].Value;
        var path = match.Groups["path"].Value;
        if (scheme is not ("http" or "https"))
        {
            return Fail(AddressProblem.BadScheme);
        }

        var parsed = ParseAuthority(authority, scheme);
        if (parsed.Problem != AddressProblem.None)
        {
            return Fail(parsed.Problem);
        }

        var pathProblem = NormalizePath(path, out var normalizedPath);
        if (pathProblem != AddressProblem.None)
        {
            return Fail(pathProblem);
        }

        var classProblem = CheckClass(parsed, network, scheme, lanCleartextAck);
        if (classProblem != AddressProblem.None)
        {
            return Fail(classProblem);
        }

        Uri? proxy = null;
        if (!string.IsNullOrEmpty(proxyUrl))
        {
            if (network != PeerNetwork.Tailnet)
            {
                return Fail(AddressProblem.ProxyNotAllowed);
            }

            // Through a proxy the name is resolved at the proxy, so the rebinding check cannot run here: only Tailnet IP literals and
            // *.ts.net names (which Tailscale resolves) are accepted.
            if (!TryParseProxy(proxyUrl, out proxy))
            {
                return Fail(AddressProblem.ProxyInvalid);
            }
        }

        var hostText = parsed.Ip is { AddressFamily: AddressFamily.InterNetworkV6 } ? "[" + parsed.Host + "]" : parsed.Host;
        var port = parsed.Port is { } p && !IsDefaultPort(scheme, p) ? ":" + p.ToString(CultureInfo.InvariantCulture) : string.Empty;
        var baseUri = new Uri(scheme + "://" + hostText + port + normalizedPath, UriKind.Absolute);
        return new AddressCheck(AddressProblem.None, new PeerAddress(baseUri, parsed.Host, network, parsed.Ip is not null, scheme == "http", proxy));
    }

    /// <summary>
    /// Whether a proxy URL is acceptable: <c>socks5://</c> (or <c>socks5h://</c>) on a loopback address with a port, nothing else.
    /// </summary>
    /// <param name="proxyUrl">The proxy URL.</param>
    /// <param name="proxy">The normalized URL when this returns true.</param>
    /// <returns>True when the URL is acceptable.</returns>
    public static bool TryParseProxy(string? proxyUrl, out Uri? proxy)
    {
        proxy = null;
        var match = string.IsNullOrEmpty(proxyUrl) || proxyUrl.Length > 100 ? null : ProxyShape().Match(proxyUrl);
        if (match is not { Success: true })
        {
            return false;
        }

        var host = match.Groups["host"].Value;
        if (!int.TryParse(match.Groups["port"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var port) || port is < 1 or > 65535)
        {
            return false;
        }

        var bracketless = host.Trim('[', ']');
        var loopback = string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
            || (IPAddress.TryParse(bracketless, out var ip) && IPAddress.IsLoopback(ip) && (ip.AddressFamily == AddressFamily.InterNetworkV6 || bracketless == ip.ToString()));
        if (!loopback)
        {
            return false;
        }

        proxy = new Uri("socks5://" + host.ToLowerInvariant() + ":" + port.ToString(CultureInfo.InvariantCulture), UriKind.Absolute);
        return true;
    }

    /// <summary>
    /// Classifies an IP address (DESIGN 8.3 T11). IPv4-mapped, IPv4-compatible, NAT64 (<c>64:ff9b::/96</c>), 6to4 (<c>2002::/16</c>) and
    /// Teredo (<c>2001::/32</c>) addresses are classified by the IPv4 address they carry.
    /// </summary>
    /// <param name="address">The address.</param>
    /// <returns>Its class.</returns>
    public static AddressClass Classify(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return ClassifyV4(address.GetAddressBytes());
        }

        if (address.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return AddressClass.Blocked;
        }

        var b = address.GetAddressBytes();
        if (address.IsIPv4MappedToIPv6)
        {
            return ClassifyV4(b[12..16]);
        }

        // ::/96 carries an IPv4 address (deprecated "compatible" form); :: and ::1 fall out here as unspecified and loopback.
        if (IsPrefixZero(b, 12))
        {
            var embedded = b[12..16];
            return embedded is [0, 0, 0, 0 or 1] ? AddressClass.Blocked : ClassifyV4(embedded);
        }

        // NAT64 64:ff9b::/96
        if (b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xff && b[3] == 0x9b && IsRangeZero(b, 4, 12))
        {
            return ClassifyV4(b[12..16]);
        }

        // 6to4 2002::/16 carries the IPv4 address in bits 16-48.
        if (b[0] == 0x20 && b[1] == 0x02)
        {
            return ClassifyV4(b[2..6]);
        }

        // Teredo 2001:0::/32: bits 32-64 are the server's IPv4 address, bits 96-128 the client's, inverted. Both must be acceptable.
        if (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0 && b[3] == 0)
        {
            var server = ClassifyV4(b[4..8]);
            var client = ClassifyV4([(byte)~b[12], (byte)~b[13], (byte)~b[14], (byte)~b[15]]);
            return server == AddressClass.Public && client == AddressClass.Public ? AddressClass.Public : AddressClass.Blocked;
        }

        // fd7a:115c:a1e0::/48 is Tailscale's.
        if (b[0] == 0xfd && b[1] == 0x7a && b[2] == 0x11 && b[3] == 0x5c && b[4] == 0xa1 && b[5] == 0xe0)
        {
            return AddressClass.Tailnet;
        }

        // fd00:ec2::254 (AWS metadata, IPv6)
        if (b[0] == 0xfd && b[1] == 0x00 && b[2] == 0x0e && b[3] == 0xc2 && IsRangeZero(b, 4, 14) && b[14] == 0x02 && b[15] == 0x54)
        {
            return AddressClass.Blocked;
        }

        if ((b[0] & 0xfe) == 0xfc)
        {
            return AddressClass.Lan; // fc00::/7
        }

        if (b[0] == 0xff || (b[0] == 0xfe && (b[1] & 0xc0) is 0x80 or 0xc0))
        {
            return AddressClass.Blocked; // multicast, link-local fe80::/10, site-local fec0::/10
        }

        if (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x0d && b[3] == 0xb8)
        {
            return AddressClass.Blocked; // documentation 2001:db8::/32
        }

        return (b[0] & 0xe0) == 0x20 ? AddressClass.Public : AddressClass.Blocked; // 2000::/3 is global unicast; the rest is reserved
    }

    /// <summary>
    /// Checks every address a name resolved to, at connect time. One unacceptable address refuses the whole set, so a name that answers
    /// with a mix of good and bad addresses (DNS rebinding) is never used.
    /// </summary>
    /// <param name="network">The network type of the peer.</param>
    /// <param name="addresses">The resolved addresses.</param>
    /// <returns>True when every address is acceptable for <paramref name="network"/>.</returns>
    public static bool CheckResolved(PeerNetwork network, IReadOnlyCollection<IPAddress> addresses)
    {
        ArgumentNullException.ThrowIfNull(addresses);
        if (addresses.Count == 0)
        {
            return false;
        }

        foreach (var address in addresses)
        {
            if (!Matches(Classify(address), network))
            {
                return false;
            }
        }

        return true;
    }

    private static bool Matches(AddressClass cls, PeerNetwork network) => (cls, network) switch
    {
        (AddressClass.Tailnet, PeerNetwork.Tailnet) => true,
        (AddressClass.Lan, PeerNetwork.Lan) => true,
        (AddressClass.Public, PeerNetwork.Public) => true,
        _ => false
    };

    private static AddressCheck Fail(AddressProblem problem) => new(problem, null);

    private static AddressCheck DiagnoseShape(string url)
    {
        var schemeEnd = url.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd <= 0 || !SchemeShape().IsMatch(url[..schemeEnd]))
        {
            return Fail(AddressProblem.NotAUrl);
        }

        var rest = url[(schemeEnd + 3)..];
        var authorityEnd = rest.IndexOfAny(['/', '?', '#']);
        var authority = authorityEnd < 0 ? rest : rest[..authorityEnd];
        if (authority.Contains('@', StringComparison.Ordinal))
        {
            return Fail(AddressProblem.UserInfo);
        }

        if (url.Contains('#', StringComparison.Ordinal))
        {
            return Fail(AddressProblem.Fragment);
        }

        return url.Contains('?', StringComparison.Ordinal) ? Fail(AddressProblem.Query) : Fail(AddressProblem.NotAUrl);
    }

    private static AddressProblem CheckClass(ParsedAuthority parsed, PeerNetwork network, string scheme, bool lanCleartextAck)
    {
        if (parsed.Ip is not null)
        {
            var cls = Classify(parsed.Ip);
            if (cls == AddressClass.Blocked)
            {
                return AddressProblem.Blocked;
            }

            if (!Matches(cls, network))
            {
                return AddressProblem.WrongNetwork;
            }
        }
        else
        {
            var name = parsed.Host;
            if (name == "localhost" || name.EndsWith(".localhost", StringComparison.Ordinal))
            {
                return AddressProblem.Blocked;
            }

            var isTailscaleName = name.EndsWith(".ts.net", StringComparison.Ordinal) && name.Length > ".ts.net".Length;
            switch (network)
            {
                case PeerNetwork.Tailnet when !isTailscaleName:
                    return AddressProblem.WrongNetwork; // a Tailnet peer is a *.ts.net name or a Tailscale IP
                case PeerNetwork.Lan when isTailscaleName:
                case PeerNetwork.Public when isTailscaleName || !name.Contains('.', StringComparison.Ordinal):
                    return AddressProblem.WrongNetwork;
                default:
                    break;
            }
        }

        if (network == PeerNetwork.Public && scheme != "https")
        {
            return AddressProblem.HttpsRequired;
        }

        return network == PeerNetwork.Lan && scheme == "http" && !lanCleartextAck ? AddressProblem.CleartextNotAcknowledged : AddressProblem.None;
    }

    private static ParsedAuthority ParseAuthority(string authority, string scheme)
    {
        string hostText;
        string? portText = null;
        if (authority.StartsWith('['))
        {
            var close = authority.IndexOf(']', StringComparison.Ordinal);
            if (close < 0)
            {
                return ParsedAuthority.Bad(AddressProblem.BadHost);
            }

            hostText = authority[1..close];
            var after = authority[(close + 1)..];
            if (after.Length > 0)
            {
                if (after[0] != ':')
                {
                    return ParsedAuthority.Bad(AddressProblem.BadHost);
                }

                portText = after[1..];
            }

            if (hostText.Contains('%', StringComparison.Ordinal) || !IPAddress.TryParse(hostText, out var v6) || v6.AddressFamily != AddressFamily.InterNetworkV6)
            {
                return ParsedAuthority.Bad(AddressProblem.BadHost);
            }

            return WithPort(new ParsedAuthority(v6.ToString(), v6, null, AddressProblem.None), portText, scheme);
        }

        var colon = authority.LastIndexOf(':');
        if (colon >= 0)
        {
            hostText = authority[..colon];
            portText = authority[(colon + 1)..];
        }
        else
        {
            hostText = authority;
        }

        if (hostText.Length == 0 || hostText.Length > MaxHostChars || hostText.Contains(':', StringComparison.Ordinal))
        {
            return ParsedAuthority.Bad(AddressProblem.BadHost);
        }

        if (hostText.EndsWith('.'))
        {
            hostText = hostText[..^1];
        }

        var lower = hostText.ToLowerInvariant();
        var labels = lower.Split('.');
        foreach (var label in labels)
        {
            if (label.Length is 0 or > 63 || !HostLabel().IsMatch(label))
            {
                return ParsedAuthority.Bad(AddressProblem.BadHost);
            }
        }

        // A host whose last label is numeric is an IPv4 address in the browsers' sense (2130706433, 0x7f.1, 0177.0.0.1, 127.1).
        // Only the canonical form of four decimal parts without leading zeros is accepted.
        var last = labels[^1];
        if (DecimalLabel().IsMatch(last) || HexLabel().IsMatch(last))
        {
            return CanonicalIpv4(labels, out var ip) ? WithPort(new ParsedAuthority(ip.ToString(), ip, null, AddressProblem.None), portText, scheme) : ParsedAuthority.Bad(AddressProblem.NonCanonicalIp);
        }

        return WithPort(new ParsedAuthority(lower, null, null, AddressProblem.None), portText, scheme);
    }

    private static ParsedAuthority WithPort(ParsedAuthority parsed, string? portText, string scheme)
    {
        if (portText is null)
        {
            return parsed;
        }

        if (portText.Length is 0 or > 5 || !DecimalLabel().IsMatch(portText) || !int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out var port) || port is < 1 or > 65535)
        {
            return ParsedAuthority.Bad(AddressProblem.BadPort);
        }

        return parsed with { Port = port };
    }

    private static bool CanonicalIpv4(string[] labels, out IPAddress ip)
    {
        ip = IPAddress.None;
        if (labels.Length != 4)
        {
            return false;
        }

        var bytes = new byte[4];
        for (var i = 0; i < 4; i++)
        {
            var label = labels[i];
            if (!DecimalLabel().IsMatch(label) || label.Length > 3 || (label.Length > 1 && label[0] == '0')
                || !byte.TryParse(label, NumberStyles.None, CultureInfo.InvariantCulture, out bytes[i]))
            {
                return false;
            }
        }

        ip = new IPAddress(bytes);
        return true;
    }

    private static AddressProblem NormalizePath(string path, out string normalized)
    {
        normalized = string.Empty;
        if (path.Length == 0 || path == "/")
        {
            return AddressProblem.None;
        }

        if (path.Length > MaxPathChars)
        {
            return AddressProblem.BadPath;
        }

        var trimmed = path.TrimEnd('/');
        foreach (var segment in trimmed[1..].Split('/'))
        {
            if (segment.Length == 0 || segment is "." or ".." || !PathSegment().IsMatch(segment))
            {
                return AddressProblem.BadPath;
            }

            // Encoded dots and separators hide traversal from checks that look at the raw text.
            var lower = segment.ToLowerInvariant();
            if (lower.Contains("%2e", StringComparison.Ordinal) || lower.Contains("%2f", StringComparison.Ordinal) || lower.Contains("%5c", StringComparison.Ordinal))
            {
                return AddressProblem.BadPath;
            }
        }

        normalized = trimmed;
        return AddressProblem.None;
    }

    private static AddressClass ClassifyV4(byte[] b)
    {
        var (a, c1, c2, d) = (b[0], b[1], b[2], b[3]);
        if (a == 0 || a == 127 || a >= 224 || (a == 169 && c1 == 254))
        {
            return AddressClass.Blocked; // "this" network, loopback, multicast and reserved (incl. broadcast), link-local (incl. cloud metadata)
        }

        if (a == 100 && c1 == 100 && c2 == 100 && d is 100 or 200)
        {
            return AddressClass.Blocked; // Tailscale's resolver and its metadata address
        }

        if ((a == 192 && c1 == 0 && c2 == 0) || (a == 168 && c1 == 63 && c2 == 129 && d == 16))
        {
            return AddressClass.Blocked; // 192.0.0.0/24 (incl. 192.0.0.192) and the Azure wire server 168.63.129.16
        }

        if ((a == 192 && c1 == 0 && c2 == 2) || (a == 198 && c1 == 51 && c2 == 100) || (a == 203 && c1 == 0 && c2 == 113) || (a == 198 && c1 is 18 or 19))
        {
            return AddressClass.Blocked; // documentation and benchmarking ranges
        }

        if (a == 100 && (c1 & 0xc0) == 0x40)
        {
            return AddressClass.Tailnet; // 100.64.0.0/10
        }

        if (a == 10 || (a == 172 && (c1 & 0xf0) == 0x10) || (a == 192 && c1 == 168))
        {
            return AddressClass.Lan;
        }

        return AddressClass.Public;
    }

    private static bool IsPrefixZero(byte[] b, int count)
    {
        for (var i = 0; i < count; i++)
        {
            if (b[i] != 0)
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsRangeZero(byte[] b, int start, int end)
    {
        for (var i = start; i < end; i++)
        {
            if (b[i] != 0)
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsDefaultPort(string scheme, int port) => (scheme == "http" && port == 80) || (scheme == "https" && port == 443);

    [GeneratedRegex(@"\A(?<scheme>[A-Za-z][A-Za-z0-9+.\-]*)://(?<authority>[^/?#@\s]+)(?<path>/[^?#\s]*)?\z", RegexOptions.CultureInvariant)]
    private static partial Regex UrlShape();

    [GeneratedRegex(@"\A[A-Za-z][A-Za-z0-9+.\-]*\z", RegexOptions.CultureInvariant)]
    private static partial Regex SchemeShape();

    [GeneratedRegex(@"\A(?:socks5h?)://(?<host>localhost|\[[0-9A-Fa-f:.]+\]|[0-9.]+):(?<port>[0-9]{1,5})\z", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex ProxyShape();

    [GeneratedRegex(@"\A[a-z0-9_](?:[a-z0-9_\-]*[a-z0-9_])?\z", RegexOptions.CultureInvariant)]
    private static partial Regex HostLabel();

    [GeneratedRegex(@"\A[0-9]+\z", RegexOptions.CultureInvariant)]
    private static partial Regex DecimalLabel();

    [GeneratedRegex(@"\A0x[0-9a-f]*\z", RegexOptions.CultureInvariant)]
    private static partial Regex HexLabel();

    [GeneratedRegex(@"\A[A-Za-z0-9._~!$&'()*+,;=:@%\-]+\z", RegexOptions.CultureInvariant)]
    private static partial Regex PathSegment();

    private sealed record ParsedAuthority(string Host, IPAddress? Ip, int? Port, AddressProblem Problem)
    {
        public static ParsedAuthority Bad(AddressProblem problem) => new(string.Empty, null, null, problem);
    }
}
