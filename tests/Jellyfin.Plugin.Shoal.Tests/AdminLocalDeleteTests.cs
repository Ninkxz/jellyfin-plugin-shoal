using System;
using System.Linq;
using System.Reflection;
using System.Security.Claims;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.Api.Admin;
using Jellyfin.Plugin.Shoal.Authorization;
using Jellyfin.Plugin.Shoal.Configuration;
using Jellyfin.Plugin.Shoal.LocalRemoval;
using Jellyfin.Plugin.Shoal.State;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Jellyfin.Plugin.Shoal.Tests;

/// <summary>2.10.4 K4a: the delete-like-Jellyfin admin routes (Table 2 rows 1 to 7) and their C0 golden shapes.</summary>
public sealed class AdminLocalDeleteTests
{
    [Fact]
    public void EveryRouteIsElevatedBoundedAndNeverAnonymous()
    {
        var type = typeof(AdminLocalDeleteController);
        Assert.Equal(Policies.RequiresElevation, type.GetCustomAttribute<AuthorizeAttribute>()!.Policy);
        Assert.Equal("Shoal/Admin", type.GetCustomAttribute<RouteAttribute>()!.Template);
        Assert.Equal(1024L, type.CustomAttributes.Single(a => a.AttributeType == typeof(RequestSizeLimitAttribute)).ConstructorArguments[0].Value);
        Assert.DoesNotContain(type.GetMethods(), m => m.GetCustomAttribute<AllowAnonymousAttribute>() is not null);
        var routes = type.GetMethods().SelectMany(m => m.GetCustomAttributes<Microsoft.AspNetCore.Mvc.Routing.HttpMethodAttribute>().Select(a => a.HttpMethods.Single() + " " + a.Template)).OrderBy(static r => r, StringComparer.Ordinal).ToArray();
        Assert.Equal(
            [
                "GET LocalRemoval/Deletions/Current", "GET LocalRemoval/Deletions/{batchId}", "GET LocalRemoval/Status", "GET Peers/{handle}/LocalRemoval",
                "POST LocalRemoval/Deletions", "POST LocalRemoval/Deletions/{batchId}/Stop", "PUT LocalRemoval/Enabled",
            ],
            routes);
    }

    // W23-7: a batch of 50 handles must fit the body limit of the start route (50 x 32 hex plus JSON is about 1.8 KB).
    [Fact]
    public void FiftyHandlesFitTheStartBodyLimitWithMargin()
    {
        var body = System.Text.Json.JsonSerializer.Serialize(new { candidateIds = Enumerable.Range(1, LocalDeleteService.MaxItems).Select(static i => i.ToString("x32")).ToArray() });
        var limit = (long)typeof(AdminLocalDeleteController).GetMethod(nameof(AdminLocalDeleteController.Start))!.CustomAttributes.Single(a => a.AttributeType == typeof(RequestSizeLimitAttribute)).ConstructorArguments[0].Value!;
        Assert.True(limit >= 2 * body.Length, $"limit {limit} for a {body.Length} byte body");
        Assert.True(limit <= 8192);
    }

    [Theory]
    [InlineData(false, false, true)] // not signed in
    [InlineData(true, true, true)] // an API key
    [InlineData(true, false, false)] // no user id
    public async Task EveryChangeRefusesAnythingButASignedInUserBeforeTheService(bool authenticated, bool apiKey, bool userId)
    {
        using var f = new Rig(authenticated, apiKey, userId);
        Assert.Equal("administratorRequired", AdminTestSupport.Body(f.Controller.Enabled(new() { Enabled = true }), 403).GetProperty("error").GetString());
        Assert.Equal("administratorRequired", AdminTestSupport.Body(f.Controller.Start(new() { CandidateIds = ["a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1"] }), 403).GetProperty("error").GetString());
        Assert.Equal("administratorRequired", AdminTestSupport.Body(f.Controller.Stop("a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1"), 403).GetProperty("error").GetString());
        Assert.False(f.Settings.Current.LocalDeleteEnabled);
        Assert.Null(f.Deletes.Service.ActiveBatchId);
        AdminTestSupport.Body(await f.Controller.Status(default)); // reads need only the elevation policy
    }

    [Theory]
    [InlineData("")]
    [InlineData("../../movie.mkv")]
    [InlineData("00000000000000000000000000000000")]
    [InlineData("ABCDEF00000000000000000000000001")]
    public void InvalidIdsAndBodiesAre400(string value)
    {
        using var f = new Rig();
        Assert.Equal("invalidRequest", AdminTestSupport.Body(f.Controller.Start(new() { CandidateIds = [value] }), 400).GetProperty("error").GetString());
        AdminTestSupport.Body(f.Controller.Get(value), 400);
        AdminTestSupport.Body(f.Controller.Stop(value), 400);
        AdminTestSupport.Body(f.Controller.Start(new()), 400);
        AdminTestSupport.Body(f.Controller.Start(new() { CandidateIds = [] }), 400);
        AdminTestSupport.Body(f.Controller.Start(new() { CandidateIds = Enumerable.Range(1, 51).Select(static i => i.ToString("x32")).ToArray() }), 400);
        AdminTestSupport.Body(f.Controller.Start(new() { CandidateIds = ["a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1", "a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1"] }), 400);
        AdminTestSupport.Body(f.Controller.Enabled(new()), 400);
        Assert.Equal(new[] { "CandidateIds" }, typeof(LocalDeleteStartRequest).GetProperties().Select(static p => p.Name));
    }

    [Fact]
    public async Task TheStateRepliesMatchTheC0Goldens()
    {
        using var f = new Rig();
        var off = await f.Controller.State(LocalDeleteServiceTests.Peer, default);
        ContractGolden.Match("local-delete.off", AdminTestSupport.Text(off));
        Assert.Empty(AdminTestSupport.Body(off).GetProperty("candidates").EnumerateArray());
        AdminTestSupport.Body(f.Controller.Enabled(new() { Enabled = true }));
        Assert.True(f.Settings.Current.LocalDeleteEnabled);
        var on = AdminTestSupport.Body(await f.Controller.State(LocalDeleteServiceTests.Peer, default));
        Assert.True(on.GetProperty("enabled").GetBoolean());
        Assert.All(on.GetProperty("candidates").EnumerateArray(), c => Assert.Matches("^[0-9a-f]{32}$", c.GetProperty("candidateId").GetString()!));
        Assert.Empty(AdminTestSupport.Body(await f.Controller.Status(default)).GetProperty("candidates").EnumerateArray());
        Assert.Equal("notFound", AdminTestSupport.Body(await f.Controller.State("nope", default), 404).GetProperty("error").GetString());
        var none = f.Controller.Current();
        ContractGolden.Match("local-delete.current-none", AdminTestSupport.Render(none).Text);
        AdminTestSupport.Body(none, 404);
    }

    [Fact]
    public async Task AStartedBatchMatchesTheGoldensAndCanBeFollowedAndStopped()
    {
        using var f = new Rig();
        AdminTestSupport.Body(f.Controller.Enabled(new() { Enabled = true }));
        var ids = AdminTestSupport.Body(await f.Controller.State(LocalDeleteServiceTests.Peer, default)).GetProperty("candidates").EnumerateArray().Select(static c => c.GetProperty("candidateId").GetString()!).ToArray();
        var gate = f.Deletes.Library.Block(f.Deletes.Targets[0].LocalItemId);
        var start = f.Controller.Start(new() { CandidateIds = ids });
        ContractGolden.Match("local-delete.start", AdminTestSupport.Render(start).Text);
        var batchId = AdminTestSupport.Body(start, 202).GetProperty("batchId").GetString()!;
        await f.Deletes.Library.Reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(batchId, AdminTestSupport.Body(await f.Controller.State(LocalDeleteServiceTests.Peer, default)).GetProperty("activeBatchId").GetString());
        Assert.Equal("busy", AdminTestSupport.Body(f.Controller.Start(new() { CandidateIds = [ids[1]] }), 409).GetProperty("error").GetString());
        var stopping = f.Controller.Stop(batchId);
        ContractGolden.Match("local-delete.stopping", AdminTestSupport.Render(stopping).Text);
        Assert.True(AdminTestSupport.Body(stopping).GetProperty("stopping").GetBoolean());
        gate.SetResult();
        await f.Deletes.Finish(batchId);
        Assert.Equal("stopped", AdminTestSupport.Body(f.Controller.Get(batchId)).GetProperty("state").GetString());
        Assert.Equal(batchId, AdminTestSupport.Body(f.Controller.Current()).GetProperty("batchId").GetString());
        Assert.Equal("notFound", AdminTestSupport.Body(f.Controller.Get("b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2"), 404).GetProperty("error").GetString());
        AdminTestSupport.Body(f.Controller.Stop("b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2"), 404);
    }

    [Fact]
    public async Task SwitchingOffStopsTheBatchForgetsHandlesAndRefusesNewStarts()
    {
        using var f = new Rig();
        AdminTestSupport.Body(f.Controller.Enabled(new() { Enabled = true }));
        var ids = AdminTestSupport.Body(await f.Controller.State(LocalDeleteServiceTests.Peer, default)).GetProperty("candidates").EnumerateArray().Select(static c => c.GetProperty("candidateId").GetString()!).ToArray();
        var gate = f.Deletes.Library.Block(f.Deletes.Targets[0].LocalItemId);
        var batchId = AdminTestSupport.Body(f.Controller.Start(new() { CandidateIds = ids }), 202).GetProperty("batchId").GetString()!;
        await f.Deletes.Library.Reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(AdminTestSupport.Body(f.Controller.Enabled(new() { Enabled = false })).GetProperty("enabled").GetBoolean());
        Assert.False(f.Settings.Current.LocalDeleteEnabled);
        Assert.True(AdminTestSupport.Body(f.Controller.Get(batchId)).GetProperty("stopping").GetBoolean());
        gate.SetResult();
        var batch = await f.Deletes.Finish(batchId);
        Assert.Equal(LocalDeleteBatchState.Stopped, batch.State);
        Assert.Equal("candidateChanged", AdminTestSupport.Body(f.Controller.Start(new() { CandidateIds = [ids[1]] }), 409).GetProperty("error").GetString());
    }

    [Fact]
    public void TheSettingsCopyKeepsTheSwitch()
    {
        Assert.False(new PluginConfiguration().LocalDeleteEnabled);
        Assert.True(PluginSettingsAccessor.Copy(new PluginConfiguration { LocalDeleteEnabled = true }).LocalDeleteEnabled);
    }

    private sealed class MemorySettings : ISettingsAccessor
    {
        private PluginConfiguration _saved = new();

        public PluginConfiguration Current => PluginSettingsAccessor.Copy(_saved);

        public void Save(PluginConfiguration configuration) => _saved = PluginSettingsAccessor.Copy(configuration);
    }

    private sealed class Rig : IDisposable
    {
        internal Rig(bool authenticated = true, bool apiKey = false, bool userId = true)
        {
            Deletes = new LocalDeleteServiceTests.Fixture(2);
            var claims = new[] { new Claim(CallerContext.UserIdClaim, userId ? Deletes.Admin.ToString("N") : string.Empty), new Claim(CallerContext.IsApiKeyClaim, apiKey ? "true" : "false") };
            var state = RemoveServerGhostTests.CoreProxy<IStateStore>.Create((m, _) => m.Name == "get_Status" ? StateStatus.Ready : null);
            Controller = new AdminLocalDeleteController(Deletes.Service, Settings, state)
            {
                ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, authenticated ? "core" : null)) } },
            };
        }

        internal LocalDeleteServiceTests.Fixture Deletes { get; }

        internal MemorySettings Settings { get; } = new();

        internal AdminLocalDeleteController Controller { get; }

        public void Dispose() => Deletes.Dispose();
    }
}
