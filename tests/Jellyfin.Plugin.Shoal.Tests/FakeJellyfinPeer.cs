using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
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

namespace Jellyfin.Plugin.Shoal.Tests;

/// <summary>
/// What a scripted endpoint answers.
/// </summary>
/// <param name="Status">The HTTP status.</param>
/// <param name="Body">The body, or null.</param>
/// <param name="ContentType">The content type of the body.</param>
/// <param name="Hang">True to never answer (the client's deadline fires).</param>
/// <param name="Location">A <c>Location</c> header, for redirects.</param>
public sealed record Reply(int Status, string? Body = null, string ContentType = "application/json; charset=utf-8", bool Hang = false, string? Location = null)
{
    public static Reply Json(object value, int status = 200) => new(status, JsonSerializer.Serialize(value));

    public static Reply Plain(int status, string body = "Error processing request.") => new(status, body, "text/plain");

    public static Reply HangForever => new(0, Hang: true);
}

/// <summary>
/// A scriptable Jellyfin 12.1 as the Quick Connect pairing sees it (DESIGN 11.3 fakepeer): <c>System/Info[/Public]</c>, <c>QuickConnect/*</c>,
/// <c>Users/AuthenticateWithQuickConnect</c>, <c>Users/Me</c>, <c>Sessions/Logout</c>. It records every request.
/// </summary>
public sealed class FakeJellyfinPeer : IAsyncDisposable
{
    public const string DefaultSecret = "AB12CD34EF56AB12CD34EF56AB12CD34EF56AB12CD34EF56AB12CD34EF56AB12";
    public const string DefaultToken = "9f2c4d6e8a0b1c3d5e7f9a0b2c4d6e8f";

    private readonly WebApplication _app = null!;
    private int _inFlight;
    private int _connectCalls;
    private int _initiates;
    private int _redeems;
    private int _logouts;

    private FakeJellyfinPeer(WebApplication app) => _app = app;

    public string BaseUrl { get; private set; } = string.Empty;

    public int Port => new Uri(BaseUrl).Port;

    public ConcurrentQueue<RecordedRequest> Requests { get; } = new();

    public ConcurrentQueue<DateTime> ConnectTimes { get; } = new();

    public int MaxConnectInFlight { get; private set; }

    public string ServerId { get; set; } = "0123456789abcdef0123456789abcdef";

    public string ServerName { get; set; } = "Hops";

    public string PublicInfoJson { get; set; } = string.Empty;

    public Reply Enabled { get; set; } = new(200, "true");

    public Reply PublicInfo { get; set; } = new(0);

    public Reply SystemInfo { get; set; } = new(0);

    public string Code { get; set; } = "739481";

    public string Secret { get; set; } = DefaultSecret;

    public string Token { get; set; } = DefaultToken;

    public Guid UserId { get; set; } = Guid.Parse("c0ffee00-1111-2222-3333-444444444444");

    public Func<Reply>? InitiateReply { get; set; }

    /// <summary>Answers poll number n (1-based).</summary>
    public Func<int, Reply> Connect { get; set; } = n => new(200, null);

    public Func<Reply>? RedeemReply { get; set; }

    /// <summary>Answers <c>POST /Shoal/Pair</c> from the request body (K22): the real invite book of a Shoal Peer plugs in here.</summary>
    public Func<byte[], Reply>? PairReply { get; set; }

    public Func<Reply>? MeReply { get; set; }

    /// <summary>Answers <c>POST /Items/{id}/PlaybackInfo</c> (the id and the request body); null answers 404.</summary>
    public Func<string, string, Reply>? PlaybackInfoReply { get; set; }

    /// <summary>The size of the virtual file <c>GET|HEAD /Videos/{id}/stream</c> serves (bytes are <c>i % 251</c>).</summary>
    public long StreamFileSize { get; set; } = 1_000_000;

    /// <summary>The <c>Content-Type</c> of the stream answer.</summary>
    public string StreamContentType { get; set; } = "video/x-matroska";

    /// <summary>Extra headers the stream answer carries (hostile ones, for F-RS-07 and F-RS-08).</summary>
    public Dictionary<string, string> StreamHeaders { get; } = new();

    /// <summary>Replaces the whole stream answer; return true when handled.</summary>
    public Func<HttpContext, Task<bool>>? StreamOverride { get; set; }

    /// <summary>A pause after each 64 KiB chunk, to hold a connection open.</summary>
    public int StreamChunkDelayMs { get; set; }

    /// <summary>Counts the bytes written to stream answers.</summary>
    public long StreamBytesSent => Interlocked.Read(ref _streamBytes);

    private long _streamBytes;

    /// <summary>Answers a playlist request (the request path, then the query); null answers 404.</summary>
    public Func<string, string, Reply>? HlsPlaylistReply { get; set; }

    /// <summary>Replaces the whole segment answer; return true when handled.</summary>
    public Func<HttpContext, Task<bool>>? SegmentOverride { get; set; }

    /// <summary>The size of one segment (bytes are <c>i % 251</c>).</summary>
    public int SegmentSize { get; set; } = 100_000;

    /// <summary>The answer to <c>DELETE /Videos/ActiveEncodings</c>.</summary>
    public int KillStatus { get; set; } = 204;

    /// <summary>Answers <c>GET /Sessions</c>; null answers an empty list.</summary>
    public Func<Reply>? SessionsReply { get; set; }

    /// <summary>Answers a subtitle request (the request path, then the query); null serves a small valid file of the requested format.</summary>
    public Func<string, string, Reply>? SubtitleReply { get; set; }

    /// <summary>A pause before a subtitle answer, to hold a call open.</summary>
    public int SubtitleDelayMs { get; set; }

    public bool Administrator { get; set; }

    public Dictionary<string, object> ExtraPolicy { get; } = new();

    public int Initiates => Volatile.Read(ref _initiates);

    public int Redeems => Volatile.Read(ref _redeems);

    public int Logouts => Volatile.Read(ref _logouts);

    public int ConnectCalls => Volatile.Read(ref _connectCalls);

    /// <summary>Polls answered with "approved" from poll number n on.</summary>
    public int ApproveFromPoll { get; set; } = int.MaxValue;

    public IEnumerable<RecordedRequest> To(string path) => Requests.Where(r => r.Path == path);

    public static async Task<FakeJellyfinPeer> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        var peer = new FakeJellyfinPeer(app);
        peer.Map(app);
        await app.StartAsync();
        peer.BaseUrl = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return peer;
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    public string Wire(bool authenticated = false) =>
        JsonSerializer.Serialize(new { Authenticated = authenticated, Secret, Code, DeviceId = "x", DeviceName = "x", AppName = "Shoal", AppVersion = "0", DateAdded = "2026-10-15T00:00:00Z" });

    private static async Task Write(HttpContext ctx, Reply reply)
    {
        if (reply.Hang)
        {
            try
            {
                await Task.Delay(Timeout.Infinite, ctx.RequestAborted);
            }
            catch (OperationCanceledException)
            {
            }

            return;
        }

        ctx.Response.StatusCode = reply.Status;
        if (reply.Location is not null)
        {
            ctx.Response.Headers.Location = reply.Location;
        }

        if (reply.Body is not null)
        {
            ctx.Response.ContentType = reply.ContentType;
            await ctx.Response.Body.WriteAsync(Encoding.UTF8.GetBytes(reply.Body));
        }
    }

    private async Task WritePlaylist(HttpContext ctx)
    {
        var reply = HlsPlaylistReply?.Invoke(ctx.Request.Path.Value ?? string.Empty, ctx.Request.QueryString.Value ?? string.Empty) ?? new Reply(404);
        ctx.Response.StatusCode = reply.Status;
        if (reply.Body is not null)
        {
            ctx.Response.ContentType = reply.ContentType;
            await ctx.Response.Body.WriteAsync(Encoding.UTF8.GetBytes(reply.Body));
        }
    }

    private async Task WriteSubtitle(HttpContext ctx)
    {
        if (SubtitleDelayMs > 0)
        {
            await Task.Delay(SubtitleDelayMs, ctx.RequestAborted);
        }

        var path = ctx.Request.Path.Value ?? string.Empty;
        var query = ctx.Request.QueryString.Value ?? string.Empty;
        var reply = SubtitleReply?.Invoke(path, query);
        if (reply is null)
        {
            var fmt = path[(path.LastIndexOf('.') + 1)..].ToLowerInvariant();
            reply = fmt switch
            {
                "srt" => new Reply(200, "1\n00:00:01,000 --> 00:00:02,000\nHello\n\n", "application/x-subrip"),
                "ass" or "ssa" => new Reply(200, "[Script Info]\nScriptType: v4.00+\n", "text/x-ssa"),
                "js" => new Reply(200, "{\"TrackEvents\":[{\"Id\":\"a\",\"Text\":\"Hello\",\"StartPositionTicks\":10000000,\"EndPositionTicks\":20000000}]}", "application/json"),
                _ => new Reply(200, "WEBVTT\n\n00:00:01.000 --> 00:00:02.000\nHello\n\n", "text/vtt"),
            };
        }

        ctx.Response.StatusCode = reply.Status;
        if (reply.Body is not null)
        {
            ctx.Response.ContentType = reply.ContentType;
            await ctx.Response.Body.WriteAsync(Encoding.UTF8.GetBytes(reply.Body));
        }
    }

    private void Map(WebApplication app)
    {
        app.Use(async (context, next) =>
        {
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

        app.MapGet("/System/Info/Public", async ctx =>
        {
            var reply = PublicInfo.Status == 0 && !PublicInfo.Hang ? Reply.Json(new { ServerName, Version = "12.1.0", Id = ServerId }) : PublicInfo;
            await Write(ctx, reply);
        });
        app.MapGet("/System/Info", async ctx =>
        {
            var reply = SystemInfo.Status == 0 && !SystemInfo.Hang ? Reply.Json(new { ServerName, Version = "12.1.0", Id = ServerId }) : SystemInfo;
            await Write(ctx, reply);
        });
        app.MapGet("/QuickConnect/Enabled", ctx => Write(ctx, Enabled));
        app.MapPost("/QuickConnect/Initiate", async ctx =>
        {
            Interlocked.Increment(ref _initiates);
            await Write(ctx, InitiateReply?.Invoke() ?? new Reply(200, Wire()));
        });
        app.MapGet("/QuickConnect/Connect", async ctx =>
        {
            var n = Interlocked.Increment(ref _connectCalls);
            ConnectTimes.Enqueue(DateTime.UtcNow);
            var now = Interlocked.Increment(ref _inFlight);
            MaxConnectInFlight = Math.Max(MaxConnectInFlight, now);
            try
            {
                var scripted = Connect(n);
                var reply = scripted.Status == 200 && scripted.Body is null && !scripted.Hang
                    ? new Reply(200, Wire(authenticated: n >= ApproveFromPoll))
                    : scripted;
                await Write(ctx, reply);
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
        });
        app.MapPost("/Users/AuthenticateWithQuickConnect", async ctx =>
        {
            Interlocked.Increment(ref _redeems);
            var reply = RedeemReply?.Invoke() ?? Reply.Json(new { AccessToken = Token, ServerId, User = new { Id = UserId.ToString("N") } });
            await Write(ctx, reply);
        });
        app.MapPost("/Shoal/Pair", async ctx =>
        {
            await Write(ctx, PairReply?.Invoke(Requests.Last().Body) ?? new Reply(404, "{}"));
        });
        app.MapGet("/Users/Me", async ctx =>
        {
            var policy = new Dictionary<string, object>
            {
                ["IsAdministrator"] = Administrator,
                ["EnableAllFolders"] = false,
                ["EnableContentDeletion"] = false,
                ["EnableContentDownloading"] = false,
                ["EnableLiveTvAccess"] = false,
                ["EnableRemoteControlOfOtherUsers"] = false,
                ["EnableAllDevices"] = false,
                ["MaxActiveSessions"] = 0,
                ["EnableUserPreferenceAccess"] = true,
                ["EnableMediaConversion"] = true,
                ["EnableSyncTranscoding"] = true,
                ["EnablePublicSharing"] = true
            };
            foreach (var (key, value) in ExtraPolicy)
            {
                policy[key] = value;
            }

            await Write(ctx, MeReply?.Invoke() ?? Reply.Json(new { Id = UserId.ToString("N"), Name = "shoal-bob", Policy = policy }));
        });
        app.MapPost("/Items/{itemId}/PlaybackInfo", async ctx =>
        {
            var body = Requests.Last().Body;
            var reply = PlaybackInfoReply?.Invoke((string)ctx.Request.RouteValues["itemId"]!, Encoding.UTF8.GetString(body)) ?? new Reply(404);
            await Write(ctx, reply);
        });
        app.MapGet("/Videos/{itemId}/{sourceId}/Subtitles/{index}/{ticks}/Stream.{fmt}", ctx => WriteSubtitle(ctx));
        app.MapGet("/Videos/{itemId}/{sourceId}/Subtitles/{index}/Stream.{fmt}", ctx => WriteSubtitle(ctx));
        app.MapGet("/Videos/{itemId}/{sourceId}/Subtitles/{index}/stream.vtt", ctx => WriteSubtitle(ctx));
        app.MapGet("/Sessions", ctx => Write(ctx, SessionsReply?.Invoke() ?? Reply.Json(Array.Empty<object>())));
        app.MapPost("/Sessions/Capabilities", ctx =>
        {
            ctx.Response.StatusCode = 204;
            return Task.CompletedTask;
        });
        app.MapGet("/Videos/{itemId}/master.m3u8", ctx => WritePlaylist(ctx));
        app.MapGet("/Videos/{itemId}/main.m3u8", ctx => WritePlaylist(ctx));
        app.MapGet("/Videos/{itemId}/{sourceId}/Subtitles/{index}/subtitles.m3u8", ctx => WritePlaylist(ctx));
        app.MapGet("/Videos/{itemId}/hls1/main/{segment}", async ctx =>
        {
            if (SegmentOverride is not null && await SegmentOverride(ctx))
            {
                return;
            }

            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "video/mp2t";
            ctx.Response.ContentLength = SegmentSize;
            var data = new byte[SegmentSize];
            for (var i = 0; i < data.Length; i++)
            {
                data[i] = (byte)(i % 251);
            }

            await ctx.Response.Body.WriteAsync(data);
        });
        app.MapDelete("/Videos/ActiveEncodings", ctx =>
        {
            ctx.Response.StatusCode = KillStatus;
            return Task.CompletedTask;
        });
        app.MapMethods("/Videos/{itemId}/stream", ["GET", "HEAD"], async ctx =>
        {
            if (StreamOverride is not null && await StreamOverride(ctx))
            {
                return;
            }

            var size = StreamFileSize;
            long first = 0;
            long last = size - 1;
            var ranged = false;
            if (ctx.Request.Headers.TryGetValue("Range", out var rangeHeader) && rangeHeader.ToString().StartsWith("bytes=", StringComparison.Ordinal))
            {
                var spec = rangeHeader.ToString()["bytes=".Length..];
                var dash = spec.IndexOf('-', StringComparison.Ordinal);
                var left = spec[..dash];
                var right = spec[(dash + 1)..];
                if (left.Length == 0)
                {
                    var n = long.Parse(right, System.Globalization.CultureInfo.InvariantCulture);
                    first = Math.Max(0, size - n);
                }
                else
                {
                    first = long.Parse(left, System.Globalization.CultureInfo.InvariantCulture);
                    last = right.Length == 0 ? size - 1 : Math.Min(size - 1, long.Parse(right, System.Globalization.CultureInfo.InvariantCulture));
                }

                if (first >= size)
                {
                    ctx.Response.StatusCode = 416;
                    ctx.Response.Headers["Content-Range"] = $"bytes */{size}";
                    return;
                }

                ranged = true;
            }

            ctx.Response.StatusCode = ranged ? 206 : 200;
            ctx.Response.ContentType = StreamContentType;
            ctx.Response.ContentLength = last - first + 1;
            ctx.Response.Headers["Accept-Ranges"] = "bytes";
            ctx.Response.Headers["Last-Modified"] = "Wed, 14 Oct 2026 12:00:00 GMT";
            if (ranged)
            {
                ctx.Response.Headers["Content-Range"] = $"bytes {first}-{last}/{size}";
            }

            foreach (var (name, value) in StreamHeaders)
            {
                ctx.Response.Headers[name] = value;
            }

            if (HttpMethods.IsHead(ctx.Request.Method))
            {
                return;
            }

            var buffer = new byte[64 * 1024];
            for (var position = first; position <= last && !ctx.RequestAborted.IsCancellationRequested;)
            {
                var count = (int)Math.Min(buffer.Length, last - position + 1);
                for (var i = 0; i < count; i++)
                {
                    buffer[i] = (byte)((position + i) % 251);
                }

                try
                {
                    await ctx.Response.Body.WriteAsync(buffer.AsMemory(0, count), ctx.RequestAborted);
                    await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
                    if (StreamChunkDelayMs > 0)
                    {
                        await Task.Delay(StreamChunkDelayMs, ctx.RequestAborted);
                    }
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                Interlocked.Add(ref _streamBytes, count);
                position += count;
            }
        });
        FakePeerExtensions.MapAll(this, app);
        app.MapPost("/Sessions/Logout", ctx =>
        {
            Interlocked.Increment(ref _logouts);
            ctx.Response.StatusCode = 204;
            return Task.CompletedTask;
        });
    }
}
