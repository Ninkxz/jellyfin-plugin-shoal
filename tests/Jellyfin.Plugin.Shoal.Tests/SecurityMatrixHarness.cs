using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.Authorization;
using Jellyfin.Plugin.Shoal.Peers;
using Jellyfin.Plugin.Shoal.State;
using Jellyfin.Plugin.Shoal.Storage;
using MediaBrowser.Common.Extensions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Jellyfin.Plugin.Shoal.Tests;

/// <summary>
/// One row of the negative authorization matrix (DESIGN 8.7, T-SEC-15): who calls, what the answer must be, and whether the fake peer may
/// see a request.
/// </summary>
/// <param name="Id">The row id (R1, R11b, P1, ...).</param>
/// <param name="Description">What the row is.</param>
/// <param name="ExpectedStatus">The status the route must answer.</param>
/// <param name="PeerRequests">How many requests the fake peer must have received: 0 for every refusal, 1 for the positive controls.</param>
/// <param name="Arrange">Sets the world up for the row and returns the principal header of the caller (null = no credentials at all).</param>
public sealed record MatrixCase(string Id, string Description, int ExpectedStatus, int PeerRequests, Func<MatrixHarness, Task<string?>> Arrange);

/// <summary>
/// The users, items, libraries and principals the matrix rows use.
/// </summary>
public sealed class MatrixWorld
{
    public static readonly Guid Admin = Guid.Parse("a0000000-0000-0000-0000-000000000001");
    public static readonly Guid Selected = Guid.Parse("a0000000-0000-0000-0000-000000000002");
    public static readonly Guid Other = Guid.Parse("a0000000-0000-0000-0000-000000000003");
    public static readonly Guid NewUser = Guid.Parse("a0000000-0000-0000-0000-000000000004");
    public static readonly Guid ShareUser = Guid.Parse("a0000000-0000-0000-0000-000000000005");
    public static readonly Guid Child = Guid.Parse("a0000000-0000-0000-0000-000000000006");
    public static readonly Guid Item = Guid.Parse("b0000000-0000-0000-0000-000000000001");
    public static readonly Guid ItemB = Guid.Parse("b0000000-0000-0000-0000-000000000002");
    public static readonly Guid ItemC = Guid.Parse("b0000000-0000-0000-0000-000000000003");
    public static readonly Guid LocalItem = Guid.Parse("c0000000-0000-0000-0000-000000000001");

    public ConcurrentDictionary<Guid, CallerUser> Users { get; } = new();

    public ConcurrentDictionary<(Guid User, Guid Item), bool> Visible { get; } = new();

    public ConcurrentDictionary<string, ClaimsPrincipal> Principals { get; } = new();

    public ConcurrentDictionary<Guid, FederatedItem> Federated { get; } = new();

    /// <summary>Gets the stub paths below the library folder of the TV items ("Show (2020)/Season 01/S01E01.strm"); an item listed here has this path instead of a movie folder.</summary>
    public ConcurrentDictionary<Guid, string> TvPaths { get; } = new();

    public string PeerKey { get; } = StateSamples.PeerKeyA;

    public const string SecondMappingKey = "movies-2";

    public string MappingKey { get; } = "movies-1";

    public static CallerUser MakeUser(Guid id, bool restricted = false, bool canPlay = true, bool disabled = false) =>
        new(id, disabled, restricted, canPlay, true, true, false, null);

    /// <summary>A principal as core's authentication would build it for a valid token.</summary>
    public static ClaimsPrincipal Principal(Guid userId, params Claim[] extra)
    {
        var claims = new List<Claim>
        {
            new(CallerContext.UserIdClaim, userId.ToString("N")),
            new(CallerContext.DeviceIdClaim, "device-" + userId.ToString("N")[^4..]),
            new(CallerContext.TokenClaim, "tok" + userId.ToString("N")),
            new(CallerContext.IsApiKeyClaim, "False")
        };
        claims.AddRange(extra);
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }

    public string Sign(string key, ClaimsPrincipal principal)
    {
        Principals[key] = principal;
        return key;
    }
}

/// <summary>
/// The default state of the matrix: one library in "Only selected users" mode with the admin and one other user selected, every ordinary
/// user able to see the item through core.
/// </summary>
public sealed class MatrixHarness : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly HttpClient _http = new();

    internal MatrixHarness(WebApplication app, FakeJellyfinPeer peer, PeerClient client, MatrixWorld world, TempDirectory dir, StateStore state, string baseUrl)
    {
        _app = app;
        Peer = peer;
        Client = client;
        World = world;
        Dir = dir;
        State = state;
        BaseUrl = baseUrl;
    }

    public FakeJellyfinPeer Peer { get; }

    public PeerClient Client { get; }

    public MatrixWorld World { get; }

    internal TempDirectory Dir { get; }

    public StateStore State { get; }

    public string BaseUrl { get; }

    internal FilterParts? Filter { get; private set; }

    public static async Task<MatrixHarness> StartAsync(int skippedStep = 0, bool withFilter = false, bool realPlayback = false)
    {
        var peer = await FakeJellyfinPeer.StartAsync();
        var dir = new TempDirectory();
        var state = new StateStore(new ShoalPaths(dir.Path), NullLogger.Instance, TimeProvider.System, null);
        var world = new MatrixWorld();
        await state.UpdateAsync(s =>
        {
            s.StubRoot = dir.Combine("stubs");
            s.Home.Peers.Add(new PeerState
            {
                PeerKey = world.PeerKey,
                DisplayName = "Hops",
                BaseUrl = "http://hops.tail1234.ts.net:8096",
                Enabled = true,
                PinnedServerId = "0123456789abcdef0123456789abcdef",
                Pairing = new PairingInfo { Method = PairingMethod.Qc },
                Libraries =
                {
                    new LibraryMapping
                    {
                        MappingKey = world.MappingKey,
                        RemoteName = "Movies",
                        LocalLibraryName = "Hops Movies",
                        Subfolder = "movies",
                        Access = new LibraryAccess { Mode = AccessMode.Selected, SelectedUserIds = { MatrixWorld.Admin, MatrixWorld.Selected } }
                    },
                    new LibraryMapping
                    {
                        MappingKey = MatrixWorld.SecondMappingKey,
                        RemoteName = "Other Movies",
                        LocalLibraryName = "Hops Other Movies",
                        Subfolder = "movies2",
                        Access = new LibraryAccess { Mode = AccessMode.Selected, SelectedUserIds = { MatrixWorld.Other } }
                    }
                }
            });
            s.PeerRole.Shares.Add(new ShareState { ShareId = "share-bob", FriendName = "Bob", ShareUserId = MatrixWorld.ShareUser });
        });
        world.Federated[MatrixWorld.Item] = new FederatedItem(world.PeerKey, world.MappingKey);
        world.Federated[MatrixWorld.ItemB] = new FederatedItem(world.PeerKey, MatrixWorld.SecondMappingKey);
        foreach (var id in new[] { MatrixWorld.Admin, MatrixWorld.Selected, MatrixWorld.Other, MatrixWorld.NewUser, MatrixWorld.ShareUser, MatrixWorld.Child })
        {
            world.Users[id] = MatrixWorld.MakeUser(id, restricted: id == MatrixWorld.Child);
            world.Visible[(id, MatrixWorld.Item)] = true;
            world.Visible[(id, MatrixWorld.ItemB)] = true;
            world.Visible[(id, MatrixWorld.LocalItem)] = true;
        }

        var client = new PeerClient(() => "Home", () => "0.1.0.0", TimeProvider.System) { EnforceAddressPolicy = false };
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(world);
        builder.Services.AddAuthentication(MatrixAuthentication.SchemeName).AddScheme<AuthenticationSchemeOptions, MatrixAuthentication>(MatrixAuthentication.SchemeName, _ => { });
        builder.Services.AddAuthorization(options =>
        {
            options.DefaultPolicy = new AuthorizationPolicyBuilder().AddRequirements(new CoreDefaultRequirement()).Build();
        });
        builder.Services.AddSingleton<IAuthorizationHandler, CoreDefaultHandler>();
        FilterParts? filter = null;
        if (withFilter)
        {
            filter = FilterParts.Register(builder, world, state, client, peer, skippedStep, realPlayback ? dir : null);
        }

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        if (withFilter)
        {
            app.MapControllers();
        }

        var authorization = app.Services.GetRequiredService<IAuthorizationService>();
        var policies = app.Services.GetRequiredService<IAuthorizationPolicyProvider>();
        var callerContext = new CallerContext(
            authorization,
            policies,
            new FakeUsers(world),
            new FakeVisible(world),
            new FakeFederated(world),
            state,
            _ => false,
            NullLogger.Instance,
            skippedStep);
        var peerEndpoint = new PeerEndpoint(world.PeerKey, "Hops", new Uri(peer.BaseUrl), "shoal-a1b2c3d4e5f6-4b0c1d2e", FakeJellyfinPeer.DefaultToken);

        // The test route: CallerContext, and only when it allows, one request to the fake peer. Every method and path shape maps here.
        app.MapMethods("/matrix/{itemId:guid}", ["GET", "POST", "HEAD"], async (HttpContext ctx, Guid itemId) =>
        {
            var decision = await callerContext.AuthorizeAsync(ctx, itemId, ctx.RequestAborted);
            if (!decision.Allowed)
            {
                ctx.Response.StatusCode = decision.Status;
                ctx.Response.Headers["X-Denied-Step"] = decision.DeniedAtStep.ToString(System.Globalization.CultureInfo.InvariantCulture);
                ctx.Response.Headers["X-Playback-Error"] = decision.PlaybackError.ToString();
                return;
            }

            using var response = await client.SendAsync(peerEndpoint, PeerRequest.GetAnonymous("/System/Info/Public"), PeerCallProfile.Info, ctx.RequestAborted);
            ctx.Response.StatusCode = (int)response.StatusCode;
            await ctx.Response.WriteAsync("user:" + decision.Caller!.UserId.ToString("N") + ";device:" + decision.Caller.DeviceId);
        });
        await app.StartAsync();
        var url = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        var harness = new MatrixHarness(app, peer, client, world, dir, state, url) { Filter = filter };
        return harness;
    }

    /// <summary>Sets one library's access mode and selection.</summary>
    public Task SetAccessAsync(AccessMode mode, bool includeRestricted = false, params Guid[] selected) =>
        State.UpdateAsync(s =>
        {
            var access = s.Home.Peers[0].Libraries[0].Access;
            access.Mode = mode;
            access.IncludeRestricted = includeRestricted;
            access.SelectedUserIds = selected.ToList();
        });

    /// <summary>Puts the world back to its default before a row.</summary>
    public async Task ResetAsync()
    {
        await SetAccessAsync(AccessMode.Selected, false, MatrixWorld.Admin, MatrixWorld.Selected);
        await State.UpdateAsync(s =>
        {
            s.Home.Peers.ForEach(static p => p.Enabled = true);
            s.Home.Peers.ForEach(static p => p.QualityLimit = new Jellyfin.Plugin.Shoal.State.QualityLimit());
            s.Home.Libraries.RemoveAll(static l => !l.IsLegacy);
            s.Home.Visibility.HiddenFriendTitles.Clear();
        });
        World.Principals.Clear();
        foreach (var id in new[] { MatrixWorld.Admin, MatrixWorld.Selected, MatrixWorld.Other, MatrixWorld.NewUser, MatrixWorld.ShareUser, MatrixWorld.Child })
        {
            World.Users[id] = MatrixWorld.MakeUser(id, restricted: id == MatrixWorld.Child);
            World.Visible[(id, MatrixWorld.Item)] = true;
            World.Visible[(id, MatrixWorld.ItemB)] = true;
            World.Visible[(id, MatrixWorld.LocalItem)] = true;
        }

        World.Federated[MatrixWorld.Item] = new FederatedItem(World.PeerKey, World.MappingKey);
        World.Federated[MatrixWorld.ItemB] = new FederatedItem(World.PeerKey, MatrixWorld.SecondMappingKey);
    }

    public async Task<HttpResponseMessage> CallAsync(string? principalKey, HttpMethod? method = null)
    {
        using var request = new HttpRequestMessage(method ?? HttpMethod.Get, BaseUrl + "/matrix/" + MatrixWorld.Item.ToString("N"));
        if (principalKey is not null)
        {
            request.Headers.Add(MatrixAuthentication.Header, principalKey);
        }

        return await _http.SendAsync(request);
    }

    /// <summary>Sends a request with a principal and returns the answer.</summary>
    public async Task<HttpResponseMessage> SendAsync(string? principalKey, HttpMethod method, string pathAndQuery, Action<HttpRequestMessage>? configure = null, System.Threading.CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(method, BaseUrl + pathAndQuery);
        if (principalKey is not null)
        {
            request.Headers.Add(MatrixAuthentication.Header, principalKey);
        }

        configure?.Invoke(request);
        return await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    }

    /// <summary>Runs one row against a real route and returns what is wrong with it (null when the row holds).</summary>
    public async Task<string?> CheckRouteAsync(MatrixCase row, HttpMethod method, string pathAndQuery)
    {
        await ResetAsync();
        var principalKey = await row.Arrange(this);
        await Filter!.Index.RebuildAsync();
        var before = Peer.Requests.Count;
        using var response = await SendAsync(principalKey, method, pathAndQuery);
        var peerRequests = Peer.Requests.Count - before;
        var core = response.Headers.Contains(FakeCoreController.Header);
        if (core)
        {
            return $"{row.Id}: core's action ran for a federated request";
        }

        if ((int)response.StatusCode != row.ExpectedStatus)
        {
            return $"{row.Id}: expected {row.ExpectedStatus}, got {(int)response.StatusCode}";
        }

        if (peerRequests != row.PeerRequests)
        {
            return $"{row.Id}: expected {row.PeerRequests} request(s) to the peer, saw {peerRequests}";
        }

        if (row.ExpectedStatus != 200 && method != HttpMethod.Head)
        {
            var body = await response.Content.ReadAsStringAsync();
            if (body.Length != 0)
            {
                return $"{row.Id}: a refusal must have no body, got {body.Length} bytes";
            }
        }

        return null;
    }

    /// <summary>Runs one row against the route and returns what is wrong with it (empty when the row holds).</summary>
    public async Task<string?> CheckAsync(MatrixCase row, HttpMethod? method = null)
    {
        await ResetAsync();
        var principalKey = await row.Arrange(this);
        var before = Peer.Requests.Count;
        using var response = await CallAsync(principalKey, method);
        var peerRequests = Peer.Requests.Count - before;
        if ((int)response.StatusCode != row.ExpectedStatus)
        {
            return $"{row.Id}: expected {row.ExpectedStatus}, got {(int)response.StatusCode}";
        }

        if (peerRequests != row.PeerRequests)
        {
            return $"{row.Id}: expected {row.PeerRequests} request(s) to the peer, saw {peerRequests}";
        }

        if (row.ExpectedStatus != 200)
        {
            var body = await response.Content.ReadAsStringAsync();
            if (body.Length != 0)
            {
                return $"{row.Id}: a refusal must have no body, got {body.Length} bytes";
            }
        }

        return null;
    }

    public async ValueTask DisposeAsync()
    {
        _http.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
        await Peer.DisposeAsync();
        Client.Dispose();
        State.Dispose();
        Dir.Dispose();
    }

    internal sealed class FakeUsers : ICallerUsers
    {
        private readonly MatrixWorld _world;

        public FakeUsers(MatrixWorld world) => _world = world;

        public CallerUser? Find(Guid userId) => _world.Users.TryGetValue(userId, out var user) ? user : null;
    }

    internal sealed class FakeVisible : IVisibleItems
    {
        private readonly MatrixWorld _world;

        public FakeVisible(MatrixWorld world) => _world = world;

        public bool IsVisible(Guid userId, Guid itemId) => _world.Visible.TryGetValue((userId, itemId), out var visible) && visible;
    }

    private sealed class FakeFederated : IFederatedItems
    {
        private readonly MatrixWorld _world;

        public FakeFederated(MatrixWorld world) => _world = world;

        public FederatedItem? Find(Guid localItemId) => _world.Federated.TryGetValue(localItemId, out var item) ? item : null;
    }
}

/// <summary>
/// Stands in for core's authentication: the header names a principal; no header, or an unknown name, is the anonymous principal that core
/// leaves for a missing, garbage, expired, logged-out or disabled-user token.
/// </summary>
public sealed class MatrixAuthentication : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "CustomAuthentication";
    public const string Header = "X-Principal";

    private readonly MatrixWorld _world;

    public MatrixAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder, MatrixWorld world)
        : base(options, logger, encoder) => _world = world;

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (Request.Headers.TryGetValue(Header, out var key) && _world.Principals.TryGetValue(key.ToString(), out var principal))
        {
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName)));
        }

        return Task.FromResult(AuthenticateResult.NoResult());
    }
}

/// <summary>
/// The requirement that stands in for core's DefaultAuthorizationHandler: remote access and the parental access schedule.
/// </summary>
public sealed class CoreDefaultRequirement : IAuthorizationRequirement
{
}

/// <summary>
/// Handles <see cref="CoreDefaultRequirement"/> from claims the matrix sets.
/// </summary>
public sealed class CoreDefaultHandler : AuthorizationHandler<CoreDefaultRequirement>
{
    public const string RemoteDenied = "t-remote-denied";
    public const string ScheduleDenied = "t-schedule-denied";
    public const string UserMissing = "t-user-missing";

    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, CoreDefaultRequirement requirement)
    {
        if (context.User.HasClaim(c => c.Type == UserMissing))
        {
            throw new ResourceNotFoundException("user is gone"); // as core's handler does for a user that no longer exists
        }

        if (context.User.HasClaim(c => c.Type == RemoteDenied || c.Type == ScheduleDenied))
        {
            context.Fail();
        }
        else if (context.User.Identity?.IsAuthenticated == true)
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}
