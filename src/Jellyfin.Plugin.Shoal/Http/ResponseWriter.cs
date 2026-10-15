using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Shoal.Http;

/// <summary>
/// What Shoal is about to write (DESIGN 8.6). The kind decides the emitted <c>Content-Type</c>, the media types accepted from a peer, and
/// whether the response is "media" (only the three base headers) or not (also a sandboxing CSP, and <c>attachment</c> on playlists and
/// subtitles).
/// </summary>
public enum ResponseKind
{
    /// <summary>A direct-play stream; the emitted type follows the validated container.</summary>
    StaticStream,

    /// <summary>An HLS playlist of any of the three kinds.</summary>
    HlsPlaylist,

    /// <summary>An HLS media segment; the emitted type follows <c>ts</c> or <c>mp4</c>.</summary>
    HlsSegment,

    /// <summary>WebVTT (an HLS subtitle segment, or an external <c>.vtt</c>).</summary>
    SubtitleVtt,

    /// <summary>SubRip.</summary>
    SubtitleSrt,

    /// <summary>ASS or SSA.</summary>
    SubtitleAss,

    /// <summary>JSON subtitles (<c>js</c>, <c>json</c>), rebuilt by Home.</summary>
    SubtitleJson,

    /// <summary>PlaybackInfo, admin JSON and error bodies.</summary>
    Json,

    /// <summary>A trickplay tile: a validated JPEG (DESIGN v2.6.5).</summary>
    TrickplayTile,

    /// <summary>A font attachment; the emitted type follows the validated magic (DESIGN v2.6.6).</summary>
    Font
}

/// <summary>
/// The one place Shoal's responses get their <c>Content-Type</c> and security headers (DESIGN 8.6, Warden A-3, B-3, B-10). The type is
/// always taken from a closed table by what Shoal validated, never copied from a peer; a peer's declared type is only an input, checked
/// with <see cref="IsAcceptedUpstream"/>. Nothing a peer sent in a header (<c>Set-Cookie</c>, <c>Location</c>, <c>Content-Disposition</c>,
/// <c>Link</c>, <c>Refresh</c>, <c>Content-Security-Policy</c>, <c>Access-Control-*</c>, any <c>X-*</c>) has a way in: handlers
/// call these methods, and the analyzer bans the direct writes (<c>BannedSymbols.txt</c>).
/// </summary>
public static class ResponseWriter
{
    /// <summary>The CSP of non-media responses: nothing loads and the document is sandboxed.</summary>
    public const string ContentSecurityPolicy = "default-src 'none'; sandbox";

    private static readonly Dictionary<string, string> StaticTypes = new(StringComparer.Ordinal)
    {
        ["mkv"] = "video/x-matroska",
        ["mp4"] = "video/mp4",
        ["m4v"] = "video/mp4",
        ["webm"] = "video/webm",
        ["mov"] = "video/quicktime",
        ["ts"] = "video/mp2t",
        ["m2ts"] = "video/mp2t",
        ["mts"] = "video/mp2t",
        ["avi"] = "video/x-msvideo",
        ["mpg"] = "video/mpeg",
        ["mpeg"] = "video/mpeg",
        ["wmv"] = "video/x-ms-wmv",
        ["ogv"] = "video/ogg"
    };

    private static readonly HashSet<string> AcceptStatic = new(StringComparer.Ordinal)
    {
        "video/x-matroska", "video/mp4", "video/x-m4v", "video/webm", "video/quicktime", "video/mp2t", "video/x-msvideo", "video/mpeg",
        "video/x-ms-wmv", "video/ogg", "video/3gpp", "video/x-flv", "application/octet-stream"
    };

    private static readonly HashSet<string> AcceptPlaylist = new(StringComparer.Ordinal) { "application/vnd.apple.mpegurl", "application/x-mpegurl" };

    private static readonly HashSet<string> AcceptSegment = new(StringComparer.Ordinal) { "video/mp2t", "video/mp4", "application/octet-stream" };

    private static readonly HashSet<string> AcceptVtt = new(StringComparer.Ordinal) { "text/vtt", "text/plain", "application/octet-stream" };

    private static readonly HashSet<string> AcceptSrt = new(StringComparer.Ordinal) { "application/x-subrip", "text/plain", "application/octet-stream" };

    private static readonly HashSet<string> AcceptAss = new(StringComparer.Ordinal) { "text/x-ssa", "text/plain", "application/octet-stream" };

    private static readonly HashSet<string> AcceptSubtitleJson = new(StringComparer.Ordinal) { "application/json", "text/plain", "application/octet-stream" };

    private static readonly HashSet<string> AcceptJson = new(StringComparer.Ordinal) { "application/json" };

    private static readonly HashSet<string> AcceptTile = new(StringComparer.Ordinal) { "image/jpeg" };

    private static readonly Dictionary<string, string> FontTypes = new(StringComparer.Ordinal)
    {
        ["ttf"] = "font/ttf",
        ["otf"] = "font/otf",
        ["collection"] = "font/collection",
        ["woff"] = "font/woff",
        ["woff2"] = "font/woff2"
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(namingPolicy: null, allowIntegerValues: false) },
        MaxDepth = 32
    };

    /// <summary>
    /// The <c>Content-Type</c> Shoal emits.
    /// </summary>
    /// <param name="kind">What is being written.</param>
    /// <param name="format">For <see cref="ResponseKind.StaticStream"/> the validated container (lower case); for <see cref="ResponseKind.HlsSegment"/> <c>ts</c> or <c>mp4</c>; otherwise unused.</param>
    /// <returns>The type, or null when nothing may be emitted (an HLS segment that is neither <c>ts</c> nor <c>mp4</c>).</returns>
    public static string? ContentTypeFor(ResponseKind kind, string? format = null) => kind switch
    {
        ResponseKind.StaticStream => format is not null && StaticTypes.TryGetValue(format, out var type) ? type : "application/octet-stream",
        ResponseKind.HlsPlaylist => "application/vnd.apple.mpegurl",
        ResponseKind.HlsSegment => format switch { "ts" => "video/mp2t", "mp4" => "video/mp4", _ => null },
        ResponseKind.SubtitleVtt => "text/vtt; charset=utf-8",
        ResponseKind.SubtitleSrt => "application/x-subrip",
        ResponseKind.SubtitleAss => "text/x-ssa",
        ResponseKind.SubtitleJson => "application/json; charset=utf-8",
        ResponseKind.Json => "application/json; charset=utf-8",
        ResponseKind.TrickplayTile => "image/jpeg",
        ResponseKind.Font => format is not null && FontTypes.TryGetValue(format, out var font) ? font : null,
        _ => null
    };

    /// <summary>
    /// Whether the media type a peer declared is one Shoal accepts for this kind (case-insensitive, parameters such as <c>charset</c> ignored).
    /// Anything else, and a missing type, is refused: the handler answers 502 and counts it.
    /// </summary>
    /// <param name="kind">What is being fetched.</param>
    /// <param name="mediaType">The peer's media type with parameters removed (as <c>PeerResponse.MediaType</c> gives it), or null.</param>
    /// <returns>True when the type is on the accepted list for the kind.</returns>
    public static bool IsAcceptedUpstream(ResponseKind kind, string? mediaType)
    {
        if (string.IsNullOrEmpty(mediaType))
        {
            return false;
        }

        var type = mediaType.Split(';')[0].Trim().ToLowerInvariant();
        var accepted = kind switch
        {
            ResponseKind.StaticStream => AcceptStatic,
            ResponseKind.HlsPlaylist => AcceptPlaylist,
            ResponseKind.HlsSegment => AcceptSegment,
            ResponseKind.SubtitleVtt => AcceptVtt,
            ResponseKind.SubtitleSrt => AcceptSrt,
            ResponseKind.SubtitleAss => AcceptAss,
            ResponseKind.SubtitleJson => AcceptSubtitleJson,
            ResponseKind.Json => AcceptJson,
            ResponseKind.TrickplayTile => AcceptTile,
            _ => null // a font: the Peer's type is never used, the magic decides (v2.6.6)
        };
        return accepted is not null && accepted.Contains(type);
    }

    /// <summary>
    /// Whether the kind is media (a stream or a segment): such responses carry only the three base headers.
    /// </summary>
    /// <param name="kind">The kind.</param>
    /// <returns>True for streams and segments.</returns>
    public static bool IsMedia(ResponseKind kind) => kind is ResponseKind.StaticStream or ResponseKind.HlsSegment;

    /// <summary>
    /// Whether the kind is served as an <c>attachment</c> (playlists and subtitles), so that navigating to the URL downloads instead of renders.
    /// </summary>
    /// <param name="kind">The kind.</param>
    /// <returns>True for playlists and subtitles.</returns>
    public static bool IsAttachment(ResponseKind kind) =>
        kind is ResponseKind.HlsPlaylist or ResponseKind.SubtitleVtt or ResponseKind.SubtitleSrt or ResponseKind.SubtitleAss or ResponseKind.SubtitleJson
            or ResponseKind.TrickplayTile or ResponseKind.Font;

    /// <summary>
    /// Sets the Content-Type and the security headers of a response (DESIGN 8.6). Call it before the body is started. It replaces whatever
    /// the headers held for these names, so no earlier code can leave a hostile value behind.
    /// </summary>
    /// <param name="response">The response.</param>
    /// <param name="kind">What is being written.</param>
    /// <param name="format">The container, segment or font format (see <see cref="ContentTypeFor"/>).</param>
    /// <returns>False when nothing may be emitted for this kind and format; the headers are then not touched.</returns>
    public static bool Apply(HttpResponse response, ResponseKind kind, string? format = null)
    {
        ArgumentNullException.ThrowIfNull(response);
        var contentType = ContentTypeFor(kind, format);
        if (contentType is null)
        {
            return false;
        }

        ApplySecurityHeaders(response, kind);
#pragma warning disable RS0030 // The one place a response Content-Type is written (DESIGN 8.6, 8.8).
        response.ContentType = contentType;
#pragma warning restore RS0030
        return true;
    }

    /// <summary>
    /// Writes a status with no body (a refusal): the base headers and the CSP, no Content-Type.
    /// </summary>
    /// <param name="response">The response.</param>
    /// <param name="status">The status code.</param>
    public static void WriteStatus(HttpResponse response, int status)
    {
        ArgumentNullException.ThrowIfNull(response);
        response.StatusCode = status;
        ApplySecurityHeaders(response, ResponseKind.Json);
    }

    /// <summary>
    /// Writes a JSON body. The serialization is Shoal's own (camel case, enum names as text, depth 32), so no member of an object can choose
    /// a header or the type.
    /// </summary>
    /// <param name="response">The response.</param>
    /// <param name="status">The status code.</param>
    /// <param name="value">The value to serialize.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>A task that completes when the body is written.</returns>
    public static async Task WriteJsonAsync(HttpResponse response, int status, object? value, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(response);
        response.StatusCode = status;
        Apply(response, ResponseKind.Json);
        await JsonSerializer.SerializeAsync(response.Body, value, value?.GetType() ?? typeof(object), JsonOptions, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Sets <c>Retry-After</c> (whole seconds) on a rate-limited answer.
    /// </summary>
    /// <param name="response">The response.</param>
    /// <param name="seconds">The seconds to wait.</param>
    public static void SetRetryAfter(HttpResponse response, int seconds)
    {
        ArgumentNullException.ThrowIfNull(response);
#pragma warning disable RS0030 // ResponseWriter owns every response header (DESIGN 8.6, 8.8).
        response.Headers.RetryAfter = Math.Max(0, seconds).ToString(CultureInfo.InvariantCulture);
#pragma warning restore RS0030
    }

    /// <summary>
    /// Sets the headers that describe the body Shoal forwards, from values it validated itself. Only these are ever copied from a peer's
    /// answer, re-serialized (DESIGN 6.3): <c>Content-Length</c> is set by the caller through <see cref="HttpResponse.ContentLength"/>.
    /// </summary>
    /// <param name="response">The response.</param>
    /// <param name="contentRange">A re-serialized <c>Content-Range</c>, or null.</param>
    /// <param name="acceptRanges">A re-serialized <c>Accept-Ranges</c> (<c>bytes</c>), or null.</param>
    /// <param name="lastModified">A re-serialized <c>Last-Modified</c>, or null.</param>
    public static void SetValidatedMediaHeaders(HttpResponse response, string? contentRange, string? acceptRanges, DateTimeOffset? lastModified)
    {
        ArgumentNullException.ThrowIfNull(response);
#pragma warning disable RS0030 // ResponseWriter owns every response header (DESIGN 8.6, 8.8).
        if (contentRange is not null)
        {
            response.Headers.ContentRange = contentRange;
        }

        if (acceptRanges is not null)
        {
            response.Headers.AcceptRanges = acceptRanges;
        }

        if (lastModified is { } modified)
        {
            response.Headers.LastModified = modified.ToString("R", CultureInfo.InvariantCulture);
        }
#pragma warning restore RS0030
    }

    private static void ApplySecurityHeaders(HttpResponse response, ResponseKind kind)
    {
#pragma warning disable RS0030 // ResponseWriter owns every response header (DESIGN 8.6, 8.8).
        var headers = response.Headers;
        headers.XContentTypeOptions = "nosniff";
        headers["Referrer-Policy"] = "no-referrer";
        headers.CacheControl = "no-store";
        headers.Remove("Content-Disposition");
        headers.Remove("Content-Security-Policy");
        if (!IsMedia(kind))
        {
            headers.ContentSecurityPolicy = ContentSecurityPolicy;
        }

        if (IsAttachment(kind))
        {
            headers.ContentDisposition = "attachment";
        }
#pragma warning restore RS0030
    }
}

/// <summary>
/// An action result that writes through <see cref="ResponseWriter"/>: JSON with Shoal's own serialization, the type from the closed table and
/// the security headers. Controllers of the admin API return this instead of <c>Ok(...)</c> and friends.
/// </summary>
public sealed class ShoalResult : IActionResult
{
    private readonly int _status;
    private readonly object? _value;
    private readonly int? _retryAfterSeconds;

    /// <summary>
    /// Initializes a new instance of the <see cref="ShoalResult"/> class.
    /// </summary>
    /// <param name="status">The status code.</param>
    /// <param name="value">The body, or null for none.</param>
    /// <param name="retryAfterSeconds">A <c>Retry-After</c> value for a 429, or null.</param>
    public ShoalResult(int status, object? value = null, int? retryAfterSeconds = null)
    {
        _status = status;
        _value = value;
        _retryAfterSeconds = retryAfterSeconds;
    }

    /// <summary>Gets the status code.</summary>
    public int Status => _status;

    /// <summary>Gets the body, or null.</summary>
    public object? Value => _value;

    /// <inheritdoc />
    public async Task ExecuteResultAsync(ActionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var response = context.HttpContext.Response;
        if (_retryAfterSeconds is { } seconds)
        {
            ResponseWriter.SetRetryAfter(response, seconds);
        }

        if (_value is null)
        {
            ResponseWriter.WriteStatus(response, _status);
            return;
        }

        await ResponseWriter.WriteJsonAsync(response, _status, _value, context.HttpContext.RequestAborted).ConfigureAwait(false);
    }
}

/// <summary>
/// An action result for a JSON body that was serialized elsewhere with Jellyfin's own naming (PascalCase), such as a PlaybackInfo answer that
/// clients parse case-sensitively. It still goes through <see cref="ResponseWriter"/> for the type and the security headers.
/// </summary>
public sealed class ShoalRawJsonResult : IActionResult
{
    private readonly int _status;
    private readonly ReadOnlyMemory<byte> _utf8Json;

    /// <summary>
    /// Initializes a new instance of the <see cref="ShoalRawJsonResult"/> class.
    /// </summary>
    /// <param name="status">The status code.</param>
    /// <param name="utf8Json">The JSON document, UTF-8.</param>
    public ShoalRawJsonResult(int status, ReadOnlyMemory<byte> utf8Json)
    {
        _status = status;
        _utf8Json = utf8Json;
    }

    /// <summary>Gets the status code.</summary>
    public int Status => _status;

    /// <summary>Gets the JSON document.</summary>
    public ReadOnlyMemory<byte> Json => _utf8Json;

    /// <inheritdoc />
    public async Task ExecuteResultAsync(ActionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var response = context.HttpContext.Response;
        response.StatusCode = _status;
        ResponseWriter.Apply(response, ResponseKind.Json);
        await response.Body.WriteAsync(_utf8Json, context.HttpContext.RequestAborted).ConfigureAwait(false);
    }
}
