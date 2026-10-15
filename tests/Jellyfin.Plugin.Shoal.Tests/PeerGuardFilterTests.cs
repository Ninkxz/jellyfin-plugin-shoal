using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Security.Claims;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.Authorization;
using Jellyfin.Plugin.Shoal.Guard;
using Jellyfin.Plugin.Shoal.Http;
using Jellyfin.Plugin.Shoal.State;
using Jellyfin.Plugin.Shoal.Storage;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Shoal.Tests;

/// <summary>K23 (DESIGN 8.4): jobs a to d of the Peer guard, route enumeration with a diff, fail-open, the 60 s bounded cache and the modes.</summary>
public sealed class PeerGuardFilterTests
{
    private static readonly Guid ShareUser = Guid.Parse("99999999-8888-7777-6666-555555555555");
    private static readonly Guid Library = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid SharedItem = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001");
    private static readonly Guid OtherItem = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");
    private static readonly Guid Visitor = Guid.Parse("cccccccc-0000-0000-0000-000000000001");

    private sealed class Items : IGuardItems
    {
        public HashSet<Guid> Shared { get; } = [SharedItem];

        public HashSet<Guid> Existing { get; } = [SharedItem, OtherItem];

        public HashSet<Guid> Visible { get; } = [];

        public bool Throw { get; set; }

        public int Lookups { get; private set; }

        public bool? IsShared(Guid itemId, IReadOnlySet<Guid> sharedLibraryIds)
        {
            Lookups++;
            if (Throw)
            {
                throw new InvalidOperationException("boom");
            }

            return Existing.Contains(itemId) ? Shared.Contains(itemId) : null;
        }

        public bool UserCanSee(Guid itemId, Guid userId) => Visible.Contains(userId);
    }

    private sealed class Transcodes : ITranscodeProbe
    {
        public HashSet<string> Active { get; } = [];

        public bool IsTranscoding(string playSessionId) => Active.Contains(playSessionId);
    }

    private sealed class Rig : IDisposable
    {
        public Rig(HardeningMode mode = HardeningMode.LogOnly, bool withShare = true)
        {
            Dir = new TempDirectory();
            Time = new ManualTimeProvider();
            State = new StateStore(new ShoalPaths(Dir.Path), NullLogger.Instance, Time, null);
            if (withShare)
            {
                State.UpdateAsync(s =>
                {
                    s.PeerRole.HardeningMode = mode;
                    s.PeerRole.Shares.Add(new ShareState { ShareId = "s1", FriendName = "Bob", ShareUserId = ShareUser, LibraryIds = [Library], MaxConcurrentTranscodes = 2 });
                }).GetAwaiter().GetResult();
            }
            else
            {
                State.UpdateAsync(s => s.PeerRole.HardeningMode = mode).GetAwaiter().GetResult();
            }

            Counters = new GuardCounters(Time);
            Cache = new SharedItemCache(Time);
            Filter = new PeerGuardFilter(new GuardShares(State, Dir.Combine("guard-lastgood.json"), NullLogger.Instance), State, Items, Transcodes, Counters, new InitiateShield(Time), Cache, Time, NullLogger.Instance);
        }

        public TempDirectory Dir { get; }

        public ManualTimeProvider Time { get; }

        public StateStore State { get; }

        public Items Items { get; } = new();

        public Transcodes Transcodes { get; } = new();

        public GuardCounters Counters { get; }

        public SharedItemCache Cache { get; }

        public PeerGuardFilter Filter { get; }

        public void Dispose()
        {
            State.Dispose();
            Dir.Dispose();
        }

        /// <summary>Runs one request through the filter; returns the status the filter set, or 0 when it let the request through.</summary>
        public async Task<(int Status, bool Reached)> RunAsync(string template, string method = "GET", Guid? user = null, bool apiKey = false, Dictionary<string, object?>? route = null, string query = "", string ip = "203.0.113.5", string agent = "Kodi/21.0", bool authorized = false)
        {
            var http = new DefaultHttpContext();
            http.Request.Method = method;
            http.Request.QueryString = new QueryString(query);
            http.Request.Headers.UserAgent = agent;
            http.Connection.RemoteIpAddress = IPAddress.Parse(ip);
            if (user is { } id)
            {
                var claims = new List<Claim> { new(CallerContext.UserIdClaim, id.ToString("N")) };
                if (apiKey)
                {
                    claims.Add(new Claim(CallerContext.IsApiKeyClaim, "True"));
                }

                http.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
            }

            var descriptor = new ActionDescriptor { AttributeRouteInfo = new AttributeRouteInfo { Template = template } };
            if (authorized)
            {
                descriptor.EndpointMetadata = [new AuthorizeAttribute()];
            }

            var routeData = new RouteData();
            foreach (var (key, value) in route ?? [])
            {
                routeData.Values[key] = value;
            }

            var context = new ResourceExecutingContext(new ActionContext(http, routeData, descriptor), [], []);
            var reached = false;
            await Filter.OnResourceExecutionAsync(context, () =>
            {
                reached = true;
                return Task.FromResult(new ResourceExecutedContext(context, []));
            });
            return (context.Result is ShoalResult r ? r.Status : 0, reached);
        }

        public Task<(int Status, bool Reached)> Stream(Guid item, Guid? user = null, bool apiKey = false, string template = "Videos/{itemId}/stream") =>
            RunAsync(template, user: user, apiKey: apiKey, route: new() { ["itemId"] = item.ToString() });
    }

    [Fact]
    public async Task WithoutAShare_TheGuardDoesNothing()
    {
        using var rig = new Rig(HardeningMode.Enforce, withShare: false);

        var (status, reached) = await rig.Stream(SharedItem);

        Assert.Equal((0, true), (status, reached));
        Assert.Equal(0, rig.Items.Lookups);
    }

    [Fact]
    public async Task T_SEC_12_LogOnly_CountsAnonymousRequestsForSharedTitles_AndRefusesNothing()
    {
        using var rig = new Rig(HardeningMode.LogOnly);

        Assert.Equal((0, true), await rig.Stream(SharedItem));
        Assert.Equal((0, true), await rig.Stream(SharedItem, user: Visitor));
        Assert.Equal((0, true), await rig.Stream(OtherItem));

        var counted = Assert.Single(rig.Counters.WouldHaveBeenBlocked);
        Assert.Equal(("Kodi", "public", "stream", 2), (counted.App, counted.Network, counted.Kind, counted.Count));
    }

    [Fact]
    public async Task T_SEC_12_Enforce_Anonymous401_NotVisible404_VisibleAndApiKeyPass_OtherItemsUntouched()
    {
        using var rig = new Rig(HardeningMode.Enforce);
        rig.Items.Visible.Add(Visitor);

        Assert.Equal((401, false), await rig.Stream(SharedItem));
        Assert.Equal((404, false), await rig.Stream(SharedItem, user: Guid.NewGuid()));
        Assert.Equal((0, true), await rig.Stream(SharedItem, user: Visitor));
        Assert.Equal((0, true), await rig.Stream(SharedItem, user: Guid.NewGuid(), apiKey: true));
        Assert.Equal((0, true), await rig.Stream(OtherItem));
        Assert.Equal((0, true), await rig.RunAsync("Videos/{itemId}/stream", authorized: true, route: new() { ["itemId"] = SharedItem.ToString() }));
    }

    [Fact]
    public async Task Enforce_LeavesPostersAnonymous_ButLogOnlyCountsThem()
    {
        using var enforce = new Rig(HardeningMode.Enforce);
        Assert.Equal((0, true), await enforce.Stream(SharedItem, template: "Items/{itemId}/Images/{imageType}"));
        Assert.Empty(enforce.Counters.WouldHaveBeenBlocked);

        using var logOnly = new Rig(HardeningMode.LogOnly);
        await logOnly.Stream(SharedItem, template: "Items/{itemId}/Images/{imageType}");
        Assert.Equal("image", logOnly.Counters.WouldHaveBeenBlocked.Single().Kind);
    }

    [Fact]
    public async Task Off_DisablesJobA_Completely()
    {
        using var rig = new Rig(HardeningMode.Off);

        Assert.Equal((0, true), await rig.Stream(SharedItem));
        Assert.Empty(rig.Counters.WouldHaveBeenBlocked);
        Assert.Equal(0, rig.Items.Lookups);
    }

    [Fact]
    public async Task AnIdThatIsNoItem_IsNeverCached_AndTheCacheIsBoundedAndExpires()
    {
        using var rig = new Rig(HardeningMode.Enforce);
        for (var i = 0; i < 50; i++)
        {
            Assert.Equal((0, true), await rig.Stream(Guid.NewGuid()));
        }

        Assert.Equal(0, rig.Cache.Count);
        await rig.Stream(SharedItem);
        await rig.Stream(SharedItem);
        Assert.Equal(1, rig.Items.Lookups - 50);
        rig.Time.Advance(TimeSpan.FromSeconds(61));
        await rig.Stream(SharedItem);
        Assert.Equal(2, rig.Items.Lookups - 50);

        var cache = new SharedItemCache(rig.Time);
        for (var i = 0; i < 10_050; i++)
        {
            cache.Put(Guid.NewGuid(), false);
        }

        Assert.Equal(10_000, cache.Count);
    }

    [Fact]
    public async Task FailOpen_AnExceptionInJobA_LetsTheRequestThroughAndCountsIt()
    {
        using var rig = new Rig(HardeningMode.Enforce);
        rig.Items.Throw = true;

        Assert.Equal((0, true), await rig.Stream(SharedItem));
        Assert.Equal(1, rig.Counters.GuardErrors);
    }

    [Fact]
    public async Task T_PAIR_11_TheInitiateShield_TenPerMinutePerAddress_ThreeHundredInTotal()
    {
        using var rig = new Rig();
        for (var i = 0; i < 10; i++)
        {
            Assert.Equal((0, true), await rig.RunAsync("QuickConnect/Initiate", "POST", ip: "198.51.100.1"));
        }

        Assert.Equal((429, false), await rig.RunAsync("QuickConnect/Initiate", "POST", ip: "198.51.100.1"));
        Assert.Equal((0, true), await rig.RunAsync("QuickConnect/Initiate", "POST", ip: "198.51.100.2"));
        Assert.Equal(1, rig.Counters.ShieldHits);
        rig.Time.Advance(TimeSpan.FromMinutes(1.1));
        Assert.Equal((0, true), await rig.RunAsync("QuickConnect/Initiate", "POST", ip: "198.51.100.1"));

        using var flood = new Rig();
        var blocked = 0;
        for (var i = 0; i < 320; i++)
        {
            if ((await flood.RunAsync("QuickConnect/Initiate", "POST", ip: "198.51.100." + ((i % 40) + 1))).Status == 429)
            {
                blocked++;
            }

            flood.Time.Advance(TimeSpan.FromSeconds(1));
        }

        Assert.True(blocked > 0, "the overall bucket stops a flood from many addresses");
    }

    [Fact]
    public async Task T_SEC_16_AShareUser_CannotMint_ButAdminsAndOthersAreUntouched()
    {
        using var rig = new Rig();

        foreach (var template in new[] { "QuickConnect/Authorize", "Users/Password", "Users/{userId}/Password", "Sessions/Capabilities", "Sessions/Capabilities/Full" })
        {
            Assert.Equal((403, false), await rig.RunAsync(template, "POST", user: ShareUser));
            Assert.Equal((0, true), await rig.RunAsync(template, "POST", user: Visitor));
        }

        Assert.Equal((0, true), await rig.RunAsync("Sessions/Capabilities", "GET", user: ShareUser));
        Assert.Equal((0, true), await rig.RunAsync("Users/{userId}", "POST", user: ShareUser));
    }

    [Fact]
    public async Task TheTranscodeLimit_RefusesANewSessionOverTheLimit_AndNeverDirectPlay()
    {
        using var rig = new Rig();
        const string Template = "Videos/{itemId}/master.m3u8";
        Task<(int Status, bool Reached)> Start(string session, string extra = "") => rig.RunAsync(Template, user: ShareUser, query: "?PlaySessionId=" + session + extra);

        rig.Transcodes.Active.UnionWith(["a", "b"]);
        Assert.Equal((0, true), await Start("a"));
        Assert.Equal((0, true), await Start("b"));
        Assert.Equal((429, false), await Start("c"));
        Assert.Equal((0, true), await Start("a")); // an active session keeps going
        Assert.Equal((0, true), await Start("d", "&static=true")); // direct play is not counted
        Assert.Equal(1, rig.Counters.TranscodeCapHits);
        rig.Transcodes.Active.Remove("b");
        rig.Time.Advance(TimeSpan.FromSeconds(25)); // a reservation counts as active until its grace is over
        Assert.Equal((0, true), await Start("c"));
        Assert.Equal((0, true), await rig.RunAsync(Template, user: Visitor, query: "?PlaySessionId=z"));
    }

    [Fact]
    public async Task M8_ATranscodeWithoutAPlaySessionId_IsRefused_AndFreshIdsAreReservedAtAcceptance()
    {
        using var rig = new Rig();
        const string Template = "Videos/{itemId}/master.m3u8";
        Task<(int Status, bool Reached)> Start(string query) => rig.RunAsync(Template, user: ShareUser, query: query);

        Assert.Equal((400, false), await Start(string.Empty));
        Assert.Equal((400, false), await Start("?PlaySessionId="));
        Assert.Equal((0, true), await Start("?PlaySessionId=f1"));
        Assert.Equal((0, true), await Start("?PlaySessionId=f2"));
        Assert.Equal((429, false), await Start("?PlaySessionId=f3")); // no job exists yet, but the first two are reserved
        Assert.Equal((0, true), await rig.RunAsync(Template, user: Visitor, query: string.Empty));
    }

    [Fact]
    public async Task InRecovery_TheLastKnownShareListKeepsTheBansOn()
    {
        using var dir = new TempDirectory();
        var time = new ManualTimeProvider();
        using var state = new StateStore(new ShoalPaths(dir.Path), NullLogger.Instance, time, null);
        await state.UpdateAsync(s => s.PeerRole.Shares.Add(new ShareState { ShareId = "s1", FriendName = "Bob", ShareUserId = ShareUser }));
        var file = dir.Combine("guard-lastgood.json");
        Assert.NotEmpty(new GuardShares(state, file, NullLogger.Instance).Current.ShareUserIds);
        Assert.True(System.IO.File.Exists(file));

        var afterDamage = new GuardShares(new DamagedState(), file, NullLogger.Instance);

        Assert.Contains(ShareUser, afterDamage.Current.ShareUserIds);
    }

    [Fact]
    public async Task L5_AChangeToTheShares_DropsTheCachedSharedAnswers_AtOnce()
    {
        using var rig = new Rig(HardeningMode.LogOnly);
        await rig.RunAsync("Videos/{itemId}/stream", route: new() { ["itemId"] = SharedItem.ToString() });
        Assert.True(rig.Cache.TryGet(SharedItem, out _));

        await rig.State.UpdateAsync(s => s.PeerRole.Shares.Add(new ShareState { ShareId = "s2", FriendName = "Cy", ShareUserId = Guid.NewGuid() }));
        await rig.RunAsync("Videos/{itemId}/stream", route: new() { ["itemId"] = Guid.NewGuid().ToString() });

        Assert.False(rig.Cache.TryGet(SharedItem, out _));
    }

    private static Microsoft.AspNetCore.Mvc.Controllers.ControllerActionDescriptor Action(string template, params object[] metadata) =>
        new() { AttributeRouteInfo = new Microsoft.AspNetCore.Mvc.Routing.AttributeRouteInfo { Template = template }, EndpointMetadata = metadata };

    [Fact]
    public void TheRouteTable_IsComputedFromTheActionDescriptors_TheSameOnesTheFilterReads()
    {
        // Real templates keep their casing and may carry constraints; the table is lower case without them being a problem.
        var actions = new List<Microsoft.AspNetCore.Mvc.Abstractions.ActionDescriptor>
        {
            Action("Videos/{itemId}/stream"),
            Action("Videos/{itemId:guid}/stream.{container}"),
            Action("Audio/{itemId}/stream"),
            Action("Audio/{itemId}/stream.{container}"),
            Action("Audio/{itemId}/hls/{segmentId}/stream.aac"),
            Action("Audio/{itemId}/hls/{segmentId}/stream.mp3"),
            Action("Items/{itemId}/Images/{imageType}"),
            Action("Items/{itemId}/Images/{imageType}/{imageIndex}"),
            Action("Items/{itemId}/Images/{imageType}/{imageIndex}/{tag}/{format}/{maxWidth}/{maxHeight}/{percentPlayed}/{unplayedCount}"),
            Action("Videos/{routeItemId}/{routeMediaSourceId}/Subtitles/{routeIndex}/Stream.{routeFormat}"),
            Action("Videos/{routeItemId}/{routeMediaSourceId}/Subtitles/{routeIndex}/{routeStartPositionTicks}/Stream.{routeFormat}"),
            Action("Videos/{itemId}/hls/{playlistId}/{segmentId}.{segmentContainer}"),
            Action("Videos/{videoId}/{mediaSourceId}/Attachments/{index}"),
            Action("Videos/{itemId}/secret", new AuthorizeAttribute()),
            Action("Users/{userId}/Items"),
            Action("System/Info/Public"),
            Action("Videos/{itemId}/anonymousButAuthorized", new AuthorizeAttribute(), new Microsoft.AspNetCore.Authorization.AllowAnonymousAttribute())
        };

        var computed = GuardRoutes.Compute(actions);

        Assert.Equal(["+videos/{}/anonymousbutauthorized"], GuardRoutes.Diff(computed, GuardRoutes.Expected));
        Assert.Empty(GuardRoutes.Diff(GuardRoutes.Compute(actions.Take(13)), GuardRoutes.Expected)); // the real ones (parameter spellings of 12.1 included) agree with the table
    }

    [Fact]
    public void TheRouteDiff_StillFailsOnADoctoredTable_AndOnAnEmptyEnumeration()
    {
        var real = GuardRoutes.Expected.Select(t => Action(t)).ToList();
        Assert.Empty(GuardRoutes.Diff(GuardRoutes.Compute(real), GuardRoutes.Expected));

        var extra = real.Append(Action("Videos/{itemId}/new-route")).ToList();
        Assert.Equal(["+videos/{}/new-route"], GuardRoutes.Diff(GuardRoutes.Compute(extra), GuardRoutes.Expected));

        var missing = real.Skip(1).ToList();
        Assert.Single(GuardRoutes.Diff(GuardRoutes.Compute(missing), GuardRoutes.Expected));

        Assert.Equal(GuardRoutes.Expected.Count, GuardRoutes.Diff(GuardRoutes.Compute(new List<Microsoft.AspNetCore.Mvc.Abstractions.ActionDescriptor>()), GuardRoutes.Expected).Count);
    }

    [Fact]
    public void M9_AnAllowAnonymousRoute_IsAnonymousEvenWithAnAuthorizeAttribute()
    {
        Assert.True(GuardRoutes.IsAnonymous([new AuthorizeAttribute(), new Microsoft.AspNetCore.Authorization.AllowAnonymousAttribute()]));
        Assert.True(GuardRoutes.IsAnonymous([]));
        Assert.False(GuardRoutes.IsAnonymous([new AuthorizeAttribute()]));

        var source = new TestEndpoints();
        source.Add("Videos/{itemId}/stream", new AuthorizeAttribute(), new Microsoft.AspNetCore.Authorization.AllowAnonymousAttribute());
        source.Add("Videos/{itemId}/secret", new AuthorizeAttribute());

        Assert.Equal(["videos/{itemid}/stream"], GuardRoutes.Compute([source]));
    }

    [Fact]
    public async Task M7_InRecovery_TheHardeningModeIsTheLastKnownOne_NotLogOnly()
    {
        using var dir = new TempDirectory();
        using var state = new StateStore(new ShoalPaths(dir.Path), NullLogger.Instance, new ManualTimeProvider(), null);
        await state.UpdateAsync(s =>
        {
            s.PeerRole.HardeningMode = HardeningMode.Enforce;
            s.PeerRole.Shares.Add(new ShareState { ShareId = "s1", FriendName = "Bob", ShareUserId = ShareUser });
        });
        var file = dir.Combine("guard-lastgood.json");
        Assert.Equal(HardeningMode.Enforce, new GuardShares(state, file, NullLogger.Instance).Mode);

        var afterDamage = new GuardShares(new DamagedState(), file, NullLogger.Instance);

        Assert.Equal(HardeningMode.Enforce, afterDamage.Mode);
        Assert.Contains(ShareUser, afterDamage.Current.ShareUserIds);
    }

    [Fact]
    public async Task M6_TheShareList_IsRebuiltOnAChange_NotOnEveryRequest_AndAnUnwritableFileDoesNotMatter()
    {
        using var dir = new TempDirectory();
        using var state = new StateStore(new ShoalPaths(dir.Path), NullLogger.Instance, new ManualTimeProvider(), null);
        await state.UpdateAsync(s => s.PeerRole.Shares.Add(new ShareState { ShareId = "s1", FriendName = "Bob", ShareUserId = ShareUser }));
        var blocked = dir.Combine("guard-lastgood.json");
        System.IO.Directory.CreateDirectory(blocked); // a directory where the file should be: every write fails
        var shares = new GuardShares(state, blocked, NullLogger.Instance);

        var first = shares.Current;
        Assert.Same(first, shares.Current);
        Assert.Same(first, shares.Current);

        await state.UpdateAsync(s => s.PeerRole.Shares.Add(new ShareState { ShareId = "s2", FriendName = "Cy", ShareUserId = Guid.NewGuid() }));
        var second = shares.Current;

        Assert.NotSame(first, second);
        Assert.Equal(2, second.ShareUserIds.Count);
        Assert.Same(second, shares.LastKnown);
    }

    [Fact]
    public void RouteEnumeration_FindsTheUnauthorizedItemRoutes_AndTheDiffFailsOnADoctoredTable()
    {
        var source = new TestEndpoints(
            ("Videos/{itemId}/stream", false),
            ("Videos/{itemId}/stream.{container}", false),
            ("Items/{itemId}/Images/{imageType}", false),
            ("Items/{itemId}/Download", true),
            ("Users/{userId}", false),
            ("Videos/{videoId}/{mediaSourceId}/Attachments/{index}", false));

        var actual = GuardRoutes.Compute([source]);

        Assert.Equal(["items/{itemid}/images/{imagetype}", "videos/{itemid}/stream", "videos/{itemid}/stream.{container}", "videos/{videoid}/{mediasourceid}/attachments/{index}"], actual);
        Assert.Empty(GuardRoutes.Diff(actual, actual));
        var doctored = GuardRoutes.Diff([.. actual, "videos/{itemid}/new-route"], actual);
        Assert.Equal(["+videos/{}/new-route"], doctored);
        Assert.Equal(["-videos/{}/stream"], GuardRoutes.Diff(actual.Where(r => r != "videos/{itemid}/stream"), actual));
        Assert.NotEmpty(GuardRoutes.Expected);
    }

    [Fact]
    public async Task TheCounters_KeepOnlyTheAppAndTheAddressClass_AndSeeAReverseProxy()
    {
        using var rig = new Rig(HardeningMode.LogOnly);
        await rig.RunAsync("Videos/{itemId}/stream", route: new() { ["itemId"] = SharedItem.ToString() }, agent: "Jellyfin Kodi/1.2 (Linux)", ip: "192.168.1.9");

        var counted = Assert.Single(rig.Counters.WouldHaveBeenBlocked);
        Assert.Equal(("Jellyfin", "lan", "stream"), (counted.App, counted.Network, counted.Kind));

        var counters = new GuardCounters(rig.Time);
        for (var i = 0; i < 60; i++)
        {
            counters.Seen("lan", "172.17.0.1");
        }

        Assert.True(counters.ProxySuspected);
    }

    [Fact]
    public void H2_RandomUserAgents_CannotGrowTheBuckets_AndTheCountsStayTrue()
    {
        var time = new ManualTimeProvider();
        var counters = new GuardCounters(time);
        for (var i = 0; i < 20_000; i++)
        {
            counters.Blocked("stream", "agent" + i + "/1.0", "public");
        }

        var rows = counters.WouldHaveBeenBlocked;
        Assert.True(rows.Count <= 65, "at most 64 names plus the shared bucket");
        Assert.Equal(20_000, rows.Sum(r => r.Count));
        Assert.Contains(rows, r => r.App == "other");

        // A week later the old hours are pruned at the next hour boundary and a new app is accepted again.
        time.Advance(TimeSpan.FromDays(8));
        counters.Blocked("stream", "Kodi/20", "lan");
        var only = Assert.Single(counters.WouldHaveBeenBlocked);
        Assert.Equal(("Kodi", 1), (only.App, only.Count));
    }

    [Fact]
    public void TheFilter_IsRegisteredAsAResourceFilter()
    {
        Assert.True(typeof(IAsyncResourceFilter).IsAssignableFrom(typeof(PeerGuardFilter)));
    }

    private sealed class DamagedState : IStateStore
    {
        public event EventHandler<StateChangedEventArgs>? Changed
        {
            add { }
            remove { }
        }

        public StateStatus Status => StateStatus.Recovery;

        public ShoalState Current => throw new InvalidOperationException();

        public StateLoadInfo LoadInfo => new();

        public bool ConfirmRestore() => false;

        public Task<ShoalState> UpdateAsync(Action<ShoalState> mutate, System.Threading.CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public IReadOnlyList<StateCandidate> GetCandidates() => [];

        public Task RestoreFromAsync(string candidateFileName, System.Threading.CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task StartOverAsync(System.Threading.CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class TestEndpoints : EndpointDataSource
    {
        private readonly List<Endpoint> _endpoints = [];

        public TestEndpoints(params (string Template, bool Authorized)[] routes)
        {
            foreach (var (template, authorized) in routes)
            {
                var metadata = authorized ? new EndpointMetadataCollection(new AuthorizeAttribute()) : EndpointMetadataCollection.Empty;
                _endpoints.Add(new RouteEndpoint(_ => Task.CompletedTask, RoutePatternFactory.Parse(template), 0, metadata, template));
            }
        }

        public void Add(string template, params object[] metadata) =>
            _endpoints.Add(new RouteEndpoint(_ => Task.CompletedTask, RoutePatternFactory.Parse(template), 0, new EndpointMetadataCollection(metadata), template));

        public override IReadOnlyList<Endpoint> Endpoints => _endpoints;

        public override Microsoft.Extensions.Primitives.IChangeToken GetChangeToken() => new Microsoft.Extensions.Primitives.CancellationChangeToken(System.Threading.CancellationToken.None);
    }
}
