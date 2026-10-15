using System;
using System.Net;
using Jellyfin.Plugin.Shoal.Peers;
using Jellyfin.Plugin.Shoal.State;
using Xunit;

namespace Jellyfin.Plugin.Shoal.Tests;

/// <summary>
/// K4 acceptance (DESIGN 11.2, 8.3 T11): the address table, including 100.100.100.200, fd00:ec2::254, decimal, octal and hex IPv4,
/// IPv4-mapped, NAT64, userinfo, backslash and non-punycode hosts; public over http refused; proxy mode Tailnet-only.
/// </summary>
public sealed class AddressPolicyTests
{
    private static AddressProblem Problem(string url, PeerNetwork network, bool ack = false, string? proxy = null) =>
        AddressPolicy.Validate(url, network, ack, proxy).Problem;

    [Theory]
    [InlineData("http://hops.tail1234.ts.net:8096")]
    [InlineData("https://hops.tail1234.ts.net")]
    [InlineData("http://x.ts.net")]
    [InlineData("http://HOPS.Tail1234.TS.NET:8096/")]
    [InlineData("http://hops.tail1234.ts.net.:8096")]
    [InlineData("http://100.64.0.1:8096")]
    [InlineData("http://100.127.255.254")]
    [InlineData("http://100.101.102.103:8096/jellyfin")]
    [InlineData("http://[fd7a:115c:a1e0::1]:8096")]
    [InlineData("http://[fd7a:115c:a1e0:ab12:4843:cd96:6200:1]")]
    [InlineData("http://[::ffff:100.64.0.1]")]
    public void Tailnet_AcceptsTailscaleNamesAndAddresses(string url)
    {
        Assert.Equal(AddressProblem.None, Problem(url, PeerNetwork.Tailnet));
    }

    [Theory]
    [InlineData("http://100.100.100.100", AddressProblem.Blocked)]
    [InlineData("http://100.100.100.200", AddressProblem.Blocked)]
    [InlineData("http://100.100.100.200:80", AddressProblem.Blocked)]
    [InlineData("http://[fd00:ec2::254]", AddressProblem.Blocked)]
    [InlineData("http://[fd00:0ec2:0:0:0:0:0:254]", AddressProblem.Blocked)]
    [InlineData("http://127.0.0.1:8096", AddressProblem.Blocked)]
    [InlineData("http://127.255.255.254", AddressProblem.Blocked)]
    [InlineData("http://0.0.0.0", AddressProblem.Blocked)]
    [InlineData("http://0.1.2.3", AddressProblem.Blocked)]
    [InlineData("http://169.254.169.254", AddressProblem.Blocked)]
    [InlineData("http://169.254.0.1", AddressProblem.Blocked)]
    [InlineData("http://192.0.0.192", AddressProblem.Blocked)]
    [InlineData("http://168.63.129.16", AddressProblem.Blocked)]
    [InlineData("http://224.0.0.1", AddressProblem.Blocked)]
    [InlineData("http://239.255.255.250", AddressProblem.Blocked)]
    [InlineData("http://255.255.255.255", AddressProblem.Blocked)]
    [InlineData("http://240.0.0.1", AddressProblem.Blocked)]
    [InlineData("http://[::1]", AddressProblem.Blocked)]
    [InlineData("http://[::]", AddressProblem.Blocked)]
    [InlineData("http://[fe80::1]", AddressProblem.Blocked)]
    [InlineData("http://[ff02::1]", AddressProblem.Blocked)]
    [InlineData("http://[::ffff:127.0.0.1]", AddressProblem.Blocked)]
    [InlineData("http://[::ffff:7f00:1]", AddressProblem.Blocked)]
    [InlineData("http://[::ffff:169.254.169.254]", AddressProblem.Blocked)]
    [InlineData("http://[::ffff:100.100.100.200]", AddressProblem.Blocked)]
    [InlineData("http://[64:ff9b::7f00:1]", AddressProblem.Blocked)]
    [InlineData("http://[64:ff9b::a9fe:a9fe]", AddressProblem.Blocked)]
    [InlineData("http://[2002:7f00:1::]", AddressProblem.Blocked)]
    [InlineData("http://[2002:a9fe:a9fe::1]", AddressProblem.Blocked)]
    [InlineData("http://[2001:0:7f00:1::]", AddressProblem.Blocked)]
    [InlineData("http://[::127.0.0.1]", AddressProblem.Blocked)]
    [InlineData("http://localhost:8096", AddressProblem.Blocked)]
    [InlineData("http://app.localhost", AddressProblem.Blocked)]
    public void EveryNeverAcceptableAddress_IsBlocked_InEveryNetworkType(string url, AddressProblem expected)
    {
        foreach (var network in Enum.GetValues<PeerNetwork>())
        {
            Assert.Equal(expected, Problem(url, network, ack: true));
        }
    }

    [Theory]
    [InlineData("http://2130706433")]
    [InlineData("http://2130706433:8096")]
    [InlineData("http://0x7f000001")]
    [InlineData("http://0x7f.0.0.1")]
    [InlineData("http://0X7F.0.0.1")]
    [InlineData("http://0177.0.0.1")]
    [InlineData("http://0177.0.0.01")]
    [InlineData("http://127.1")]
    [InlineData("http://127.0.1")]
    [InlineData("http://10.1")]
    [InlineData("http://1.2.3.256")]
    [InlineData("http://010.1.1.1")]
    [InlineData("http://1.2.3.04")]
    [InlineData("http://1.2.3.4.5")]
    [InlineData("http://3232235777")]
    public void NonCanonicalIpv4_IsRefused_WhateverItWouldMeanToABrowser(string url)
    {
        foreach (var network in Enum.GetValues<PeerNetwork>())
        {
            Assert.Equal(AddressProblem.NonCanonicalIp, Problem(url, network, ack: true));
        }
    }

    [Theory]
    [InlineData("http://user:pw@hops.ts.net", AddressProblem.UserInfo)]
    [InlineData("http://hops.ts.net@evil.example", AddressProblem.UserInfo)]
    [InlineData("http://@hops.ts.net", AddressProblem.UserInfo)]
    [InlineData("http://hops.ts.net\\@evil.example", AddressProblem.IllegalCharacter)]
    [InlineData("http://hops.ts.net\\", AddressProblem.IllegalCharacter)]
    [InlineData("http:\\\\hops.ts.net", AddressProblem.IllegalCharacter)]
    [InlineData("http://hops.ts.net/a\\b", AddressProblem.IllegalCharacter)]
    [InlineData("http://hops.ts.net/a b", AddressProblem.IllegalCharacter)]
    [InlineData("http://hops.ts.net/\r\nHost: x", AddressProblem.IllegalCharacter)]
    [InlineData(" http://hops.ts.net", AddressProblem.IllegalCharacter)]
    [InlineData("http://hops.ts.net/?a=b", AddressProblem.Query)]
    [InlineData("http://hops.ts.net?a=b", AddressProblem.Query)]
    [InlineData("http://hops.ts.net/#frag", AddressProblem.Fragment)]
    [InlineData("http://hops.ts.net#frag", AddressProblem.Fragment)]
    [InlineData("http://h\u00f6ps.ts.net", AddressProblem.BadHost)]
    [InlineData("http://xn--hps-pla.ts.net", AddressProblem.None)]
    [InlineData("http://\u2603.ts.net", AddressProblem.BadHost)]
    [InlineData("http://hops_.ts.net", AddressProblem.None)]
    [InlineData("http://-hops.ts.net", AddressProblem.BadHost)]
    [InlineData("http://hops-.ts.net", AddressProblem.BadHost)]
    [InlineData("http://hops..ts.net", AddressProblem.BadHost)]
    [InlineData("http://.ts.net", AddressProblem.BadHost)]
    [InlineData("http://hops.ts.net:0", AddressProblem.BadPort)]
    [InlineData("http://hops.ts.net:65536", AddressProblem.BadPort)]
    [InlineData("http://hops.ts.net:80a", AddressProblem.BadPort)]
    [InlineData("http://hops.ts.net:", AddressProblem.BadPort)]
    [InlineData("http://[fd7a:115c:a1e0::1", AddressProblem.BadHost)]
    [InlineData("http://[fe80::1%eth0]", AddressProblem.BadHost)]
    [InlineData("http://[1.2.3.4]", AddressProblem.BadHost)]
    [InlineData("ftp://hops.ts.net", AddressProblem.BadScheme)]
    [InlineData("gopher://hops.ts.net", AddressProblem.BadScheme)]
    [InlineData("file:///etc/passwd", AddressProblem.NotAUrl)]
    [InlineData("hops.ts.net", AddressProblem.NotAUrl)]
    [InlineData("//hops.ts.net", AddressProblem.NotAUrl)]
    [InlineData("", AddressProblem.NotAUrl)]
    [InlineData("http://", AddressProblem.NotAUrl)]
    [InlineData("http:///hops.ts.net", AddressProblem.NotAUrl)]
    [InlineData("http://hops.ts.net/..", AddressProblem.BadPath)]
    [InlineData("http://hops.ts.net/a/../b", AddressProblem.BadPath)]
    [InlineData("http://hops.ts.net/a/./b", AddressProblem.BadPath)]
    [InlineData("http://hops.ts.net/a//b", AddressProblem.BadPath)]
    [InlineData("http://hops.ts.net//a", AddressProblem.BadPath)]
    [InlineData("http://hops.ts.net/%2e%2e/x", AddressProblem.BadPath)]
    [InlineData("http://hops.ts.net/a%2Fb", AddressProblem.BadPath)]
    [InlineData("http://hops.ts.net/a%5Cb", AddressProblem.BadPath)]
    public void MalformedAndHostileUrls_AreRefusedWithAFixedProblem(string url, AddressProblem expected)
    {
        Assert.Equal(expected, Problem(url, PeerNetwork.Tailnet));
    }

    [Fact]
    public void ATooLongUrlAndPath_AreRefused()
    {
        Assert.Equal(AddressProblem.NotAUrl, Problem("http://hops.ts.net/" + new string('a', 600), PeerNetwork.Tailnet));
        Assert.Equal(AddressProblem.BadPath, Problem("http://hops.ts.net/" + new string('a', 250), PeerNetwork.Tailnet));
    }

    [Theory]
    [InlineData("http://10.0.0.5:8096", PeerNetwork.Tailnet, AddressProblem.WrongNetwork)]
    [InlineData("http://example.com", PeerNetwork.Tailnet, AddressProblem.WrongNetwork)]
    [InlineData("http://jellyfin", PeerNetwork.Tailnet, AddressProblem.WrongNetwork)]
    [InlineData("http://8.8.8.8", PeerNetwork.Tailnet, AddressProblem.WrongNetwork)]
    [InlineData("http://[fd12:3456::1]", PeerNetwork.Tailnet, AddressProblem.WrongNetwork)]
    [InlineData("https://100.64.0.1", PeerNetwork.Lan, AddressProblem.WrongNetwork)]
    [InlineData("https://hops.ts.net", PeerNetwork.Lan, AddressProblem.WrongNetwork)]
    [InlineData("https://[fd7a:115c:a1e0::1]", PeerNetwork.Lan, AddressProblem.WrongNetwork)]
    [InlineData("https://172.32.0.1", PeerNetwork.Lan, AddressProblem.WrongNetwork)]
    [InlineData("https://172.15.255.255", PeerNetwork.Lan, AddressProblem.WrongNetwork)]
    [InlineData("https://8.8.8.8", PeerNetwork.Lan, AddressProblem.WrongNetwork)]
    [InlineData("https://192.168.1.1", PeerNetwork.Public, AddressProblem.WrongNetwork)]
    [InlineData("https://10.1.2.3", PeerNetwork.Public, AddressProblem.WrongNetwork)]
    [InlineData("https://100.64.0.1", PeerNetwork.Public, AddressProblem.WrongNetwork)]
    [InlineData("https://[fd12::1]", PeerNetwork.Public, AddressProblem.WrongNetwork)]
    [InlineData("https://intranet", PeerNetwork.Public, AddressProblem.WrongNetwork)]
    [InlineData("https://hops.tail1234.ts.net", PeerNetwork.Public, AddressProblem.WrongNetwork)]
    [InlineData("http://example.com", PeerNetwork.Public, AddressProblem.HttpsRequired)]
    [InlineData("http://8.8.8.8", PeerNetwork.Public, AddressProblem.HttpsRequired)]
    [InlineData("http://[2606:4700::1111]", PeerNetwork.Public, AddressProblem.HttpsRequired)]
    [InlineData("https://example.com", PeerNetwork.Public, AddressProblem.None)]
    [InlineData("https://example.com:8920/jellyfin", PeerNetwork.Public, AddressProblem.None)]
    [InlineData("https://8.8.8.8", PeerNetwork.Public, AddressProblem.None)]
    [InlineData("https://[2606:4700::1111]:8920", PeerNetwork.Public, AddressProblem.None)]
    [InlineData("https://172.32.0.1", PeerNetwork.Public, AddressProblem.None)]
    [InlineData("https://[::ffff:8.8.8.8]", PeerNetwork.Public, AddressProblem.None)]
    [InlineData("https://192.168.1.5:8920", PeerNetwork.Lan, AddressProblem.None)]
    [InlineData("https://nas.local", PeerNetwork.Lan, AddressProblem.None)]
    [InlineData("https://jellyfin", PeerNetwork.Lan, AddressProblem.None)]
    [InlineData("https://172.16.0.1", PeerNetwork.Lan, AddressProblem.None)]
    [InlineData("https://172.31.255.255", PeerNetwork.Lan, AddressProblem.None)]
    [InlineData("https://10.255.255.255", PeerNetwork.Lan, AddressProblem.None)]
    [InlineData("https://[fd12:3456::1]", PeerNetwork.Lan, AddressProblem.None)]
    [InlineData("https://[fc00::1]", PeerNetwork.Lan, AddressProblem.None)]
    [InlineData("https://[::ffff:192.168.1.1]", PeerNetwork.Lan, AddressProblem.None)]
    public void TheAddressMustBelongToTheChosenNetworkType(string url, PeerNetwork network, AddressProblem expected)
    {
        Assert.Equal(expected, Problem(url, network));
    }

    [Fact]
    public void PlainHttpOnALan_NeedsTheAcknowledgement()
    {
        Assert.Equal(AddressProblem.CleartextNotAcknowledged, Problem("http://192.168.1.5:8096", PeerNetwork.Lan));
        Assert.Equal(AddressProblem.CleartextNotAcknowledged, Problem("http://nas.local", PeerNetwork.Lan, ack: false));
        Assert.Equal(AddressProblem.None, Problem("http://192.168.1.5:8096", PeerNetwork.Lan, ack: true));
        Assert.Equal(AddressProblem.None, Problem("https://192.168.1.5:8096", PeerNetwork.Lan, ack: false));
        Assert.Equal(AddressProblem.None, Problem("http://100.64.0.1", PeerNetwork.Tailnet, ack: false)); // WireGuard encrypts the Tailnet
    }

    [Theory]
    [InlineData("HTTP://HOPS.Tail1234.TS.NET:80/", "http://hops.tail1234.ts.net/")]
    [InlineData("https://x.ts.net:443/jf/", "https://x.ts.net/jf")]
    [InlineData("https://x.ts.net:8920/jf/sub", "https://x.ts.net:8920/jf/sub")]
    [InlineData("http://x.ts.net:8096", "http://x.ts.net:8096/")]
    [InlineData("http://[FD7A:115C:A1E0:0:0:0:0:1]:8096", "http://[fd7a:115c:a1e0::1]:8096/")]
    [InlineData("http://100.64.0.1/", "http://100.64.0.1/")]
    [InlineData("http://x.ts.net./", "http://x.ts.net/")]
    public void TheBaseUrlIsNormalized(string url, string expected)
    {
        var check = AddressPolicy.Validate(url, PeerNetwork.Tailnet, false);

        Assert.True(check.Ok, check.Problem.ToString());
        Assert.Equal(expected, check.Address!.BaseUri.ToString());
        Assert.False(check.Address.BaseUri.AbsolutePath.EndsWith("//", StringComparison.Ordinal));
    }

    [Fact]
    public void TheValidatedAddress_DescribesTheHost()
    {
        var literal = AddressPolicy.Validate("http://100.64.0.1:8096", PeerNetwork.Tailnet, false).Address!;
        var named = AddressPolicy.Validate("https://hops.ts.net", PeerNetwork.Tailnet, false).Address!;

        Assert.True(literal.HostIsLiteral);
        Assert.Equal("100.64.0.1", literal.Host);
        Assert.True(literal.IsCleartext);
        Assert.False(named.HostIsLiteral);
        Assert.False(named.IsCleartext);
        Assert.Null(named.Proxy);
    }

    [Theory]
    [InlineData("socks5://127.0.0.1:1055")]
    [InlineData("socks5://localhost:1055")]
    [InlineData("socks5h://127.0.0.1:1080")]
    [InlineData("socks5://[::1]:1055")]
    [InlineData("SOCKS5://127.0.0.1:1055")]
    public void ProxyMode_AcceptsALoopbackSocks5Proxy_ForTailnetPeers(string proxy)
    {
        var check = AddressPolicy.Validate("http://hops.tail1234.ts.net:8096", PeerNetwork.Tailnet, false, proxy);

        Assert.True(check.Ok, check.Problem.ToString());
        Assert.NotNull(check.Address!.Proxy);
        Assert.Equal("socks5", check.Address.Proxy!.Scheme);
    }

    [Theory]
    [InlineData("socks5://10.0.0.1:1055")]
    [InlineData("socks5://192.168.1.1:1055")]
    [InlineData("socks5://example.com:1055")]
    [InlineData("socks5://127.0.0.1")]
    [InlineData("socks5://127.0.0.1:0")]
    [InlineData("socks5://127.0.0.1:99999")]
    [InlineData("socks5://127.1:1055")]
    [InlineData("socks5://0.0.0.0:1055")]
    [InlineData("http://127.0.0.1:1055")]
    [InlineData("https://127.0.0.1:1055")]
    [InlineData("socks4://127.0.0.1:1055")]
    [InlineData("socks5://user:pw@127.0.0.1:1055")]
    [InlineData("socks5://127.0.0.1:1055/x")]
    [InlineData("127.0.0.1:1055")]
    public void ProxyMode_RefusesAnythingButALoopbackSocks5Url(string proxy)
    {
        Assert.Equal(AddressProblem.ProxyInvalid, Problem("http://hops.tail1234.ts.net:8096", PeerNetwork.Tailnet, proxy: proxy));
    }

    [Theory]
    [InlineData("https://192.168.1.5", PeerNetwork.Lan)]
    [InlineData("https://example.com", PeerNetwork.Public)]
    public void ProxyMode_IsTailnetOnly(string url, PeerNetwork network)
    {
        Assert.Equal(AddressProblem.ProxyNotAllowed, Problem(url, network, ack: true, proxy: "socks5://127.0.0.1:1055"));
    }

    [Fact]
    public void ProxyMode_StillRequiresATailnetNameOrLiteral()
    {
        // Through a proxy the name is resolved at the proxy; only names Tailscale resolves and Tailnet literals are acceptable.
        Assert.Equal(AddressProblem.WrongNetwork, Problem("http://example.com", PeerNetwork.Tailnet, proxy: "socks5://127.0.0.1:1055"));
        Assert.Equal(AddressProblem.WrongNetwork, Problem("http://10.0.0.5", PeerNetwork.Tailnet, proxy: "socks5://127.0.0.1:1055"));
        Assert.Equal(AddressProblem.Blocked, Problem("http://100.100.100.200", PeerNetwork.Tailnet, proxy: "socks5://127.0.0.1:1055"));
        Assert.Equal(AddressProblem.None, Problem("http://100.64.0.9", PeerNetwork.Tailnet, proxy: "socks5://127.0.0.1:1055"));
    }

    [Theory]
    [InlineData("100.64.0.1", AddressClass.Tailnet)]
    [InlineData("100.127.255.255", AddressClass.Tailnet)]
    [InlineData("100.63.255.255", AddressClass.Public)]
    [InlineData("100.128.0.0", AddressClass.Public)]
    [InlineData("100.100.100.99", AddressClass.Tailnet)]
    [InlineData("100.100.100.100", AddressClass.Blocked)]
    [InlineData("100.100.100.200", AddressClass.Blocked)]
    [InlineData("10.0.0.1", AddressClass.Lan)]
    [InlineData("172.16.0.1", AddressClass.Lan)]
    [InlineData("172.31.255.255", AddressClass.Lan)]
    [InlineData("172.32.0.0", AddressClass.Public)]
    [InlineData("192.168.255.255", AddressClass.Lan)]
    [InlineData("192.169.0.1", AddressClass.Public)]
    [InlineData("8.8.8.8", AddressClass.Public)]
    [InlineData("1.1.1.1", AddressClass.Public)]
    [InlineData("127.0.0.1", AddressClass.Blocked)]
    [InlineData("169.254.169.254", AddressClass.Blocked)]
    [InlineData("0.0.0.0", AddressClass.Blocked)]
    [InlineData("255.255.255.255", AddressClass.Blocked)]
    [InlineData("192.0.0.192", AddressClass.Blocked)]
    [InlineData("192.0.2.1", AddressClass.Blocked)]
    [InlineData("198.51.100.7", AddressClass.Blocked)]
    [InlineData("203.0.113.9", AddressClass.Blocked)]
    [InlineData("198.18.0.1", AddressClass.Blocked)]
    [InlineData("168.63.129.16", AddressClass.Blocked)]
    [InlineData("::1", AddressClass.Blocked)]
    [InlineData("::", AddressClass.Blocked)]
    [InlineData("fe80::1", AddressClass.Blocked)]
    [InlineData("fec0::1", AddressClass.Blocked)]
    [InlineData("ff02::fb", AddressClass.Blocked)]
    [InlineData("fd7a:115c:a1e0::5", AddressClass.Tailnet)]
    [InlineData("fd7a:115c:a1e1::5", AddressClass.Lan)]
    [InlineData("fd12:3456::1", AddressClass.Lan)]
    [InlineData("fc00::1", AddressClass.Lan)]
    [InlineData("fd00:ec2::254", AddressClass.Blocked)]
    [InlineData("fd00:ec2::255", AddressClass.Lan)]
    [InlineData("2001:db8::1", AddressClass.Blocked)]
    [InlineData("2606:4700:4700::1111", AddressClass.Public)]
    [InlineData("2a00:1450:4001::1", AddressClass.Public)]
    [InlineData("::ffff:8.8.8.8", AddressClass.Public)]
    [InlineData("::ffff:10.0.0.1", AddressClass.Lan)]
    [InlineData("::ffff:100.64.0.1", AddressClass.Tailnet)]
    [InlineData("::ffff:127.0.0.1", AddressClass.Blocked)]
    [InlineData("::10.0.0.1", AddressClass.Lan)]
    [InlineData("::127.0.0.1", AddressClass.Blocked)]
    [InlineData("64:ff9b::808:808", AddressClass.Public)]
    [InlineData("64:ff9b::a00:1", AddressClass.Lan)]
    [InlineData("64:ff9b::7f00:1", AddressClass.Blocked)]
    [InlineData("2002:808:808::1", AddressClass.Public)]
    [InlineData("2002:7f00:1::1", AddressClass.Blocked)]
    [InlineData("2002:a00:1::1", AddressClass.Lan)]
    [InlineData("100::1", AddressClass.Blocked)]
    [InlineData("4000::1", AddressClass.Blocked)]
    public void Classify_UnwrapsEmbeddedIpv4_AndKnowsTheSpecialRanges(string address, AddressClass expected)
    {
        Assert.Equal(expected, AddressPolicy.Classify(IPAddress.Parse(address)));
    }

    [Fact]
    public void Classify_TeredoNeedsBothEmbeddedAddressesToBePublic()
    {
        // 2001:0:<server>:<flags>:<port xor ffff>:<client xor ffffffff>
        Assert.Equal(AddressClass.Public, AddressPolicy.Classify(IPAddress.Parse("2001:0:808:808::f7f7:f7f7")));
        Assert.Equal(AddressClass.Blocked, AddressPolicy.Classify(IPAddress.Parse("2001:0:7f00:1::f7f7:f7f7")));
        Assert.Equal(AddressClass.Blocked, AddressPolicy.Classify(IPAddress.Parse("2001:0:808:808::80ff:fffe"))); // client 127.0.0.1
    }

    [Fact]
    public void CheckResolved_RefusesTheWholeSet_WhenOneAddressIsNotAcceptable()
    {
        var good = IPAddress.Parse("100.64.0.1");
        var loopback = IPAddress.Parse("127.0.0.1");

        Assert.True(AddressPolicy.CheckResolved(PeerNetwork.Tailnet, [good]));
        Assert.True(AddressPolicy.CheckResolved(PeerNetwork.Tailnet, [good, IPAddress.Parse("100.64.0.2")]));
        Assert.False(AddressPolicy.CheckResolved(PeerNetwork.Tailnet, [good, loopback]));
        Assert.False(AddressPolicy.CheckResolved(PeerNetwork.Tailnet, [loopback]));
        Assert.False(AddressPolicy.CheckResolved(PeerNetwork.Tailnet, []));
        Assert.False(AddressPolicy.CheckResolved(PeerNetwork.Tailnet, [IPAddress.Parse("10.0.0.1")]));
        Assert.True(AddressPolicy.CheckResolved(PeerNetwork.Lan, [IPAddress.Parse("::ffff:192.168.1.1"), IPAddress.Parse("10.0.0.2")]));
        Assert.False(AddressPolicy.CheckResolved(PeerNetwork.Public, [IPAddress.Parse("8.8.8.8"), IPAddress.Parse("10.0.0.1")]));
        Assert.True(AddressPolicy.CheckResolved(PeerNetwork.Public, [IPAddress.Parse("8.8.8.8"), IPAddress.Parse("2606:4700::1111")]));
        Assert.False(AddressPolicy.CheckResolved(PeerNetwork.Tailnet, [IPAddress.Parse("100.100.100.200")]));
    }
}
