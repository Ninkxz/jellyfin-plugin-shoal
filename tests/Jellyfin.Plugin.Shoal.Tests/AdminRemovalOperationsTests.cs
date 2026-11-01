using System;
using System.Linq;
using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.Api.Admin;
using Jellyfin.Plugin.Shoal.Authorization;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Jellyfin.Plugin.Shoal.Tests;

/// <summary>2.10.4 K4b: the legacy controller keeps only Restore; the move, purge and permanent-deletion routes are gone (404).</summary>
public sealed class AdminRemovalOperationsTests
{
    [Fact]
    public void OnlyRestoreIsLeftInsideTheElevatedBoundedController()
    {
        var type = typeof(AdminRemovalOperationsController);
        Assert.Equal(Policies.RequiresElevation, type.GetCustomAttribute<AuthorizeAttribute>()!.Policy);
        Assert.Equal("Shoal/Admin", type.GetCustomAttribute<RouteAttribute>()!.Template);
        Assert.Equal(1024L, type.CustomAttributes.Single(a => a.AttributeType == typeof(RequestSizeLimitAttribute)).ConstructorArguments[0].Value);
        Assert.DoesNotContain(type.GetMethods(), m => m.GetCustomAttribute<AllowAnonymousAttribute>() is not null);
        var routes = type.GetMethods().SelectMany(m => m.GetCustomAttributes<Microsoft.AspNetCore.Mvc.Routing.HttpMethodAttribute>()).Select(a => a.HttpMethods.Single() + " " + a.Template).ToArray();
        Assert.Equal(["POST LocalRemoval/Jobs/{jobId}/Restore"], routes);
    }

    [Fact]
    public void TheRemovedRoutesAreNotServedByAnyController()
    {
        var templates = typeof(AdminRemovalOperationsController).Assembly.GetTypes().Where(static t => typeof(ControllerBase).IsAssignableFrom(t))
            .SelectMany(static t => t.GetMethods()).SelectMany(static m => m.GetCustomAttributes<Microsoft.AspNetCore.Mvc.Routing.HttpMethodAttribute>()).Select(static a => a.Template ?? string.Empty).ToArray();
        foreach (var removed in new[] { "LocalRemoval/Plans", "LocalRemoval/Plans/{planId}/Confirm", "LocalRemoval/Jobs/{jobId}/PurgePlans", "LocalRemoval/PurgePlans/{planId}/Confirm", "LocalRemoval/PermanentDeletionEnabled" })
        {
            Assert.DoesNotContain(removed, templates);
        }

        Assert.Null(typeof(AdminRemovalOperationsController).Assembly.GetType("Jellyfin.Plugin.Shoal.Api.Admin.RemovalPurgeConfirmRequest"));
        Assert.Null(typeof(AdminRemovalOperationsController).Assembly.GetType("Jellyfin.Plugin.Shoal.Api.Admin.RemovalPlanRequest"));
    }

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    public async Task RestoreRejectsUnauthenticatedApiKeyAndMissingUserWithoutCallingService(bool authenticated, bool apiKey, bool userId)
    {
        var claims = new[] { new Claim(CallerContext.UserIdClaim, userId ? Guid.NewGuid().ToString("N") : string.Empty), new Claim(CallerContext.IsApiKeyClaim, apiKey ? "true" : "false") };
        var controller = new AdminRemovalOperationsController(null!) { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, authenticated ? "core" : null)) } } };
        Assert.Equal("administratorRequired", AdminTestSupport.Body(await controller.Restore(Guid.NewGuid().ToString("N"), default), 403).GetProperty("error").GetString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("../../movie.mkv")]
    [InlineData("00000000000000000000000000000000")]
    [InlineData("ABCDEF00000000000000000000000001")]
    public async Task InvalidHandlesNeverReachTheService(string value)
    {
        var controller = new AdminRemovalOperationsController(null!);
        Assert.Equal("invalidRequest", AdminTestSupport.Body(await controller.Restore(value, default), 400).GetProperty("error").GetString());
    }

    [Theory]
    [InlineData("mediaFileUnavailable")]
    [InlineData("removalRecoveryDirectory")]
    public async Task FileErrorsDuringRestoreAreRecoveryRequired(string native)
    {
        var run = typeof(AdminRemovalOperationsController).GetMethod("RunAsync", BindingFlags.NonPublic | BindingFlags.Static)!;
        var result = Assert.IsType<Jellyfin.Plugin.Shoal.Http.ShoalResult>(await (Task<IActionResult>)run.Invoke(null, [new Func<Task<object>>(() => throw new System.IO.IOException(native))])!);
        Assert.Equal(409, result.Status);
        Assert.Equal("recoveryRequired", JsonSerializer.SerializeToElement(result.Value).GetProperty("error").GetString());
    }
}
