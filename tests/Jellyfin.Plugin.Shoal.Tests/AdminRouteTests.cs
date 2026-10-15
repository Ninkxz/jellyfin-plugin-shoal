using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Reflection;
using System.Text;
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


    // Routes shoal.js calls that no controller has. It was four long in the v1.0.1 audit and must stay empty: a new call needs its route.
    private static readonly string[] KnownMissing = [];

    /// <summary>The (verb, route) pairs the admin page asks for: every <c>call('VERB', path)</c> of its api table, the path with each variable part as <c>{}</c> and no query.</summary>
    internal static IReadOnlyList<string> UiCalls()
    {
        using var stream = typeof(Plugin).Assembly.GetManifestResourceStream("Jellyfin.Plugin.Shoal.Configuration.shoal.js");
        Assert.NotNull(stream);
        using var reader = new StreamReader(stream);
        var script = reader.ReadToEnd();
        var table = script; // every call('VERB', ...) in the file, wherever it sits
        var calls = new List<string>();
        var rx = new System.Text.RegularExpressions.Regex(@"call\('(GET|POST|PUT|DELETE)', ", System.Text.RegularExpressions.RegexOptions.None);
        foreach (System.Text.RegularExpressions.Match m in rx.Matches(table))
        {
            var expr = FirstArgument(table, m.Index + m.Length);
            var path = new StringBuilder();
            foreach (var part in SplitPlus(expr))
            {
                var piece = part.Trim();
                path.Append(piece.Length >= 2 && piece[0] == '\'' ? piece[1..^1] : "{}");
            }

            var text = path.ToString();
            var query = text.IndexOf('?', StringComparison.Ordinal);
            calls.Add(m.Groups[1].Value + " " + ("Shoal/Admin/" + (query >= 0 ? text[..query] : text)).ToLowerInvariant());
        }

        return calls;
    }

    private static string FirstArgument(string text, int start)
    {
        var depth = 0;
        var quote = false;
        for (var i = start; i < text.Length; i++)
        {
            var c = text[i];
            if (quote)
            {
                quote = c != '\'';
                continue;
            }

            if (c == '\'')
            {
                quote = true;
            }
            else if (c == '(')
            {
                depth++;
            }
            else if (c == ')' || c == ',')
            {
                if (depth == 0)
                {
                    return text[start..i];
                }

                if (c == ')')
                {
                    depth--;
                }
            }
        }

        throw new InvalidOperationException("unterminated call");
    }

    private static List<string> SplitPlus(string expr)
    {
        var parts = new List<string>();
        var depth = 0;
        var quote = false;
        var from = 0;
        for (var i = 0; i < expr.Length; i++)
        {
            var c = expr[i];
            if (quote)
            {
                quote = c != '\'';
                continue;
            }

            if (c == '\'')
            {
                quote = true;
            }
            else if (c == '(')
            {
                depth++;
            }
            else if (c == ')')
            {
                depth--;
            }
            else if (c == '+' && depth == 0)
            {
                parts.Add(expr[from..i]);
                from = i + 1;
            }
        }

        parts.Add(expr[from..]);
        return parts;
    }

    [Fact]
    public void EveryRouteTheAdminPageCalls_HasAController()
    {
        var have = AllRoutes().Select(r => r.Verb + " " + NormalizeTemplate(r.Route)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var calls = UiCalls();
        Assert.True(calls.Count > 40, "the api table was read (" + calls.Count + " calls)");
        var missing = calls.Select(c => c.Substring(0, c.IndexOf(' ', StringComparison.Ordinal)) + " " + NormalizeTemplate(c[(c.IndexOf(' ', StringComparison.Ordinal) + 1)..]))
            .Where(c => !have.Contains(c))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(c => c, StringComparer.Ordinal)
            .ToList();
        Assert.Equal(KnownMissing.OrderBy(c => c, StringComparer.Ordinal), missing);
    }

    [Fact]
    public void EveryAdminController_IsAdminOnlyAtClassLevel_OnTheAdminPrefix()
    {
        var controllers = typeof(AdminPairingController).Assembly.GetTypes().Where(t => typeof(ControllerBase).IsAssignableFrom(t) && !t.IsAbstract).ToList();
        var admin = controllers.Where(t => t.Name.StartsWith("Admin", StringComparison.Ordinal)).ToList();
        Assert.True(admin.Count >= 10, "the admin controllers were found (" + admin.Count + ")");

        var wrong = new List<string>();
        foreach (var type in admin)
        {
            var policy = type.GetCustomAttribute<Microsoft.AspNetCore.Authorization.AuthorizeAttribute>(inherit: true)?.Policy;
            if (policy != MediaBrowser.Common.Api.Policies.RequiresElevation)
            {
                wrong.Add(type.Name + ": class-level policy is '" + policy + "'");
            }

            if (type.GetCustomAttribute<RouteAttribute>()?.Template != "Shoal/Admin")
            {
                wrong.Add(type.Name + ": route prefix is not Shoal/Admin");
            }

            if (type.GetCustomAttribute<Microsoft.AspNetCore.Authorization.AllowAnonymousAttribute>(inherit: true) is not null
                || type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly).Any(m => m.GetCustomAttribute<Microsoft.AspNetCore.Authorization.AllowAnonymousAttribute>() is not null))
            {
                wrong.Add(type.Name + ": allows anonymous callers");
            }
        }

        // The other direction: nothing outside the Admin* names may sit on the admin prefix.
        wrong.AddRange(controllers
            .Where(t => !admin.Contains(t) && t.GetCustomAttribute<RouteAttribute>()?.Template?.StartsWith("Shoal/Admin", StringComparison.OrdinalIgnoreCase) == true)
            .Select(t => t.Name + ": on the admin prefix without an Admin name"));
        Assert.Empty(wrong);
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
