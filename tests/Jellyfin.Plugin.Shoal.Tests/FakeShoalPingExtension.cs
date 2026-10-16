using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Jellyfin.Plugin.Shoal.Tests;

/// <summary>What a fake peer answers to <c>GET /Shoal/Ping</c>; the default is an unknown address (404), like a server without Shoal.</summary>
internal sealed class FakeShoalPing
{
    private static readonly ConditionalWeakTable<FakeJellyfinPeer, FakeShoalPing> Settings = new();

    public Reply Reply { get; set; } = new(404, "{}");

    public static FakeShoalPing For(FakeJellyfinPeer peer) => Settings.GetOrCreateValue(peer);
}

internal sealed class FakeShoalPingExtension : IFakePeerExtension
{
    public void Map(FakeJellyfinPeer? peer, WebApplication app)
    {
        if (peer is null)
        {
            return;
        }

        app.MapGet("/Shoal/Ping", async ctx =>
        {
            var reply = FakeShoalPing.For(peer).Reply;
            ctx.Response.StatusCode = reply.Status;
            ctx.Response.ContentType = reply.ContentType;
            await ctx.Response.WriteAsync(reply.Body ?? string.Empty);
        });
    }
}
