using System;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Jellyfin.Plugin.Shoal.Tests;

/// <summary>
/// How the fake peer answers <c>POST /Shoal/Share/Rotate</c> (K26 tests). By default it issues a new random token for <see cref="DeviceId"/>.
/// </summary>
internal sealed class RotateBehavior
{
    private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Gets or sets the DeviceId the answer carries (Home's own).</summary>
    public string DeviceId { get; set; } = string.Empty;

    /// <summary>Gets or sets the status to answer; 200 issues a token.</summary>
    public int Status { get; set; } = 200;

    /// <summary>Gets or sets the whole body to send instead of the default one.</summary>
    public string? Body { get; set; }

    /// <summary>Gets or sets the token the 200 answer carries (random by default).</summary>
    public string Token { get; set; } = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));

    /// <summary>Gets or sets a value indicating whether the connection is cut after the request was read (a lost response).</summary>
    public bool AbortConnection { get; set; }

    /// <summary>Gets or sets what happens on the Peer when the request arrives (for example the old token dies).</summary>
    public Action? OnReceived { get; set; }

    /// <summary>Gets or sets a gate that holds the answer until it is completed (a rotation in flight).</summary>
    public TaskCompletionSource? Hold { get; set; }

    /// <summary>Gets a task that completes when the request has arrived.</summary>
    public Task Entered => _entered.Task;

    /// <summary>Gets how many requests arrived.</summary>
    public int Calls { get; private set; }

    internal void Arrived()
    {
        Calls++;
        _entered.TrySetResult();
    }
}

/// <summary>
/// <c>POST /Shoal/Share/Rotate</c> on the fake peers (see <see cref="IFakePeerExtension"/>).
/// </summary>
internal sealed class FakePeerRotate : IFakePeerExtension
{
    private static readonly ConditionalWeakTable<FakeJellyfinPeer, RotateBehavior> Behaviors = [];

    public static RotateBehavior For(FakeJellyfinPeer peer) => Behaviors.GetOrCreateValue(peer);

    public void Map(FakeJellyfinPeer? peer, WebApplication app)
    {
        if (peer is null)
        {
            return;
        }

        app.MapPost("/Shoal/Share/Rotate", async ctx =>
        {
            var behavior = For(peer);
            behavior.Arrived();
            behavior.OnReceived?.Invoke();
            if (behavior.AbortConnection)
            {
                ctx.Abort();
                return;
            }

            if (behavior.Hold is { } hold)
            {
                await hold.Task;
            }

            ctx.Response.StatusCode = behavior.Status;
            ctx.Response.ContentType = "application/json; charset=utf-8";
            var body = behavior.Body ?? (behavior.Status == 200
                ? JsonSerializer.Serialize(new { v = 1, token = behavior.Token, deviceId = behavior.DeviceId, rotatedUtc = DateTime.UtcNow })
                : "{}");
            await ctx.Response.Body.WriteAsync(Encoding.UTF8.GetBytes(body));
        });
    }
}
