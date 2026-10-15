using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.Api;
using Jellyfin.Plugin.Shoal.Http;
using Jellyfin.Plugin.Shoal.Pairing;
using Jellyfin.Plugin.Shoal.State;
using Jellyfin.Plugin.Shoal.Storage;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Shoal.Tests;

/// <summary>
/// The admin routes of K5: every action needs an administrator; the answers carry codes (never tokens); conflicts map to 409, rate limits to 429.
/// </summary>
public sealed class AdminPairingControllerTests
{
    private static AdminPairingController Controller(PairingRig rig, bool signedIn = true)
    {
        var controller = new AdminPairingController(rig.Service, rig.State);
        var http = new DefaultHttpContext();
        if (signedIn)
        {
            http.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("Jellyfin-UserId", PairingRig.Admin.ToString("N"))], "test"));
        }

        controller.ControllerContext = new ControllerContext { HttpContext = http };
        return controller;
    }

    private static T Value<T>(IActionResult result) => (T)((ShoalResult)result).Value!;

    private static int Status(IActionResult result) => ((ShoalResult)result).Status;

    [Fact]
    public void EveryRoute_RequiresAnAdministrator_AndNothingIsAnonymous()
    {
        var type = typeof(AdminPairingController);
        var authorize = type.GetCustomAttribute<AuthorizeAttribute>();

        Assert.NotNull(authorize);
        Assert.Equal(Policies.RequiresElevation, authorize.Policy);
        Assert.Null(type.GetCustomAttribute<AllowAnonymousAttribute>());
        var actions = type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
        Assert.NotEmpty(actions);
        Assert.All(actions, method => Assert.Null(method.GetCustomAttribute<AllowAnonymousAttribute>()));
        Assert.All(actions, method => Assert.NotNull(method.GetCustomAttributes().OfType<Microsoft.AspNetCore.Mvc.Routing.HttpMethodAttribute>().SingleOrDefault()));
    }

    [Fact]
    public async Task TheWholeFlow_ThroughTheRoutes()
    {
        await using var peer = await FakeJellyfinPeer.StartAsync();
        peer.ApproveFromPoll = 2;
        using var rig = new PairingRig();
        var controller = Controller(rig);

        Assert.Equal(404, Status(controller.Get()));
        var probe = Value<ProbeResult>(await controller.Probe(new ProbeRequest { Url = peer.BaseUrl }, default));
        Assert.True(probe.Ok);
        var started = Value<PairingSessionInfo>(await controller.Start(new StartPairingRequest { ProbeHandle = probe.ProbeHandle, DisplayName = "Hops", Method = "qc" }, default));
        Assert.Equal(PairingMethod.Qc, started.Method);

        var second = await controller.Start(new StartPairingRequest { ProbeHandle = probe.ProbeHandle, DisplayName = "Hops", Method = "qc" }, default);
        Assert.Equal("sessionActive", ErrorOf(second));
        Assert.Equal(409, Status(second));

        await rig.UntilStepAsync(PairingStep.AwaitingConfirm);
        var current = Value<PairingSessionInfo>(controller.Get());
        var json = JsonSerializer.Serialize(current, new JsonSerializerOptions { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } });
        Assert.Contains("\"step\":\"awaitingConfirm\"", json, StringComparison.Ordinal);
        Assert.Contains("\"safetyCode\":\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain(peer.Token, json, StringComparison.Ordinal);
        Assert.DoesNotContain(peer.Secret, json, StringComparison.Ordinal);

        var done = Value<PairingSessionInfo>(await controller.Confirm(new ConfirmPairingRequest { Match = true }, default));
        Assert.Equal("confirmed", done.State);
        var again = await controller.Confirm(new ConfirmPairingRequest { Match = true }, default);
        Assert.Equal("notAwaitingConfirm", ErrorOf(again));
    }

    [Fact]
    public async Task BadRequests_AreRefused_WithoutTouchingTheService()
    {
        using var rig = new PairingRig();

        var anonymous = await Controller(rig, signedIn: false).Start(new StartPairingRequest { ProbeHandle = "x", DisplayName = "y" }, default);
        var missing = await Controller(rig).Start(new StartPairingRequest(), default);
        var noAnswer = await Controller(rig).Confirm(new ConfirmPairingRequest(), default);

        Assert.Equal(400, Status(anonymous));
        Assert.Equal(400, Status(missing));
        Assert.Equal(400, Status(noAnswer));
        Assert.Null(rig.Service.GetSession());
    }

    [Fact]
    public async Task TheSixthStart_Is429_WithRetryAfter()
    {
        await using var peer = await FakeJellyfinPeer.StartAsync();
        peer.Enabled = new Reply(200, "false");
        using var rig = new PairingRig();
        var controller = Controller(rig);
        for (var i = 0; i < 5; i++)
        {
            var probe = await rig.ProbeAsync(peer);
            Assert.Equal(200, Status(await controller.Start(new StartPairingRequest { ProbeHandle = probe.ProbeHandle, DisplayName = "A" + i }, default)));
            await rig.UntilFinishedAsync();
        }

        var last = await rig.ProbeAsync(peer);
        var limited = await controller.Start(new StartPairingRequest { ProbeHandle = last.ProbeHandle, DisplayName = "A6" }, default);

        Assert.Equal(429, Status(limited));
        var http = controller.ControllerContext.HttpContext;
        http.Response.Body = new MemoryStream();
        await limited.ExecuteResultAsync(controller.ControllerContext);
        Assert.True(int.Parse(http.Response.Headers.RetryAfter.ToString(), System.Globalization.CultureInfo.InvariantCulture) > 0);
        Assert.Equal("application/json; charset=utf-8", http.Response.ContentType);
        Assert.Equal("nosniff", http.Response.Headers.XContentTypeOptions.ToString());
    }

    [Fact]
    public async Task Cancel_Dismiss_AndConfirmRestore_Map_To_TheirStatusCodes()
    {
        await using var peer = await FakeJellyfinPeer.StartAsync();
        peer.Enabled = new Reply(200, "false");
        using var rig = new PairingRig();
        var controller = Controller(rig);

        Assert.Equal(404, Status(await controller.Cancel(default)));
        Assert.Equal("unknownPeer", ErrorOf(await controller.DismissPairing("00000000000000000000000000000000", default)));
        Assert.Equal("nothingToConfirm", ErrorOf(controller.ConfirmRestore()));

        var started = await rig.BeginAsync(peer);
        await rig.UntilFinishedAsync();
        var dismissed = Value<DismissResult>(await controller.DismissPairing(started.PeerHandle, default));
        Assert.True(dismissed.Removed);
    }

    [Fact]
    public async Task ConfirmRestore_ClearsTheBanner_Once()
    {
        using var dir = new TempDirectory();
        var paths = new ShoalPaths(dir.Path);
        using (var first = new StateStore(paths, NullLogger.Instance, TimeProvider.System, null))
        {
            await first.UpdateAsync(StateSamples.Fill);
            await first.UpdateAsync(s => s.StubRoot = "/other");
        }

        File.WriteAllText(paths.StateFile, "{ corrupt");
        using var store = new StateStore(paths, NullLogger.Instance, TimeProvider.System, null);
        Assert.True(store.LoadInfo.AccessReviewRequired);
        Assert.NotNull(store.LoadInfo.RestoredUtc);
        using var rig = new PairingRig();
        var controller = new AdminPairingController(rig.Service, store);

        Assert.Equal(204, Status(controller.ConfirmRestore()));
        Assert.False(store.LoadInfo.AccessReviewRequired);
        Assert.Equal("nothingToConfirm", ErrorOf(controller.ConfirmRestore()));
        Assert.Equal(StateSource.Backup, store.LoadInfo.Source); // only the banner changed
    }

    private static string? ErrorOf(IActionResult? result)
    {
        var value = (result as ShoalResult)?.Value;
        return value?.GetType().GetProperty("error")?.GetValue(value) as string;
    }
}
