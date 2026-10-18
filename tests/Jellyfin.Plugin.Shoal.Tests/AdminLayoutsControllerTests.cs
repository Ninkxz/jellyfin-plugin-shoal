using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Claims;
using System.Text;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.Api.Admin;
using Jellyfin.Plugin.Shoal.State;
using Jellyfin.Plugin.Shoal.Sync;
using Jellyfin.Plugin.Shoal.Visibility;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Shoal.Tests;

/// <summary>
/// The K44/K45 admin routes: admin only at class level, answers without secrets, and each route reaches its service with the refusals the page can show.
/// </summary>
public sealed class AdminLayoutsControllerTests
{
    private static (AdminLayoutsController Controller, LayoutRig Rig, LayoutJobService Jobs) Create()
    {
        var rig = new LayoutRig(LayoutPreset.PerFriend);
        var jobs = new LayoutJobService(rig.State, new CatalogStore(rig.Paths, NullLogger<CatalogStore>.Instance), rig.Layout, rig.Accounts, new FakeRefresh(), rig.Paths, TimeProvider.System, NullLogger.Instance);
        var presets = new LayoutPresetService(rig.State, rig.Accounts, jobs);
        var controller = new AdminLayoutsController(rig.State, rig.Layout, rig.Access, jobs, presets)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("Jellyfin-UserId", rig.Admin.Id.ToString("N"))], "test")) },
            },
        };
        return (controller, rig, jobs);
    }

    private static void Send(ControllerBase controller, string json)
    {
        controller.Request.ContentType = "application/json";
        controller.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(json));
    }

    [Fact]
    public void TheController_IsAdminOnly_AtClassLevel()
    {
        var authorize = typeof(AdminLayoutsController).GetCustomAttribute<AuthorizeAttribute>();
        Assert.Equal(Policies.RequiresElevation, authorize?.Policy);
        Assert.Null(typeof(AdminLayoutsController).GetCustomAttribute<AllowAnonymousAttribute>());
        Assert.DoesNotContain(typeof(AdminLayoutsController).GetMethods(), m => m.GetCustomAttribute<AllowAnonymousAttribute>() is not null);
    }

    [Fact]
    public async Task CreateListRenameOrderDelete_ThroughTheRoutes()
    {
        var (controller, rig, _) = Create();
        using (rig)
        {
            Send(controller, "{\"name\":\"Friends' movies\",\"type\":\"movies\",\"placement\":\"Shoal\"}");
            var created = AdminTestSupport.Body(await controller.CreateLibrary(default), 201);
            var key = created.GetProperty("libKey").GetString()!;
            Send(controller, "{\"name\":\"Friends in Movies\",\"type\":\"movies\",\"placement\":\"Main\",\"mainLibraryId\":\"" + rig.Main.Id + "\"}");
            var main = AdminTestSupport.Body(await controller.CreateLibrary(default), 201).GetProperty("libKey").GetString()!;

            var list = AdminTestSupport.Body(controller.GetLibraries());
            Assert.Equal("PerFriend", list.GetProperty("layout").GetProperty("preset").GetString());
            Assert.Equal([key, main], list.GetProperty("order").EnumerateArray().Select(static e => e.GetString()));
            Assert.Equal("Main", list.GetProperty("libraries")[1].GetProperty("placement").GetString());

            Send(controller, "{\"order\":[\"" + main + "\",\"" + key + "\"]}");
            AdminTestSupport.Body(await controller.SetOrder(default));
            Assert.Equal([main, key], rig.State.Current.Home.LibraryOrder);

            Send(controller, "{\"name\":\"Pals\"}");
            Assert.Equal("Pals", AdminTestSupport.Body(await controller.UpdateLibrary(key, default)).GetProperty("library").GetProperty("name").GetString());

            Assert.Equal("removed", AdminTestSupport.Body(await controller.DeleteLibrary(key, default)).EnumerateObject().First().Name);
            Assert.Equal("notFound", AdminTestSupport.Body(await controller.DeleteLibrary(key, default), 404).GetProperty("error").GetString());
        }
    }

    [Fact]
    public async Task UpdateAccess_NeedsTheRatingsAcknowledgementForARestrictedProfile_AndReportsWhoGains()
    {
        var (controller, rig, _) = Create();
        using (rig)
        {
            var key = (await rig.Layout.CreateAsync(new LibraryCreateRequest("Friends' movies", "movies", LibraryPlacement.Shoal), rig.Admin.Id)).LibKey!;
            var kid = rig.Accounts.Add("kid", restricted: true);

            Send(controller, "{\"access\":{\"mode\":\"Selected\",\"selectedUserIds\":[\"" + rig.Admin.Id + "\",\"" + kid.Id + "\"]}}");
            Assert.Equal("acknowledgementRequired", AdminTestSupport.Body(await controller.UpdateLibrary(key, default), 400).GetProperty("error").GetString());

            Send(controller, "{\"access\":{\"mode\":\"Everyone\"}}");
            var body = AdminTestSupport.Body(await controller.UpdateLibrary(key, default));
            Assert.Contains(rig.Alice.Id.ToString(), body.GetProperty("gaining").GetRawText(), StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task Refusals_MapToTheWordsThePageKnows()
    {
        var (controller, rig, _) = Create();
        using (rig)
        {
            Send(controller, "{\"name\":\"X\",\"type\":\"music\",\"placement\":\"Shoal\"}");
            Assert.Equal("invalid", AdminTestSupport.Body(await controller.CreateLibrary(default), 400).GetProperty("error").GetString());
            Send(controller, "{\"name\":\"X\",\"type\":\"movies\",\"placement\":\"Main\",\"mainLibraryId\":\"" + rig.Shows.Id + "\"}");
            Assert.Equal("mainLibraryInvalid", AdminTestSupport.Body(await controller.CreateLibrary(default), 400).GetProperty("error").GetString());
            Send(controller, "{\"name\":\"Movies\",\"type\":\"movies\",\"placement\":\"Shoal\"}");
            Assert.Equal("nameInUse", AdminTestSupport.Body(await controller.CreateLibrary(default), 409).GetProperty("error").GetString());
            Send(controller, "{\"preset\":\"Sideways\"}");
            Assert.Equal(400, AdminTestSupport.Render(await controller.Preview(default)).Status);
            Send(controller, "{\"hidden\":true}");
            Assert.Equal("notFound", AdminTestSupport.Body(await controller.SetFriendTitles(Guid.NewGuid(), default), 404).GetProperty("error").GetString());
            Send(controller, "{\"type\":\"movies\",\"libKey\":\"0000000000000000\"}");
            Assert.Equal("notFound", AdminTestSupport.Body(await controller.MoveConnection("ffffffffffffffffffffffffffffffff", default), 404).GetProperty("error").GetString());

            using (rig.Lock.TryEnter(job: true))
            {
                Send(controller, "{\"name\":\"Y\",\"type\":\"movies\",\"placement\":\"Shoal\"}");
                Assert.Equal("busy", AdminTestSupport.Body(await controller.CreateLibrary(default), 409).GetProperty("error").GetString());
            }
        }
    }

    [Fact]
    public async Task MoveConnection_StartsTheJob_AndTheJobRouteShowsIt()
    {
        var (controller, rig, jobs) = Create();
        using (rig)
        {
            var key = (await rig.Layout.CreateAsync(new LibraryCreateRequest("Friends' movies", "movies", LibraryPlacement.Shoal), rig.Admin.Id)).LibKey!;

            Send(controller, "{\"type\":\"movies\",\"libKey\":\"" + key + "\"}");
            var started = AdminTestSupport.Body(await controller.MoveConnection(LayoutRig.PeerA, default), 202);
            await jobs.Running;

            Assert.True(started.TryGetProperty("job", out _));
            var job = AdminTestSupport.Body(controller.GetJob());
            Assert.Equal("Done", job.GetProperty("phase").GetString());
            Assert.Equal([LayoutRig.PeerA], rig.Library(key).Feeds);
            Assert.DoesNotContain("token", job.GetRawText(), StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task PreviewAndApply_ThroughTheRoutes()
    {
        var (controller, rig, jobs) = Create();
        using (rig)
        {
            Send(controller, "{\"preset\":\"AllMixed\"}");
            var preview = AdminTestSupport.Body(await controller.Preview(default));
            Assert.Equal(2, preview.GetProperty("moves").GetArrayLength());
            Assert.True(preview.TryGetProperty("usersGainingAccess", out _));
            var hash = preview.GetProperty("planHash").GetString();

            Send(controller, "{\"preset\":\"AllMixed\"}");
            Assert.Equal("invalid", AdminTestSupport.Body(await controller.Apply(default), 400).GetProperty("error").GetString()); // no plan hash (Warden 10 L3)
            Send(controller, "{\"preset\":\"AllMixed\",\"planHash\":\"00000000000000000000000000000000\"}");
            Assert.Equal("planChanged", AdminTestSupport.Body(await controller.Apply(default), 409).GetProperty("error").GetString());

            Send(controller, "{\"preset\":\"AllMixed\",\"planHash\":\"" + hash + "\"}");
            AdminTestSupport.Body(await controller.Apply(default), 202);
            await jobs.Running;

            Assert.Equal(LayoutPreset.AllMixed, rig.State.Current.Home.Layout.Default);
            Assert.Equal(2, rig.State.Current.Home.Libraries.Single(l => !l.IsLegacy).Feeds.Count);
        }
    }

    [Fact]
    public async Task FriendTitles_ThroughTheRoute_RefusesASharingAccount()
    {
        var (controller, rig, _) = Create();
        using (rig)
        {
            var share = rig.Accounts.Add("shoal-x");
            await rig.State.UpdateAsync(s => s.PeerRole.Shares.Add(new ShareState { ShareId = "s", ShareUserId = share.Id, FriendName = "X" }));

            Send(controller, "{\"hidden\":true}");
            AdminTestSupport.Body(await controller.SetFriendTitles(rig.Alice.Id, default));
            Send(controller, "{\"hidden\":true}");
            Assert.Equal("shareUser", AdminTestSupport.Body(await controller.SetFriendTitles(share.Id, default), 400).GetProperty("error").GetString());

            Assert.Equal([rig.Alice.Id], rig.State.Current.Home.Visibility.HiddenFriendTitles);
        }
    }
}
