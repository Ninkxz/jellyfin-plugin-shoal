using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.Shoal.Playback;

/// <summary>
/// What Shoal does with a federated item on a core route (DESIGN 6.1).
/// </summary>
public enum RouteKind
{
    /// <summary><c>Items/{itemId}/PlaybackInfo</c>, GET and POST: the PlaybackInfo handler.</summary>
    PlaybackInfo,

    /// <summary><c>Videos/{itemId}/stream[.{container}]</c>: the static stream handler when <c>Static=true</c>, otherwise 404.</summary>
    StaticStream,

    /// <summary>The core subtitle route <c>Videos/{id}/{source}/Subtitles/{index}/[{ticks}/]Stream.{format}</c>: the subtitle handler.</summary>
    Subtitle,

    /// <summary><c>Items/{itemId}/Download</c> and <c>Items/{itemId}/File</c>: 403, they would serve the <c>.strm</c>.</summary>
    Forbidden,

    /// <summary><c>POST LiveStreams/Open</c>: 400 when any <c>itemId</c> query value is federated.</summary>
    LiveStreamsOpen,

    /// <summary>The core HLS, attachment, trickplay and legacy segment routes: 404, clients must use Shoal's own <c>TranscodingUrl</c>.</summary>
    CoreHls,

    /// <summary>The <c>Audio/...</c> routes (MVP federates movies only): 404, counted like a non-static stream.</summary>
    Audio,

    /// <summary><c>DELETE Videos/ActiveEncodings</c>: answered by Shoal when the play session belongs to one of the caller's grants.</summary>
    ActiveEncodings,

    /// <summary><c>Videos/{id}/Trickplay/{width}/{index}.jpg</c>: the trickplay handler (v2.6.5; the default answers 404).</summary>
    Trickplay,

    /// <summary><c>Videos/{id}/Trickplay/{width}/tiles.m3u8</c>: 404, counted (v2.6.5).</summary>
    TrickplayPlaylist,

    /// <summary><c>Videos/{id}/{source}/Attachments/{index}</c>: the attachment handler (v2.6.6; the default answers 404).</summary>
    Attachment,
}

/// <summary>
/// One row of the interception table: the kind, the name of the route value that holds the item id, and whether the route is one of the five
/// whose core handler would read the stub (so an index miss still applies the path rule, DESIGN 3.5).
/// </summary>
/// <param name="Kind">What to do.</param>
/// <param name="IdName">The route value (or, for <see cref="RouteKind.LiveStreamsOpen"/>, the query key) holding the item id; null when there is none.</param>
/// <param name="ReadsStub">Whether core's handler would read the stub file.</param>
public sealed record InterceptRoute(RouteKind Kind, string? IdName, bool ReadsStub);

/// <summary>
/// The closed table of core routes that Shoal takes over for federated items (DESIGN 6.1, 8.8). Matching is by route template: the template is
/// normalized (parameter names, constraints and optional markers removed) and compared with the known shapes, so a trailing slash or letter case is
/// routing's business, not ours (W B-8). A template that is not in the table is not touched.
/// </summary>
public static class InterceptTable
{
    private static readonly Dictionary<string, RouteKind> Exact = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Items/{}/PlaybackInfo"] = RouteKind.PlaybackInfo,
        ["Videos/{}/stream"] = RouteKind.StaticStream,
        ["Videos/{}/stream.{}"] = RouteKind.StaticStream,
        ["Videos/{}/{}/Subtitles/{}/Stream.{}"] = RouteKind.Subtitle,
        ["Videos/{}/{}/Subtitles/{}/{}/Stream.{}"] = RouteKind.Subtitle,
        ["Items/{}/Download"] = RouteKind.Forbidden,
        ["Items/{}/File"] = RouteKind.Forbidden,
        ["LiveStreams/Open"] = RouteKind.LiveStreamsOpen,
        ["Videos/ActiveEncodings"] = RouteKind.ActiveEncodings,
        ["Videos/{}/Trickplay/{}/{}.jpg"] = RouteKind.Trickplay,
        ["Videos/{}/Trickplay/{}/tiles.m3u8"] = RouteKind.TrickplayPlaylist,
        ["Videos/{}/{}/Attachments/{}"] = RouteKind.Attachment,
    };

    // Segments that mark a core HLS, trickplay or attachment route below Videos/{id}/... and Audio/{id}/...
    private static readonly HashSet<string> CoreHlsMarkers = new(StringComparer.OrdinalIgnoreCase)
    {
        "master.m3u8", "main.m3u8", "live.m3u8", "hls", "hls1", "subtitles.m3u8", "Trickplay", "Attachments",
    };

    private static readonly HashSet<string> AudioMarkers = new(StringComparer.OrdinalIgnoreCase)
    {
        "stream", "universal", "master.m3u8", "main.m3u8", "live.m3u8", "hls", "hls1",
    };

    /// <summary>
    /// Looks a route template up.
    /// </summary>
    /// <param name="template">The attribute route template of the action, or null.</param>
    /// <returns>The row, or null when Shoal leaves the route to core.</returns>
    public static InterceptRoute? Find(string? template)
    {
        if (string.IsNullOrEmpty(template))
        {
            return null;
        }

        var segments = template.Trim('/').Split('/');
        var shape = string.Join('/', segments.Select(Normalize));
        if (Exact.TryGetValue(shape, out var kind))
        {
            return Describe(kind, segments);
        }

        var normalized = shape.Split('/');
        if (normalized.Length >= 3 && normalized[1] == "{}")
        {
            if (normalized[0].Equals("Videos", StringComparison.OrdinalIgnoreCase) && normalized.Skip(2).Any(CoreHlsMarkers.Contains))
            {
                return Describe(RouteKind.CoreHls, segments);
            }

            if (normalized[0].Equals("Audio", StringComparison.OrdinalIgnoreCase)
                && normalized.Skip(2).Any(s => AudioMarkers.Contains(s) || s.StartsWith("stream.", StringComparison.OrdinalIgnoreCase)))
            {
                return Describe(RouteKind.Audio, segments);
            }
        }

        return null;
    }

    /// <summary>
    /// Whether a template names a core HLS, trickplay, attachment or Audio route under <c>Videos/{id}</c> or <c>Audio/{id}</c>, for the reflection test.
    /// </summary>
    /// <param name="template">The template.</param>
    /// <returns>True when the table covers it.</returns>
    public static bool Covers(string? template) => Find(template) is not null;

    private static InterceptRoute Describe(RouteKind kind, string[] segments)
    {
        var idName = kind switch
        {
            RouteKind.LiveStreamsOpen => "itemId",
            RouteKind.ActiveEncodings => null,
            _ => ParameterName(segments.Length > 1 ? segments[1] : string.Empty),
        };

        // Every route whose core handler hands item.Path (the .strm) to ffmpeg, the attachment extractor or the stream opener reads the stub, so an
        // index miss falls back to the path rule there (Scout 4.1). Only the routes Shoal answers from its own handler do not.
        var readsStub = kind is not (RouteKind.PlaybackInfo or RouteKind.ActiveEncodings or RouteKind.LiveStreamsOpen);
        return new InterceptRoute(kind, idName, readsStub);
    }

    /// <summary>
    /// Replaces every <c>{name:constraint?}</c> with <c>{}</c>.
    /// </summary>
    private static string Normalize(string segment)
    {
        var builder = new System.Text.StringBuilder(segment.Length);
        var depth = 0;
        foreach (var c in segment)
        {
            if (c == '{')
            {
                if (depth++ == 0)
                {
                    builder.Append("{}");
                }
            }
            else if (c == '}')
            {
                depth = Math.Max(0, depth - 1);
            }
            else if (depth == 0 && c != '?')
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }

    private static string? ParameterName(string segment)
    {
        var start = segment.IndexOf('{', StringComparison.Ordinal);
        if (start < 0)
        {
            return null;
        }

        var end = segment.IndexOfAny([':', '?', '}'], start + 1);
        return end < 0 ? null : segment[(start + 1)..end];
    }
}
