using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Jellyfin.Plugin.Shoal.Tests;

/// <summary>
/// What a fake peer answers for an attachment (K31).
/// </summary>
internal sealed class FakeAttachments
{
    private static readonly ConditionalWeakTable<FakeJellyfinPeer, FakeAttachments> Settings = new();

    private int _active;
    private int _maxActive;

    /// <summary>Gets or sets the answer for an index; the default is a small TrueType font.</summary>
    public Func<int, TileReply> Reply { get; set; } = static _ => TileReply.Font("\0\u0001\0\0", 2000);

    /// <summary>Gets or sets how long the peer waits before answering.</summary>
    public int DelayMs { get; set; }

    /// <summary>Gets the most requests that were in flight at once.</summary>
    public int MaxActive => _maxActive;

    public static FakeAttachments For(FakeJellyfinPeer peer) => Settings.GetOrCreateValue(peer);

    public void Reset()
    {
        Reply = static _ => TileReply.Font("\0\u0001\0\0", 2000);
        DelayMs = 0;
        _active = 0;
        _maxActive = 0;
    }

    internal async Task ServeAsync(HttpContext ctx, int index)
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

            var reply = Reply(index);
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

/// <summary>
/// <c>GET /Videos/{id}/{source}/Attachments/{index}</c> on the fake peers.
/// </summary>
internal sealed class FakeAttachmentsExtension : IFakePeerExtension
{
    public void Map(FakeJellyfinPeer? peer, WebApplication app)
    {
        if (peer is null)
        {
            return;
        }

        app.MapGet("/Videos/{itemId}/{sourceId}/Attachments/{index:int}", ctx =>
            FakeAttachments.For(peer).ServeAsync(ctx, int.Parse((string)ctx.Request.RouteValues["index"]!, System.Globalization.CultureInfo.InvariantCulture)));
    }
}
