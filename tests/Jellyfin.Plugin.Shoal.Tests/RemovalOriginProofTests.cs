using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.Shoal.Api;
using Jellyfin.Plugin.Shoal.Authorization;
using Jellyfin.Plugin.Shoal.Http;
using Jellyfin.Plugin.Shoal.LocalRemoval;
using Jellyfin.Plugin.Shoal.Sharing;
using Jellyfin.Plugin.Shoal.State;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Jellyfin.Plugin.Shoal.Tests;

public sealed class RemovalOriginProofTests
{
    private static readonly Guid User = Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly Guid Item = Guid.Parse("20000000-0000-0000-0000-000000000002");
    private const string Token = "private-token-that-must-not-leak";
    private const string Hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    private static RemovalOriginSource Source(string generation = "fresh") => new("private-root", "private-path", new Dictionary<string, string> { ["Tmdb"] = "123" }, 200_000_000, generation);

    private static Task<RemovalFileSnapshot> Probe(string root, string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return Task.FromResult(new RemovalFileSnapshot(path, root, 1024, Hash, "private-file-identity"));
    }

    [Fact]
    public async Task AdmittedSourceIsFreshlyProbedAndResponseContainsNoPrivateValues()
    {
        var reads = 0;
        var probes = 0;
        using var service = new RemovalOriginProof((user, token, item) => { reads++; return user == User && token == Token && item == Item ? Source() : null; },
            (root, path, token) => { probes++; return Probe(root, path, token); }, new ManualTimeProvider());
        var result = await service.ReadAsync(User, Token, Item, default);
        Assert.Equal(200, result.Status);
        Assert.Equal(2, reads);
        Assert.Equal(1, probes);
        Assert.Equal(1024, result.Proof!.Length);
        Assert.Equal(Hash, result.Proof.ContentSha256);
        Assert.Equal(64, result.Proof.Generation.Length);
        var text = JsonSerializer.Serialize(result.Proof);
        Assert.DoesNotContain(Token, text, StringComparison.Ordinal);
        Assert.DoesNotContain("private-root", text, StringComparison.Ordinal);
        Assert.DoesNotContain("private-path", text, StringComparison.Ordinal);
        Assert.DoesNotContain("private-file-identity", text, StringComparison.Ordinal);
        Assert.DoesNotContain(Item.ToString(), text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task UnknownCallerOrItemCannotStartHashing(int missing)
    {
        var probes = 0;
        using var service = new RemovalOriginProof((_, _, _) => null, (root, path, token) => { probes++; return Probe(root, path, token); }, new ManualTimeProvider());
        var result = await service.ReadAsync(missing == 0 ? Guid.Empty : User, missing == 1 ? string.Empty : Token, missing == 2 ? Guid.Empty : Item, default);
        Assert.Equal(404, result.Status);
        Assert.Equal(0, probes);
    }

    [Fact]
    public async Task GrantRevokedDuringHashingReturnsUniformNotFound()
    {
        var admitted = true;
        using var service = new RemovalOriginProof((_, _, _) => admitted ? Source() : null, (root, path, token) => { admitted = false; return Probe(root, path, token); }, new ManualTimeProvider());
        Assert.Equal(404, (await service.ReadAsync(User, Token, Item, default)).Status);
    }

    [Theory]
    [InlineData("notShare")]
    [InlineData("paused")]
    [InlineData("unexpectedDevice")]
    [InlineData("policyDrift")]
    [InlineData("administrator")]
    [InlineData("missingUser")]
    public async Task ProductionAuthorizationRejectsInvalidSharingAccountsBeforeAnyItemLookup(string reason)
    {
        using var rig = new PairingRig();
        var share = new ShareState { ShareId = "1111111111111111", ShareUserId = User, ExpectedDeviceIds = ["device"], LibraryIds = [Guid.NewGuid()] };
        if (reason != "notShare") await rig.State.UpdateAsync(s => s.PeerRole.Shares.Add(share));
        if (reason == "paused") await rig.State.UpdateAsync(s => s.PeerRole.Shares[0].Enabled = false);
        var account = new Jellyfin.Database.Implementations.Entities.User("synthetic", "test", "test") { Id = User };
        if (reason == "administrator") account.Permissions.Add(new Permission(PermissionKind.IsAdministrator, true));
        var shares = new FakeShareUsers();
        shares.Policies[User] = ShareService.PolicyOf(share, rig.State.Current);
        shares.Tokens[Token] = (User, reason == "unexpectedDevice" ? "wrong-device" : "device");
        shares.DriftNext = reason == "policyDrift";
        var lookups = 0;
        var library = RemoveServerGhostTests.CoreProxy<ILibraryManager>.Create((_, _) => { lookups++; throw new InvalidOperationException("No core item lookup expected"); });
        var accounts = RemoveServerGhostTests.CoreProxy<IUserManager>.Create((method, _) => method.Name == "GetUserById" ? reason == "missingUser" ? null : account : null);
        using var service = new RemovalOriginProof(rig.State, shares, library, accounts, null!, null!, null!, rig.Time);
        Assert.Equal(404, (await service.ReadAsync(User, Token, Item, default)).Status);
        Assert.Equal(0, lookups);
    }

    [Fact]
    public async Task ChangedItemGenerationAfterHashingCannotReturnProof()
    {
        var generation = "before";
        using var service = new RemovalOriginProof((_, _, _) => Source(generation), (root, path, token) => { generation = "after"; return Probe(root, path, token); }, new ManualTimeProvider());
        Assert.Equal(409, (await service.ReadAsync(User, Token, Item, default)).Status);
    }

    [Fact]
    public async Task ProbeResultCannotSubstituteADifferentPathOrRoot()
    {
        using var service = new RemovalOriginProof((_, _, _) => Source(), (_, _, _) => Task.FromResult(new RemovalFileSnapshot("elsewhere", "other-root", 1024, Hash, "identity")), new ManualTimeProvider());
        Assert.Equal(409, (await service.ReadAsync(User, Token, Item, default)).Status);
    }

    [Fact]
    public async Task ConcurrentHashesHaveNoWaitingQueue()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var service = new RemovalOriginProof((_, _, _) => Source(), async (root, path, token) =>
        {
            entered.SetResult();
            await release.Task.WaitAsync(token);
            return await Probe(root, path, token);
        }, new ManualTimeProvider());
        var first = service.ReadAsync(User, Token, Item, default);
        await entered.Task;
        var second = await service.ReadAsync(Guid.NewGuid(), Token, Item, default);
        Assert.Equal(429, second.Status);
        release.SetResult();
        Assert.Equal(200, (await first).Status);
    }

    [Fact]
    public async Task PerShareBurstIsBoundedAndRefillsWithTheClock()
    {
        var time = new ManualTimeProvider();
        using var service = new RemovalOriginProof((_, _, _) => Source(), Probe, time);
        for (var i = 0; i < 3; i++) Assert.Equal(200, (await service.ReadAsync(User, Token, Item, default)).Status);
        Assert.Equal(429, (await service.ReadAsync(User, Token, Item, default)).Status);
        time.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(200, (await service.ReadAsync(User, Token, Item, default)).Status);
    }

    [Fact]
    public async Task GlobalBurstCannotBeBypassedByChangingShareAccounts()
    {
        using var service = new RemovalOriginProof((_, _, _) => Source(), Probe, new ManualTimeProvider());
        for (var i = 0; i < 6; i++) Assert.Equal(200, (await service.ReadAsync(Guid.NewGuid(), Token, Item, default)).Status);
        Assert.Equal(429, (await service.ReadAsync(Guid.NewGuid(), Token, Item, default)).Status);
    }

    [Fact]
    public async Task CallerCancellationIsNotConvertedToASuccessOrSwallowed()
    {
        using var cancellation = new CancellationTokenSource();
        using var service = new RemovalOriginProof((_, _, _) => Source(), async (_, _, token) => { cancellation.Cancel(); await Task.Delay(Timeout.Infinite, token); throw new IOException(); }, new ManualTimeProvider());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ReadAsync(User, Token, Item, cancellation.Token));
    }

    [Fact]
    public async Task LaterCallMustOpenSourceAgainAfterTheFileWasQuarantined()
    {
        var present = true;
        var probes = 0;
        using var service = new RemovalOriginProof((_, _, _) => Source(), (root, path, token) =>
        {
            probes++;
            return present ? Probe(root, path, token) : throw new FileNotFoundException();
        }, new ManualTimeProvider());
        Assert.Equal(200, (await service.ReadAsync(User, Token, Item, default)).Status);
        present = false;
        Assert.Equal(409, (await service.ReadAsync(User, Token, Item, default)).Status);
        Assert.Equal(2, probes);
    }

    [Fact]
    public async Task ActualNativeProbeHashesTheFullFileAndFreshlyObservesChangedBytes()
    {
        using var directory = new TempDirectory();
        var path = directory.Combine("movie.mp4");
        var bytes = new byte[256 * 1024];
        bytes[0] = 1;
        bytes[^1] = 2;
        await File.WriteAllBytesAsync(path, bytes);
        var source = Source() with { Root = directory.Path, Path = path };
        using var service = new RemovalOriginProof((_, _, _) => source, new RemovalFiles().ProbeAsync, new ManualTimeProvider());
        var first = await service.ReadAsync(User, Token, Item, default);
        Assert.Equal(200, first.Status);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), first.Proof!.ContentSha256);
        bytes[^1] = 3;
        await File.WriteAllBytesAsync(path, bytes);
        var next = await service.ReadAsync(User, Token, Item, default);
        Assert.Equal(200, next.Status);
        Assert.NotEqual(first.Proof.Generation, next.Proof!.Generation);
        Assert.NotEqual(first.Proof.ContentSha256, next.Proof.ContentSha256);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), next.Proof.ContentSha256);
    }

    [Fact]
    public async Task PortableProbeThroughTheProofHashesTheFullFileAndSeesChangedBytes()
    {
        using var directory = new TempDirectory();
        var path = directory.Combine("Sintel (2010)", "Sintel.mkv");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var bytes = new byte[1536 * 1024];
        bytes[0] = 1;
        bytes[^1] = 2;
        await File.WriteAllBytesAsync(path, bytes);
        var source = Source() with { Root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory.Path)), Path = Path.GetFullPath(path) };
        using var service = new RemovalOriginProof((_, _, _) => source, new PortableFileProbe().ProbeAsync, new ManualTimeProvider());
        var first = await service.ReadAsync(User, Token, Item, default);
        Assert.Equal(200, first.Status);
        Assert.Equal(1, first.Proof!.V);
        Assert.Equal(bytes.Length, first.Proof.Length);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), first.Proof.ContentSha256);
        bytes[^1] = 3;
        await File.WriteAllBytesAsync(path, bytes);
        var next = await service.ReadAsync(User, Token, Item, default);
        Assert.Equal(200, next.Status);
        Assert.NotEqual(first.Proof.ContentSha256, next.Proof!.ContentSha256);
        Assert.NotEqual(first.Proof.Generation, next.Proof.Generation);
        var text = JsonSerializer.Serialize(next.Proof);
        Assert.DoesNotContain("Sintel", text, StringComparison.Ordinal);
        Assert.DoesNotContain("p:", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ProductionConstructorUsesThePortableProbe()
    {
        // 2.10.4 K1: the native probe refuses every network-share filesystem, so the friend side must hash with the portable one.
        using var service = new RemovalOriginProof(null!, null!, null!, null!, null!, null!, null!, new ManualTimeProvider());
        var probe = (Delegate)typeof(RemovalOriginProof).GetField("_probe", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service)!;
        Assert.IsType<PortableFileProbe>(probe.Target);
    }

    [Fact]
    public void FriendProofCeilingIsTheLargestScaledDeadlineNotTheOldTwoMinutes()
    {
        Assert.Equal(TimeSpan.FromMinutes(130), RemovalOriginProof.FriendProofCeiling);
        Assert.True(RemovalOriginProof.FriendProofCeiling > RemovalPolicy.HashingDeadline);
    }

    [Fact]
    public async Task OuterCeilingEndsAStuckProbeAsConflictAndFreesTheWorker()
    {
        var calls = 0;
        using var service = new RemovalOriginProof((_, _, _) => Source(), async (_, _, token) =>
        {
            calls++;
            await Task.Delay(Timeout.Infinite, token);
            throw new IOException();
        }, new ManualTimeProvider(), TimeSpan.FromMilliseconds(50));
        Assert.Equal(409, (await service.ReadAsync(User, Token, Item, default)).Status);
        Assert.Equal(409, (await service.ReadAsync(User, Token, Item, default)).Status); // not 429: the single worker slot was released
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task ANativeStyleRefusalOfANetworkShareIsAUniformConflict()
    {
        // What a 2.10.3 friend answers for a file on SMB; the caller maps it to originUnavailable.
        using var service = new RemovalOriginProof((_, _, _) => Source(), (_, _, _) => throw new IOException("networkShareUnsupported"), new ManualTimeProvider());
        var result = await service.ReadAsync(User, Token, Item, default);
        Assert.Equal(409, result.Status);
        Assert.Null(result.Proof);
    }

    [Theory]
    [InlineData(0L, 310, 320)]
    [InlineData(1L, 385, 395)]
    [InlineData(20L << 30, 1810, 1820)]
    [InlineData(100L << 30, 7810, 7820)] // 2.10.4: 100 GiB, the hard maximum (130 min + 10 s)
    [InlineData((100L << 30) + 1, 7810, 7820)]
    public void ClientProfileWaitsForTheFriendsScaledHash(long bytes, int headersSeconds, int totalSeconds)
    {
        var profile = RemovalOriginClient.HashProfileFor(bytes);
        Assert.Equal(TimeSpan.FromSeconds(headersSeconds), profile.Headers);
        Assert.Equal(TimeSpan.FromSeconds(totalSeconds), profile.Total);
        Assert.Equal(16 * 1024, profile.MaxBytes);
        Assert.True(profile.Headers > RemovalPolicy.DeleteHashingDeadlineFor(bytes), "the caller must outlast the friend's own deadline");
    }

    [Theory]
    [InlineData("source.strm", false, false)]
    [InlineData("source.mp4", true, false)]
    [InlineData("source.mp4", false, true)]
    public void PhysicalSourceShapeRefusesRelaysShortcutsAndPlaceholders(string name, bool shortcut, bool placeholder)
    {
        using var directory = new TempDirectory();
        var movie = new Movie { Path = directory.Combine(name), IsShortcut = shortcut, IsPlaceHolder = placeholder, VideoType = VideoType.VideoFile };
        Assert.False(RemovalOriginProof.SupportsMovieShape(movie));
    }

    [Fact]
    public async Task RouteUsesCoreClaimsAndRejectsApiKeyBeforeSourceLookup()
    {
        var calls = 0;
        using var service = new RemovalOriginProof((_, _, _) => { calls++; return Source(); }, Probe, new ManualTimeProvider());
        var controller = new RemovalOriginController(service) { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };
        controller.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(CallerContext.UserIdClaim, User.ToString()), new Claim(CallerContext.TokenClaim, Token), new Claim(CallerContext.IsApiKeyClaim, "true")], "core"));
        var result = Assert.IsType<ShoalResult>(await controller.Get(Item, default));
        Assert.Equal(404, result.Status);
        Assert.Equal(0, calls);
        Assert.Single(typeof(RemovalOriginController).GetCustomAttributes<AuthorizeAttribute>());
        Assert.Empty(typeof(RemovalOriginController).GetCustomAttributes<AllowAnonymousAttribute>());
    }
}
