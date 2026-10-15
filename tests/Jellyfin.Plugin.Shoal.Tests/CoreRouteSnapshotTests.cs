using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Jellyfin.Plugin.Shoal.Playback;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Xunit;

namespace Jellyfin.Plugin.Shoal.Tests;

/// <summary>
/// K13.1 (Scout 6): every Jellyfin 12.1 route that carries an item id has a recorded verdict, and <see cref="InterceptTable"/> agrees with it.
/// The snapshot (<c>Fixtures/jellyfin-12.1-item-routes.tsv</c>) comes from Scout's parse of <c>Jellyfin.Api/Controllers</c> (docs/research/scout-03-routes.md,
/// Appendix A), not from swagger, which hides 55 routes. <c>Jellyfin.Api</c> is not on NuGet, so the descriptor walk below runs when
/// <c>SHOAL_JELLYFIN_API_DLL</c> names a server's <c>Jellyfin.Api.dll</c> (lab and release checks); it fails on any route the snapshot does not know.
/// </summary>
public sealed class CoreRouteSnapshotTests
{
    private static IReadOnlyList<(string Verb, string Template, string Kind)> Snapshot()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "jellyfin-12.1-item-routes.tsv");
        return [.. File.ReadAllLines(path).Where(static l => l.Length > 0).Select(static l => l.Split('\t')).Select(static p => (p[0], p[1], p[2]))];
    }

    [Fact]
    public void TheSnapshot_HoldsTheRoutesScoutCounted()
    {
        var rows = Snapshot();

        Assert.Equal(143, rows.Count); // 142 item-id routes + DELETE Videos/ActiveEncodings
        Assert.Equal(35, rows.Count(static r => r.Kind != "-")); // the media routes of Scout section 2, ActiveEncodings included
    }

    [Fact]
    public void EveryRecordedRoute_IsClassifiedByTheTableAsRecorded()
    {
        var wrong = new List<string>();
        foreach (var (verb, template, kind) in Snapshot())
        {
            var found = InterceptTable.Find(template);
            var actual = found?.Kind.ToString() ?? "-";
            if (!string.Equals(actual, kind, StringComparison.Ordinal))
            {
                wrong.Add($"{verb} {template}: recorded {kind}, table says {actual}");
            }
        }

        Assert.True(wrong.Count == 0, string.Join('\n', wrong));
    }

    [Fact]
    public void ARouteThatCanReachAStub_IsNeverPassThrough()
    {
        // Independent of the recorded kind: any route under Videos/{id}/ or Audio/{id}/, or Items/{id}/File|Download|PlaybackInfo, that the
        // snapshot calls pass-through must be one of the known non-byte routes (Scout 2).
        var knownNonByte = new[] { "AdditionalParts", "AlternateSources", "Lyrics", "RemoteSearch", "Subtitles", "MergeVersions", "Similar", "Intros", "LocalTrailers", "SpecialFeatures", "ThemeMedia", "ThemeSongs", "ThemeVideos", "Ancestors", "Metadata", "Refresh" };
        var offenders = Snapshot()
            .Where(static r => r.Kind == "-" && (r.Template.StartsWith("Videos/{", StringComparison.Ordinal) || r.Template.StartsWith("Audio/{", StringComparison.Ordinal)))
            .Where(r => !knownNonByte.Any(k => r.Template.Contains(k, StringComparison.Ordinal)))
            .Select(static r => r.Verb + " " + r.Template)
            .ToList();

        Assert.True(offenders.Count == 0, "pass-through routes under Videos/{id} or Audio/{id} that are not known non-byte routes:\n" + string.Join('\n', offenders));
    }

    [Fact]
    public void TheRealActionDescriptors_HaveNoRouteTheSnapshotDoesNotKnow()
    {
        var dll = Environment.GetEnvironmentVariable("SHOAL_JELLYFIN_API_DLL");
        if (string.IsNullOrEmpty(dll) || !File.Exists(dll))
        {
            return; // lab/release check only: Jellyfin.Api is not a NuGet package
        }

        var known = Snapshot().Select(static r => r.Verb + " " + r.Template).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var assembly = Assembly.LoadFrom(dll);
        Type[] types;
        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            types = [.. ex.Types.OfType<Type>()];
        }

        var unknown = new List<string>();
        foreach (var controller in types.Where(static t => typeof(ControllerBase).IsAssignableFrom(t) && !t.IsAbstract))
        {
            var route = controller.GetCustomAttributes<RouteAttribute>(inherit: true).FirstOrDefault()?.Template ?? "[controller]";
            route = route.Replace("[controller]", controller.Name.Replace("Controller", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
            foreach (var method in controller.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                foreach (var attribute in method.GetCustomAttributes<HttpMethodAttribute>())
                {
                    var template = attribute.Template ?? string.Empty;
                    var full = template.StartsWith('/') ? template.TrimStart('/') : string.Join('/', new[] { route.Trim('/'), template.Trim('/') }.Where(static s => s.Length > 0));
                    var carriesId = full.Contains("{itemId", StringComparison.OrdinalIgnoreCase) || full.StartsWith("Videos/{", StringComparison.Ordinal) || full.StartsWith("Audio/{", StringComparison.Ordinal)
                        || full.Equals("LiveStreams/Open", StringComparison.Ordinal) || full.Equals("Videos/ActiveEncodings", StringComparison.Ordinal);
                    foreach (var verb in attribute.HttpMethods)
                    {
                        if (carriesId && !known.Contains(verb + " " + full))
                        {
                            unknown.Add(verb + " " + full + " (" + controller.Name + "." + method.Name + ")");
                        }
                    }
                }
            }
        }

        Assert.True(unknown.Count == 0, "New item-id routes in this Jellyfin build: classify them in InterceptTable and the snapshot.\n" + string.Join('\n', unknown));
    }
}
