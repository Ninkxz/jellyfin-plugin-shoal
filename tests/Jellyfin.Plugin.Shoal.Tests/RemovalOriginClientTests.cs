using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.LocalRemoval;
using Jellyfin.Plugin.Shoal.Peers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Jellyfin.Plugin.Shoal.Tests;

/// <summary>
/// 2.10.4 lab fix (Pilot 2 check 3): the words the friend-proof client gives for each real friend answer. A friend that answers with a valid
/// proof of a different file (another length or runtime: "Delta, different audio") is <c>contentNotEquivalent</c>; a friend that cannot be
/// reached, refuses or confirms nothing stays <c>originUnavailable</c>; a 429 carries the friend's Retry-After.
/// </summary>
public sealed class RemovalOriginClientTests
{
    private const string PeerKey = "00000000000000000000000000000abc";
    private const long Bytes = 4096;
    private const long Runtime = 72_000_000_000;
    private static readonly string Digest = new('a', 64);

    [Theory]
    [InlineData("length", "contentNotEquivalent")] // the lab case: the friend's copy has other audio, so another length
    [InlineData("runtime", "contentNotEquivalent")]
    [InlineData("notFound", "originUnavailable")]
    [InlineData("sourceChanged", "originUnavailable")]
    [InlineData("badDigest", "originUnavailable")]
    [InlineData("oldVersion", "originUnavailable")]
    [InlineData("otherMovie", "originUnavailable")]
    [InlineData("noMedia", "originUnavailable")]
    public async Task EachFriendAnswerGetsItsOwnWord(string answer, string word)
    {
        await using var friend = await Friend.StartAsync(answer);
        var ex = await Assert.ThrowsAnyAsync<InvalidOperationException>(() => friend.Client.ProveAsync(Target(), CancellationToken.None));
        Assert.Equal(word, ex.Message);
    }

    [Fact]
    public async Task AnIdenticalCopyIsProved()
    {
        await using var friend = await Friend.StartAsync("same");
        var proof = await friend.Client.ProveAsync(Target(), CancellationToken.None);
        Assert.Equal((Bytes, Digest), (proof.Length, proof.ContentSha256));
    }

    [Fact]
    public async Task AFriendThatCannotBeReachedIsOriginUnavailable()
    {
        using var client = new PeerClient(() => "Home", () => "0.1.0.0", TimeProvider.System) { EnforceAddressPolicy = false };
        var origin = new RemovalOriginClient(new FakeEndpoints(new PeerEndpoint(PeerKey, "dead", new Uri("http://127.0.0.1:1"), "shoal-1a2b3c4d5e6f-4b0c1d2e", "9f2c4d6e8a0b1c3d5e7f9a0b2c4d6e8f")), client);
        Assert.Equal("originUnavailable", (await Assert.ThrowsAnyAsync<InvalidOperationException>(() => origin.ProveAsync(Target(), CancellationToken.None))).Message);
    }

    [Theory]
    [InlineData("limited", 10)] // the friend's bucket is empty (RemovalOriginProof: 429, Retry-After 10)
    [InlineData("workerBusy", 2)] // the friend is hashing another file (429, Retry-After 2)
    public async Task ARateLimitedProofCarriesTheFriendsRetryAfter(string answer, int seconds)
    {
        await using var friend = await Friend.StartAsync(answer);
        var ex = await Assert.ThrowsAsync<RemovalOriginRateLimitedException>(() => friend.Client.ProveAsync(Target(), CancellationToken.None));
        Assert.Equal("originRateLimited", ex.Message);
        Assert.Equal(TimeSpan.FromSeconds(seconds), ex.RetryAfter);
    }

    [Fact]
    public async Task ARateLimitedRangeReadCarriesTheFriendsRetryAfter()
    {
        await using var friend = await Friend.StartAsync("limited");
        var ex = await Assert.ThrowsAsync<RemovalOriginRateLimitedException>(() => friend.Client.ReadRangeAsync(Target(), 0, 1024, CancellationToken.None));
        Assert.Equal(TimeSpan.FromSeconds(10), ex.RetryAfter);
    }

    private static RemovalTarget Target() => new()
    {
        PeerKey = PeerKey, MappingKey = "movies-1", PeerItemId = Guid.Parse("0123456789abcdef0123456789abcdef"), LocalItemId = Guid.NewGuid(), LibraryId = Guid.NewGuid(),
        Title = "Delta", Year = 2020, Root = "/media/Movies", Path = "/media/Movies/Delta/d.mkv", RecoveryRoot = string.Empty,
        ProviderIds = new Dictionary<string, string> { ["Tmdb"] = "4242" }, RuntimeTicks = Runtime, Bytes = Bytes, Generation = "g",
    };

    // A friend as the real one answers: /Shoal/Share/RemovalOrigin/{id} (RemovalOriginController through ShoalResult and PeerJson) and core's
    // static stream with a byte range.
    private sealed class Friend : IAsyncDisposable
    {
        private readonly WebApplication _app;
        private readonly PeerClient _client;

        private Friend(WebApplication app, PeerClient client, RemovalOriginClient origin)
        {
            _app = app;
            _client = client;
            Client = origin;
        }

        public RemovalOriginClient Client { get; }

        public static async Task<Friend> StartAsync(string answer)
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            var app = builder.Build();
            app.MapGet("/Shoal/Share/RemovalOrigin/{id}", async (HttpContext ctx) =>
            {
                switch (answer)
                {
                    case "notFound":
                        ctx.Response.StatusCode = 404;
                        await Json(ctx, new { });
                        return;
                    case "sourceChanged":
                        ctx.Response.StatusCode = 409;
                        await Json(ctx, new { error = "sourceChanged" });
                        return;
                    case "limited":
                    case "workerBusy":
                        ctx.Response.StatusCode = 429;
                        ctx.Response.Headers.RetryAfter = answer == "limited" ? "10" : "2";
                        await Json(ctx, new { error = "rateLimited" });
                        return;
                }

                var proof = new RemovalOriginDto(answer == "oldVersion" ? 2 : 1, answer == "badDigest" ? "nothex" : Digest, answer == "length" ? Bytes + 512 : Bytes,
                    new Dictionary<string, string> { ["Tmdb"] = answer == "otherMovie" ? "9999" : "4242" }, answer == "runtime" ? Runtime + 10_000_000 : Runtime, new string('b', 64));
                await Json(ctx, proof);
            });
            app.MapGet("/Videos/{id}/stream", async (HttpContext ctx) =>
            {
                if (answer == "limited")
                {
                    ctx.Response.StatusCode = 429;
                    ctx.Response.Headers.RetryAfter = "10";
                    return;
                }

                if (answer == "noMedia")
                {
                    ctx.Response.StatusCode = 404;
                    return;
                }

                var range = ctx.Request.Headers.Range.ToString()["bytes=".Length..].Split('-').Select(long.Parse).ToArray();
                var length = (int)(range[1] - range[0] + 1);
                ctx.Response.StatusCode = 206;
                ctx.Response.ContentType = "video/x-matroska";
                ctx.Response.ContentLength = length;
                ctx.Response.Headers.ContentRange = "bytes " + range[0] + "-" + range[1] + "/" + Bytes;
                await ctx.Response.Body.WriteAsync(new byte[length]);
            });
            await app.StartAsync();
            var url = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
            var client = new PeerClient(() => "Home", () => "0.1.0.0", TimeProvider.System) { EnforceAddressPolicy = false };
            var endpoint = new PeerEndpoint(PeerKey, "friend", new Uri(url), "shoal-1a2b3c4d5e6f-4b0c1d2e", "9f2c4d6e8a0b1c3d5e7f9a0b2c4d6e8f");
            return new Friend(app, client, new RemovalOriginClient(new FakeEndpoints(endpoint), client));
        }

        public async ValueTask DisposeAsync()
        {
            _client.Dispose();
            await _app.DisposeAsync();
        }

        private static async Task Json(HttpContext ctx, object value)
        {
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsync(JsonSerializer.Serialize(value, value.GetType(), PeerJson.Options));
        }
    }
}
