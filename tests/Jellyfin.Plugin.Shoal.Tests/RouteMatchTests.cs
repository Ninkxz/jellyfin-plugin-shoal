using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.Api;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Jellyfin.Plugin.Shoal.Tests;

/// <summary>
/// Calls every route of every controller over real HTTP routing and checks that the request lands on the action that declares it. A route template that ASP.NET Core
/// cannot match (a variable named <c>{action}</c>, which MVC reserves, answered a bare 404 on every Holds Release and Keep call: Pilot 06) is invisible to tests that
/// call an action directly. The actions are never run, only matched, so no service is needed.
/// </summary>
public sealed class RouteMatchTests
{
    private static readonly Regex Variable = new(@"\{[^}]+\}", RegexOptions.Compiled);

    [Fact]
    public async Task EveryRouteOfEveryController_IsMatchedByTheActionThatDeclaresIt()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddControllers().AddApplicationPart(typeof(AdminPairingController).Assembly);
        await using var app = builder.Build();
        app.UseRouting();
        app.MapControllers();
        app.Run(context =>
        {
            var action = context.GetEndpoint()?.Metadata.GetMetadata<ControllerActionDescriptor>();
            context.Response.StatusCode = action is null ? 404 : 200;
            if (action is not null)
            {
                context.Response.Headers["X-Matched"] = action.ControllerTypeInfo.Name + "." + action.MethodInfo.Name;
            }

            return Task.CompletedTask;
        });
        await app.StartAsync();
        var address = app.Urls.First();
        using var http = new HttpClient { BaseAddress = new Uri(address) };

        var misses = new List<string>();
        foreach (var (verb, route, method) in AdminRouteTests.AllRoutes())
        {
            var path = "/" + Variable.Replace(route, static m => m.Value.Contains(":int", StringComparison.Ordinal) ? "1" : m.Value.Contains(":regex", StringComparison.Ordinal) ? "ts" : "0123456789abcdef0123456789abcdef");
            using var response = await http.SendAsync(new HttpRequestMessage(new HttpMethod(verb), path));
            var body = response.Headers.TryGetValues("X-Matched", out var values) ? values.First() : string.Empty;
            var expected = method.DeclaringType!.Name + "." + method.Name;
            if (response.StatusCode != System.Net.HttpStatusCode.OK || body != expected)
            {
                misses.Add($"{verb} {route} -> {(int)response.StatusCode} {body} (expected {expected})");
            }
        }

        Assert.Empty(misses);
    }

    [Fact]
    public void NoRouteTemplate_UsesAVariableNameMvcReserves()
    {
        var reserved = new[] { "action", "controller", "area", "handler", "page" };
        var bad = AdminRouteTests.AllRoutes()
            .Where(r => Variable.Matches(r.Route).Any(m => reserved.Contains(m.Value.Trim('{', '}').Split(':', '?', '=')[0], StringComparer.OrdinalIgnoreCase)))
            .Select(static r => r.Verb + " " + r.Route)
            .ToList();
        Assert.Empty(bad);
    }
}
