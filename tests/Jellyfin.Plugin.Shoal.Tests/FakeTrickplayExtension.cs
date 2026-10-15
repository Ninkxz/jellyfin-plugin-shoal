using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Jellyfin.Plugin.Shoal.Tests;

/// <summary>
/// What a fake peer answers for a trickplay tile (K30).
/// </summary>
internal sealed class FakeTrickplay
{
    private static readonly ConditionalWeakTable<FakeJellyfinPeer, FakeTrickplay> Settings = new();

    /// <summary>Gets or sets the answer for (width, index); the default is a 3200 by 1800 JPEG.</summary>
    public Func<int, int, TileReply> Reply { get; set; } = static (_, _) => TileReply.Jpeg(ImageBuilder.Jpeg(3200, 1800, 5000));

    /// <summary>Gets or sets how long the peer waits before answering.</summary>
    public int DelayMs { get; set; }

    /// <summary>Gets the number of requests that are in flight now.</summary>
    public int Active => _active;

    /// <summary>Gets the most requests that were in flight at once.</summary>
    public int MaxActive => _maxActive;

    private int _active;
    private int _maxActive;

    public static FakeTrickplay For(FakeJellyfinPeer peer) => Settings.GetOrCreateValue(peer);

    public void Reset()
    {
        Reply = static (_, _) => TileReply.Jpeg(ImageBuilder.Jpeg(3200, 1800, 5000));
        DelayMs = 0;
        _active = 0;
        _maxActive = 0;
    }

    internal async Task ServeAsync(HttpContext ctx, int width, int index)
    {
        var now = Interlocked.Increment(ref _active);
        int seen;
        do
        {
            seen = _maxActive;
        }
        while (now > seen && Interlocked.CompareExchange(ref _maxActive, now, seen) != seen);
        try
        {
            if (DelayMs > 0)
            {
                await Task.Delay(DelayMs, ctx.RequestAborted);
            }

            var reply = Reply(width, index);
            ctx.Response.StatusCode = reply.Status;
            if (reply.ContentType is not null)
            {
                ctx.Response.ContentType = reply.ContentType;
            }

            ctx.Response.ContentLength = reply.Body.Length;
            await ctx.Response.Body.WriteAsync(reply.Body, ctx.RequestAborted);
        }
        finally
        {
            Interlocked.Decrement(ref _active);
        }
    }
}

internal sealed record TileReply(int Status, string? ContentType, byte[] Body)
{
    public static TileReply Jpeg(byte[] body) => new(200, "image/jpeg", body);

    public static TileReply Font(string magic, int size, string contentType = "font/ttf") =>
        new(200, contentType, [.. System.Text.Encoding.Latin1.GetBytes(magic), .. new byte[Math.Max(0, size - magic.Length)]]);
}

/// <summary>
/// <c>GET /Videos/{id}/Trickplay/{width}/{index}.jpg</c> on the fake peers.
/// </summary>
internal sealed class FakeTrickplayExtension : IFakePeerExtension
{
    public void Map(FakeJellyfinPeer? peer, WebApplication app)
    {
        if (peer is null)
        {
            return;
        }

        app.MapGet("/Videos/{itemId}/Trickplay/{width:int}/{index:int}.jpg", ctx =>
            FakeTrickplay.For(peer).ServeAsync(ctx, int.Parse((string)ctx.Request.RouteValues["width"]!, System.Globalization.CultureInfo.InvariantCulture), int.Parse((string)ctx.Request.RouteValues["index"]!, System.Globalization.CultureInfo.InvariantCulture)));
    }
}
