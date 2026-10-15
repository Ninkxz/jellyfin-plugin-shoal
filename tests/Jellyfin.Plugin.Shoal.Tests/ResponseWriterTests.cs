using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.Http;
using Jellyfin.Plugin.Shoal.Peers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Jellyfin.Plugin.Shoal.Tests;

/// <summary>
/// K7 acceptance (DESIGN 11.2, 8.6): every row of the type table and the header set; F-RS-01, -02, -03 (and -07, -08) against a stub handler
/// that fetches from a hostile peer: the emitted type comes from the table and never from the peer, and non-media rows carry the CSP and
/// <c>attachment</c>. Subtitle body checks.
/// </summary>
public sealed class ResponseWriterTests : IClassFixture<PeerServerFixture>
{
    private readonly PeerServerFixture _server;

    public ResponseWriterTests(PeerServerFixture server) => _server = server;

    private static HttpResponse NewResponse()
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        return context.Response;
    }

    [Theory]
    [InlineData("mkv", "video/x-matroska")]
    [InlineData("mp4", "video/mp4")]
    [InlineData("m4v", "video/mp4")]
    [InlineData("webm", "video/webm")]
    [InlineData("mov", "video/quicktime")]
    [InlineData("ts", "video/mp2t")]
    [InlineData("m2ts", "video/mp2t")]
    [InlineData("mts", "video/mp2t")]
    [InlineData("avi", "video/x-msvideo")]
    [InlineData("mpg", "video/mpeg")]
    [InlineData("mpeg", "video/mpeg")]
    [InlineData("wmv", "video/x-ms-wmv")]
    [InlineData("ogv", "video/ogg")]
    [InlineData("flv", "application/octet-stream")]
    [InlineData("html", "application/octet-stream")]
    [InlineData("svg", "application/octet-stream")]
    [InlineData("MKV", "application/octet-stream")]
    [InlineData("", "application/octet-stream")]
    [InlineData(null, "application/octet-stream")]
    public void TheStaticType_FollowsTheValidatedContainer(string? container, string expected)
    {
        Assert.Equal(expected, ResponseWriter.ContentTypeFor(ResponseKind.StaticStream, container));
    }

    [Theory]
    [InlineData(ResponseKind.HlsPlaylist, null, "application/vnd.apple.mpegurl")]
    [InlineData(ResponseKind.HlsSegment, "ts", "video/mp2t")]
    [InlineData(ResponseKind.HlsSegment, "mp4", "video/mp4")]
    [InlineData(ResponseKind.HlsSegment, "m4s", null)]
    [InlineData(ResponseKind.HlsSegment, null, null)]
    [InlineData(ResponseKind.SubtitleVtt, null, "text/vtt; charset=utf-8")]
    [InlineData(ResponseKind.SubtitleSrt, null, "application/x-subrip")]
    [InlineData(ResponseKind.SubtitleAss, null, "text/x-ssa")]
    [InlineData(ResponseKind.SubtitleJson, null, "application/json; charset=utf-8")]
    [InlineData(ResponseKind.Json, null, "application/json; charset=utf-8")]
    public void TheEmittedType_EveryRowOfTheTable(ResponseKind kind, string? format, string? expected)
    {
        Assert.Equal(expected, ResponseWriter.ContentTypeFor(kind, format));
    }

    [Theory]
    [InlineData(ResponseKind.StaticStream, "video/x-matroska", true)]
    [InlineData(ResponseKind.StaticStream, "VIDEO/MP4; codecs=avc1", true)]
    [InlineData(ResponseKind.StaticStream, "video/3gpp", true)]
    [InlineData(ResponseKind.StaticStream, "video/x-flv", true)]
    [InlineData(ResponseKind.StaticStream, "application/octet-stream", true)]
    [InlineData(ResponseKind.StaticStream, "text/html", false)]
    [InlineData(ResponseKind.StaticStream, "image/svg+xml", false)]
    [InlineData(ResponseKind.StaticStream, "application/json", false)]
    [InlineData(ResponseKind.StaticStream, "text/plain", false)]
    [InlineData(ResponseKind.StaticStream, "", false)]
    [InlineData(ResponseKind.StaticStream, null, false)]
    [InlineData(ResponseKind.HlsPlaylist, "application/vnd.apple.mpegurl", true)]
    [InlineData(ResponseKind.HlsPlaylist, "application/x-mpegURL", true)]
    [InlineData(ResponseKind.HlsPlaylist, "text/html", false)]
    [InlineData(ResponseKind.HlsPlaylist, "text/plain", false)]
    [InlineData(ResponseKind.HlsSegment, "video/mp2t", true)]
    [InlineData(ResponseKind.HlsSegment, "video/mp4", true)]
    [InlineData(ResponseKind.HlsSegment, "application/octet-stream", true)]
    [InlineData(ResponseKind.HlsSegment, "text/html", false)]
    [InlineData(ResponseKind.SubtitleVtt, "text/vtt", true)]
    [InlineData(ResponseKind.SubtitleVtt, "text/plain", true)]
    [InlineData(ResponseKind.SubtitleVtt, "application/octet-stream", true)]
    [InlineData(ResponseKind.SubtitleVtt, "text/html", false)]
    [InlineData(ResponseKind.SubtitleSrt, "application/x-subrip", true)]
    [InlineData(ResponseKind.SubtitleSrt, "text/x-ssa", false)]
    [InlineData(ResponseKind.SubtitleAss, "text/x-ssa", true)]
    [InlineData(ResponseKind.SubtitleAss, "text/plain", true)]
    [InlineData(ResponseKind.SubtitleAss, "image/svg+xml", false)]
    [InlineData(ResponseKind.SubtitleJson, "application/json", true)]
    [InlineData(ResponseKind.SubtitleJson, "text/plain", true)]
    [InlineData(ResponseKind.SubtitleJson, "application/octet-stream", true)]
    [InlineData(ResponseKind.SubtitleJson, "text/html", false)]
    [InlineData(ResponseKind.Json, "application/json", true)]
    [InlineData(ResponseKind.Json, "application/json; charset=bogus", true)]
    [InlineData(ResponseKind.Json, "text/plain", false)]
    [InlineData(ResponseKind.Json, "application/octet-stream", false)]
    public void TheUpstreamTypes_AreAcceptedPerRow_CaseInsensitively_ParametersIgnored(ResponseKind kind, string? mediaType, bool accepted)
    {
        Assert.Equal(accepted, ResponseWriter.IsAcceptedUpstream(kind, mediaType));
    }

    [Theory]
    [InlineData(ResponseKind.StaticStream, true, false)]
    [InlineData(ResponseKind.HlsSegment, true, false)]
    [InlineData(ResponseKind.HlsPlaylist, false, true)]
    [InlineData(ResponseKind.SubtitleVtt, false, true)]
    [InlineData(ResponseKind.SubtitleSrt, false, true)]
    [InlineData(ResponseKind.SubtitleAss, false, true)]
    [InlineData(ResponseKind.SubtitleJson, false, true)]
    [InlineData(ResponseKind.Json, false, false)]
    public void MediaCarriesTheBaseHeadersOnly_AndEverythingElseTheCspToo(ResponseKind kind, bool media, bool attachment)
    {
        var response = NewResponse();
        response.Headers["Content-Disposition"] = "inline; filename=evil.html";
        response.Headers["Content-Security-Policy"] = "default-src *";

        Assert.True(ResponseWriter.Apply(response, kind, "ts"));

        Assert.Equal("nosniff", response.Headers.XContentTypeOptions.ToString());
        Assert.Equal("no-referrer", response.Headers["Referrer-Policy"].ToString());
        Assert.Equal("no-store", response.Headers.CacheControl.ToString());
        Assert.Equal(media, ResponseWriter.IsMedia(kind));
        Assert.Equal(attachment, ResponseWriter.IsAttachment(kind));
        Assert.Equal(media ? string.Empty : "default-src 'none'; sandbox", response.Headers.ContentSecurityPolicy.ToString());
        Assert.Equal(attachment ? "attachment" : string.Empty, response.Headers.ContentDisposition.ToString());
        Assert.NotNull(response.ContentType);
    }

    [Fact]
    public void ApplyingTwice_NeverDuplicatesAHeader()
    {
        var response = NewResponse();

        ResponseWriter.Apply(response, ResponseKind.Json);
        ResponseWriter.Apply(response, ResponseKind.Json);
        ResponseWriter.WriteStatus(response, 404);

        Assert.Single(response.Headers["Referrer-Policy"]);
        Assert.Single(response.Headers.CacheControl);
        Assert.Single(response.Headers.XContentTypeOptions);
    }

    [Fact]
    public void ASegmentOfAnUnknownFormat_EmitsNothing_AndLeavesTheHeadersAlone()
    {
        var response = NewResponse();

        Assert.False(ResponseWriter.Apply(response, ResponseKind.HlsSegment, "m4s"));

        Assert.Empty(response.Headers);
    }

    [Fact]
    public async Task JsonIsWrittenWithShoalsOwnSerialization_AndAnErrorBodyIsAnObjectWithACode()
    {
        var response = NewResponse();

        await ResponseWriter.WriteJsonAsync(response, 409, new { error = "sessionActive", Kind = Jellyfin.Plugin.Shoal.Pairing.PairingErrorKind.RequestReplaced });

        response.Body.Position = 0;
        var text = new StreamReader(response.Body).ReadToEnd();
        Assert.Equal(409, response.StatusCode);
        Assert.Equal("application/json; charset=utf-8", response.ContentType);
        Assert.Equal("{\"error\":\"sessionActive\",\"kind\":\"requestReplaced\"}", text);
        Assert.Equal("default-src 'none'; sandbox", response.Headers.ContentSecurityPolicy.ToString());
    }

    [Fact]
    public void AStatusWithNoBody_HasNoContentType()
    {
        var response = NewResponse();

        ResponseWriter.WriteStatus(response, 404);

        Assert.Equal(404, response.StatusCode);
        Assert.Null(response.ContentType);
        Assert.Equal("nosniff", response.Headers.XContentTypeOptions.ToString());
        ResponseWriter.SetRetryAfter(response, 42);
        Assert.Equal("42", response.Headers.RetryAfter.ToString());
    }

    [Fact]
    public void OnlyValidatedMediaHeadersAreSet_ReSerialized()
    {
        var response = NewResponse();

        ResponseWriter.SetValidatedMediaHeaders(response, "bytes 0-9/100", "bytes", new DateTimeOffset(2026, 10, 15, 12, 0, 0, TimeSpan.Zero));

        Assert.Equal("bytes 0-9/100", response.Headers.ContentRange.ToString());
        Assert.Equal("bytes", response.Headers.AcceptRanges.ToString());
        Assert.Equal("Thu, 15 Oct 2026 12:00:00 GMT", response.Headers.LastModified.ToString());
        Assert.DoesNotContain(response.Headers, h => h.Key is "Set-Cookie" or "Location" or "Link" or "Refresh" or "Server");
    }

    // --- subtitle body checks

    [Theory]
    [InlineData(ResponseKind.SubtitleVtt, "WEBVTT\n\n00:00.000 --> 00:01.000\nHi\n", true)]
    [InlineData(ResponseKind.SubtitleVtt, "\uFEFFWEBVTT\n", true)]
    [InlineData(ResponseKind.SubtitleVtt, "<html>", false)]
    [InlineData(ResponseKind.SubtitleVtt, "WEB", false)]
    [InlineData(ResponseKind.SubtitleSrt, "1\n00:00:00,000 --> 00:00:01,000\nHi\n", true)]
    [InlineData(ResponseKind.SubtitleSrt, "\n\n  42 \r\n00:00", true)]
    [InlineData(ResponseKind.SubtitleSrt, "1234567\n", false)]
    [InlineData(ResponseKind.SubtitleSrt, "<script>\n", false)]
    [InlineData(ResponseKind.SubtitleSrt, "", false)]
    [InlineData(ResponseKind.SubtitleAss, "[Script Info]\nTitle: x\n", true)]
    [InlineData(ResponseKind.SubtitleAss, "\uFEFF[Script Info]\n", true)]
    [InlineData(ResponseKind.SubtitleAss, "[V4+ Styles]\n", false)]
    [InlineData(ResponseKind.SubtitleJson, "{\"TrackEvents\":[]}", true)]
    [InlineData(ResponseKind.Json, "{}", false)]
    public void TheBodyOpensLikeItsFormat(ResponseKind kind, string body, bool acceptable)
    {
        Assert.Equal(acceptable, SubtitleBodyCheck.IsAcceptable(kind, Encoding.UTF8.GetBytes(body)));
    }

    [Fact]
    public void ABodyWithANulOrInvalidUtf8_IsRefused_InEveryFormat()
    {
        foreach (var kind in new[] { ResponseKind.SubtitleVtt, ResponseKind.SubtitleSrt, ResponseKind.SubtitleAss, ResponseKind.SubtitleJson })
        {
            Assert.False(SubtitleBodyCheck.IsAcceptable(kind, "WEBVTT\0\n1\n[Script Info]"u8));
            Assert.False(SubtitleBodyCheck.IsAcceptable(kind, new byte[] { 0x57, 0x45, 0x42, 0x56, 0x54, 0x54, 0xff, 0xfe }));
        }
    }

    [Fact]
    public void TheAssHeaderMustBeInTheFirst4Kilobytes()
    {
        var late = new string('x', 5000) + "[Script Info]";

        Assert.False(SubtitleBodyCheck.IsAcceptable(ResponseKind.SubtitleAss, Encoding.UTF8.GetBytes(late)));
    }

    // --- F-RS-01, -02, -03, -07, -08 against a stub handler

    private async Task<(HttpResponseMessage Response, string Body)> ThroughStubAsync(ResponseKind kind, string format, string peerPath)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        using var client = new PeerClient(() => "Home", () => "0.1.0.0", TimeProvider.System) { EnforceAddressPolicy = false };
        var peer = new PeerEndpoint("00000000000000000000000000000abc", "peer", new Uri(_server.BaseUrl), "shoal-1a2b3c4d5e6f-4b0c1d2e", "9f2c4d6e8a0b1c3d5e7f9a0b2c4d6e8f");
        app.MapGet("/stub", async (HttpContext ctx) =>
        {
            using var upstream = await client.SendAsync(peer, PeerRequest.GetMedia(peerPath), PeerCallProfile.HlsSegment, ctx.RequestAborted);
            if (!upstream.HasBody || !ResponseWriter.IsAcceptedUpstream(kind, upstream.MediaType))
            {
                ResponseWriter.WriteStatus(ctx.Response, 502);
                return;
            }

            if (!ResponseWriter.Apply(ctx.Response, kind, format))
            {
                ResponseWriter.WriteStatus(ctx.Response, 502);
                return;
            }

            await upstream.OpenBody().CopyToAsync(ctx.Response.Body, ctx.RequestAborted);
        });
        await app.StartAsync();
        var url = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        using var http = new HttpClient();
        var response = await http.GetAsync(url + "/stub");
        return (response, await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData(ResponseKind.StaticStream, "mkv", "/html-script")]
    [InlineData(ResponseKind.HlsSegment, "ts", "/html-script")]
    [InlineData(ResponseKind.SubtitleVtt, "vtt", "/html-script")]
    [InlineData(ResponseKind.SubtitleSrt, "srt", "/html-script")]
    [InlineData(ResponseKind.SubtitleAss, "ass", "/html-script")]
    [InlineData(ResponseKind.HlsPlaylist, "m3u8", "/html-script")]
    [InlineData(ResponseKind.StaticStream, "mkv", "/svg-script")]
    [InlineData(ResponseKind.HlsSegment, "ts", "/svg-script")]
    [InlineData(ResponseKind.SubtitleVtt, "vtt", "/svg-script")]
    [InlineData(ResponseKind.SubtitleSrt, "srt", "/svg-script")]
    [InlineData(ResponseKind.SubtitleAss, "ass", "/svg-script")]
    [InlineData(ResponseKind.SubtitleJson, "json", "/svg-script")]
    [InlineData(ResponseKind.Json, "json", "/html-script")]
    public async Task F_RS_01_And_02_AScriptServedAsADocumentOrAnImage_IsRefused_OnEveryRoute(ResponseKind kind, string format, string peerPath)
    {
        var (response, body) = await ThroughStubAsync(kind, format, peerPath);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal(string.Empty, body);
        Assert.DoesNotContain("script", body, StringComparison.OrdinalIgnoreCase);
        Assert.Null(response.Content.Headers.ContentType);
        Assert.Contains("nosniff", response.Headers.GetValues("X-Content-Type-Options"));
        Assert.Contains("default-src 'none'; sandbox", response.Headers.GetValues("Content-Security-Policy"));
    }

    [Fact]
    public async Task F_RS_01_AnAcceptedBody_GetsTheTypeOfTheTable_NeverThePeers()
    {
        var (response, body) = await ThroughStubAsync(ResponseKind.StaticStream, "mp4", "/hostile-headers-mkv");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(10, body.Length);
        Assert.Equal("video/mp4", response.Content.Headers.ContentType!.MediaType); // the peer said video/x-matroska; the validated container says mp4
        Assert.Contains("nosniff", response.Headers.GetValues("X-Content-Type-Options"));
        Assert.Contains("no-store", response.Headers.GetValues("Cache-Control"));
        Assert.Contains("no-referrer", response.Headers.GetValues("Referrer-Policy"));
        Assert.False(response.Headers.Contains("Content-Security-Policy")); // media: the three base headers only
    }

    [Fact]
    public async Task F_RS_07_And_08_NothingTheHostilePeerSendsInAHeaderReachesTheClient()
    {
        var (response, _) = await ThroughStubAsync(ResponseKind.StaticStream, "mkv", "/hostile-headers-mkv");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var all = response.Headers.Concat(response.Content.Headers).Select(h => h.Key).ToList();
        foreach (var forbidden in new[] { "Set-Cookie", "Location", "X-Peer-Secret", "Link", "Refresh", "Access-Control-Allow-Origin", "Content-Disposition" })
        {
            Assert.DoesNotContain(forbidden, all, StringComparer.OrdinalIgnoreCase);
        }

        Assert.False(response.Headers.Contains("Content-Security-Policy"));
        Assert.Equal("video/x-matroska", response.Content.Headers.ContentType!.MediaType);
    }

    [Fact]
    public async Task F_RS_03_ABogusCharset_NeverReachesTheClient_ThePlaylistAndSegmentRowsStillApply()
    {
        var (response, body) = await ThroughStubAsync(ResponseKind.Json, "json", "/bogus-charset");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("utf-8", response.Content.Headers.ContentType.CharSet);
        Assert.Equal("{\"a\":\"x\"}", body);
        Assert.False(response.Headers.Contains("Set-Cookie"));
        Assert.Contains("default-src 'none'; sandbox", response.Headers.GetValues("Content-Security-Policy"));
    }

    [Fact]
    public async Task APlaylistAndASubtitle_AreServedAsAttachments_WithTheTableTypes()
    {
        var (vtt, _) = await ThroughStubAsync(ResponseKind.SubtitleVtt, "vtt", "/vtt");
        Assert.Equal("text/vtt", vtt.Content.Headers.ContentType!.MediaType);
        Assert.Equal("utf-8", vtt.Content.Headers.ContentType.CharSet);
        Assert.Equal("attachment", vtt.Content.Headers.ContentDisposition!.DispositionType);
        Assert.Contains("default-src 'none'; sandbox", vtt.Headers.GetValues("Content-Security-Policy"));

        var (segment, body) = await ThroughStubAsync(ResponseKind.HlsSegment, "ts", "/ts-segment");
        Assert.Equal("video/mp2t", segment.Content.Headers.ContentType!.MediaType);
        Assert.Null(segment.Content.Headers.ContentDisposition);
        Assert.Equal(4, body.Length);
    }
}
