using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.Api.Admin;
using Jellyfin.Plugin.Shoal.Security;
using Jellyfin.Plugin.Shoal.State;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Shoal.Tests;

/// <summary>
/// U2 (DESIGN 11.2): the Home endpoints of the Overview, against the real state store and stubbed live data. Acceptance:
/// ui-spec F5 (no token, invite or peer-side id in any admin JSON), cooldowns from <c>Retry-After</c>, admin policy on every action.
/// </summary>
public sealed class AdminControllerTests
{
    private static readonly Regex Guid = new(@"[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}", RegexOptions.IgnoreCase);

    private sealed class FakeSecrets : ISecretStore
    {
        public SecretStoreStatus Status { get; set; } = SecretStoreStatus.Ready;

        public SecretStoreInfo Info => new() { Status = Status, KeySource = KeySource.KeyFile };

        public IReadOnlyCollection<string>? PeerKeys => Status == SecretStoreStatus.Ready ? [] : null;

        public PeerCredential? TryGet(string peerKey) => null;

        public Task PutAsync(string peerKey, PeerCredential credential, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<bool> RemoveAsync(string peerKey, CancellationToken cancellationToken = default) => Task.FromResult(false);

        public Task ResetAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FixedName : IHomeNameSource
    {
        public string? Name => "Rich's Jellyfin";
    }

    private sealed class Live : IPeerLiveStatus
    {
        public int SyncsStarted { get; private set; }

        public PeerLiveInfo ForPeer(string peerKey) => new() { Status = "Online", Path = "direct", RttMs = 38, Mbps = 42, TitleCount = 1204 };

        public ShareLiveInfo ForShare(string shareId) => new() { PlayingNow = 2, TranscodingNow = 1, UnexpectedDeviceCount = 1, LastSeenUtc = new DateTime(2026, 10, 14, 21, 0, 0, DateTimeKind.Utc) };

        public int StartSyncAll() => ++SyncsStarted;

        public bool StartSync(string peerKey) => true;
    }

    private static (AdminController Controller, StateStore Store, ManualTimeProvider Time, TempDirectory Dir) Create(Action<TempDirectory>? beforeOpen = null, IPeerLiveStatus? live = null, FakeSecrets? secrets = null, Jellyfin.Plugin.Shoal.Visibility.IAccountSource? accounts = null, Jellyfin.Plugin.Shoal.Playback.InterceptCounters? intercept = null, Jellyfin.Plugin.Shoal.Sync.IImageSandboxStatus? sandbox = null, Jellyfin.Plugin.Shoal.Sync.PeerCatalogReader? reader = null, Func<StateStore, ILibraryView>? view = null)
    {
        var dir = new TempDirectory();
        beforeOpen?.Invoke(dir);
        var time = new ManualTimeProvider();
        var store = new StateStore(new Jellyfin.Plugin.Shoal.Storage.ShoalPaths(dir.Path), NullLogger.Instance, time, null);
        var controller = new AdminController(store, secrets ?? new FakeSecrets(), live ?? new UnknownPeerLiveStatus(), new FixedName(), new AdminCooldowns(time), new NoGuardCounters(), view?.Invoke(store) ?? new NoLibraryView(), NullLogger<AdminController>.Instance, accounts, intercept, null, sandbox, reader)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        return (controller, store, time, dir);
    }

    private static JsonElement Body(IActionResult result, int expectedStatus = 200) => AdminTestSupport.Body(result, expectedStatus);

    private sealed class FakeSandbox(Jellyfin.Plugin.Shoal.Sync.ImageSandboxStatus status) : Jellyfin.Plugin.Shoal.Sync.IImageSandboxStatus
    {
        public Jellyfin.Plugin.Shoal.Sync.ImageSandboxStatus GetStatus() => status;

        public void RequestSelfTest()
        {
        }
    }

    [Fact]
    public void Status_CarriesTheImageSandbox_StateLimitsReasonAndRejected()
    {
        var sandbox = new FakeSandbox(new Jellyfin.Plugin.Shoal.Sync.ImageSandboxStatus("unavailable", "partial", "ffmpegMissing", 3));
        var (controller, store, _, dir) = Create(sandbox: sandbox);
        using (dir)
        using (store)
        {
            var json = Body(controller.GetStatus()).GetProperty("imageSandbox");

            Assert.Equal("unavailable", json.GetProperty("state").GetString());
            Assert.Equal("partial", json.GetProperty("limits").GetString());
            Assert.Equal("ffmpegMissing", json.GetProperty("reason").GetString());
            Assert.Equal(3, json.GetProperty("rejected").GetInt64());
        }
    }

    [Fact]
    public void Status_OnANewInstall_IsEmptyAndNormal()
    {
        var (controller, store, _, dir) = Create();
        using (dir)
        using (store)
        {
            var json = Body(controller.GetStatus());

            Assert.Equal(0, json.GetProperty("servers").GetArrayLength());
            Assert.Equal(0, json.GetProperty("friends").GetArrayLength());
            Assert.Equal("Normal", json.GetProperty("state").GetProperty("mode").GetString());
            Assert.False(json.GetProperty("secretsUnreadable").GetBoolean());
            Assert.Equal("file", json.GetProperty("keySource").GetString());
            Assert.Equal("Rich's Jellyfin", json.GetProperty("homeName").GetString());
        }
    }

    [Fact]
    public void Status_CarriesTheInterceptCounters_WithNoUrlIdOrUserAgent()
    {
        var counters = new Jellyfin.Plugin.Shoal.Playback.InterceptCounters(new ManualTimeProvider());
        counters.Increment(Jellyfin.Plugin.Shoal.Playback.InterceptCounters.AudioRoute);
        counters.Increment(Jellyfin.Plugin.Shoal.Playback.InterceptCounters.AudioRoute);
        var (controller, store, _, dir) = Create(intercept: counters);
        using (dir)
        using (store)
        {
            var json = Body(controller.GetStatus());

            var intercept = json.GetProperty("intercept");
            Assert.True(intercept.GetProperty("sinceUtc").GetDateTime() > DateTime.MinValue);
            var listed = intercept.GetProperty("counters").EnumerateArray().ToDictionary(static c => c.GetProperty("name").GetString()!, static c => c.GetProperty("count").GetInt64());
            Assert.Equal(Jellyfin.Plugin.Shoal.Playback.InterceptCounters.Names, listed.Keys);
            Assert.Equal(2, listed["audio-route"]);
            Assert.Equal(0, listed["nonstatic-stream"]);
            Assert.All(intercept.EnumerateObject().Concat(intercept.GetProperty("counters").EnumerateArray().SelectMany(static c => c.EnumerateObject())).Select(static p => p.Name), name => Assert.True(name is "sinceUtc" or "counters" or "name" or "count", name));
        }
    }

    [Fact]
    public async Task Status_ShowsHowOftenOptionalDataWasLeftOut_NextToTheInterceptCounters()
    {
        await using var rig = await SyncRig.CreateAsync(p => p.Add("The Matrix", 1999, tmdb: 603, configure: m =>
        {
            m.Sources = [new PeerSource { Id = m.Id }];
            m.SourceExtra = new Dictionary<string, object?> { ["MediaAttachments"] = new[] { new Dictionary<string, object?> { ["Index"] = new Dictionary<string, object?> { ["x"] = 1 } } } };
        }));
        await rig.SyncAsync();
        var counters = new Jellyfin.Plugin.Shoal.Playback.InterceptCounters(new ManualTimeProvider());
        var (controller, store, _, dir) = Create(intercept: counters, reader: rig.Reader);
        using (dir)
        using (store)
        {
            var json = Body(controller.GetStatus());

            var listed = json.GetProperty("intercept").GetProperty("counters").EnumerateArray().ToDictionary(static c => c.GetProperty("name").GetString()!, static c => c.GetProperty("count").GetInt64());
            Assert.Equal(1, listed["optional-data-dropped"]);
            Assert.Equal(0, listed["audio-route"]);
        }
    }

    [Fact]
    public void Status_ListsSharingAccountsNoShareUses_ForTheAdminToDelete()
    {
        var accounts = new FakeAccounts();
        var left = accounts.Add("shoal-carol-3c4d");
        accounts.Add("Alex");
        var (controller, store, _, dir) = Create(accounts: accounts);
        using (dir)
        using (store)
        {
            var json = Body(controller.GetStatus());

            var item = Assert.Single(json.GetProperty("orphanAccounts").EnumerateArray());
            Assert.Equal("shoal-carol-3c4d", item.GetProperty("name").GetString());
            Assert.Equal(left.Id, item.GetProperty("id").GetGuid());
            Assert.Empty(accounts.Writes);
        }
    }

    [Fact]
    public async Task Status_ListsPeersAndFriendsFromTheState_WithCamelCaseNamesAndHandlesOnly()
    {
        var (controller, store, _, dir) = Create(live: new Live());
        using (dir)
        using (store)
        {
            await store.UpdateAsync(StateSamples.Fill);

            var json = Body(controller.GetStatus());

            var server = Assert.Single(json.GetProperty("servers").EnumerateArray());
            Assert.Equal(StateSamples.PeerKeyA, server.GetProperty("handle").GetString());
            Assert.Equal("Hops", server.GetProperty("displayName").GetString());
            Assert.Equal("Tailnet", server.GetProperty("network").GetString());
            Assert.Equal("Online", server.GetProperty("status").GetString());
            Assert.Equal(1204, server.GetProperty("titleCount").GetInt32());
            Assert.Equal(4, server.GetProperty("activeStreams").GetProperty("max").GetInt32());
            var friend = Assert.Single(json.GetProperty("friends").EnumerateArray());
            Assert.Equal("share-bob", friend.GetProperty("handle").GetString());
            Assert.Equal("Bob", friend.GetProperty("name").GetString());
            Assert.Equal("Active", friend.GetProperty("status").GetString());
            Assert.Equal(2, friend.GetProperty("playingNow").GetInt32());
            Assert.Equal(1, friend.GetProperty("unexpectedDeviceCount").GetInt32());
        }
    }

    [Fact]
    public async Task Status_ANetworkOfTypeLan_IsSpelledLikeTheDesign()
    {
        var (controller, store, _, dir) = Create();
        using (dir)
        using (store)
        {
            await store.UpdateAsync(s =>
            {
                StateSamples.Fill(s);
                s.Home.Peers[0].Network = PeerNetwork.Lan;
            });

            var server = Assert.Single(Body(controller.GetStatus()).GetProperty("servers").EnumerateArray());

            Assert.Equal("LAN", server.GetProperty("network").GetString());
        }
    }

    [Fact]
    public async Task Status_NothingMeasuredYet_IsNeverShownAsOnline()
    {
        var (controller, store, _, dir) = Create();
        using (dir)
        using (store)
        {
            await store.UpdateAsync(StateSamples.Fill);

            var server = Assert.Single(Body(controller.GetStatus()).GetProperty("servers").EnumerateArray());

            Assert.Equal("Offline", server.GetProperty("status").GetString());
            Assert.True(!server.TryGetProperty("offlineSinceUtc", out var since) || since.ValueKind == JsonValueKind.Null, "no invented time");
            Assert.True(!server.TryGetProperty("mbps", out var mbps) || mbps.ValueKind == JsonValueKind.Null, "no invented speed");
        }
    }

    [Fact]
    public async Task F5_NoAdminResponseCarriesATokenAnInviteOrAnId()
    {
        var (controller, store, _, dir) = Create(live: new Live());
        using (dir)
        using (store)
        {
            await store.UpdateAsync(StateSamples.Fill);

            var texts = new[]
            {
                AdminTestSupport.Text(controller.GetStatus()),
                AdminTestSupport.Text(controller.GetPeer(StateSamples.PeerKeyA)),
                AdminTestSupport.Text(controller.GetPeer("nope")),
                AdminTestSupport.Text(controller.SyncAll()),
                AdminTestSupport.Text(controller.SyncAll())
            };

            foreach (var text in texts)
            {
                Assert.DoesNotMatch(Guid, text);
                Assert.DoesNotContain(StateSamples.AdminUser.ToString("N"), text, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("shoal1-", text, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("token", text, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("password", text, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("shoal-1a2b3c4d5e6f", text, StringComparison.Ordinal); // the expected device id
            }
        }
    }

    [Fact]
    public async Task PeerDetail_ShowsTheServerIdPrefixNeverTheFullId()
    {
        var (controller, store, _, dir) = Create();
        using (dir)
        using (store)
        {
            await store.UpdateAsync(StateSamples.Fill);

            var json = Body(controller.GetPeer(StateSamples.PeerKeyA));

            Assert.Equal("01234567", json.GetProperty("serverIdPrefix").GetString());
            Assert.DoesNotContain("0123456789abcdef0123456789abcdef", json.GetRawText(), StringComparison.Ordinal);
            Assert.Equal("qc", json.GetProperty("pairingMethod").GetString());
            Assert.Equal(4, json.GetProperty("settings").GetProperty("maxConcurrentStreams").GetInt32());
        }
    }

    [Fact]
    public async Task Contract_PeerDetail_Libraries_MatchesTheGoldenJson()
    {
        LibraryViewFixture? fixture = null;
        var (controller, store, _, dir) = Create(view: s => (fixture = new LibraryViewFixture(s)).View);
        using (dir)
        using (store)
        using (fixture)
        {
            await fixture!.SeedAsync();

            var rendered = AdminTestSupport.Render(controller.GetPeer(LibraryViewFixture.Hops));

            Assert.Equal(200, rendered.Status);
            var libraries = System.Text.Json.Nodes.JsonNode.Parse(rendered.Text)!["libraries"]!.DeepClone(); // 2.9.3 K3: the libraries part of GET Peers/{handle}
            ContractGolden.Match("peers.detail", new System.Text.Json.Nodes.JsonObject { ["libraries"] = libraries }.ToJsonString());
        }
    }

    [Fact]
    public void PeerDetail_AnUnknownHandle_Is404WithOneFixedWord()
    {
        var (controller, store, _, dir) = Create();
        using (dir)
        using (store)
        {
            var json = Body(controller.GetPeer("../../etc/passwd"), 404);

            Assert.Equal("notFound", json.GetProperty("error").GetString());
        }
    }

    [Fact]
    public async Task Sync_RunsOnce_ThenAnswers429WithRetryAfter_ThenWorksAgain()
    {
        var live = new Live();
        var (controller, store, time, dir) = Create(live: live);
        using (dir)
        using (store)
        {
            await store.UpdateAsync(StateSamples.Fill);

            Body(controller.SyncAll(), 202);
            var secondResult = AdminTestSupport.Render(controller.SyncAll());

            Assert.Equal(429, secondResult.Status);
            Assert.Equal("rateLimited", secondResult.Json.GetProperty("error").GetString());
            Assert.Equal("300", secondResult.Headers["Retry-After"].ToString());
            Assert.Equal(1, live.SyncsStarted);
            var until = Body(controller.GetStatus()).GetProperty("cooldowns").GetProperty("syncAllUntilUtc").GetDateTime();
            Assert.Equal(time.GetUtcNow().UtcDateTime.AddMinutes(5), until);

            time.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1));

            Body(controller.SyncAll(), 202);
            Assert.Equal(2, live.SyncsStarted);
        }
    }

    [Fact]
    public async Task PauseAndResume_ChangeTheStateAndTheStatus()
    {
        var (controller, store, _, dir) = Create(live: new Live());
        using (dir)
        using (store)
        {
            await store.UpdateAsync(StateSamples.Fill);

            Body(await controller.Pause(StateSamples.PeerKeyA, CancellationToken.None));

            Assert.False(store.Current.Home.Peers[0].Enabled);
            Assert.Equal("Disabled", Assert.Single(Body(controller.GetStatus()).GetProperty("servers").EnumerateArray()).GetProperty("status").GetString());

            Body(await controller.Resume(StateSamples.PeerKeyA, CancellationToken.None));

            Assert.True(store.Current.Home.Peers[0].Enabled);
            Assert.Equal("Online", Assert.Single(Body(controller.GetStatus()).GetProperty("servers").EnumerateArray()).GetProperty("status").GetString());
            Body(await controller.Pause("unknown", CancellationToken.None), 404);
        }
    }

    [Fact]
    public void RecoveryMode_IsReportedAndRefusesChanges()
    {
        var (controller, store, _, dir) = Create(d =>
        {
            var paths = new Jellyfin.Plugin.Shoal.Storage.ShoalPaths(d.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(paths.StateFile)!);
            File.WriteAllText(paths.StateFile, "{ not json");
        });
        using (dir)
        using (store)
        {
            Assert.Equal(StateStatus.Recovery, store.Status);

            var status = Body(controller.GetStatus());

            Assert.Equal("Recovery", status.GetProperty("state").GetProperty("mode").GetString());
            Assert.Equal("unreadable", status.GetProperty("state").GetProperty("reason").GetString());
            Assert.Equal(0, status.GetProperty("servers").GetArrayLength());
            Body(controller.SyncAll(), 409);
        }
    }

    [Fact]
    public void UnreadableSecrets_AreReportedSoThePageCanShowItsBanner()
    {
        var (controller, store, _, dir) = Create(secrets: new FakeSecrets { Status = SecretStoreStatus.Unreadable });
        using (dir)
        using (store)
        {
            var status = Body(controller.GetStatus());

            Assert.True(status.GetProperty("secretsUnreadable").GetBoolean());
        }
    }

    [Fact]
    public void EveryAdminActionRequiresTheAdminPolicy_AndNothingIsAnonymous()
    {
        var type = typeof(AdminController);

        var authorize = type.GetCustomAttribute<AuthorizeAttribute>();
        Assert.NotNull(authorize);
        Assert.Equal("RequiresElevation", authorize!.Policy);
        Assert.Equal("Shoal/Admin", type.GetCustomAttribute<RouteAttribute>()!.Template);
        Assert.Null(type.GetCustomAttribute<AllowAnonymousAttribute>());
        foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
        {
            Assert.Null(method.GetCustomAttribute<AllowAnonymousAttribute>());
        }
    }
}
