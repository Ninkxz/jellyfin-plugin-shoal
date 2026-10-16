using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.Api.Admin;
using Jellyfin.Plugin.Shoal.Pairing;
using Jellyfin.Plugin.Shoal.Peers;
using Jellyfin.Plugin.Shoal.Security;
using Jellyfin.Plugin.Shoal.State;
using Jellyfin.Plugin.Shoal.Sync;
using Jellyfin.Plugin.Shoal.Visibility;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Shoal.Tests;

/// <summary>
/// <c>DELETE Shoal/Admin/Peers/{h}?deleteLibrary=</c> (DESIGN 9.6, ui-spec 8.3.8): "Remove server" did nothing but fail, because the route did not exist.
/// </summary>
public sealed class AdminPeerRemoveTests
{
    private const string Token = "9f2c4d6e8a0b1c3d5e7f9a0b2c4d6e8f";
    private const string Device = "shoal-1a2b3c4d5e6f-4b0c1d2e";

    private sealed class Gate(bool live, bool cancellable = false) : IPairingGate
    {
        public int Cancels { get; private set; }

        public bool IsLive(string peerKey) => live;

        public Task<bool> CancelAsync(string peerKey, CancellationToken cancellationToken = default)
        {
            Cancels++;
            live = !cancellable && live;
            return Task.FromResult(cancellable);
        }
    }

    private sealed class Activity(params bool[] answers) : ISyncActivity
    {
        private int _calls;

        public int Calls => _calls;

        public bool IsSyncing(string peerKey) => answers[Math.Min(_calls++, answers.Length - 1)];

        public int Stops { get; private set; }

        public Task StopAsync(string peerKey, TimeSpan wait)
        {
            Stops++;
            return Task.CompletedTask;
        }
    }

    private sealed class Rig : IDisposable
    {
        public required PairingRig Pairing { get; init; }

        public required FakeLibraries Libraries { get; init; }

        public required AdminPeerSettingsController Controller { get; init; }

        public required string StubFolder { get; init; }

        public required Gate Gate { get; init; }

        public string PeerFolder => Pairing.Paths.PeerDir(StateSamples.PeerKeyA);

        public void Dispose() => Pairing.Dispose();
    }

    private static async Task<Rig> SetUpAsync(bool pairingLive = false, ISyncActivity? sync = null, string? baseUrl = null, bool withCredential = true, bool cancellablePairing = false)
    {
        var rig = new PairingRig();
        var stubRoot = Path.Combine(rig.Dir.Path, "stubs");
        await rig.State.UpdateAsync(s =>
        {
            StateSamples.Fill(s);
            s.StubRoot = stubRoot;
            if (baseUrl is not null)
            {
                s.Home.Peers[0].BaseUrl = baseUrl;
            }
        });
        if (withCredential)
        {
            await rig.Secrets.PutAsync(StateSamples.PeerKeyA, new PeerCredential(Token, Device, DateTime.UtcNow, PairingMethod.Qc));
        }

        var libraries = new FakeLibraries();
        var accounts = new FakeAccounts();
        var admin = accounts.Add("admin", admin: true);
        var visibility = new VisibilityService(rig.State, libraries, accounts, rig.Time, NullLogger<VisibilityService>.Instance);
        var service = new ShoalLibraryService(rig.State, libraries, visibility, rig.Time, NullLogger<ShoalLibraryService>.Instance);
        var created = await service.CreateAsync(StateSamples.PeerKeyA, "movies-1", admin.Id);
        Assert.True(created.Ok);
        var folder = VisibilityService.MappingPath(stubRoot, StateSamples.PeerKeyA, "movies")!;
        File.WriteAllText(Path.Combine(folder, "stub.txt"), "x");
        Directory.CreateDirectory(rig.Paths.PeerDir(StateSamples.PeerKeyA));
        File.WriteAllText(Path.Combine(rig.Paths.PeerDir(StateSamples.PeerKeyA), "catalog.json"), "{}");
        var gate = new Gate(pairingLive, cancellablePairing);
        var controller = new AdminPeerSettingsController(
            rig.State,
            new PeerDirectory(rig.State, rig.Secrets),
            rig.Client,
            rig.Registry,
            new PeerAddressService(rig.State, rig.Secrets, rig.Registry, gate),
            rig.Secrets,
            service,
            rig.Paths,
            NullLogger<AdminPeerSettingsController>.Instance,
            gate,
            sync)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
        return new Rig { Pairing = rig, Libraries = libraries, Controller = controller, StubFolder = folder, Gate = gate };
    }

    private static async Task<IActionResult> RemoveAsync(Rig rig, string json, bool deleteLibrary, string handle = StateSamples.PeerKeyA)
    {
        rig.Controller.Request.ContentType = "application/json";
        rig.Controller.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(json));
        return await rig.Controller.Remove(handle, deleteLibrary, default);
    }

    [Fact]
    public async Task WithDeleteLibrary_TheServerItsKeyItsLibraryAndItsFilesAreGone()
    {
        using var rig = await SetUpAsync();
        Assert.Single(rig.Libraries.List());
        Assert.True(Directory.Exists(rig.StubFolder));

        var body = AdminTestSupport.Body(await RemoveAsync(rig, """{"confirmName":"Hops"}""", deleteLibrary: true));

        Assert.True(body.GetProperty("removed").GetBoolean());
        Assert.Equal(1, body.GetProperty("librariesRemoved").GetInt32());
        Assert.DoesNotContain(rig.Pairing.State.Current.Home.Peers, p => p.PeerKey == StateSamples.PeerKeyA);
        Assert.Null(rig.Pairing.Secrets.TryGet(StateSamples.PeerKeyA));
        Assert.Empty(rig.Libraries.List());
        Assert.Contains("purge:Hops Movies", rig.Libraries.Operations);
        Assert.False(Directory.Exists(rig.StubFolder));
        Assert.False(Directory.Exists(rig.PeerFolder));
    }

    [Fact]
    public async Task WithoutDeleteLibrary_TheServerAndItsKeyGo_ButTheLibraryAndTheFilesStay()
    {
        using var rig = await SetUpAsync();

        AdminTestSupport.Body(await RemoveAsync(rig, """{"confirmName":"Hops"}""", deleteLibrary: false));

        Assert.DoesNotContain(rig.Pairing.State.Current.Home.Peers, p => p.PeerKey == StateSamples.PeerKeyA);
        Assert.Null(rig.Pairing.Secrets.TryGet(StateSamples.PeerKeyA));
        Assert.Single(rig.Libraries.List());
        Assert.True(File.Exists(Path.Combine(rig.StubFolder, "stub.txt")));
        Assert.True(File.Exists(Path.Combine(rig.PeerFolder, "catalog.json")));
    }

    [Theory]
    [InlineData("""{"confirmName":"hops"}""")]
    [InlineData("""{"confirmName":""}""")]
    [InlineData("""{}""")]
    [InlineData("not json")]
    public async Task WithoutTheNameTypedBack_NothingIsRemoved(string json)
    {
        using var rig = await SetUpAsync();

        var body = AdminTestSupport.Body(await RemoveAsync(rig, json, deleteLibrary: true), 400);

        Assert.Equal("confirmName", body.GetProperty("error").GetString());
        Assert.Contains(rig.Pairing.State.Current.Home.Peers, p => p.PeerKey == StateSamples.PeerKeyA);
        Assert.NotNull(rig.Pairing.Secrets.TryGet(StateSamples.PeerKeyA));
        Assert.Single(rig.Libraries.List());
        Assert.True(Directory.Exists(rig.StubFolder));
    }

    [Fact]
    public async Task WhileASyncOfTheServerRuns_NothingIsRemoved()
    {
        var activity = new Activity(true);
        using var rig = await SetUpAsync(sync: activity);

        var body = AdminTestSupport.Body(await RemoveAsync(rig, """{"confirmName":"Hops"}""", deleteLibrary: true), 409);

        Assert.Equal("syncing", body.GetProperty("error").GetString());
        Assert.Contains(rig.Pairing.State.Current.Home.Peers, p => p.PeerKey == StateSamples.PeerKeyA && p.Enabled);
        Assert.NotNull(rig.Pairing.Secrets.TryGet(StateSamples.PeerKeyA));
        Assert.Single(rig.Libraries.List());
        Assert.True(Directory.Exists(rig.StubFolder));
    }

    [Fact]
    public async Task ASyncThatCannotBeStopped_StopsTheRemovalAndTheServerIsEnabledAgain()
    {
        var activity = new Activity(true, true); // still running after it was told to stop
        using var rig = await SetUpAsync(sync: activity);

        var body = AdminTestSupport.Body(await RemoveAsync(rig, """{"confirmName":"Hops"}""", deleteLibrary: true), 409);

        Assert.Equal("syncing", body.GetProperty("error").GetString());
        Assert.Equal(1, activity.Stops);
        Assert.Contains(rig.Pairing.State.Current.Home.Peers, p => p.PeerKey == StateSamples.PeerKeyA && p.Enabled);
        Assert.NotNull(rig.Pairing.Secrets.TryGet(StateSamples.PeerKeyA));
    }

    [Fact]
    public async Task WhenNoSyncRuns_TheServerIsDisabledFirstSoNoNewSyncCanStartBehindTheRemoval()
    {
        var activity = new Activity(false);
        using var rig = await SetUpAsync(sync: activity);

        AdminTestSupport.Body(await RemoveAsync(rig, """{"confirmName":"Hops"}""", deleteLibrary: true));

        Assert.True(activity.Calls >= 1);
        Assert.DoesNotContain(rig.Pairing.State.Current.Home.Peers, p => p.PeerKey == StateSamples.PeerKeyA);
    }

    [Fact]
    public async Task TheSessionOnTheServerIsEnded_EvenWhenTheServerIsPaused_AndTheAnswerSaysSo()
    {
        await using var fake = await FakeJellyfinPeer.StartAsync();
        using var rig = await SetUpAsync(baseUrl: fake.BaseUrl);
        await rig.Pairing.State.UpdateAsync(s => s.Home.Peers[0].Enabled = false); // the directory refuses a paused server, the credential is still there

        var body = AdminTestSupport.Body(await RemoveAsync(rig, """{"confirmName":"Hops"}""", deleteLibrary: true));

        Assert.True(body.GetProperty("signedOut").GetBoolean());
        Assert.Equal(1, fake.Logouts);
    }

    [Fact]
    public async Task AServerWithoutAKeyOrOutOfReach_IsRemovedAllTheSame_AndTheAnswerSaysNoSignOutWasSent()
    {
        using var rig = await SetUpAsync(withCredential: false);

        var body = AdminTestSupport.Body(await RemoveAsync(rig, """{"confirmName":"Hops"}""", deleteLibrary: true));

        Assert.False(body.GetProperty("signedOut").GetBoolean());
        Assert.DoesNotContain(rig.Pairing.State.Current.Home.Peers, p => p.PeerKey == StateSamples.PeerKeyA);
    }

    [Fact]
    public async Task AnUnknownServer_Is404()
    {
        using var rig = await SetUpAsync();
        AdminTestSupport.Body(await RemoveAsync(rig, """{"confirmName":"Hops"}""", true, "nope"), 404);
    }

    [Fact]
    public async Task ARunningSyncIsStopped_AndTheRemovalGoesThrough()
    {
        var activity = new Activity(true, false); // running when asked, ended after the stop
        using var rig = await SetUpAsync(sync: activity);

        AdminTestSupport.Body(await RemoveAsync(rig, """{"confirmName":"Hops"}""", deleteLibrary: true));

        Assert.Equal(1, activity.Stops);
        Assert.DoesNotContain(rig.Pairing.State.Current.Home.Peers, p => p.PeerKey == StateSamples.PeerKeyA);
    }

    [Fact]
    public async Task ALivePairing_IsCancelled_AndTheRemovalGoesThrough()
    {
        using var rig = await SetUpAsync(pairingLive: true, cancellablePairing: true);

        AdminTestSupport.Body(await RemoveAsync(rig, """{"confirmName":"Hops"}""", true));

        Assert.Equal(1, rig.Gate.Cancels);
        Assert.DoesNotContain(rig.Pairing.State.Current.Home.Peers, p => p.PeerKey == StateSamples.PeerKeyA);
    }

    [Fact]
    public async Task WhileAPairingLives_TheServerStays()
    {
        using var rig = await SetUpAsync(pairingLive: true);

        var body = AdminTestSupport.Body(await RemoveAsync(rig, """{"confirmName":"Hops"}""", true), 409);

        Assert.Equal("pairingActive", body.GetProperty("error").GetString());
        Assert.Contains(rig.Pairing.State.Current.Home.Peers, p => p.PeerKey == StateSamples.PeerKeyA);
    }

    [Fact]
    public async Task AFolderThatIsALink_IsNeverFollowed()
    {
        using var rig = await SetUpAsync();
        var outside = Path.Combine(rig.Pairing.Dir.Path, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "keep.txt"), "x");
        Directory.Delete(rig.StubFolder, recursive: true);
        try
        {
            Directory.CreateSymbolicLink(rig.StubFolder, outside);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return; // no right to make links on this machine
        }

        AdminTestSupport.Body(await RemoveAsync(rig, """{"confirmName":"Hops"}""", deleteLibrary: true));

        Assert.True(File.Exists(Path.Combine(outside, "keep.txt")));
    }
}
