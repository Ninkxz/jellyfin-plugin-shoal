using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.Api.Admin;
using Jellyfin.Plugin.Shoal.Peers;
using Jellyfin.Plugin.Shoal.State;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Shoal.Tests;

/// <summary>
/// <c>GET Shoal/Admin/Peers/{h}/Certificate</c> and <c>POST .../TrustCertificate</c> (DESIGN 4.4, 9.6, Warden 11 H1): the admin is shown the key a pinned server
/// presents, compares its fingerprint with the friend, and only then is that exact key pinned. The Server detail page showed the button and got a 404.
/// </summary>
public sealed class AdminTrustCertificateTests : IClassFixture<TlsPeerFixture>
{
    private const string DeviceId = "shoal-1a2b3c4d5e6f-4b0c1d2e";
    private const string Token = "0123456789abcdef0123456789abcdef";
    private readonly TlsPeerFixture _tls;

    public AdminTrustCertificateTests(TlsPeerFixture tls) => _tls = tls;

    private sealed class FixedEndpoints(PeerEndpoint? endpoint, Action? onFind = null) : IPeerEndpoints
    {
        public PeerEndpoint? Find(string peerKey)
        {
            onFind?.Invoke();
            return endpoint;
        }
    }

    private static string WrongPin() => Convert.ToBase64String(SHA256.HashData("another key"u8));

    private async Task<(PairingRig Rig, AdminPeerSettingsController Controller)> SetUpAsync(string url, string? pin, PeerStatus status, bool known = true, PeerNetwork network = PeerNetwork.Public, Action<PairingRig>? onFind = null)
    {
        var rig = new PairingRig();
        await rig.State.UpdateAsync(s =>
        {
            StateSamples.Fill(s);
            var peer = s.Home.Peers[0];
            peer.BaseUrl = url;
            peer.Network = network;
            peer.TlsSpkiPin = pin;
        });
        rig.Registry.SetStatus(StateSamples.PeerKeyA, status);
        var endpoint = known ? new PeerEndpoint(StateSamples.PeerKeyA, "Hops", new Uri(url), DeviceId, Token) { Network = network, TlsSpkiPin = pin } : null;
        var library = new Jellyfin.Plugin.Shoal.Visibility.ShoalLibraryService(rig.State, new FakeLibraries(), new Jellyfin.Plugin.Shoal.Visibility.VisibilityService(rig.State, new FakeLibraries(), new FakeAccounts(), rig.Time, NullLogger<Jellyfin.Plugin.Shoal.Visibility.VisibilityService>.Instance), rig.Time, NullLogger<Jellyfin.Plugin.Shoal.Visibility.ShoalLibraryService>.Instance);
        return (rig, new AdminPeerSettingsController(rig.State, new FixedEndpoints(endpoint, onFind is null ? null : () => onFind(rig)), rig.Client, rig.Registry, new PeerAddressService(rig.State, rig.Secrets, rig.Registry, rig.Service), rig.Secrets, library, rig.Paths, NullLogger<AdminPeerSettingsController>.Instance, rig.Service)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        });
    }

    private static async Task<IActionResult> TrustAsync(AdminPeerSettingsController controller, string json, string handle = StateSamples.PeerKeyA)
    {
        controller.Request.ContentType = "application/json";
        controller.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(json));
        return await controller.TrustCertificate(handle, default);
    }

    private string Shown() => TlsPin.Compute(_tls.Certificate);

    // ---------------- GET Certificate ----------------

    [Fact]
    public async Task TheCertificateRoute_ShowsThePinAFingerprintToReadAloudAndWhetherTheChainIsValid()
    {
        var (rig, controller) = await SetUpAsync(_tls.BaseUrl, WrongPin(), PeerStatus.TlsKeyChanged);
        using (rig)
        {
            var body = AdminTestSupport.Body(await controller.Certificate(StateSamples.PeerKeyA, default));
            Assert.Equal(Shown(), body.GetProperty("pin").GetString());
            var fingerprint = body.GetProperty("fingerprint").GetString()!;
            Assert.Matches("^([0-9A-F]{2}:){31}[0-9A-F]{2}$", fingerprint);
            Assert.Equal(Convert.ToHexString(SHA256.HashData(_tls.Certificate.PublicKey.ExportSubjectPublicKeyInfo())), fingerprint.Replace(":", string.Empty, StringComparison.Ordinal));
            Assert.False(body.GetProperty("chainValid").GetBoolean()); // a self-signed test certificate
            Assert.True(body.GetProperty("changed").GetBoolean());
            Assert.Matches("^([0-9A-F]{2}:){31}[0-9A-F]{2}$", body.GetProperty("pinnedFingerprint").GetString());
            Assert.Equal(WrongPin(), rig.State.Current.Home.Peers[0].TlsSpkiPin); // read-only
        }
    }

    [Fact]
    public async Task TheCertificateRoute_ForAServerThatIsNotPinned_HasNoPinnedFields()
    {
        var (rig, controller) = await SetUpAsync(_tls.BaseUrl, null, PeerStatus.Online);
        using (rig)
        {
            var body = AdminTestSupport.Body(await controller.Certificate(StateSamples.PeerKeyA, default));
            Assert.True(!body.TryGetProperty("changed", out var changed) || changed.ValueKind == System.Text.Json.JsonValueKind.Null);
            Assert.True(!body.TryGetProperty("pinnedFingerprint", out var pinned) || pinned.ValueKind == System.Text.Json.JsonValueKind.Null);
            Assert.Null(_tls.LastAuthorization);
        }
    }

    [Fact]
    public async Task TheCertificateRoute_Answers404_400ForPlainHttp_And502WhenTheServerCannotBeReached()
    {
        var (rig, controller) = await SetUpAsync(_tls.BaseUrl, null, PeerStatus.Online);
        using (rig)
        {
            AdminTestSupport.Body(await controller.Certificate("nope", default), 404);
        }

        var (plain, plainController) = await SetUpAsync("http://hops.tail1234.ts.net:8096", null, PeerStatus.Online, network: PeerNetwork.Tailnet);
        using (plain)
        {
            Assert.Equal("notHttps", AdminTestSupport.Body(await plainController.Certificate(StateSamples.PeerKeyA, default), 400).GetProperty("error").GetString());
        }

        var (down, downController) = await SetUpAsync("https://127.0.0.1:1/", null, PeerStatus.Online);
        using (down)
        {
            Assert.Equal("unreachable", AdminTestSupport.Body(await downController.Certificate(StateSamples.PeerKeyA, default), 502).GetProperty("error").GetString());
        }
    }

    // ---------------- POST TrustCertificate ----------------

    [Fact]
    public async Task TheKeyThatWasShown_IsPinnedAndTheStopIsLifted()
    {
        var (rig, controller) = await SetUpAsync(_tls.BaseUrl, WrongPin(), PeerStatus.TlsKeyChanged);
        using (rig)
        {
            var body = AdminTestSupport.Body(await TrustAsync(controller, "{\"expectedPin\":\"" + Shown() + "\",\"acceptUntrusted\":true}"));
            Assert.True(body.GetProperty("trusted").GetBoolean());
            Assert.Equal(Shown(), rig.State.Current.Home.Peers[0].TlsSpkiPin);
            Assert.Equal(PeerStatus.Offline, rig.Registry.StatusOf(StateSamples.PeerKeyA));
        }
    }

    [Fact]
    public async Task AfterTrusting_TokenCallsStayBlockedUntilTheServerIdHasBeenConfirmedOnTheNewKey()
    {
        var (rig, controller) = await SetUpAsync(_tls.BaseUrl, WrongPin(), PeerStatus.TlsKeyChanged);
        using (rig)
        {
            AdminTestSupport.Body(await TrustAsync(controller, "{\"expectedPin\":\"" + Shown() + "\",\"acceptUntrusted\":true}"));

            Assert.Equal(PeerStatus.Offline, rig.Registry.StatusOf(StateSamples.PeerKeyA));
            Assert.Equal(TrustDenial.Blocked, rig.Registry.Check(StateSamples.PeerKeyA, carriesToken: true, TrustRequirement.None)); // sign-out and rotation carry the token
            Assert.Equal(TrustDenial.Blocked, rig.Registry.Check(StateSamples.PeerKeyA, carriesToken: true, TrustRequirement.PinFresh));
            Assert.Equal(TrustDenial.None, rig.Registry.Check(StateSamples.PeerKeyA, carriesToken: false, TrustRequirement.None)); // the identity check itself

            rig.Registry.RecordPinVerified(StateSamples.PeerKeyA);

            Assert.Equal(TrustDenial.None, rig.Registry.Check(StateSamples.PeerKeyA, carriesToken: true, TrustRequirement.PinFresh));
            Assert.Equal(TrustDenial.None, rig.Registry.Check(StateSamples.PeerKeyA, carriesToken: true, TrustRequirement.None));
        }
    }

    [Fact]
    public async Task IfTheAddressChangesWhileTheKeyIsBeingRead_TheKeyIsNotPinned()
    {
        var pin = WrongPin();
        var (rig, controller) = await SetUpAsync(
            _tls.BaseUrl,
            pin,
            PeerStatus.TlsKeyChanged,
            onFind: r => r.State.UpdateAsync(s => s.Home.Peers[0].BaseUrl = "https://elsewhere.example:8920").GetAwaiter().GetResult());
        using (rig)
        {
            var body = AdminTestSupport.Body(await TrustAsync(controller, "{\"expectedPin\":\"" + Shown() + "\",\"acceptUntrusted\":true}"), 409);

            Assert.Equal("changedAgain", body.GetProperty("error").GetString());
            Assert.Equal(pin, rig.State.Current.Home.Peers[0].TlsSpkiPin);
            Assert.Equal(PeerStatus.TlsKeyChanged, rig.Registry.StatusOf(StateSamples.PeerKeyA));
        }
    }

    [Fact]
    public async Task AKeyThatChangedAgainSinceItWasShown_IsRefusedAndNothingIsPinned()
    {
        var pin = WrongPin();
        var (rig, controller) = await SetUpAsync(_tls.BaseUrl, pin, PeerStatus.TlsKeyChanged);
        using (rig)
        {
            var other = Convert.ToBase64String(SHA256.HashData("yet another key"u8));
            var body = AdminTestSupport.Body(await TrustAsync(controller, "{\"expectedPin\":\"" + other + "\",\"acceptUntrusted\":true}"), 409);
            Assert.Equal("changedAgain", body.GetProperty("error").GetString());
            Assert.Equal(Shown(), body.GetProperty("pin").GetString()); // the new key, so the page can show it
            Assert.Matches("^([0-9A-F]{2}:){31}[0-9A-F]{2}$", body.GetProperty("fingerprint").GetString());
            Assert.Equal(pin, rig.State.Current.Home.Peers[0].TlsSpkiPin);
            Assert.Equal(PeerStatus.TlsKeyChanged, rig.Registry.StatusOf(StateSamples.PeerKeyA));
        }
    }

    [Theory]
    [InlineData("""{}""")]
    [InlineData("""{"expectedPin":""}""")]
    [InlineData("""{"expectedPin":"nonsense"}""")]
    [InlineData("not json")]
    public async Task WithoutAnExpectedPin_NothingIsPinned(string json)
    {
        var pin = WrongPin();
        var (rig, controller) = await SetUpAsync(_tls.BaseUrl, pin, PeerStatus.TlsKeyChanged);
        using (rig)
        {
            var body = AdminTestSupport.Body(await TrustAsync(controller, json), 400);
            Assert.Equal("expectedPin", body.GetProperty("error").GetString());
            Assert.Equal(pin, rig.State.Current.Home.Peers[0].TlsSpkiPin);
        }
    }

    [Fact]
    public async Task APublicServerWhoseChainIsNotValid_IsRefusedUnlessTheAdminAcceptsIt()
    {
        var pin = WrongPin();
        var (rig, controller) = await SetUpAsync(_tls.BaseUrl, pin, PeerStatus.TlsKeyChanged);
        using (rig)
        {
            var refused = AdminTestSupport.Body(await TrustAsync(controller, "{\"expectedPin\":\"" + Shown() + "\"}"), 409);
            Assert.Equal("untrusted", refused.GetProperty("error").GetString());
            Assert.False(refused.GetProperty("chainValid").GetBoolean());
            Assert.Equal(pin, rig.State.Current.Home.Peers[0].TlsSpkiPin);
            AdminTestSupport.Body(await TrustAsync(controller, "{\"expectedPin\":\"" + Shown() + "\",\"acceptUntrusted\":false}"), 409);
            AdminTestSupport.Body(await TrustAsync(controller, "{\"expectedPin\":\"" + Shown() + "\",\"acceptUntrusted\":true}"));
            Assert.Equal(Shown(), rig.State.Current.Home.Peers[0].TlsSpkiPin);
        }
    }

    [Fact]
    public async Task OnATailnet_ASelfSignedKeyThatWasShown_NeedsNoSecondAcknowledgement()
    {
        var (rig, controller) = await SetUpAsync(_tls.BaseUrl, WrongPin(), PeerStatus.TlsKeyChanged, network: PeerNetwork.Tailnet);
        using (rig)
        {
            AdminTestSupport.Body(await TrustAsync(controller, "{\"expectedPin\":\"" + Shown() + "\"}"));
            Assert.Equal(Shown(), rig.State.Current.Home.Peers[0].TlsSpkiPin);
        }
    }

    [Fact]
    public async Task ReadingTheKey_SendsNoCredentialAtAll()
    {
        var (rig, controller) = await SetUpAsync(_tls.BaseUrl, WrongPin(), PeerStatus.TlsKeyChanged);
        using (rig)
        {
            await TrustAsync(controller, "{\"expectedPin\":\"" + Shown() + "\",\"acceptUntrusted\":true}");
            Assert.Null(_tls.LastAuthorization);
        }
    }

    [Fact]
    public async Task AServerThatIsNotStopped_IsNotChangedAndKeepsItsPin()
    {
        var pin = WrongPin();
        var (rig, controller) = await SetUpAsync(_tls.BaseUrl, pin, PeerStatus.Online);
        using (rig)
        {
            var body = AdminTestSupport.Body(await TrustAsync(controller, "{\"expectedPin\":\"" + Shown() + "\"}"), 409);
            Assert.Equal("notChanged", body.GetProperty("error").GetString());
            Assert.Equal(pin, rig.State.Current.Home.Peers[0].TlsSpkiPin);
        }
    }

    [Fact]
    public async Task AServerWithoutAPin_HasNothingToTrust()
    {
        var (rig, controller) = await SetUpAsync(_tls.BaseUrl, null, PeerStatus.TlsKeyChanged);
        using (rig)
        {
            var body = AdminTestSupport.Body(await TrustAsync(controller, "{\"expectedPin\":\"" + Shown() + "\"}"), 409);
            Assert.Equal("notPinned", body.GetProperty("error").GetString());
            Assert.Null(rig.State.Current.Home.Peers[0].TlsSpkiPin);
        }
    }

    [Fact]
    public async Task AnUnknownServer_Is404()
    {
        var (rig, controller) = await SetUpAsync(_tls.BaseUrl, WrongPin(), PeerStatus.TlsKeyChanged);
        using (rig)
        {
            AdminTestSupport.Body(await TrustAsync(controller, "{\"expectedPin\":\"" + Shown() + "\"}", "nope"), 404);
        }

        var (rig2, controller2) = await SetUpAsync(_tls.BaseUrl, WrongPin(), PeerStatus.TlsKeyChanged, known: false);
        using (rig2)
        {
            AdminTestSupport.Body(await TrustAsync(controller2, "{\"expectedPin\":\"" + Shown() + "\"}"), 404);
        }
    }

    [Fact]
    public async Task AServerThatCannotBeReached_ChangesNothing()
    {
        var pin = WrongPin();
        var (rig, controller) = await SetUpAsync("https://127.0.0.1:1/", pin, PeerStatus.TlsKeyChanged);
        using (rig)
        {
            var body = AdminTestSupport.Body(await TrustAsync(controller, "{\"expectedPin\":\"" + Shown() + "\"}"), 502);
            Assert.Equal("unreachable", body.GetProperty("error").GetString());
            Assert.Equal(pin, rig.State.Current.Home.Peers[0].TlsSpkiPin);
            Assert.Equal(PeerStatus.TlsKeyChanged, rig.Registry.StatusOf(StateSamples.PeerKeyA));
        }
    }

    [Fact]
    public async Task APlainHttpServer_ShowsNoKey()
    {
        using var rig = new PairingRig();
        var endpoint = new PeerEndpoint(StateSamples.PeerKeyA, "Hops", new Uri("http://127.0.0.1:1/"), DeviceId, Token) { Network = PeerNetwork.Public };
        Assert.Null(await rig.Client.ReadPresentedKeyAsync(endpoint, default));
    }

    [Fact]
    public async Task WithTheAddressPolicyOn_ALoopbackAddressIsNeverContacted()
    {
        using var rig = new PairingRig();
        rig.Client.EnforceAddressPolicy = true;
        var before = _tls.Requests;
        var endpoint = new PeerEndpoint(StateSamples.PeerKeyA, "Hops", new Uri(_tls.BaseUrl), DeviceId, Token) { Network = PeerNetwork.Public };
        Assert.Null(await rig.Client.ReadPresentedKeyAsync(endpoint, default));
        Assert.Equal(before, _tls.Requests);
    }
}
