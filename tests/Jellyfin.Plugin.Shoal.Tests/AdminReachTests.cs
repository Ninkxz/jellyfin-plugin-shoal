using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.Api.Admin;
using Jellyfin.Plugin.Shoal.Diagnostics;
using Jellyfin.Plugin.Shoal.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Jellyfin.Plugin.Shoal.Tests;

/// <summary>Pairing flow v2, item 3: <c>GET Shoal/Admin/Shares/Reach</c>.</summary>
public sealed class AdminReachTests
{
    private sealed class FakeTailscale : ITailscaleStatus, ITailscaleSelf
    {
        public TailscaleSelf? Self { get; set; }

        public Task<TailscalePeerPath?> FindAsync(string host, CancellationToken cancellationToken) => Task.FromResult<TailscalePeerPath?>(null);

        public Task<TailscaleSelf?> SelfAsync(CancellationToken cancellationToken) => Task.FromResult(Self);
    }

    private sealed class FakeNetwork(int port, params string[] addresses) : IReachNetwork
    {
        public int HttpPort => port;

        public IReadOnlyList<IPAddress> Addresses() => [.. addresses.Select(IPAddress.Parse)];
    }

    private static async Task<JsonElement> ReachAsync(ITailscaleStatus tailscale, IReachNetwork network, string scheme = "http", string host = "192.168.1.5:8096")
    {
        var http = new DefaultHttpContext();
        http.Request.Scheme = scheme;
        http.Request.Host = HostString.FromUriComponent(host);
        var controller = new AdminReachController(tailscale, network) { ControllerContext = new ControllerContext { HttpContext = http } };
        var result = Assert.IsType<ShoalResult>(await controller.Reach(CancellationToken.None));
        Assert.Equal(200, result.Status);
        return JsonSerializer.SerializeToElement(result.Value);
    }

    [Fact]
    public async Task Tailscale_Https_AndLan_AreSuggested_WhenTheyAreKnown()
    {
        var tailscale = new FakeTailscale { Self = new TailscaleSelf("host.tail1234.ts.net", "100.64.0.7") };

        var json = await ReachAsync(tailscale, new FakeNetwork(8096, "192.168.1.5", "10.0.0.8"), "https", "jelly.example.org");

        Assert.Equal("host.tail1234.ts.net", json.GetProperty("tailscale").GetProperty("dnsName").GetString());
        Assert.Equal("100.64.0.7", json.GetProperty("tailscale").GetProperty("ip").GetString());
        Assert.Equal("https://jelly.example.org", json.GetProperty("https").GetProperty("url").GetString());
        Assert.Equal(["http://192.168.1.5:8096", "http://10.0.0.8:8096"], json.GetProperty("lan").EnumerateArray().Select(static e => e.GetProperty("url").GetString()));
    }

    [Fact]
    public async Task WhatIsNotKnown_IsLeftOut()
    {
        var json = await ReachAsync(new FakeTailscale(), new FakeNetwork(8096));

        Assert.Empty(json.EnumerateObject());
    }

    [Fact]
    public async Task OnlyPrivateLanAddresses_AreOffered_AtMostFour()
    {
        var json = await ReachAsync(new FakeTailscale(), new FakeNetwork(8096, "127.0.0.1", "169.254.3.4", "100.64.0.7", "8.8.8.8", "172.15.0.1", "172.32.0.1", "172.16.0.9", "192.168.1.5", "192.168.1.5", "10.1.1.1", "10.1.1.2", "10.1.1.3", "10.1.1.4"));

        Assert.Equal(["172.16.0.9", "192.168.1.5", "10.1.1.1", "10.1.1.2"], json.GetProperty("lan").EnumerateArray().Select(static e => new Uri(e.GetProperty("url").GetString()!).Host));
    }

    [Theory]
    [InlineData("http", "jelly.example.org")] // not https
    [InlineData("https", "192.168.1.5:8920")] // an IP literal
    [InlineData("https", "localhost")]
    [InlineData("https", "jellyserver")] // one label
    [InlineData("https", "nas.local")]
    public async Task TheHttpsSuggestion_NeedsARealHttpsName(string scheme, string host)
    {
        var json = await ReachAsync(new FakeTailscale(), new FakeNetwork(8096), scheme, host);

        Assert.False(json.TryGetProperty("https", out _));
    }

    [Fact]
    public async Task AnHttpsPortThatIsNot443_IsKept()
    {
        var json = await ReachAsync(new FakeTailscale(), new FakeNetwork(8096), "https", "jelly.example.org:8920");

        Assert.Equal("https://jelly.example.org:8920", json.GetProperty("https").GetProperty("url").GetString());
    }

    [Fact]
    public void TheTailscaleStatusDocument_GivesTheSelfNodeName_AndAnIpv4Address()
    {
        var doc = Encoding.UTF8.GetBytes("{\"Self\":{\"DNSName\":\"host.tail1234.ts.net.\",\"TailscaleIPs\":[\"fd7a:115c::1\",\"100.64.0.7\"]},\"Peer\":{}}");

        var self = TailscaleLocalApi.ParseSelf(doc);

        Assert.Equal(new TailscaleSelf("host.tail1234.ts.net", "100.64.0.7"), self);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"Self\":{}}")]
    [InlineData("{\"Self\":{\"DNSName\":\"bad name!.ts.net.\"}}")]
    [InlineData("{\"Self\":{\"DNSName\":\"<script>.ts.net\"}}")]
    [InlineData("not json")]
    public void ABadStatusDocument_GivesNothing(string json)
    {
        Assert.Null(TailscaleLocalApi.ParseSelf(Encoding.UTF8.GetBytes(json)));
    }
}
