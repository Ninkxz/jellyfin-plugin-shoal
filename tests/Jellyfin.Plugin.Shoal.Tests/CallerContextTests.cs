using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.Authorization;
using Jellyfin.Plugin.Shoal.State;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Shoal.Tests;

/// <summary>
/// The rows of the negative authorization matrix (DESIGN 8.7, T-SEC-15) that CallerContext decides, run against a test route backed by the fake
/// peer. Every refusal asserts three things: the status, no body, and <b>zero requests at the fake peer</b>. The positive controls P1 to P4
/// run alongside so a broken harness cannot pass vacuously. K13 to K18 run the same rows against the real routes.
/// </summary>
public sealed class CallerContextTests
{
    private const string SharedKey = "5ab1e0c0ffee1234";

    public static IReadOnlyList<MatrixCase> Rows { get; } = BuildRows();

    public static TheoryData<string> RowIds => new(Rows.Select(r => r.Id));

    private static IReadOnlyList<MatrixCase> BuildRows()
    {
        static Task<string?> As(MatrixHarness h, Guid user, params Claim[] extra) =>
            Task.FromResult<string?>(h.World.Sign("p-" + user.ToString("N"), MatrixWorld.Principal(user, extra)));

        return
        [
            new("R1", "no token", 401, 0, _ => Task.FromResult<string?>(null)),
            new("R2", "garbage token (core leaves an anonymous principal)", 401, 0, _ => Task.FromResult<string?>("garbage")),
            new("R3", "valid token of a disabled user (core leaves an anonymous principal)", 401, 0, _ => Task.FromResult<string?>("disabled-user-token")),
            new("R3b", "a disabled user that reached CallerContext anyway (belt and braces)", 401, 0, h =>
            {
                h.World.Users[MatrixWorld.Selected] = MatrixWorld.MakeUser(MatrixWorld.Selected, disabled: true);
                return As(h, MatrixWorld.Selected);
            }),
            new("R4", "token of a deleted user (core's default handler throws ResourceNotFound)", 401, 0, h => As(h, MatrixWorld.Selected, new Claim(CoreDefaultHandler.UserMissing, "1"))),
            new("R4b", "token of a deleted user that the policy let through", 401, 0, h =>
            {
                h.World.Users.TryRemove(MatrixWorld.Selected, out _);
                return As(h, MatrixWorld.Selected);
            }),
            new("R5", "logged-out token (anonymous principal)", 401, 0, _ => Task.FromResult<string?>("logged-out")),
            new("R6", "API key", 403, 0, h => As(h, MatrixWorld.Admin, new Claim(CallerContext.IsApiKeyClaim, "True"))),
            new("R6b", "API key, any letter case of the claim value", 403, 0, h => As(h, MatrixWorld.Admin, new Claim(CallerContext.IsApiKeyClaim, "true"))),
            new("R7", "EnableRemoteAccess=false from a non-LAN address", 403, 0, h => As(h, MatrixWorld.Selected, new Claim(CoreDefaultHandler.RemoteDenied, "1"))),
            new("R8", "outside AccessSchedule", 403, 0, h => As(h, MatrixWorld.Selected, new Claim(CoreDefaultHandler.ScheduleDenied, "1"))),
            new("R9", "no access to the Shoal library in core (the item is not visible)", 404, 0, h =>
            {
                h.World.Visible[(MatrixWorld.Selected, MatrixWorld.Item)] = false;
                return As(h, MatrixWorld.Selected);
            }),
            new("R10", "parental rating excludes the item", 404, 0, async h =>
            {
                await h.SetAccessAsync(AccessMode.Selected, true, MatrixWorld.Child);
                h.World.Visible[(MatrixWorld.Child, MatrixWorld.Item)] = false;
                return await As(h, MatrixWorld.Child);
            }),
            new("R11", "mode Selected, user not selected (has core library access)", 404, 0, h => As(h, MatrixWorld.Other)),
            new("R11b", "mode Everyone, restricted profile, includeRestricted=false", 404, 0, async h =>
            {
                await h.SetAccessAsync(AccessMode.Everyone, false, MatrixWorld.Admin);
                return await As(h, MatrixWorld.Child);
            }),
            new("R11c", "switched Everyone then back to Selected; a user created while in Everyone mode", 404, 0, async h =>
            {
                await h.SetAccessAsync(AccessMode.Everyone, false, MatrixWorld.Admin, MatrixWorld.Selected);
                await h.SetAccessAsync(AccessMode.Selected, false, MatrixWorld.Admin, MatrixWorld.Selected);
                return await As(h, MatrixWorld.NewUser);
            }),
            new("R12", "EnableMediaPlayback=false", 403, 0, h =>
            {
                h.World.Users[MatrixWorld.Selected] = MatrixWorld.MakeUser(MatrixWorld.Selected, canPlay: false);
                return As(h, MatrixWorld.Selected);
            }),
            new("R15", "a Shoal share user in Everyone mode", 404, 0, async h =>
            {
                await h.SetAccessAsync(AccessMode.Everyone, true, MatrixWorld.Admin);
                return await As(h, MatrixWorld.ShareUser);
            }),
            new("R15b", "a Shoal share user whose id was put in the selection by hand", 404, 0, async h =>
            {
                await h.SetAccessAsync(AccessMode.Selected, true, MatrixWorld.Admin, MatrixWorld.ShareUser);
                return await As(h, MatrixWorld.ShareUser);
            }),
            new("R16", "an item that is not federated", 404, 0, h =>
            {
                h.World.Federated.TryRemove(MatrixWorld.Item, out _);
                return As(h, MatrixWorld.Admin);
            }),
            new("R17", "an item of a library that is no longer in the state", 404, 0, h =>
            {
                h.World.Federated[MatrixWorld.Item] = new FederatedItem(h.World.PeerKey, "gone");
                return As(h, MatrixWorld.Admin);
            }),
            new("R18", "a paused (disabled) peer, even for the pairing admin", 404, 0, async h =>
            {
                await h.State.UpdateAsync(s => s.Home.Peers.First(p => p.PeerKey == h.World.PeerKey).Enabled = false);
                return await As(h, MatrixWorld.Admin);
            }),
            new("R19", "K45: a shared library that does not admit the user, though the mapping's own access would", 404, 0, async h =>
            {
                await SharedLibraryAsync(h, feeds: true, MatrixWorld.Admin);
                return await As(h, MatrixWorld.Selected);
            }),
            new("R20", "K45: an item in a shared library its connection no longer feeds", 404, 0, async h =>
            {
                await SharedLibraryAsync(h, feeds: false, MatrixWorld.Admin, MatrixWorld.Selected);
                return await As(h, MatrixWorld.Selected);
            }),
            new("R21", "K44: a title folder in a shared library whose owner is not known (fail closed)", 404, 0, async h =>
            {
                await SharedLibraryAsync(h, feeds: true, MatrixWorld.Admin);
                h.World.Federated[MatrixWorld.Item] = new FederatedItem(string.Empty, string.Empty, SharedKey);
                return await As(h, MatrixWorld.Admin);
            }),
            new("R22", "K45: a user who hides friends' titles", 404, 0, async h =>
            {
                await h.State.UpdateAsync(s => s.Home.Visibility.HiddenFriendTitles.Add(MatrixWorld.Selected));
                return await As(h, MatrixWorld.Selected);
            }),
            new("R23", "K45: a Shoal share user admitted by a shared library in Everyone mode", 404, 0, async h =>
            {
                await SharedLibraryAsync(h, feeds: true, MatrixWorld.Admin);
                await h.State.UpdateAsync(s => s.Home.Libraries.First(l => l.LibKey == SharedKey).Access = new LibraryAccess { Mode = AccessMode.Everyone, IncludeRestricted = true });
                return await As(h, MatrixWorld.ShareUser);
            }),
            new("P6", "K45: a shared library admits the user (the mapping's own access does not)", 200, 1, async h =>
            {
                await h.SetAccessAsync(AccessMode.Selected, false, MatrixWorld.Admin);
                await SharedLibraryAsync(h, feeds: true, MatrixWorld.Admin, MatrixWorld.Other);
                return await As(h, MatrixWorld.Other);
            }),
            new("P1", "the pairing admin in Selected mode", 200, 1, h => As(h, MatrixWorld.Admin)),
            new("P2", "a selected user", 200, 1, h => As(h, MatrixWorld.Selected)),
            new("P3", "mode Everyone, an account created after the switch", 200, 1, async h =>
            {
                await h.SetAccessAsync(AccessMode.Everyone, false, MatrixWorld.Admin);
                return await As(h, MatrixWorld.NewUser);
            }),
            new("P4", "switched back to Selected: a user from the restored selection", 200, 1, async h =>
            {
                await h.SetAccessAsync(AccessMode.Everyone, false, MatrixWorld.Admin, MatrixWorld.Selected);
                await h.SetAccessAsync(AccessMode.Selected, false, MatrixWorld.Admin, MatrixWorld.Selected);
                return await As(h, MatrixWorld.Selected);
            }),
            new("P5", "mode Everyone, a restricted profile with includeRestricted on", 200, 1, async h =>
            {
                await h.SetAccessAsync(AccessMode.Everyone, true, MatrixWorld.Admin);
                return await As(h, MatrixWorld.Child);
            }),
        ];
    }

    [Theory]
    [MemberData(nameof(RowIds))]
    public async Task T_SEC_15_EveryRow_HoldsOnEveryMethod_AndTheFakePeerSeesNothingOnARefusal(string id)
    {
        await using var harness = await MatrixHarness.StartAsync();
        var row = Rows.Single(r => r.Id == id);

        foreach (var method in new[] { HttpMethod.Get, HttpMethod.Post, HttpMethod.Head })
        {
            Assert.Null(await harness.CheckAsync(row, method));
        }
    }

    [Fact]
    public async Task T_SEC_15_ThePositiveControls_EchoTheCallerAndACappedDeviceId()
    {
        await using var harness = await MatrixHarness.StartAsync();
        await harness.ResetAsync();
        harness.World.Principals["p"] = MatrixWorld.Principal(MatrixWorld.Admin);

        using var response = await harness.CallAsync("p");

        Assert.Equal(200, (int)response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal("user:" + MatrixWorld.Admin.ToString("N") + ";device:device-" + MatrixWorld.Admin.ToString("N")[^4..], body);
        Assert.Single(harness.Peer.Requests);
    }

    [Fact]
    public async Task T_SEC_15_AllRowsTogether_AreGreen()
    {
        await using var harness = await MatrixHarness.StartAsync();
        var failures = new List<string>();
        foreach (var row in Rows)
        {
            if (await harness.CheckAsync(row) is { } problem)
            {
                failures.Add(problem);
            }
        }

        Assert.Empty(failures);
        Assert.Equal(Rows.Count(r => r.PeerRequests == 1), harness.Peer.Requests.Count);
    }

    [Fact]
    public async Task T_SEC_15_Mutation_RemovingStep6_TurnsTheAccessModeRowsRed()
    {
        await using var harness = await MatrixHarness.StartAsync(skippedStep: 6);
        var failures = new List<string>();
        foreach (var row in Rows)
        {
            if (await harness.CheckAsync(row) is { } problem)
            {
                failures.Add(problem);
            }
        }

        foreach (var id in new[] { "R11", "R11b", "R11c", "R15", "R15b", "R16", "R17" })
        {
            Assert.Contains(failures, f => f.StartsWith(id + ":", StringComparison.Ordinal));
        }

        Assert.DoesNotContain(failures, f => f.StartsWith("P1:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task T_SEC_15_Mutation_RemovingStep4_TurnsTheDisabledAndDeletedRowsRed()
    {
        await using var harness = await MatrixHarness.StartAsync(skippedStep: 4);
        var failures = new List<string>();
        foreach (var row in Rows)
        {
            if (await harness.CheckAsync(row) is { } problem)
            {
                failures.Add(problem);
            }
        }

        Assert.Contains(failures, f => f.StartsWith("R3b:", StringComparison.Ordinal));
        Assert.Contains(failures, f => f.StartsWith("R4b:", StringComparison.Ordinal));
        Assert.DoesNotContain(failures, f => f.StartsWith("R1:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ARefusalAtStep7_CarriesNotAllowed_ForPlaybackInfo()
    {
        await using var harness = await MatrixHarness.StartAsync();
        await harness.ResetAsync();
        harness.World.Users[MatrixWorld.Selected] = MatrixWorld.MakeUser(MatrixWorld.Selected, canPlay: false);
        harness.World.Principals["p"] = MatrixWorld.Principal(MatrixWorld.Selected);

        using var response = await harness.CallAsync("p");

        Assert.Equal(403, (int)response.StatusCode);
        Assert.Equal("NotAllowed", response.Headers.GetValues("X-Playback-Error").Single());
        Assert.Equal("7", response.Headers.GetValues("X-Denied-Step").Single());
    }

    [Fact]
    public async Task InRecoveryMode_EveryFederatedRequestIsRefused_AfterTheEarlierChecks()
    {
        using var dir = new TempDirectory();
        var paths = new Jellyfin.Plugin.Shoal.Storage.ShoalPaths(dir.Path);
        Directory.CreateDirectory(paths.Root);
        File.WriteAllText(paths.StateFile, "{ nope");
        using var recovery = new StateStore(paths, NullLogger.Instance, TimeProvider.System, null);
        Assert.Equal(StateStatus.Recovery, recovery.Status);
        await using var harness = await MatrixHarness.StartAsync();
        await harness.ResetAsync();
        var world = harness.World;
        harness.World.Principals["p"] = MatrixWorld.Principal(MatrixWorld.Admin);
        var context = new DefaultHttpContext { User = world.Principals["p"] };
        var callerContext = new CallerContext(
            new AllowAllAuthorization(),
            new Microsoft.AspNetCore.Authorization.DefaultAuthorizationPolicyProvider(Microsoft.Extensions.Options.Options.Create(new AuthorizationOptions())),
            new Users(world),
            new Visible(),
            new Federated(world),
            recovery,
            _ => false,
            NullLogger.Instance);

        var decision = await callerContext.AuthorizeAsync(context, MatrixWorld.Item);

        Assert.False(decision.Allowed);
        Assert.Equal(403, decision.Status);
        Assert.Equal(PlaybackDenial.NotAllowed, decision.PlaybackError);
        Assert.Equal(6, decision.DeniedAtStep);
    }

    [Fact]
    public void ACallerDoesNotPrintItsToken()
    {
        var decision = Allowed();

        Assert.DoesNotContain(decision.Caller!.Token, decision.Caller.ToString(), StringComparison.Ordinal);
        Assert.StartsWith("tok", decision.Caller.Token, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, "unknown")]
    [InlineData("", "unknown")]
    [InlineData("shoal-1234", "shoal-1234")]
    [InlineData("Jellyfin Web (Chrome) 123", "Jellyfin Web (Chrome) 123")]
    public void TheDeviceId_IsKeptWhenItIsShortPrintableAscii(string? input, string expected)
    {
        Assert.Equal(expected, DeviceKey.Cap(input));
    }

    [Theory]
    [InlineData("device\r\nid")]
    [InlineData("device\u0000")]
    [InlineData("Gerät")]
    [InlineData("‮x")]
    public void ADeviceIdWithOddCharacters_IsHashed(string input)
    {
        var key = DeviceKey.Cap(input);

        Assert.StartsWith("h-", key, StringComparison.Ordinal);
        Assert.Equal(34, key.Length);
        Assert.Equal(key, DeviceKey.Cap(input));
        Assert.NotEqual(key, DeviceKey.Cap(input + "x"));
    }

    [Fact]
    public void ADeviceIdOver128Characters_IsHashed_AtExactly128ItIsKept()
    {
        var exact = new string('d', 128);
        var tooLong = new string('d', 129);

        Assert.Equal(exact, DeviceKey.Cap(exact));
        Assert.Equal(34, DeviceKey.Cap(tooLong).Length);
        Assert.StartsWith("h-", DeviceKey.Cap(tooLong), StringComparison.Ordinal);
    }

    [Fact]
    public void Admitted_FollowsTheRuleOfDesign56()
    {
        var shares = new HashSet<Guid> { MatrixWorld.ShareUser };
        var ordinary = MatrixWorld.MakeUser(MatrixWorld.Other);
        var restricted = MatrixWorld.MakeUser(MatrixWorld.Child, restricted: true);
        var share = MatrixWorld.MakeUser(MatrixWorld.ShareUser);
        var selected = new LibraryAccess { Mode = AccessMode.Selected, SelectedUserIds = [MatrixWorld.Child, MatrixWorld.ShareUser] };
        var everyone = new LibraryAccess { Mode = AccessMode.Everyone, SelectedUserIds = [MatrixWorld.Other] };
        var everyoneRestricted = new LibraryAccess { Mode = AccessMode.Everyone, IncludeRestricted = true };

        Assert.False(AccessPolicy.Admitted(ordinary, selected, shares));
        Assert.True(AccessPolicy.Admitted(restricted, selected, shares)); // selected individually, even though restricted
        Assert.False(AccessPolicy.Admitted(share, selected, shares)); // a share user is refused first, even when selected
        Assert.True(AccessPolicy.Admitted(ordinary, everyone, shares));
        Assert.False(AccessPolicy.Admitted(restricted, everyone, shares));
        Assert.True(AccessPolicy.Admitted(restricted, everyoneRestricted, shares));
        Assert.False(AccessPolicy.Admitted(share, everyoneRestricted, shares));
        Assert.False(AccessPolicy.Admitted(MatrixWorld.MakeUser(Guid.NewGuid()), selected, shares));
    }

    [Fact]
    public void TheShoalIndex_ClassifiesByPath_AndAnEmptyStubRootFederatesNothing()
    {
        var state = new ShoalState { StubRoot = Path.Combine(Path.GetTempPath(), "shoal-stubs") };
        state.Home.Peers.Add(new PeerState { PeerKey = StateSamples.PeerKeyA, Libraries = { new LibraryMapping { MappingKey = "m1", Subfolder = "movies" } } });
        var stub = Path.Combine(state.StubRoot, StateSamples.PeerKeyA, "movies", "The Matrix (1999)", "The Matrix (1999).strm");

        Assert.Equal(new FederatedItem(StateSamples.PeerKeyA, "m1"), Authorization.ShoalIndex.FromPath(state, stub));
        Assert.Null(Authorization.ShoalIndex.FromPath(state, Path.Combine(state.StubRoot, StateSamples.PeerKeyA, "tv", "x", "y.strm")));
        Assert.Null(Authorization.ShoalIndex.FromPath(state, Path.Combine(state.StubRoot, StateSamples.PeerKeyB, "movies", "x", "y.strm")));
        Assert.Null(Authorization.ShoalIndex.FromPath(state, Path.Combine(Path.GetTempPath(), "elsewhere", "movies", "x.strm")));
        Assert.Null(Authorization.ShoalIndex.FromPath(state, Path.Combine(state.StubRoot, "..", "shoal-stubs-other", StateSamples.PeerKeyA, "movies", "x", "y.strm")));
        Assert.Null(Authorization.ShoalIndex.FromPath(state, Path.Combine(state.StubRoot, StateSamples.PeerKeyA, "movies.strm")));
        state.StubRoot = string.Empty;
        Assert.Null(Authorization.ShoalIndex.FromPath(state, stub));
    }

    [Fact]
    public void T_SEC_15_Reflection_EveryControllerActionIsAuthorized_AndAnonymousIsPinnedToThePairingAction()
    {
        var controllers = typeof(Plugin).Assembly.GetTypes().Where(t => typeof(ControllerBase).IsAssignableFrom(t) && !t.IsAbstract).ToList();
        Assert.NotEmpty(controllers);
        // W6 L-7: a controller is a routable door. Only the admin API and the planned peer-facing controllers may exist; a leftover spike
        // would bypass CallerContext. A new one needs a deliberate edit here and a Warden look.
        Assert.All(controllers, c => Assert.True(c.Name.StartsWith("Admin", StringComparison.Ordinal) || c.Name is "PairController" or "ShoalPingController" or "ShoalHlsController" or "ShoalSubtitlesController" or "ShareRotateController", c.Name + " is not an expected controller"));
        var anonymous = new List<string>();
        foreach (var controller in controllers)
        {
            var classAuthorize = controller.GetCustomAttributes<AuthorizeAttribute>(inherit: true).Any();
            var classAnonymous = controller.GetCustomAttributes<AllowAnonymousAttribute>(inherit: true).Any();
            Assert.False(classAnonymous, controller.Name + " is anonymous as a whole");
            var actions = controller.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(m => m.GetCustomAttributes().Any(a => a is Microsoft.AspNetCore.Mvc.Routing.HttpMethodAttribute))
                .ToList();
            Assert.NotEmpty(actions);
            foreach (var action in actions)
            {
                var methodAuthorize = action.GetCustomAttributes<AuthorizeAttribute>(inherit: true).Any();
                var methodAnonymous = action.GetCustomAttributes<AllowAnonymousAttribute>(inherit: true).Any();
                Assert.True(classAuthorize || methodAuthorize || methodAnonymous, controller.Name + "." + action.Name + " has no authorization attribute");
                if (methodAnonymous)
                {
                    anonymous.Add(controller.Name + "." + action.Name);
                }
            }
        }

        // DESIGN 4.1b / 8.4: the anonymous actions in Shoal are PairController.Pair (K22) and ShoalPingController.Ping (pairing v2). Nothing else may be.
        Assert.All(anonymous, name => Assert.Contains(name, new[] { "PairController.Pair", "ShoalPingController.Ping" }));
        Assert.True(anonymous.Count <= 2);
    }

    [Fact]
    public void W6_L4_TheStepSkippingSeam_IsNotReachableThroughDependencyInjection()
    {
        // DI (AddScoped<CallerContext>) only sees public constructors; none of them may take the skipped-step switch.
        Assert.All(typeof(CallerContext).GetConstructors(), c => Assert.DoesNotContain(c.GetParameters(), p => p.ParameterType == typeof(int)));
    }

    [Fact]
    public void TheBannedSymbols_RejectTheCredentialReParsingAndTheUnscopedLookups()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "src", "Jellyfin.Plugin.Shoal", "BannedSymbols.txt")))
        {
            directory = directory.Parent;
        }

        var text = File.ReadAllText(Path.Combine(directory!.FullName, "src", "Jellyfin.Plugin.Shoal", "BannedSymbols.txt"));
        Assert.Contains("IAuthorizationContext.GetAuthorizationInfo(Microsoft.AspNetCore.Http.HttpContext)", text, StringComparison.Ordinal);
        Assert.Contains("IAuthorizationContext.GetAuthorizationInfo(Microsoft.AspNetCore.Http.HttpRequest)", text, StringComparison.Ordinal);
        Assert.Contains("ILibraryManager.GetItemById``1(System.Guid,Jellyfin.Database.Implementations.Entities.User)", text, StringComparison.Ordinal);
        Assert.Contains("ILibraryManager.GetItemById``1(System.Guid,System.Guid)", text, StringComparison.Ordinal);
        Assert.Contains("ILibraryManager.GetItemById(System.Guid)", text, StringComparison.Ordinal);
    }

    // A shared library of v2.9 (K44) with the matrix item in its folder; with feeds=false the item's connection does not feed it.
    private static async Task SharedLibraryAsync(MatrixHarness h, bool feeds, params Guid[] selected)
    {
        await h.State.UpdateAsync(s => s.Home.Libraries.Add(new ShoalLibrary
        {
            LibKey = SharedKey,
            Name = "Friends' movies",
            Type = "movies",
            PathRel = ShoalLibrary.LibsFolder + "/" + SharedKey,
            Feeds = feeds ? [h.World.PeerKey] : [],
            Access = new LibraryAccess { Mode = AccessMode.Selected, SelectedUserIds = [.. selected] },
        }));
        h.World.Federated[MatrixWorld.Item] = new FederatedItem(h.World.PeerKey, h.World.MappingKey, SharedKey);
    }

    private static CallerDecision Allowed()
    {
        var user = MatrixWorld.MakeUser(MatrixWorld.Admin);
        var principal = MatrixWorld.Principal(MatrixWorld.Admin);
        var caller = (Caller)typeof(Caller).GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance)[0].Invoke([user, principal.FindFirst(CallerContext.TokenClaim)!.Value, "d", false]);
        return (CallerDecision)typeof(CallerDecision).GetMethod("Allow", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [caller])!;
    }

    private sealed class AllowAllAuthorization : IAuthorizationService
    {
        public Task<AuthorizationResult> AuthorizeAsync(ClaimsPrincipal user, object? resource, IEnumerable<IAuthorizationRequirement> requirements) => Task.FromResult(AuthorizationResult.Success());

        public Task<AuthorizationResult> AuthorizeAsync(ClaimsPrincipal user, object? resource, string policyName) => Task.FromResult(AuthorizationResult.Success());
    }

    private sealed class Users : ICallerUsers
    {
        private readonly MatrixWorld _world;

        public Users(MatrixWorld world) => _world = world;

        public CallerUser? Find(Guid userId) => _world.Users.TryGetValue(userId, out var user) ? user : null;
    }

    private sealed class Visible : IVisibleItems
    {
        public bool IsVisible(Guid userId, Guid itemId) => true;
    }

    private sealed class Federated : IFederatedItems
    {
        private readonly MatrixWorld _world;

        public Federated(MatrixWorld world) => _world = world;

        public FederatedItem? Find(Guid localItemId) => _world.Federated.TryGetValue(localItemId, out var item) ? item : null;
    }
}
