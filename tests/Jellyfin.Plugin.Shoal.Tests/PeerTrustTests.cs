using System;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.Peers;
using Jellyfin.Plugin.Shoal.State;
using Xunit;

namespace Jellyfin.Plugin.Shoal.Tests;

/// <summary>
/// K4 (DESIGN 4.4 "Trust window", 8.3 T13; Warden T-PAIR-4): no token-bearing request is sent unless the server id was confirmed
/// unauthenticated within the last 120 s, and none after the pin fails.
/// </summary>
public sealed class PeerTrustTests : IClassFixture<PeerServerFixture>
{
    private const string Token = "0123456789abcdef0123456789abcdef";
    private const string DeviceId = "shoal-1a2b3c4d5e6f-4b0c1d2e";
    private const string PinnedId = "0123456789abcdef0123456789abcdef";

    private static int _keyCounter;
    private readonly PeerServerFixture _server;

    public PeerTrustTests(PeerServerFixture server)
    {
        _server = server;
        _server.PublicServerId = PinnedId;
        _server.MeStatus = 200;
    }

    private static string NewKey() => Convert.ToHexStringLower(BitConverter.GetBytes(Interlocked.Increment(ref _keyCounter))).PadLeft(32, '0');

    private PeerEndpoint Peer(string? key = null, string? pinned = PinnedId) =>
        new(key ?? NewKey(), "peer", new Uri(_server.BaseUrl), DeviceId, Token, "K7QM") { PinnedServerId = pinned };

    private (PeerClient Client, PeerRegistry Registry, PeerTrust Trust, ManualTimeProvider Time) Rig()
    {
        var time = new ManualTimeProvider();
        var registry = new PeerRegistry(time);
        var client = new PeerClient(() => "Home", () => "0.1.0.0", time, registry) { EnforceAddressPolicy = false };
        return (client, registry, new PeerTrust(client, registry, time), time);
    }

    private int TokenBearingRequests() => _server.Requests.Count(r => r.Headers.TryGetValue("Authorization", out var a) && a.Contains("Token=", StringComparison.Ordinal));

    [Fact]
    public void Registry_StartsOffline_AndAnUnverifiedPeerIsNotTrusted()
    {
        var registry = new PeerRegistry(new ManualTimeProvider());

        Assert.Equal(PeerStatus.Offline, registry.StatusOf("k"));
        Assert.Equal(TrustDenial.None, registry.Check("k", carriesToken: false, TrustRequirement.PinFresh));
        Assert.Equal(TrustDenial.PinStale, registry.Check("k", carriesToken: true, TrustRequirement.PinFresh));
        Assert.Equal(TrustDenial.None, registry.Check("k", carriesToken: true, TrustRequirement.None));
    }

    [Fact]
    public void Registry_TheWindowIs120Seconds_ForThePinAndForTheToken()
    {
        var time = new ManualTimeProvider();
        var registry = new PeerRegistry(time);
        registry.RecordPinVerified("k");

        time.Advance(TimeSpan.FromSeconds(120));
        Assert.Equal(TrustDenial.None, registry.Check("k", true, TrustRequirement.PinFresh));
        Assert.Equal(TrustDenial.AuthStale, registry.Check("k", true, TrustRequirement.PinAndAuthFresh));

        registry.RecordAuthVerified("k");
        Assert.Equal(TrustDenial.None, registry.Check("k", true, TrustRequirement.PinAndAuthFresh));

        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(TrustDenial.PinStale, registry.Check("k", true, TrustRequirement.PinFresh));
        Assert.Equal(TrustDenial.PinStale, registry.Check("k", true, TrustRequirement.PinAndAuthFresh));
    }

    [Fact]
    public void Registry_AMismatchStopsEverything_AndClearsWhatWasTrusted_UntilTheAdminActs()
    {
        var registry = new PeerRegistry(new ManualTimeProvider());
        registry.RecordPinVerified("k");
        registry.RecordAuthVerified("k");

        registry.SetStatus("k", PeerStatus.IdentityMismatch);

        Assert.Equal(TrustDenial.Blocked, registry.Check("k", false, TrustRequirement.None));
        Assert.Equal(TrustDenial.Blocked, registry.Check("k", true, TrustRequirement.None));
        registry.RecordPinVerified("k");
        registry.RecordAuthVerified("k");
        Assert.Null(registry.PinVerifiedUtc("k"));
        Assert.Null(registry.AuthVerifiedUtc("k"));

        registry.SetStatus("k", PeerStatus.Offline); // "Re-trust"
        registry.RecordPinVerified("k");
        Assert.Equal(TrustDenial.None, registry.Check("k", true, TrustRequirement.PinFresh));
    }

    [Fact]
    public void Registry_AuthFailedStopsTokenBearingCallsOnly()
    {
        var registry = new PeerRegistry(new ManualTimeProvider());
        registry.SetStatus("k", PeerStatus.AuthFailed);

        Assert.Equal(TrustDenial.None, registry.Check("k", false, TrustRequirement.None));
        Assert.Equal(TrustDenial.Blocked, registry.Check("k", true, TrustRequirement.None));
        Assert.Equal(TrustDenial.Blocked, registry.Check("k", true, TrustRequirement.PinFresh));
    }

    [Fact]
    public async Task T_PAIR_4_NoTokenIsSentUntilThePinWasConfirmed()
    {
        var (client, _, _, _) = Rig();
        var peer = Peer();
        var before = _server.Requests.Count;

        var failure = await Assert.ThrowsAsync<PeerCallException>(() => client.SendAsync(peer, PeerRequest.Get("/ok-json"), PeerCallProfile.Info, CancellationToken.None));

        Assert.Equal(PeerFailureKind.TrustLapsed, failure.Kind);
        Assert.Equal(before, _server.Requests.Count);
    }

    [Fact]
    public async Task T_PAIR_4_AVerifiedPin_OpensTheWindow_AndItClosesAfter120Seconds()
    {
        var (client, _, trust, time) = Rig();
        var peer = Peer();

        Assert.Equal(PinOutcome.Verified, await trust.VerifyPinAsync(peer));
        using (var ok = await client.SendAsync(peer, PeerRequest.Get("/ok-json"), PeerCallProfile.Info, CancellationToken.None))
        {
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        }

        // The identity check itself carried no token.
        var identityCheck = _server.Last("/System/Info/Public");
        Assert.DoesNotContain("Token=", identityCheck.Headers["Authorization"], StringComparison.Ordinal);

        time.Advance(TimeSpan.FromSeconds(121));
        var bearing = TokenBearingRequests();
        var failure = await Assert.ThrowsAsync<PeerCallException>(() => client.SendAsync(peer, PeerRequest.Get("/ok-json"), PeerCallProfile.Info, CancellationToken.None));

        Assert.Equal(PeerFailureKind.TrustLapsed, failure.Kind);
        Assert.Equal(bearing, TokenBearingRequests());
    }

    [Fact]
    public async Task T_PAIR_4_APinThatFails_StopsEverything_AndNoTokenIsEverSentAgain()
    {
        var (client, registry, trust, time) = Rig();
        var peer = Peer();
        Assert.Equal(PinOutcome.Verified, await trust.VerifyPinAsync(peer));
        _server.PublicServerId = "ffffffffffffffffffffffffffffff00"; // a reset server, or another one on the same address
        time.Advance(TimeSpan.FromSeconds(30));
        var bearing = TokenBearingRequests();

        Assert.Equal(PinOutcome.Mismatch, await trust.VerifyPinAsync(peer));

        Assert.Equal(PeerStatus.IdentityMismatch, registry.StatusOf(peer.PeerKey));
        var all = _server.Requests.Count;
        foreach (var request in new[] { PeerRequest.Get("/ok-json"), PeerRequest.GetAnonymous("/ok-json"), PeerRequest.Probe(), PeerRequest.Get("/Users/Me").WithTrust(TrustRequirement.None) })
        {
            var failure = await Assert.ThrowsAsync<PeerCallException>(() => client.SendAsync(peer, request, PeerCallProfile.Info, CancellationToken.None));
            Assert.Equal(PeerFailureKind.Blocked, failure.Kind);
        }

        Assert.Equal(all, _server.Requests.Count);
        Assert.Equal(bearing, TokenBearingRequests());

        // Even the identity check is not repeated by itself; the admin has to act.
        Assert.Equal(PinOutcome.Mismatch, await trust.VerifyPinAsync(peer));
        Assert.Equal(all, _server.Requests.Count);

        // The admin re-trusts (here: status reset), the server is the right one again, and the window can open.
        _server.PublicServerId = PinnedId;
        registry.SetStatus(peer.PeerKey, PeerStatus.Offline);
        Assert.Equal(PinOutcome.Verified, await trust.VerifyPinAsync(peer));
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("")]
    [InlineData("0123456789abcdef")]
    public async Task AnAnswerThatIsNotAServerId_NeverOpensTheWindow(string answer)
    {
        var (client, registry, trust, _) = Rig();
        var peer = Peer();
        _server.PublicServerId = answer;

        Assert.Equal(PinOutcome.Unavailable, await trust.VerifyPinAsync(peer));

        Assert.NotEqual(PeerStatus.IdentityMismatch, registry.StatusOf(peer.PeerKey));
        await Assert.ThrowsAsync<PeerCallException>(() => client.SendAsync(peer, PeerRequest.Get("/ok-json"), PeerCallProfile.Info, CancellationToken.None));
    }

    [Fact]
    public async Task TheServerId_IsComparedInItsCanonicalForm()
    {
        var (_, _, trust, _) = Rig();
        _server.PublicServerId = "01234567-89AB-CDEF-0123-456789ABCDEF";

        Assert.Equal(PinOutcome.Verified, await trust.VerifyPinAsync(Peer(pinned: "0123456789ABCDEF0123456789abcdef")));
    }

    [Fact]
    public async Task APeerWithoutAPinnedId_HasNothingToCompare()
    {
        var (client, _, trust, _) = Rig();
        var peer = Peer(pinned: null);
        var before = _server.Requests.Count;

        Assert.Equal(PinOutcome.NotPinned, await trust.VerifyPinAsync(peer));

        Assert.Equal(before, _server.Requests.Count);
        await Assert.ThrowsAsync<PeerCallException>(() => client.SendAsync(peer, PeerRequest.Get("/ok-json"), PeerCallProfile.Info, CancellationToken.None));
    }

    [Fact]
    public async Task TheTokenCheck_OpensTheAuthWindow_AndARefusalMakesTheStatusAuthFailed()
    {
        var (client, registry, trust, _) = Rig();
        var peer = Peer();
        await trust.VerifyPinAsync(peer);

        Assert.Equal(AuthOutcome.Verified, await trust.VerifyAuthAsync(peer));
        using (var response = await client.SendAsync(peer, PeerRequest.GetMedia("/ok-json").WithTrust(TrustRequirement.PinAndAuthFresh), PeerCallProfile.Info, CancellationToken.None))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        _server.MeStatus = 401;
        Assert.Equal(AuthOutcome.Refused, await trust.VerifyAuthAsync(peer));

        Assert.Equal(PeerStatus.AuthFailed, registry.StatusOf(peer.PeerKey));
        var bearing = TokenBearingRequests();
        var failure = await Assert.ThrowsAsync<PeerCallException>(() => client.SendAsync(peer, PeerRequest.Get("/ok-json"), PeerCallProfile.Info, CancellationToken.None));
        Assert.Equal(PeerFailureKind.Blocked, failure.Kind);
        Assert.Equal(bearing, TokenBearingRequests());
        Assert.Equal(AuthOutcome.Unavailable, await trust.VerifyAuthAsync(peer)); // no re-login loop: the check is blocked too
    }

    [Fact]
    public async Task StaticProxying_NeedsTheTokenConfirmedWithinTheWindowToo()
    {
        var (client, _, trust, time) = Rig();
        var peer = Peer();
        await trust.VerifyPinAsync(peer);
        var staticRequest = PeerRequest.GetMedia("/ok-json").WithTrust(TrustRequirement.PinAndAuthFresh);

        var failure = await Assert.ThrowsAsync<PeerCallException>(() => client.SendAsync(peer, staticRequest, PeerCallProfile.Info, CancellationToken.None));
        Assert.Equal(PeerFailureKind.TrustLapsed, failure.Kind);

        await trust.VerifyAuthAsync(peer);
        using (await client.SendAsync(peer, staticRequest, PeerCallProfile.Info, CancellationToken.None))
        {
        }

        time.Advance(TimeSpan.FromSeconds(90));
        await trust.VerifyPinAsync(peer); // the pin is refreshed, the token check is not
        time.Advance(TimeSpan.FromSeconds(40));
        await trust.VerifyPinAsync(peer);
        failure = await Assert.ThrowsAsync<PeerCallException>(() => client.SendAsync(peer, staticRequest, PeerCallProfile.Info, CancellationToken.None));
        Assert.Equal(PeerFailureKind.TrustLapsed, failure.Kind);
    }

    [Fact]
    public async Task EnsureAsync_RefreshesWhatIsStale_AtMostOncePerTenSeconds()
    {
        var (_, _, trust, time) = Rig();
        var peer = Peer();
        var before = _server.Count("/System/Info/Public");

        Assert.True(await trust.EnsureAsync(peer, TrustRequirement.PinAndAuthFresh));
        Assert.True(await trust.EnsureAsync(peer, TrustRequirement.PinAndAuthFresh));
        Assert.Equal(1, _server.Count("/System/Info/Public") - before);

        time.Advance(TimeSpan.FromSeconds(125));
        _server.PublicServerId = "ffffffffffffffffffffffffffffff00";
        Assert.False(await trust.EnsureAsync(peer, TrustRequirement.PinFresh));
        Assert.False(await trust.EnsureAsync(peer, TrustRequirement.PinFresh));
        Assert.Equal(2, _server.Count("/System/Info/Public") - before);
    }

    [Fact]
    public async Task EnsureAsync_DoesNotHammerAPeerThatIsDown()
    {
        var time = new ManualTimeProvider();
        var registry = new PeerRegistry(time);
        var client = new PeerClient(() => "Home", () => "0.1.0.0", time, registry) { EnforceAddressPolicy = false };
        var trust = new PeerTrust(client, registry, time);
        var dead = new PeerEndpoint(NewKey(), "dead", new Uri("http://127.0.0.1:1"), DeviceId, Token, null) { PinnedServerId = PinnedId };

        for (var i = 0; i < 20; i++)
        {
            Assert.False(await trust.EnsureAsync(dead, TrustRequirement.PinFresh));
        }

        Assert.Equal(1, client.BreakerOf(dead).ConsecutiveFailures);
        time.Advance(TimeSpan.FromSeconds(11));
        Assert.False(await trust.EnsureAsync(dead, TrustRequirement.PinFresh));
        Assert.Equal(2, client.BreakerOf(dead).ConsecutiveFailures);
    }
}
