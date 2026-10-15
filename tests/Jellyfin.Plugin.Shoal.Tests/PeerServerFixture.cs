using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
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
/// One request as the fake peer saw it: every header and the body, byte for byte (the "fakepeer records every request byte" rule of
/// DESIGN 11.3, T-SEC-20).
/// </summary>
/// <param name="Method">The method.</param>
/// <param name="Path">The path.</param>
/// <param name="Query">The query string including the leading '?', or empty.</param>
/// <param name="Headers">Header name to raw value, as received.</param>
/// <param name="Body">The body bytes.</param>
public sealed record RecordedRequest(string Method, string Path, string Query, IReadOnlyDictionary<string, string> Headers, byte[] Body);

/// <summary>
/// A real local HTTP server that behaves like a hostile peer (Warden B-2; fixtures F-RS-01 to F-RS-09): endless chunked bodies,
/// trickling bodies, stalls before and after the headers, oversized declared lengths, redirects, bad encodings, hostile headers.
/// It records every request it receives.
/// </summary>
public sealed class PeerServerFixture : IAsyncLifetime
{
    private WebApplication? _app;

    public string BaseUrl { get; private set; } = string.Empty;

    public ConcurrentDictionary<string, int> Hits { get; } = new();

    public ConcurrentQueue<RecordedRequest> Requests { get; } = new();

    /// <summary>The server id <c>/System/Info/Public</c> answers with (32 hex characters, or anything a test wants to send).</summary>
    public string PublicServerId { get; set; } = "0123456789abcdef0123456789abcdef";

    /// <summary>The status <c>/Users/Me</c> answers with.</summary>
    public int MeStatus { get; set; } = 200;

    /// <summary>The paths of the requests received so far, oldest first.</summary>
    public IReadOnlyList<string> Paths => Requests.Select(r => r.Path).ToList();

    public RecordedRequest Last(string path) => Requests.Last(r => r.Path == path);

    public int Count(string path) => Requests.Count(r => r.Path == path);

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();

        app.Use(async (context, next) =>
        {
            Hits.AddOrUpdate(context.Request.Path.Value ?? string.Empty, 1, (_, n) => n + 1);
            using var body = new System.IO.MemoryStream();
            await context.Request.Body.CopyToAsync(body);
            Requests.Enqueue(new RecordedRequest(
                context.Request.Method,
                context.Request.Path.Value ?? string.Empty,
                context.Request.QueryString.Value ?? string.Empty,
                context.Request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString(), StringComparer.OrdinalIgnoreCase),
                body.ToArray()));
            await next();
        });

        app.MapGet("/System/Info/Public", (HttpContext ctx) => Results.Text("{\"ServerName\":\"Hops\",\"Version\":\"12.1.0\",\"Id\":\"" + PublicServerId + "\"}", "application/json"));
        app.MapGet("/Users/Me", (HttpContext ctx) =>
        {
            ctx.Response.StatusCode = MeStatus;
            return Task.CompletedTask;
        });

        app.MapGet("/ok-json", () => Results.Text("{\"a\":1}", "application/json"));
        app.MapGet("/ok-json-utf8", () => Results.Text("{\"a\":1}", "application/json; charset=utf-8"));
        app.MapGet("/text-plain", () => Results.Text("{\"a\":1}", "text/plain"));
        app.MapPost("/echo", async (HttpContext ctx) =>
        {
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsync("{\"ok\":true}");
        });

        app.MapGet("/not-found", () => Results.Text("nope nope nope", "text/plain", Encoding.UTF8, StatusCodes.Status404NotFound));
        app.MapGet("/boom", () => Results.Text("boom", "text/plain", Encoding.UTF8, StatusCodes.Status500InternalServerError));
        app.MapGet("/unauthorized", () => Results.StatusCode(StatusCodes.Status401Unauthorized));
        app.MapGet("/System/Ping", () => Results.Text("Jellyfin Server", "text/plain"));

        app.MapGet("/big", async (HttpContext ctx) =>
        {
            ctx.Response.ContentLength = 5 * 1024 * 1024;
            ctx.Response.ContentType = "application/json";
            await ctx.Response.StartAsync();
            await ctx.Response.Body.FlushAsync(); // StartAsync alone does not put the headers on the wire
            var chunk = new byte[64 * 1024];
            try
            {
                for (var i = 0; i < 80; i++)
                {
                    await ctx.Response.Body.WriteAsync(chunk, ctx.RequestAborted);
                }
            }
            catch (OperationCanceledException)
            {
            }
        });

        // Declares 100 MB, sends only the headers and a little (F-CAP style: the cap must refuse it from the header alone).
        app.MapGet("/declared-100mb", async (HttpContext ctx) =>
        {
            ctx.Response.ContentLength = 100L * 1024 * 1024;
            ctx.Response.ContentType = "application/json";
            await ctx.Response.StartAsync();
            await ctx.Response.Body.FlushAsync();
            try
            {
                await Task.Delay(Timeout.Infinite, ctx.RequestAborted);
            }
            catch (OperationCanceledException)
            {
            }
        });

        // F-RS-04: No Content-Length: chunked, and it never ends.
        app.MapGet("/endless", async (HttpContext ctx) =>
        {
            ctx.Response.ContentType = "application/json";
            var chunk = new byte[8 * 1024];
            Array.Fill(chunk, (byte)'a');
            try
            {
                while (!ctx.RequestAborted.IsCancellationRequested)
                {
                    await ctx.Response.Body.WriteAsync(chunk, ctx.RequestAborted);
                }
            }
            catch (OperationCanceledException)
            {
            }
        });

        // F-RS-05: headers at once, then one byte every 100 ms, forever.
        app.MapGet("/trickle", async (HttpContext ctx) =>
        {
            ctx.Response.ContentType = "application/json";
            await ctx.Response.StartAsync();
            await ctx.Response.Body.FlushAsync(); // StartAsync alone does not put the headers on the wire
            try
            {
                while (!ctx.RequestAborted.IsCancellationRequested)
                {
                    await ctx.Response.Body.WriteAsync(new byte[] { (byte)'a' }, ctx.RequestAborted);
                    await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
                    await Task.Delay(100, ctx.RequestAborted);
                }
            }
            catch (OperationCanceledException)
            {
            }
        });

        // Headers at once, then nothing at all.
        app.MapGet("/stall-after-headers", async (HttpContext ctx) =>
        {
            ctx.Response.ContentType = "application/json";
            await ctx.Response.StartAsync();
            await ctx.Response.Body.FlushAsync(); // StartAsync alone does not put the headers on the wire
            try
            {
                await Task.Delay(Timeout.Infinite, ctx.RequestAborted);
            }
            catch (OperationCanceledException)
            {
            }
        });

        // Not even headers.
        app.MapGet("/stall-before-headers", async (HttpContext ctx) =>
        {
            try
            {
                await Task.Delay(Timeout.Infinite, ctx.RequestAborted);
            }
            catch (OperationCanceledException)
            {
            }
        });

        // Headers at once, the (declared) body only after a long pause.
        app.MapGet("/slow-body", async (HttpContext ctx) =>
        {
            ctx.Response.ContentLength = 4;
            ctx.Response.ContentType = "video/x-matroska";
            await ctx.Response.StartAsync();
            await ctx.Response.Body.FlushAsync(); // StartAsync alone does not put the headers on the wire
            try
            {
                await Task.Delay(4000, ctx.RequestAborted);
                await ctx.Response.Body.WriteAsync(new byte[] { 1, 2, 3, 4 }, ctx.RequestAborted);
            }
            catch (OperationCanceledException)
            {
            }
        });

        // F-RS-09: a redirect to the metadata address. The target must never be requested.
        app.MapGet("/redirect", (HttpContext ctx) =>
        {
            ctx.Response.StatusCode = StatusCodes.Status302Found;
            ctx.Response.Headers.Location = "/redirect-target";
        });

        app.MapGet("/redirect-metadata", (HttpContext ctx) =>
        {
            ctx.Response.StatusCode = StatusCodes.Status302Found;
            ctx.Response.Headers.Location = "http://169.254.169.254/latest/meta-data/";
        });

        app.MapGet("/redirect-target", () => Results.Text("target", "text/plain"));

        // F-RS-07 and F-RS-08: a Location header on a 200 and a cookie.
        app.MapGet("/location-and-cookie", async (HttpContext ctx) =>
        {
            ctx.Response.ContentType = "application/json";
            ctx.Response.Headers.Location = "http://evil.example/";
            ctx.Response.Headers["Set-Cookie"] = "session=stolen; Path=/";
            ctx.Response.Headers["X-Peer-Secret"] = "nope";
            await ctx.Response.Body.WriteAsync("{\"a\":1}"u8.ToArray());
        });

        app.MapGet("/bad-utf8", async (HttpContext ctx) =>
        {
            ctx.Response.ContentType = "application/json";
            await ctx.Response.Body.WriteAsync(new byte[] { 0x7b, 0xff, 0xfe, 0x7d });
        });

        // F-RS-03
        app.MapGet("/bogus-charset", async (HttpContext ctx) =>
        {
            ctx.Response.Headers.ContentType = "application/json; charset=bogus";
            ctx.Response.Headers["Set-Cookie"] = "a=b";
            await ctx.Response.Body.WriteAsync("{\"a\":\"x\"}"u8.ToArray());
        });

        // F-RS-06: declares 100 bytes, sends 10 and hangs up.
        app.MapGet("/short-body", async (HttpContext ctx) =>
        {
            ctx.Response.ContentLength = 100;
            ctx.Response.ContentType = "application/json";
            await ctx.Response.Body.WriteAsync(new byte[10]);
            await ctx.Response.Body.FlushAsync(); // the headers and 10 bytes must be on the wire before the hang-up, or the client sees a reset instead of a short body
            await Task.Delay(100);
            ctx.Abort();
        });

        app.MapGet("/huge-headers", (HttpContext ctx) =>
        {
            ctx.Response.Headers["X-Big"] = new string('a', 40 * 1024);
            return Results.Text("{}", "application/json");
        });

        // 10,000 levels of nesting.
        app.MapGet("/deep-json", async (HttpContext ctx) =>
        {
            ctx.Response.ContentType = "application/json";
            await ctx.Response.Body.WriteAsync(Encoding.ASCII.GetBytes(new string('[', 10_000) + new string(']', 10_000)));
        });

        // F-RS-01 and F-RS-02: a script served as a document or an image, on any route.
        app.MapGet("/html-script", () => Results.Text("<html><script>alert(1)</script></html>", "text/html"));
        app.MapGet("/svg-script", () => Results.Text("<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script></svg>", "image/svg+xml"));

        // A harmless body with every header a peer could use against Home.
        app.MapGet("/hostile-headers-mkv", async (HttpContext ctx) =>
        {
            ctx.Response.ContentType = "video/x-matroska";
            ctx.Response.ContentLength = 10;
            ctx.Response.Headers.Location = "http://evil.example/";
            ctx.Response.Headers["Set-Cookie"] = "session=stolen";
            ctx.Response.Headers["X-Peer-Secret"] = "nope";
            ctx.Response.Headers["Content-Disposition"] = "inline; filename=evil.html";
            ctx.Response.Headers["Link"] = "<http://evil.example/>; rel=preload";
            ctx.Response.Headers["Refresh"] = "0; url=http://evil.example/";
            ctx.Response.Headers["Content-Security-Policy"] = "default-src *";
            ctx.Response.Headers["Access-Control-Allow-Origin"] = "*";
            await ctx.Response.Body.WriteAsync(new byte[10]);
        });

        app.MapGet("/vtt", () => Results.Text("WEBVTT\n\n00:00.000 --> 00:01.000\nHello\n", "text/vtt"));
        app.MapGet("/ts-segment", async (HttpContext ctx) =>
        {
            ctx.Response.ContentType = "video/mp2t";
            ctx.Response.ContentLength = 4;
            await ctx.Response.Body.WriteAsync(new byte[4]);
        });

        app.MapGet("/json-null", () => Results.Text("null", "application/json"));

        // A static-stream style answer with a declared length and a range.
        app.MapGet("/static-ok", async (HttpContext ctx) =>
        {
            ctx.Response.StatusCode = StatusCodes.Status206PartialContent;
            ctx.Response.ContentType = "video/x-matroska";
            ctx.Response.Headers.ContentRange = "bytes 0-9/100";
            ctx.Response.ContentLength = 10;
            await ctx.Response.Body.WriteAsync(new byte[10]);
        });

        // A static-stream style answer without a declared length (chunked).
        app.MapGet("/static-chunked", async (HttpContext ctx) =>
        {
            ctx.Response.ContentType = "video/x-matroska";
            await ctx.Response.Body.WriteAsync(new byte[10]);
            await ctx.Response.Body.FlushAsync();
        });

        FakePeerExtensions.MapAll(null, app);

        await app.StartAsync();
        _app = app;
        BaseUrl = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
    }

    public async Task DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }
}
