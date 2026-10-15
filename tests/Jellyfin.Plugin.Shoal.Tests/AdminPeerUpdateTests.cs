using System;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.Api.Admin;
using Jellyfin.Plugin.Shoal.Pairing;
using Jellyfin.Plugin.Shoal.Peers;
using Jellyfin.Plugin.Shoal.Security;
using Jellyfin.Plugin.Shoal.State;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Shoal.Tests;

/// <summary>
/// <c>PUT Shoal/Admin/Peers/{h}</c> (DESIGN 9.6): the Server detail page saved nothing, because the route did not exist. A new host needs the
/// name typed back and drops the token first (DESIGN 4.4, Warden N-8).
/// </summary>
public sealed class AdminPeerUpdateTests : IClassFixture<TlsPeerFixture>
{
    private const string Token = "9f2c4d6e8a0b1c3d5e7f9a0b2c4d6e8f";
    private const string Device = "shoal-1a2b3c4d5e6f-4b0c1d2e";
    private readonly TlsPeerFixture _tls;

    public AdminPeerUpdateTests(TlsPeerFixture tls) => _tls = tls;

    private sealed class Gate(bool live) : IPairingGate
    {
        public bool IsLive(string peerKey) => live;
    }

    private sealed class FailingRemove(ISecretStore inner) : ISecretStore
    {
        public SecretStoreStatus Status => inner.Status;

        public SecretStoreInfo Info => inner.Info;

        public System.Collections.Generic.IReadOnlyCollection<string>? PeerKeys => inner.PeerKeys;

        public PeerCredential? TryGet(string peerKey) => inner.TryGet(peerKey);

        public Task PutAsync(string peerKey, PeerCredential credential, System.Threading.CancellationToken cancellationToken = default) => inner.PutAsync(peerKey, credential, cancellationToken);

        public Task<bool> RemoveAsync(string peerKey, System.Threading.CancellationToken cancellationToken = default) => throw new IOException("disk full");

        public Task ResetAsync(System.Threading.CancellationToken cancellationToken = default) => inner.ResetAsync(cancellationToken);
    }

    private static async Task<(PairingRig Rig, AdminPeerSettingsController Controller)> SetUpAsync(Action<PeerState>? shape = null, bool pairingLive = false, Func<ISecretStore, ISecretStore>? wrapSecrets = null)
    {
        var rig = new PairingRig();
        await rig.State.UpdateAsync(s =>
        {
            StateSamples.Fill(s);
            shape?.Invoke(s.Home.Peers[0]);
        });
        await rig.Secrets.PutAsync(StateSamples.PeerKeyA, new PeerCredential(Token, Device, DateTime.UtcNow, PairingMethod.Qc));
        var gate = new Gate(pairingLive);
        var secrets = wrapSecrets is null ? rig.Secrets : wrapSecrets(rig.Secrets);
        var controller = new AdminPeerSettingsController(
            rig.State,
            new PeerDirectory(rig.State, rig.Secrets),
            rig.Client,
            rig.Registry,
            new PeerAddressService(rig.State, secrets, rig.Registry, gate),
            secrets,
            new Jellyfin.Plugin.Shoal.Visibility.ShoalLibraryService(rig.State, new FakeLibraries(), new Jellyfin.Plugin.Shoal.Visibility.VisibilityService(rig.State, new FakeLibraries(), new FakeAccounts(), rig.Time, NullLogger<Jellyfin.Plugin.Shoal.Visibility.VisibilityService>.Instance), rig.Time, NullLogger<Jellyfin.Plugin.Shoal.Visibility.ShoalLibraryService>.Instance),
            rig.Paths,
            NullLogger<AdminPeerSettingsController>.Instance,
            gate)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
        return (rig, controller);
    }

    private static async Task<IActionResult> PutAsync(AdminPeerSettingsController controller, string json, string handle = StateSamples.PeerKeyA)
    {
        controller.Request.ContentType = "application/json";
        controller.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(json));
        return await controller.Update(handle, default);
    }

    [Fact]
    public async Task TheLimitAndThePriority_AreSaved_AndTheConversionLimitFollowsTheStreamLimit()
    {
        var (rig, controller) = await SetUpAsync(p => p.MaxConcurrentTranscodes = 4);
        using (rig)
        {
            var body = AdminTestSupport.Body(await PutAsync(controller, """{"maxConcurrentStreams":2,"priority":7}"""));
            Assert.False(body.GetProperty("needsPairing").GetBoolean());
            var peer = rig.State.Current.Home.Peers[0];
            Assert.Equal(2, peer.MaxConcurrentStreams);
            Assert.Equal(7, peer.Priority);
            Assert.Equal(2, peer.MaxConcurrentTranscodes);
            Assert.NotNull(rig.Secrets.TryGet(StateSamples.PeerKeyA));
        }
    }

    [Theory]
    [InlineData("""{"maxConcurrentStreams":0}""")]
    [InlineData("""{"maxConcurrentStreams":17}""")]
    [InlineData("""{"priority":0}""")]
    [InlineData("""{"priority":1001}""")]
    [InlineData("not json")]
    public async Task OutOfRangeNumbersAndBrokenBodies_AreRefused_AndNothingChanges(string json)
    {
        var (rig, controller) = await SetUpAsync();
        using (rig)
        {
            AdminTestSupport.Body(await PutAsync(controller, json), 400);
            Assert.Equal(4, rig.State.Current.Home.Peers[0].MaxConcurrentStreams);
            Assert.Equal(100, rig.State.Current.Home.Peers[0].Priority);
        }
    }

    [Fact]
    public async Task ANewHost_NeedsTheNameTypedBack_AndChangesNothingWithoutIt()
    {
        var (rig, controller) = await SetUpAsync();
        using (rig)
        {
            var body = AdminTestSupport.Body(await PutAsync(controller, """{"url":"http://elsewhere.tail1234.ts.net:8096","maxConcurrentStreams":3}"""), 400);
            Assert.Equal("confirmName", body.GetProperty("error").GetString());
            AdminTestSupport.Body(await PutAsync(controller, """{"url":"http://elsewhere.tail1234.ts.net:8096","confirmName":"hops"}"""), 400);
            Assert.Equal("http://hops.tail1234.ts.net:8096", rig.State.Current.Home.Peers[0].BaseUrl);
            Assert.Equal(4, rig.State.Current.Home.Peers[0].MaxConcurrentStreams);
            Assert.NotNull(rig.Secrets.TryGet(StateSamples.PeerKeyA));
        }
    }

    [Fact]
    public async Task ANewHost_WithTheName_DropsTheTokenFirst_AndTheServerNeedsPairing()
    {
        var (rig, controller) = await SetUpAsync(p => p.TlsSpkiPin = Convert.ToBase64String(new byte[32]));
        using (rig)
        {
            var body = AdminTestSupport.Body(await PutAsync(controller, """{"url":"http://elsewhere.tail1234.ts.net:8096","confirmName":"Hops"}"""));
            Assert.True(body.GetProperty("needsPairing").GetBoolean());
            var peer = rig.State.Current.Home.Peers[0];
            Assert.Equal("http://elsewhere.tail1234.ts.net:8096", peer.BaseUrl);
            Assert.Null(peer.TlsSpkiPin);
            Assert.Null(rig.Secrets.TryGet(StateSamples.PeerKeyA));
            Assert.Equal(PeerStatus.AuthFailed, rig.Registry.StatusOf(StateSamples.PeerKeyA));
        }
    }

    [Fact]
    public async Task ANewHost_EndsTheSessionOnTheOldServerFirst_AndSaysSo()
    {
        await using var fake = await FakeJellyfinPeer.StartAsync();
        var (rig, controller) = await SetUpAsync(p => p.BaseUrl = fake.BaseUrl);
        using (rig)
        {
            var body = AdminTestSupport.Body(await PutAsync(controller, """{"url":"http://elsewhere.tail1234.ts.net:8096","confirmName":"Hops"}"""));

            Assert.True(body.GetProperty("needsPairing").GetBoolean());
            Assert.True(body.GetProperty("signedOut").GetBoolean());
            Assert.Equal(1, fake.Logouts);
            Assert.Null(rig.Secrets.TryGet(StateSamples.PeerKeyA));
        }
    }

    [Fact]
    public async Task ANewHost_WhenTheOldServerCannotBeReached_StillSaves_AndSaysTheSignOutWasSkipped()
    {
        var (rig, controller) = await SetUpAsync();
        using (rig)
        {
            rig.Client.Resolver = (_, _) => throw new System.Net.Sockets.SocketException();

            var body = AdminTestSupport.Body(await PutAsync(controller, """{"url":"http://elsewhere.tail1234.ts.net:8096","confirmName":"Hops"}"""));

            Assert.True(body.GetProperty("needsPairing").GetBoolean());
            Assert.False(body.GetProperty("signedOut").GetBoolean());
            Assert.Equal("http://elsewhere.tail1234.ts.net:8096", rig.State.Current.Home.Peers[0].BaseUrl);
        }
    }

    [Fact]
    public async Task WithoutAHostChange_NoSignOutIsSent()
    {
        var (rig, controller) = await SetUpAsync();
        using (rig)
        {
            var body = AdminTestSupport.Body(await PutAsync(controller, """{"priority":9}"""));

            Assert.False(body.GetProperty("needsPairing").GetBoolean());
            Assert.True(!body.TryGetProperty("signedOut", out var signedOut) || signedOut.ValueKind == System.Text.Json.JsonValueKind.Null);
        }
    }

    [Fact]
    public async Task AChangeOfPortOrScheme_KeepsTheTokenAndNeedsNoName()
    {
        var (rig, controller) = await SetUpAsync();
        using (rig)
        {
            var body = AdminTestSupport.Body(await PutAsync(controller, """{"url":"http://hops.tail1234.ts.net:8097"}"""));
            Assert.False(body.GetProperty("needsPairing").GetBoolean());
            Assert.Equal("http://hops.tail1234.ts.net:8097", rig.State.Current.Home.Peers[0].BaseUrl);
            Assert.NotNull(rig.Secrets.TryGet(StateSamples.PeerKeyA));
        }
    }

    [Fact]
    public async Task AnAddressTheNetworkPolicyRefuses_IsRefusedWithAReasonWord()
    {
        var (rig, controller) = await SetUpAsync();
        using (rig)
        {
            var body = AdminTestSupport.Body(await PutAsync(controller, """{"url":"ftp://hops.tail1234.ts.net/","confirmName":"Hops"}"""), 400);
            Assert.Equal("invalid", body.GetProperty("error").GetString());
            Assert.Matches("^[A-Za-z]{1,40}$", body.GetProperty("detail").GetString());
            Assert.Equal("http://hops.tail1234.ts.net:8096", rig.State.Current.Home.Peers[0].BaseUrl);
            Assert.NotNull(rig.Secrets.TryGet(StateSamples.PeerKeyA));
        }
    }

    [Fact]
    public async Task IfTheTokenCannotBeDropped_NoPartOfTheSaveHappens()
    {
        var (rig, controller) = await SetUpAsync(wrapSecrets: s => new FailingRemove(s));
        using (rig)
        {
            await Assert.ThrowsAsync<IOException>(() => PutAsync(controller, """{"url":"http://elsewhere.tail1234.ts.net:8096","confirmName":"Hops","maxConcurrentStreams":2,"priority":7,"proxyUrl":"socks5://127.0.0.1:1055"}"""));

            var peer = rig.State.Current.Home.Peers[0];
            Assert.Equal("http://hops.tail1234.ts.net:8096", peer.BaseUrl);
            Assert.Equal(4, peer.MaxConcurrentStreams);
            Assert.Equal(100, peer.Priority);
            Assert.Null(peer.ProxyUrl);
            Assert.NotNull(rig.Secrets.TryGet(StateSamples.PeerKeyA));
        }
    }

    [Fact]
    public async Task AnAddressAndAProxyAndTheLimitsChangeInOneSave()
    {
        var (rig, controller) = await SetUpAsync();
        using (rig)
        {
            var body = AdminTestSupport.Body(await PutAsync(controller, """{"url":"http://elsewhere.tail1234.ts.net:8096","confirmName":"Hops","maxConcurrentStreams":2,"priority":7,"proxyUrl":"socks5://127.0.0.1:1055"}"""));

            Assert.True(body.GetProperty("needsPairing").GetBoolean());
            var peer = rig.State.Current.Home.Peers[0];
            Assert.Equal("http://elsewhere.tail1234.ts.net:8096", peer.BaseUrl);
            Assert.Equal(2, peer.MaxConcurrentStreams);
            Assert.Equal(7, peer.Priority);
            Assert.Equal("socks5://127.0.0.1:1055", peer.ProxyUrl);
        }
    }

    [Fact]
    public async Task TheProxy_IsSetKeptAndCleared()
    {
        var (rig, controller) = await SetUpAsync();
        using (rig)
        {
            AdminTestSupport.Body(await PutAsync(controller, """{"proxyUrl":"socks5://127.0.0.1:1055"}"""));
            Assert.Equal("socks5://127.0.0.1:1055", rig.State.Current.Home.Peers[0].ProxyUrl);
            AdminTestSupport.Body(await PutAsync(controller, """{"priority":9}"""));
            Assert.Equal("socks5://127.0.0.1:1055", rig.State.Current.Home.Peers[0].ProxyUrl);
            AdminTestSupport.Body(await PutAsync(controller, """{"proxyUrl":null}"""));
            Assert.Null(rig.State.Current.Home.Peers[0].ProxyUrl);
            AdminTestSupport.Body(await PutAsync(controller, """{"proxyUrl":"http://proxy.example:3128"}"""), 400);
            Assert.Null(rig.State.Current.Home.Peers[0].ProxyUrl);
        }
    }

    [Fact]
    public async Task WhileAPairingLives_TheAddressAndTheProxyCannotChange_ButTheLimitsCan()
    {
        var (rig, controller) = await SetUpAsync(pairingLive: true);
        using (rig)
        {
            var body = AdminTestSupport.Body(await PutAsync(controller, """{"url":"http://hops.tail1234.ts.net:8097"}"""), 409);
            Assert.Equal("pairingActive", body.GetProperty("error").GetString());
            AdminTestSupport.Body(await PutAsync(controller, """{"proxyUrl":"socks5://127.0.0.1:1055"}"""), 409);
            AdminTestSupport.Body(await PutAsync(controller, """{"maxConcurrentStreams":6,"url":"http://hops.tail1234.ts.net:8096"}"""));
            Assert.Equal(6, rig.State.Current.Home.Peers[0].MaxConcurrentStreams);
        }
    }

    [Fact]
    public async Task AnUnknownServer_Is404()
    {
        var (rig, controller) = await SetUpAsync();
        using (rig)
        {
            AdminTestSupport.Body(await PutAsync(controller, """{"priority":5}""", "nope"), 404);
        }
    }

    [Fact]
    public async Task Pinning_ReadsTheKeyTheServerShows_AndUnpinningClearsIt()
    {
        var (rig, controller) = await SetUpAsync(p =>
        {
            p.BaseUrl = "https://hops.example:" + new Uri(_tls.BaseUrl).Port;
            p.Network = PeerNetwork.Public;
        });
        using (rig)
        {
            rig.Client.Resolver = (_, _) => Task.FromResult(new[] { IPAddress.Loopback });
            AdminTestSupport.Body(await PutAsync(controller, "{\"tlsPinned\":true,\"expectedPin\":\"" + TlsPin.Compute(_tls.Certificate) + "\",\"acceptUntrusted\":true}"));
            Assert.Equal(TlsPin.Compute(_tls.Certificate), rig.State.Current.Home.Peers[0].TlsSpkiPin);
            AdminTestSupport.Body(await PutAsync(controller, """{"tlsPinned":true,"priority":8}"""));
            Assert.Equal(TlsPin.Compute(_tls.Certificate), rig.State.Current.Home.Peers[0].TlsSpkiPin);
            AdminTestSupport.Body(await PutAsync(controller, """{"tlsPinned":false}"""));
            Assert.Null(rig.State.Current.Home.Peers[0].TlsSpkiPin);
        }
    }

    [Fact]
    public async Task Pinning_NeedsTheKeyTheAdminWasShown_AndRefusesAnInvalidChainUnlessAccepted()
    {
        var (rig, controller) = await SetUpAsync(p =>
        {
            p.BaseUrl = "https://hops.example:" + new Uri(_tls.BaseUrl).Port;
            p.Network = PeerNetwork.Public;
        });
        using (rig)
        {
            rig.Client.Resolver = (_, _) => Task.FromResult(new[] { IPAddress.Loopback });
            var shown = TlsPin.Compute(_tls.Certificate);

            Assert.Equal("expectedPin", AdminTestSupport.Body(await PutAsync(controller, """{"tlsPinned":true,"priority":8}"""), 400).GetProperty("error").GetString());
            var other = Convert.ToBase64String(SHA256.HashData("another key"u8));
            var changed = AdminTestSupport.Body(await PutAsync(controller, "{\"tlsPinned\":true,\"expectedPin\":\"" + other + "\",\"acceptUntrusted\":true}"), 409);
            Assert.Equal("changedAgain", changed.GetProperty("error").GetString());
            Assert.Equal(shown, changed.GetProperty("pin").GetString());
            var untrusted = AdminTestSupport.Body(await PutAsync(controller, "{\"tlsPinned\":true,\"expectedPin\":\"" + shown + "\"}"), 409);
            Assert.Equal("untrusted", untrusted.GetProperty("error").GetString());
            Assert.False(untrusted.GetProperty("chainValid").GetBoolean());
            Assert.Null(rig.State.Current.Home.Peers[0].TlsSpkiPin);
            Assert.Equal(100, rig.State.Current.Home.Peers[0].Priority);

            AdminTestSupport.Body(await PutAsync(controller, "{\"tlsPinned\":true,\"expectedPin\":\"" + shown + "\",\"acceptUntrusted\":true}"));
            Assert.Equal(shown, rig.State.Current.Home.Peers[0].TlsSpkiPin);
        }
    }

    [Fact]
    public async Task OnATailnet_PinningTheShownSelfSignedKey_NeedsNoSecondAcknowledgement()
    {
        var (rig, controller) = await SetUpAsync(p =>
        {
            p.BaseUrl = "https://hops.tail1234.ts.net:" + new Uri(_tls.BaseUrl).Port;
            p.Network = PeerNetwork.Tailnet;
        });
        using (rig)
        {
            rig.Client.Resolver = (_, _) => Task.FromResult(new[] { IPAddress.Loopback });
            AdminTestSupport.Body(await PutAsync(controller, "{\"tlsPinned\":true,\"expectedPin\":\"" + TlsPin.Compute(_tls.Certificate) + "\"}"));
            Assert.Equal(TlsPin.Compute(_tls.Certificate), rig.State.Current.Home.Peers[0].TlsSpkiPin);
        }
    }

    [Fact]
    public async Task Pinning_NeedsHttps_AndAServerThatCannotBeReachedIsNotPinned()
    {
        var (rig, controller) = await SetUpAsync();
        using (rig)
        {
            var body = AdminTestSupport.Body(await PutAsync(controller, """{"tlsPinned":true}"""), 400);
            Assert.Equal("pinNeedsHttps", body.GetProperty("error").GetString());
        }

        var (rig2, controller2) = await SetUpAsync(p =>
        {
            p.BaseUrl = "https://hops.example:1";
            p.Network = PeerNetwork.Public;
        });
        using (rig2)
        {
            rig2.Client.Resolver = (_, _) => Task.FromResult(new[] { IPAddress.Loopback });
            var body = AdminTestSupport.Body(await PutAsync(controller2, "{\"tlsPinned\":true,\"priority\":3,\"expectedPin\":\"" + TlsPin.Compute(_tls.Certificate) + "\"}"), 502);
            Assert.Equal("unreachable", body.GetProperty("error").GetString());
            Assert.Null(rig2.State.Current.Home.Peers[0].TlsSpkiPin);
            Assert.Equal(100, rig2.State.Current.Home.Peers[0].Priority);
        }
    }

    [Fact]
    public async Task ClearingThePin_LiftsATlsKeyChangedStop()
    {
        var (rig, controller) = await SetUpAsync(p => p.TlsSpkiPin = Convert.ToBase64String(new byte[32]));
        using (rig)
        {
            rig.Registry.SetStatus(StateSamples.PeerKeyA, PeerStatus.TlsKeyChanged);
            AdminTestSupport.Body(await PutAsync(controller, """{"tlsPinned":false}"""));
            Assert.Null(rig.State.Current.Home.Peers[0].TlsSpkiPin);
            Assert.Equal(PeerStatus.Offline, rig.Registry.StatusOf(StateSamples.PeerKeyA));
        }
    }
}
