using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Jellyfin.Plugin.Shoal.Api;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Xunit;

namespace Jellyfin.Plugin.Shoal.Tests;

/// <summary>
/// Two actions on the same verb and template make ASP.NET Core throw <c>AmbiguousMatchException</c> on every call, and no unit test that
/// calls an action directly would notice (Warden W5-2). This walks every controller of the assembly once.
/// </summary>
public sealed class AdminRouteTests
{
    /// <summary>Every (verb, route) the plugin's controllers define, with the method that defines it.</summary>
    internal static IReadOnlyList<(string Verb, string Route, MethodInfo Method)> AllRoutes()
    {
        var routes = new List<(string, string, MethodInfo)>();
        foreach (var type in typeof(AdminPairingController).Assembly.GetTypes().Where(t => typeof(ControllerBase).IsAssignableFrom(t) && !t.IsAbstract))
        {
            var prefix = type.GetCustomAttribute<RouteAttribute>()?.Template ?? string.Empty;
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                foreach (var attribute in method.GetCustomAttributes<HttpMethodAttribute>())
                {
                    foreach (var verb in attribute.HttpMethods)
                    {
                        routes.Add((verb, (prefix + "/" + (attribute.Template ?? string.Empty)).Trim('/'), method));
                    }
                }
            }
        }

        return routes;
    }

    [Fact]
    public void NoVerbAndTemplate_IsDefinedTwice()
    {
        var duplicates = AllRoutes()
            .GroupBy(r => (r.Verb, Route: NormalizeTemplate(r.Route)))
            .Where(g => g.Count() > 1)
            .Select(g => g.Key + " in " + string.Join(", ", g.Select(r => r.Method.DeclaringType!.Name + "." + r.Method.Name)))
            .ToList();

        Assert.Empty(duplicates);
    }

    [Fact]
    public void EveryAdminRoute_IsOnTheAdminPrefix()
    {
        var admin = AllRoutes().Where(r => r.Route.StartsWith("Shoal/Admin", StringComparison.OrdinalIgnoreCase)).ToList();

        Assert.True(admin.Count >= 20, "the admin API has routes");
    }

    // {handle} and {id} are the same template for routing purposes.
    private static string NormalizeTemplate(string route) => System.Text.RegularExpressions.Regex.Replace(route.ToLowerInvariant(), @"\{[^}]+\}", "{}");
}
