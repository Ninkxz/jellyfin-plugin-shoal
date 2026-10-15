using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.Api.Admin;
using Jellyfin.Plugin.Shoal.State;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Jellyfin.Plugin.Shoal.Tests;

/// <summary>The Peer-side admin routes until K21 and K22: reads from the state, every change "not built", the typed name judged first.</summary>
public sealed class AdminSharesControllerTests
{
    private static async Task<(AdminSharesController Controller, StateStore Store, TempDirectory Dir)> CreateAsync()
    {
        var dir = new TempDirectory();
        var store = new StateStore(new Jellyfin.Plugin.Shoal.Storage.ShoalPaths(dir.Path), Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, new ManualTimeProvider(), null);
        await store.UpdateAsync(StateSamples.Fill);
        var controller = new AdminSharesController(new PendingShareService(store), store) { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };
        return (controller, store, dir);
    }

    private static void Send(ControllerBase controller, string json)
    {
        controller.Request.ContentType = "application/json";
        controller.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(json));
    }

    [Fact]
    public async Task TheReads_ComeFromTheState_AndAShareHasNoAccountOrToken()
    {
        var (controller, store, dir) = await CreateAsync();
        using (dir)
        using (store)
        {
            var list = AdminTestSupport.Body(controller.List());
            var share = Assert.Single(list.GetProperty("shares").EnumerateArray());
            Assert.Equal("Bob", share.GetProperty("friendName").GetString());
            Assert.Equal(4, share.GetProperty("maxConcurrentTranscodes").GetInt32());

            var detail = AdminTestSupport.Body(controller.Get("share-bob"));
            Assert.Equal("Active", detail.GetProperty("status").GetString());
            Assert.Equal(1, detail.GetProperty("deviceCount").GetInt32());
            Assert.DoesNotContain("shoal-1a2b3c4d5e6f", detail.GetRawText());
            AdminTestSupport.Body(controller.Get("nope"), 404);
        }
    }

    [Fact]
    public async Task EveryChange_IsNotBuiltYet_ButTheTypedNameIsJudgedFirst()
    {
        var (controller, store, dir) = await CreateAsync();
        using (dir)
        using (store)
        {
            Send(controller, "{\"friendName\":\"Riley\",\"libraryIds\":[]}");
            Assert.Equal("notImplemented", AdminTestSupport.Body(await controller.Create(CancellationToken.None), 501).GetProperty("error").GetString());
            Send(controller, "{\"friendName\":\"\"}");
            AdminTestSupport.Body(await controller.Create(CancellationToken.None), 400);
            Send(controller, "{\"friendName\":\"Riley\",\"maxConcurrentTranscodes\":11}");
            AdminTestSupport.Body(await controller.Create(CancellationToken.None), 400);

            Send(controller, "{\"confirmName\":\"bob\"}");
            Assert.Equal("confirmName", AdminTestSupport.Body(await controller.Delete("share-bob", CancellationToken.None), 400).GetProperty("error").GetString());
            Send(controller, "{\"confirmName\":\"Bob\"}");
            AdminTestSupport.Body(await controller.Delete("share-bob", CancellationToken.None), 501);
            AdminTestSupport.Body(await controller.Delete("nope", CancellationToken.None), 404);

            AdminTestSupport.Body(await controller.Pause("share-bob", CancellationToken.None), 501);
            AdminTestSupport.Body(await controller.Resume("nope", CancellationToken.None), 404);
            Send(controller, "{\"approve\":true}");
            AdminTestSupport.Body(await controller.Decide("share-bob", CancellationToken.None), 400);
            Assert.True(store.Current.PeerRole.Shares[0].Enabled, "nothing was changed");
        }
    }

    [Fact]
    public void EveryAction_RequiresTheAdminPolicy_AndNothingIsAnonymous()
    {
        var type = typeof(AdminSharesController);
        var actions = type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);

        Assert.Equal("RequiresElevation", type.GetCustomAttribute<AuthorizeAttribute>()!.Policy);
        Assert.Null(type.GetCustomAttribute<AllowAnonymousAttribute>());
        Assert.All(actions, m => Assert.Null(m.GetCustomAttribute<AllowAnonymousAttribute>()));
        Assert.True(actions.Length >= 12);
    }
}
