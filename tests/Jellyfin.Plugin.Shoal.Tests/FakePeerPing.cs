using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Jellyfin.Plugin.Shoal.Tests;

/// <summary>
/// <c>GET /System/Ping</c> on the fake peers, which the breaker's half-open probe sends. By default the peer answers; a test can make it hang
/// (a paused peer) or answer something else.
/// </summary>
internal sealed class FakePeerPing : IFakePeerExtension
{
    private static readonly ConditionalWeakTable<FakeJellyfinPeer, PingBehavior> Behaviors = [];

    public static PingBehavior For(FakeJellyfinPeer peer) => Behaviors.GetOrCreateValue(peer);

    public void Map(FakeJellyfinPeer? peer, WebApplication app)
    {
        if (peer is null)
        {
            return;
        }

        app.MapGet("/System/Ping", async ctx =>
        {
            var behavior = For(peer);
            if (behavior.Hang)
            {
                try
                {
                    await Task.Delay(Timeout.Infinite, ctx.RequestAborted);
                }
                catch (TaskCanceledException)
                {
                }

                return;
            }

            ctx.Response.StatusCode = behavior.Status;
            ctx.Response.ContentType = "text/plain";
            await ctx.Response.Body.WriteAsync(Encoding.UTF8.GetBytes("Jellyfin Server"));
        });
    }
}

internal sealed class PingBehavior
{
    public bool Hang { get; set; }

    public int Status { get; set; } = 200;
}
