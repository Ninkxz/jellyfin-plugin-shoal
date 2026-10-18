using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.Api.Admin;
using Jellyfin.Plugin.Shoal.Sharing;
using Jellyfin.Plugin.Shoal.State;
using Jellyfin.Plugin.Shoal.Storage;
using Jellyfin.Plugin.Shoal.Visibility;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Shoal.Tests;

internal sealed class FakeShareUsers : IShareUserSource
{
    public Dictionary<Guid, SharePolicy> Policies { get; } = [];

    public Dictionary<Guid, string> Names { get; } = [];

    public Dictionary<Guid, List<ShareDevice>> DevicesOf { get; } = [];

    public List<string> Calls { get; } = [];

    public int StubbornDevices { get; set; }

    public bool DriftNext { get; set; }

    public bool FailPolicy { get; set; }

    /// <summary>Gets or sets what happens right after a policy was applied (a pause that lands in the middle of an operation).</summary>
    public Action? OnPolicyApplied { get; set; }

    public bool FailDelete { get; set; }

    public bool Exists(Guid userId) => Policies.ContainsKey(userId);

    public Task<Guid> CreateAsync(string userName, string password, CancellationToken cancellationToken)
    {
        Assert.False(string.IsNullOrEmpty(password));
        var id = Guid.NewGuid();
        Policies[id] = new SharePolicy(false, [], null, []);
        Names[id] = userName;
        DevicesOf[id] = [];
        Calls.Add("create");
        return Task.FromResult(id);
    }

    public Task ApplyPolicyAsync(Guid userId, SharePolicy policy, CancellationToken cancellationToken)
    {
        if (FailPolicy)
        {
            throw new IOException("policy failed");
        }

        Policies[userId] = policy;
        Calls.Add(policy.Disabled ? "policy-disabled" : "policy");
        OnPolicyApplied?.Invoke();
        return Task.CompletedTask;
    }

    public bool PolicyMatches(Guid userId, SharePolicy expected)
    {
        if (DriftNext)
        {
            DriftNext = false;
            return false;
        }

        var actual = Policies[userId];
        return actual.Disabled == expected.Disabled && actual.Libraries.SequenceEqual(expected.Libraries) && actual.AllowedDevices.SequenceEqual(expected.AllowedDevices)
            && (expected.BlockedTags is null || expected.BlockedTags.All(t => (actual.BlockedTags ?? []).Contains(t))); // as the real check
    }

    public IReadOnlyList<ShareDevice> Devices(Guid userId) => [.. DevicesOf[userId]];

    public void Mint(Guid userId, string deviceId) => DevicesOf[userId].Add(new ShareDevice(deviceId, "Chrome on Windows", "Jellyfin Web", "12.1.0", DateTime.UtcNow, DateTime.UtcNow));

    /// <summary>Gets or sets a gate that holds every device removal until it is completed (a rotation in flight).</summary>
    public TaskCompletionSource? RemoveGate { get; set; }

    public int RemovalsEntered { get; private set; }

    public async Task RemoveDeviceAsync(Guid userId, string deviceId, CancellationToken cancellationToken)
    {
        RemovalsEntered++;
        if (RemoveGate is { } removeGate)
        {
            await removeGate.Task;
        }

        if (StubbornDevices > 0)
        {
            StubbornDevices--;
            return;
        }

        DevicesOf[userId].RemoveAll(d => d.DeviceId == deviceId);
        foreach (var dead in Tokens.Where(t => t.Value.User == userId && t.Value.Device == deviceId).Select(static t => t.Key).ToList())
        {
            Tokens.Remove(dead);
        }

        Calls.Add("remove-device");
    }

    public Task DeleteAsync(Guid userId, CancellationToken cancellationToken)
    {
        if (FailDelete)
        {
            throw new IOException("delete failed");
        }

        Policies.Remove(userId);
        DevicesOf.Remove(userId);
        Calls.Add("delete");
        return Task.CompletedTask;
    }

    public List<(Guid User, string Device)> Minted { get; } = [];

    public TaskCompletionSource? MintGate { get; set; }

    public bool FailMint { get; set; }

    /// <summary>Gets the live tokens: token to (user, device). A removed device takes its tokens with it.</summary>
    public Dictionary<string, (Guid User, string Device)> Tokens { get; } = [];

    /// <summary>Gets or sets a value indicating whether each mint returns a new token (the default is one constant token).</summary>
    public bool UniqueTokens { get; set; }

    /// <summary>Gets or sets how many of the next mints fail.</summary>
    public int FailMintTimes { get; set; }

    public string? DeviceIdOfToken(Guid userId, string accessToken) => Tokens.TryGetValue(accessToken, out var found) && found.User == userId ? found.Device : null;

    public async Task<string> MintTokenAsync(Guid userId, string deviceId, string deviceName, CancellationToken cancellationToken)
    {
        Assert.Contains(deviceId, Policies[userId].AllowedDevices);
        Minted.Add((userId, deviceId));
        Mint(userId, deviceId);
        if (MintGate is { } gate)
        {
            await gate.Task;
        }

        if (FailMint || FailMintTimes > 0)
        {
            FailMintTimes = Math.Max(0, FailMintTimes - 1);
            throw new InvalidOperationException("mint failed");
        }

        var token = UniqueTokens ? Guid.NewGuid().ToString("N") : "0123456789abcdef0123456789abcdef";
        Tokens[token] = (userId, deviceId);
        return token;
    }

    public ShareActivity Activity(Guid userId) => new(1, 1);
}

/// <summary>K21 (DESIGN 4.1b, 4.5): the sharing account, the sweep, drift repair, pause and resume, revoke, and the library refusals.</summary>
public sealed class ShareServiceTests
{
    private static async Task<(ShareService Service, FakeShareUsers Users, FakeLibraries Libraries, StateStore State, TempDirectory Dir, LibraryInfo Movies)> CreateAsync()
    {
        var dir = new TempDirectory();
        var state = new StateStore(new ShoalPaths(dir.Path), NullLogger.Instance, TimeProvider.System, null);
        await state.UpdateAsync(s => s.StubRoot = dir.Combine("stubs"));
        var libraries = new FakeLibraries();
        var movies = libraries.AddLocal("Movies");
        var users = new FakeShareUsers();
        return (new ShareService(state, users, libraries, new NoShareInvites(), TimeProvider.System, NullLogger.Instance), users, libraries, state, dir, movies);
    }

    private static ShareRequest Request(string name, params Guid[] libraries) => new() { FriendName = name, LibraryIds = [.. libraries] };

    [Fact]
    public async Task M3_AFailedCreate_LeavesNoAccountAndNoShare_EvenWhenTheRequestWasCancelled()
    {
        var (service, users, _, state, dir, movies) = await CreateAsync();
        using (dir)
        using (state)
        {
            users.FailPolicy = true;
            var (outcome, _) = await service.CreateAsync(Request("Bob", movies.Id), default);

            Assert.Equal(ShareOutcome.Invalid, outcome);
            Assert.Empty(state.Current.PeerRole.Shares);
            Assert.Empty(users.Policies);
            Assert.Contains("delete", users.Calls);

            users.FailPolicy = false;
            using var cancelled = new CancellationTokenSource();
            await cancelled.CancelAsync();
            var (again, _) = await service.CreateAsync(Request("Bob", movies.Id), cancelled.Token);

            Assert.Equal(ShareOutcome.Ok, again);
            var share = Assert.Single(state.Current.PeerRole.Shares);
            Assert.True(users.Exists(share.ShareUserId));
        }
    }

    [Fact]
    public async Task Create_MakesTheAccountWithTheFullPolicy_AndDefaultsToFourTranscodes()
    {
        var (service, users, _, state, dir, movies) = await CreateAsync();
        using (dir)
        using (state)
        {
            var (outcome, body) = await service.CreateAsync(Request("Bob Smith", movies.Id), default);

            Assert.Equal(ShareOutcome.Ok, outcome);
            Assert.NotNull(body);
            var share = Assert.Single(state.Current.PeerRole.Shares);
            Assert.Matches("^shoal-bobsmith-[0-9a-f]{4}$", users.Names[share.ShareUserId]);
            Assert.Equal(4, share.MaxConcurrentTranscodes);
            Assert.Equal([movies.Id], users.Policies[share.ShareUserId].Libraries);
            Assert.Empty(users.Policies[share.ShareUserId].AllowedDevices);
            Assert.False(users.Policies[share.ShareUserId].Disabled);
        }
    }

    [Fact]
    public async Task Create_RefusesALibraryUnderTheStubRoot_AShoalLibrary_AnUnknownOne_AndADuplicateName()
    {
        var (service, users, libraries, state, dir, movies) = await CreateAsync();
        using (dir)
        using (state)
        {
            var stub = new LibraryInfo(Guid.NewGuid(), "Hops Movies", [Path.Combine(state.Current.StubRoot, "peer", "movies")]);
            await libraries.CreateAsync(stub.Name, stub.Locations[0], default);
            var stubId = libraries.List().Single(l => l.Name == "Hops Movies").Id;

            Assert.Equal(ShareOutcome.Invalid, (await service.CreateAsync(Request("A", stubId), default)).Outcome);
            Assert.Equal(ShareOutcome.Invalid, (await service.CreateAsync(Request("B", Guid.NewGuid()), default)).Outcome);
            Assert.Equal(ShareOutcome.Ok, (await service.CreateAsync(Request("Bob", movies.Id), default)).Outcome);
            Assert.Equal(ShareOutcome.Invalid, (await service.CreateAsync(Request("bob", movies.Id), default)).Outcome);
            Assert.Single(state.Current.PeerRole.Shares);
            Assert.Single(users.Names);
            var listed = service.List().ToString();
            Assert.NotNull(listed);
        }
    }

    [Fact]
    public async Task M5_TheStubRootRefusal_SeesThroughDotDotSegments_ExtraSeparators_AndSymbolicLinks()
    {
        var (service, _, libraries, state, dir, _) = await CreateAsync();
        using (dir)
        using (state)
        {
            var stubs = state.Current.StubRoot;
            Directory.CreateDirectory(Path.Combine(stubs, "peer"));
            var dotted = Path.Combine(stubs, "other", "..", "peer");
            var doubled = stubs + Path.DirectorySeparatorChar + Path.DirectorySeparatorChar + "peer";
            var sibling = stubs + "-media"; // shares the prefix only: must stay allowed
            Directory.CreateDirectory(sibling);
            await libraries.CreateAsync("Dotted", dotted, default);
            await libraries.CreateAsync("Doubled", doubled, default);
            await libraries.CreateAsync("Sibling", sibling, default);
            var id = (string name) => libraries.List().Single(l => l.Name == name).Id;

            Assert.Equal(ShareOutcome.Invalid, (await service.CreateAsync(Request("A", id("Dotted")), default)).Outcome);
            Assert.Equal(ShareOutcome.Invalid, (await service.CreateAsync(Request("B", id("Doubled")), default)).Outcome);
            Assert.Equal(ShareOutcome.Ok, (await service.CreateAsync(Request("C", id("Sibling")), default)).Outcome);

            var link = Path.Combine(dir.Path, "innocent");
            try
            {
                Directory.CreateSymbolicLink(link, Path.Combine(stubs, "peer"));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return; // no symbolic-link right on this machine: the rest is covered on Linux
            }

            await libraries.CreateAsync("Linked", link, default);
            Assert.Equal(ShareOutcome.Invalid, (await service.CreateAsync(Request("D", id("Linked")), default)).Outcome);
        }
    }

    [Fact]
    public async Task M5_TheSweep_TakesALibraryThatNowLiesUnderTheStubRoot_OffTheShare()
    {
        var (service, _, _, state, dir, movies) = await CreateAsync();
        using (dir)
        using (state)
        {
            await service.CreateAsync(Request("Bob", movies.Id), default);
            await state.UpdateAsync(s => s.StubRoot = movies.Locations[0]);

            await service.SweepAllAsync(default);

            Assert.Empty(state.Current.PeerRole.Shares[0].LibraryIds);
        }
    }

    [Fact]
    public async Task TheSweep_SignsOutAMintedDeviceAtOnce_AndKeepsHomesDevice()
    {
        var (service, users, _, state, dir, movies) = await CreateAsync();
        using (dir)
        using (state)
        {
            await service.CreateAsync(Request("Bob", movies.Id), default);
            var share = state.Current.PeerRole.Shares[0];
            await state.UpdateAsync(s => s.PeerRole.Shares[0].ExpectedDeviceIds = ["shoal-home-1"]);
            users.Mint(share.ShareUserId, "shoal-home-1");
            users.Mint(share.ShareUserId, "attacker-minted");

            await service.SweepAllAsync(default);

            Assert.Equal(["shoal-home-1"], users.DevicesOf[share.ShareUserId].Select(d => d.DeviceId));
            Assert.Equal(1, service.Live(share.ShareId).UnexpectedDeviceCount);
        }
    }

    [Fact]
    public async Task Pause_DisablesTheAccount_AndResumeFirstSweepsThenEnables()
    {
        var (service, users, _, state, dir, movies) = await CreateAsync();
        using (dir)
        using (state)
        {
            await service.CreateAsync(Request("Bob", movies.Id), default);
            var share = state.Current.PeerRole.Shares[0];
            await state.UpdateAsync(s => s.PeerRole.Shares[0].ExpectedDeviceIds = ["shoal-home-1"]);

            Assert.Equal(ShareOutcome.Ok, await service.SetEnabledAsync(share.ShareId, false, default));
            Assert.True(users.Policies[share.ShareUserId].Disabled);
            users.Mint(share.ShareUserId, "shoal-home-1");
            users.Mint(share.ShareUserId, "minted-while-paused");
            users.Calls.Clear();

            Assert.Equal(ShareOutcome.Ok, await service.SetEnabledAsync(share.ShareId, true, default));

            Assert.Equal(["remove-device", "policy"], users.Calls);
            Assert.False(users.Policies[share.ShareUserId].Disabled);
            Assert.Equal(["shoal-home-1"], users.DevicesOf[share.ShareUserId].Select(d => d.DeviceId));
        }
    }

    [Fact]
    public async Task ADashboardEdit_IsRepairedOnTheNextPass()
    {
        var (service, users, _, state, dir, movies) = await CreateAsync();
        using (dir)
        using (state)
        {
            await service.CreateAsync(Request("Bob", movies.Id), default);
            users.Calls.Clear();
            users.DriftNext = true;

            await service.SweepAllAsync(default);

            Assert.Equal(["policy"], users.Calls);
        }
    }

    [Fact]
    public async Task Revoke_NeedsTheTypedName_ThenDisablesSignsOutChecksAndDeletes()
    {
        var (service, users, _, state, dir, movies) = await CreateAsync();
        using (dir)
        using (state)
        {
            await service.CreateAsync(Request("Bob", movies.Id), default);
            var share = state.Current.PeerRole.Shares[0];
            users.Mint(share.ShareUserId, "d1");
            users.Mint(share.ShareUserId, "d2");

            Assert.Equal(ShareOutcome.ConfirmName, (await service.RevokeAsync(share.ShareId, "bob", default)).Outcome);
            Assert.True(users.Exists(share.ShareUserId));

            var (outcome, body) = await service.RevokeAsync(share.ShareId, "Bob", default);

            Assert.Equal(ShareOutcome.Ok, outcome);
            var json = System.Text.Json.JsonSerializer.Serialize(body);
            Assert.Contains("\"signedOut\":2", json, StringComparison.Ordinal);
            Assert.Contains("\"remaining\":0", json, StringComparison.Ordinal);
            Assert.Contains("\"deleted\":true", json, StringComparison.Ordinal);
            Assert.False(users.Exists(share.ShareUserId));
            Assert.Empty(state.Current.PeerRole.Shares);
            Assert.True(users.Calls.IndexOf("policy-disabled") < users.Calls.IndexOf("delete"), "disabled before deleted");
        }
    }

    [Fact]
    public async Task Revoke_RetriesOnce_AndReportsADeviceThatWillNotGo()
    {
        var (service, users, _, state, dir, movies) = await CreateAsync();
        using (dir)
        using (state)
        {
            await service.CreateAsync(Request("Bob", movies.Id), default);
            var share = state.Current.PeerRole.Shares[0];
            users.Mint(share.ShareUserId, "d1");
            users.StubbornDevices = 1;

            var (_, body) = await service.RevokeAsync(share.ShareId, "Bob", default);

            var json = System.Text.Json.JsonSerializer.Serialize(body);
            Assert.Contains("\"remaining\":0", json, StringComparison.Ordinal);

            await service.CreateAsync(Request("Cy", movies.Id), default);
            var second = state.Current.PeerRole.Shares[0];
            users.Mint(second.ShareUserId, "x1");
            users.StubbornDevices = 5;
            var (_, stubborn) = await service.RevokeAsync(second.ShareId, "Cy", default);
            Assert.Contains("\"remaining\":1", System.Text.Json.JsonSerializer.Serialize(stubborn), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ThePolicyOfASharingAccount_TurnsSyncPlayOff_AndNeverLocksTheAccountOut()
    {
        var dto = new MediaBrowser.Model.Users.UserPolicy
        {
            SyncPlayAccess = Jellyfin.Database.Implementations.Enums.SyncPlayUserAccessType.CreateAndJoinGroups,
            LoginAttemptsBeforeLockout = 3,
            EnableAllDevices = true
        };

        JellyfinShareUsers.Fill(dto, new SharePolicy(false, [], null, ["d1"]));

        Assert.Equal(Jellyfin.Database.Implementations.Enums.SyncPlayUserAccessType.None, dto.SyncPlayAccess);
        Assert.Equal(-1, dto.LoginAttemptsBeforeLockout);
        Assert.False(dto.EnableAllDevices);
        Assert.Equal(["d1"], dto.EnabledDevices);
    }

    [Fact]
    public async Task L4_AChangeToASharingAccount_WakesTheSweep_ButOtherUsersDoNot()
    {
        var (service, _, _, state, dir, movies) = await CreateAsync();
        using (dir)
        using (state)
        {
            await service.CreateAsync(Request("Bob", movies.Id), default);
            var share = state.Current.PeerRole.Shares[0];
            using var sweeper = new ShareSweeper(service, null!, null!, NullLogger<ShareSweeper>.Instance);

            sweeper.UserChanged(Guid.NewGuid());
            Assert.False(sweeper.WakePending);
            sweeper.UserChanged(share.ShareUserId);
            sweeper.UserChanged(share.ShareUserId); // a second change while one is due is absorbed
            Assert.True(sweeper.WakePending);
        }
    }

    [Fact]
    public async Task M4_ARevokeThatCannotDeleteTheAccount_StaysPaused_AndTheRepairDoesNotReEnableIt()
    {
        var (service, users, _, state, dir, movies) = await CreateAsync();
        using (dir)
        using (state)
        {
            await service.CreateAsync(Request("Bob", movies.Id), default);
            var share = state.Current.PeerRole.Shares[0];
            users.FailDelete = true;

            var (_, body) = await service.RevokeAsync(share.ShareId, "Bob", default);

            Assert.Contains("\"deleted\":false", System.Text.Json.JsonSerializer.Serialize(body), StringComparison.Ordinal);
            Assert.False(Assert.Single(state.Current.PeerRole.Shares).Enabled);
            await service.SweepAllAsync(default);
            Assert.True(users.Policies[share.ShareUserId].Disabled, "the sweep and repair leave a half-revoked share disabled");
        }
    }

    [Fact]
    public async Task AGoneAccount_ShowsAsAccessRemoved()
    {
        var (service, users, _, state, dir, movies) = await CreateAsync();
        using (dir)
        using (state)
        {
            await service.CreateAsync(Request("Bob", movies.Id), default);
            var share = state.Current.PeerRole.Shares[0];
            users.Policies.Remove(share.ShareUserId);

            Assert.True(service.Live(share.ShareId).AccessRemoved);
            Assert.Contains("AccessRemoved", System.Text.Json.JsonSerializer.Serialize(service.List()), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task K45_ASharingAccount_BlocksTheShoalTag_AndEveryLibraryTag()
    {
        var (service, users, _, state, dir, movies) = await CreateAsync();
        using (dir)
        using (state)
        {
            await state.UpdateAsync(s => s.Home.Libraries.Add(new Jellyfin.Plugin.Shoal.State.ShoalLibrary { LibKey = "0123456789abcdef", Name = "Friends", PathRel = "libs/0123456789abcdef", Placement = LibraryPlacement.Main }));

            await service.CreateAsync(Request("Bob", movies.Id), default);

            var policy = users.Policies[state.Current.PeerRole.Shares[0].ShareUserId];
            Assert.Equal(["Shoal", "shoal-lib-01234567"], policy.BlockedTags);
        }
    }

    [Fact]
    public async Task W10L5_ANewLibrarysTag_ReachesTheSharingAccountAtOnce_NotAtTheNextSweep()
    {
        var (service, users, _, state, dir, movies) = await CreateAsync();
        using (dir)
        using (state)
        {
            await service.CreateAsync(Request("Bob", movies.Id), default);
            var shareUser = state.Current.PeerRole.Shares[0].ShareUserId;
            Assert.Equal(["Shoal"], users.Policies[shareUser].BlockedTags);

            await state.UpdateAsync(s => s.Home.Libraries.Add(new Jellyfin.Plugin.Shoal.State.ShoalLibrary { LibKey = "0123456789abcdef", Name = "Friends", PathRel = "libs/0123456789abcdef", Placement = LibraryPlacement.Main }));
            var tags = new Jellyfin.Plugin.Shoal.Visibility.TagService(state, new FakeAccounts(), new FakeLibraries(), Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance) { SharePolicies = service.RepairPoliciesAsync };
            await tags.ReconcileAsync();
            tags.Dispose();

            Assert.Equal(["Shoal", "shoal-lib-01234567"], users.Policies[shareUser].BlockedTags);
        }
    }

    [Fact]
    public void K45_TheSharePolicy_SetsShoalTags_AndKeepsAnOwnersTag()
    {
        var dto = new MediaBrowser.Model.Users.UserPolicy { BlockedTags = ["Horror", "shoal-lib-deadbeef"] };

        JellyfinShareUsers.Fill(dto, new SharePolicy(false, [], null, ["d1"], ["Shoal", "shoal-lib-01234567"]));

        Assert.Equal(["Horror", "Shoal", "shoal-lib-01234567"], dto.BlockedTags);
    }

    [Fact]
    public async Task K45_AMainLibraryHoldingAShoalFolderOfPlacementMain_StaysShareable_ButAShoalOnlyLibraryIsNot()
    {
        var (service, _, libraries, state, dir, movies) = await CreateAsync();
        using (dir)
        using (state)
        {
            const string Key = "0123456789abcdef";
            await state.UpdateAsync(s => s.Home.Libraries.Add(new Jellyfin.Plugin.Shoal.State.ShoalLibrary { LibKey = Key, Name = "Friends", PathRel = "libs/" + Key, Placement = LibraryPlacement.Main }));
            var folder = Path.Combine(state.Current.StubRoot, "libs", Key);
            await libraries.AddPathAsync("Movies", folder, default);
            await libraries.CreateAsync("Only friends", folder, default);
            var onlyFriends = libraries.List().Single(l => l.Name == "Only friends").Id;

            Assert.Equal(ShareOutcome.Ok, (await service.CreateAsync(Request("Bob", movies.Id), default)).Outcome);
            Assert.Equal(ShareOutcome.Invalid, (await service.CreateAsync(Request("Ann", onlyFriends), default)).Outcome);

            // Any other folder below the stub root next to it still refuses the library.
            await libraries.AddPathAsync("Movies", Path.Combine(state.Current.StubRoot, "elsewhere"), default);
            Assert.Equal(ShareOutcome.Invalid, (await service.CreateAsync(Request("Cid", movies.Id), default)).Outcome);
        }
    }
}
