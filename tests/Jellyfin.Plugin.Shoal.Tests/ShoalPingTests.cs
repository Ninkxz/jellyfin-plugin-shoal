using System;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.Api;
using Jellyfin.Plugin.Shoal.Http;
using Jellyfin.Plugin.Shoal.Pairing;
using Jellyfin.Plugin.Shoal.State;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Jellyfin.Plugin.Shoal.Tests;

/// <summary>Pairing flow v2, item 2: the anonymous Shoal ping and <c>hasShoal</c> on the probe (Warden reviews this one).</summary>
public sealed class ShoalPingTests
{
    private static ShoalPingController Controller(PingLimiter limiter, Func<bool> detectable, string address = "100.64.0.9")
    {
        var http = new DefaultHttpContext();
        http.Connection.RemoteIpAddress = IPAddress.Parse(address);
        var type = typeof(ShoalPingController);
        var controller = (ShoalPingController)type.GetConstructor(BindingFlags.NonPublic | BindingFlags.Instance, [typeof(PingLimiter), typeof(Func<bool>)])!.Invoke([limiter, detectable]);
        controller.ControllerContext = new ControllerContext { HttpContext = http };
        return controller;
    }

    private static (int Status, string Body, int? RetryAfter) Run(ShoalPingController controller)
    {
        var result = Assert.IsType<ShoalResult>(controller.Ping());
        return (result.Status, JsonSerializer.Serialize(result.Value), null);
    }

    [Fact]
    public void ItAnswersOnlyThatShoalIsHere_NoVersionNoIdNoName()
    {
        var (status, body, _) = Run(Controller(new PingLimiter(TimeProvider.System), () => true));

        Assert.Equal(200, status);
        Assert.Equal("{\"Shoal\":true}", body.Replace("shoal", "Shoal"));
        using var json = JsonDocument.Parse(body);
        Assert.Single(json.RootElement.EnumerateObject());
    }

    [Fact]
    public void WithTheSettingOff_ItAnswersTheOneNotFound_LikeAnUnknownAddress()
    {
        var (status, body, _) = Run(Controller(new PingLimiter(TimeProvider.System), () => false));

        Assert.Equal(404, status);
        Assert.Equal("{}", body);
    }

    [Fact]
    public void ItIsRateLimited_PerAddressAndOverall_AndWithTheSettingOffItIsAlwaysThePlain404()
    {
        var time = new ManualTimeProvider();
        var limiter = new PingLimiter(time);
        for (var i = 0; i < PingLimiter.PerAddress; i++)
        {
            Assert.Equal(200, Run(Controller(limiter, () => true, "10.0.0.1")).Status);
        }

        Assert.Equal(429, Run(Controller(limiter, () => true, "10.0.0.1")).Status);

        // Warden 12 L1: with the setting off the limiter is never consulted, so no flood can draw a 429 that a server without Shoal would never send.
        for (var i = 0; i < PingLimiter.Overall; i++)
        {
            Assert.Equal(404, Run(Controller(limiter, () => false, "10.0.0.2")).Status);
        }

        Assert.Equal(404, Run(Controller(limiter, () => false, "10.0.0.2")).Status);

        Assert.NotEqual(429, Run(Controller(limiter, () => true, "10.0.0.3")).Status); // another address is not limited by it
        time.Advance(TimeSpan.FromMinutes(2));
        Assert.NotEqual(429, Run(Controller(limiter, () => true, "10.0.0.1")).Status);

        var crowd = new PingLimiter(new ManualTimeProvider());
        var refused = Enumerable.Range(0, PingLimiter.Overall + 50).Count(i => !crowd.TryTake("10.1." + (i / 200).ToString(System.Globalization.CultureInfo.InvariantCulture) + "." + (i % 200).ToString(System.Globalization.CultureInfo.InvariantCulture)));
        Assert.Equal(50, refused);
    }

    [Fact]
    public void AnIpv6Host_IsCountedAsItsSlash64_AndANewCallerIsNeverLockedOutByOldOnes()
    {
        Assert.Equal(PingLimiter.KeyOf(IPAddress.Parse("2001:db8:1:2::1")), PingLimiter.KeyOf(IPAddress.Parse("2001:db8:1:2:ffff:ffff:ffff:ffff")));
        Assert.NotEqual(PingLimiter.KeyOf(IPAddress.Parse("2001:db8:1:2::1")), PingLimiter.KeyOf(IPAddress.Parse("2001:db8:1:3::1")));
        Assert.Equal("10.0.0.7", PingLimiter.KeyOf(IPAddress.Parse("::ffff:10.0.0.7")));
        var time = new ManualTimeProvider();
        var limiter = new PingLimiter(time);
        for (var i = 0; i < PingLimiter.PerAddress; i++)
        {
            Assert.True(limiter.TryTake(PingLimiter.KeyOf(IPAddress.Parse("2001:db8::" + i.ToString("x", System.Globalization.CultureInfo.InvariantCulture)))));
        }

        Assert.False(limiter.TryTake(PingLimiter.KeyOf(IPAddress.Parse("2001:db8::ffff")))); // one /64: the same budget however many addresses a host uses

        // A full table of live addresses: the one heard from longest ago makes room, so a newcomer is still answered.
        var small = new PingLimiter(time, 4);
        foreach (var name in new[] { "a", "b", "c", "d" })
        {
            time.Advance(TimeSpan.FromSeconds(1));
            Assert.True(small.TryTake(name));
        }

        time.Advance(TimeSpan.FromSeconds(1));
        Assert.True(small.TryTake("newcomer"));
        Assert.True(small.TryTake("a")); // "a" was the oldest: it was dropped, so it starts again with an empty window
    }

    [Fact]
    public void TheActionIsAnonymous_TheControllerFailsClosed_AndNothingElseOnItIs()
    {
        var type = typeof(ShoalPingController);

        Assert.Single(type.GetCustomAttributes(typeof(AuthorizeAttribute), inherit: false));
        Assert.Empty(type.GetCustomAttributes(typeof(AllowAnonymousAttribute), inherit: false));
        var anonymous = type.GetMethods().Where(static m => m.GetCustomAttributes(typeof(AllowAnonymousAttribute), inherit: false).Length > 0).ToList();
        Assert.Equal("Ping", Assert.Single(anonymous).Name);
        Assert.Equal("Ping", Assert.Single(type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly), static m => m.GetCustomAttributes<Microsoft.AspNetCore.Mvc.Routing.HttpMethodAttribute>().Any()).Name);
    }

    [Fact]
    public async Task TheProbe_SaysHasShoal_WhenThePingAnswersYes_AndNoOtherwise()
    {
        await using var peer = await FakeJellyfinPeer.StartAsync();
        using var rig = InviteHomeTests.NewRig();

        var plain = await rig.Service.ProbeAsync(new ProbeRequest { Url = peer.BaseUrl, Network = PeerNetwork.Tailnet });
        FakeShoalPing.For(peer).Reply = new Reply(200, "{\"shoal\":true}");
        var shoal = await rig.Service.ProbeAsync(new ProbeRequest { Url = peer.BaseUrl, Network = PeerNetwork.Tailnet });
        FakeShoalPing.For(peer).Reply = new Reply(200, "{\"shoal\":\"yes\",\"version\":\"1\"}");
        var odd = await rig.Service.ProbeAsync(new ProbeRequest { Url = peer.BaseUrl, Network = PeerNetwork.Tailnet });
        FakeShoalPing.For(peer).Reply = new Reply(500, "boom");
        var broken = await rig.Service.ProbeAsync(new ProbeRequest { Url = peer.BaseUrl, Network = PeerNetwork.Tailnet });
        FakeShoalPing.For(peer).Reply = new Reply(429, "{}");
        var throttled = await rig.Service.ProbeAsync(new ProbeRequest { Url = peer.BaseUrl, Network = PeerNetwork.Tailnet });

        Assert.False(plain.HasShoal);
        Assert.True(shoal.HasShoal);
        Assert.True(shoal.Ok);
        Assert.False(odd.HasShoal);
        Assert.True(broken.Ok);
        Assert.False(broken.HasShoal);
        Assert.Contains("\"hasShoal\":true", JsonSerializer.Serialize(shoal), StringComparison.Ordinal);
        Assert.False(plain.ShoalPingThrottled);
        Assert.False(shoal.ShoalPingThrottled);
        Assert.False(throttled.HasShoal); // unknown, which is not the same as "no Shoal" ...
        Assert.True(throttled.ShoalPingThrottled); // ... and the page can say so
    }
}
