using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.Api.Admin;
using Jellyfin.Plugin.Shoal.Configuration;
using Jellyfin.Plugin.Shoal.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Shoal.Tests;

/// <summary>U9 (DESIGN 7.3 item 1): the optional Tailscale LocalAPI.</summary>
public sealed class TailscaleLocalApiTests
{
    private const string Status = """
        {"Version":"1.80.0","Peer":{
          "nodekey:aa":{"DNSName":"hops.tail1234.ts.net.","TailscaleIPs":["100.64.1.2","fd7a:115c:a1e0::2"],"CurAddr":"203.0.113.9:41641","Relay":"fra","Online":true},
          "nodekey:bb":{"DNSName":"fern.tail1234.ts.net.","TailscaleIPs":["100.64.7.7"],"CurAddr":"","Relay":"fra","Online":true},
          "nodekey:cc":{"DNSName":"gone.tail1234.ts.net.","TailscaleIPs":["100.64.9.9"],"CurAddr":"","Relay":"","Online":false}
        }}
        """;

    private static TailscalePeerPath? Find(string host) => TailscaleLocalApi.Parse(Encoding.UTF8.GetBytes(Status), host);

    [Fact]
    public void ADirectPeer_IsNotRelayed_ByIpOrByName()
    {
        Assert.Equal(new TailscalePeerPath(false, null), Find("100.64.1.2"));
        Assert.Equal(new TailscalePeerPath(false, null), Find("hops.tail1234.ts.net"));
        Assert.Equal(new TailscalePeerPath(false, null), Find("HOPS")); // a short name matches the machine name
    }

    [Fact]
    public void AnEmptyCurAddr_MeansRelayed_WithTheRegion()
    {
        Assert.Equal(new TailscalePeerPath(true, "fra"), Find("100.64.7.7"));
        Assert.Equal(new TailscalePeerPath(true, "fra"), Find("fern.tail1234.ts.net."));
    }

    [Fact]
    public void L3_ARegionIsAShortPlainWord_AndAShortNameThatTwoNodesShareIsNotGuessed()
    {
        var long_ = new string('x', 3000);
        var doc = """
            {"Peer":{
              "a":{"DNSName":"hops.tail1.ts.net.","TailscaleIPs":["100.64.1.2"],"CurAddr":"","Relay":"LONGREGION","Online":true},
              "b":{"DNSName":"fern.tail1.ts.net.","TailscaleIPs":["100.64.2.2"],"CurAddr":"","Relay":"<b>x</b>","Online":true},
              "c":{"DNSName":"dup.tail1.ts.net.","TailscaleIPs":["100.64.3.3"],"CurAddr":"1.2.3.4:5","Online":true},
              "d":{"DNSName":"dup.tail9.ts.net.","TailscaleIPs":["100.64.4.4"],"CurAddr":"","Relay":"ams","Online":true}
            }}
            """;
        var bytes = Encoding.UTF8.GetBytes(doc.Replace("LONGREGION", long_, StringComparison.Ordinal));

        Assert.Equal(new TailscalePeerPath(true, null), TailscaleLocalApi.Parse(bytes, "100.64.1.2")); // relayed, but the region is not kept
        Assert.Equal(new TailscalePeerPath(true, null), TailscaleLocalApi.Parse(bytes, "fern.tail1.ts.net"));
        Assert.Null(TailscaleLocalApi.Parse(bytes, "dup")); // two nodes answer to that short name
        Assert.Equal(new TailscalePeerPath(false, null), TailscaleLocalApi.Parse(bytes, "dup.tail1.ts.net")); // the full name is exact
        Assert.Equal(new TailscalePeerPath(true, "ams"), TailscaleLocalApi.Parse(bytes, "100.64.4.4"));
    }

    [Fact]
    public void AnOfflineOrUnknownPeer_AndGarbage_GiveNoAnswer()
    {
        Assert.Null(Find("100.64.9.9"));
        Assert.Null(Find("100.64.200.1"));
        Assert.Null(Find("example.com"));
        Assert.Null(TailscaleLocalApi.Parse(Encoding.UTF8.GetBytes("{ not json"), "100.64.1.2"));
        Assert.Null(TailscaleLocalApi.Parse(Encoding.UTF8.GetBytes("""{"Peer":[]}"""), "100.64.1.2"));
    }

    [Fact]
    public async Task OverAUnixSocket_TheStatusIsRead_AndOffOrAMissingSocketNeverAsks()
    {
        var path = Path.Combine(Path.GetTempPath(), "shoal-ts-" + Guid.NewGuid().ToString("N")[..8] + ".sock");
        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            listener.Bind(new UnixDomainSocketEndPoint(path));
        }
        catch (Exception ex) when (ex is SocketException or PlatformNotSupportedException)
        {
            return; // no Unix sockets on this machine
        }

        listener.Listen(1);
        string? requestLine = null;
        var server = Task.Run(async () =>
        {
            using var connection = await listener.AcceptAsync();
            var buffer = new byte[2048];
            var read = await connection.ReceiveAsync(buffer);
            requestLine = Encoding.ASCII.GetString(buffer, 0, read).Split('\r')[0];
            var body = Encoding.UTF8.GetBytes(Status);
            var head = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
            await connection.SendAsync(head);
            await connection.SendAsync(body);
        });

        try
        {
            var api = new TailscaleLocalApi(new FixedSettings(path), NullLogger<TailscaleLocalApi>.Instance);

            var found = await api.FindAsync("100.64.7.7", default);
            await server;

            Assert.Equal(new TailscalePeerPath(true, "fra"), found);
            Assert.Equal("GET /localapi/v0/status HTTP/1.1", requestLine);
            Assert.Null(await new TailscaleLocalApi(new FixedSettings("off"), NullLogger<TailscaleLocalApi>.Instance).FindAsync("100.64.7.7", default));
            Assert.Null(await new TailscaleLocalApi(new FixedSettings(Path.Combine(Path.GetTempPath(), "no-such.sock")), NullLogger<TailscaleLocalApi>.Instance).FindAsync("100.64.7.7", default));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private sealed class FixedSettings(string socket) : ISettingsAccessor
    {
        public PluginConfiguration Current => new() { TailscaleSocket = socket };

        public void Save(PluginConfiguration configuration)
        {
        }
    }
}
