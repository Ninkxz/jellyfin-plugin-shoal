using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.Api.Admin;
using Jellyfin.Plugin.Shoal.State;
using Jellyfin.Plugin.Shoal.Storage;
using Jellyfin.Plugin.Shoal.Visibility;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Shoal.Tests;

/// <summary>
/// Pilot 15 retest defects: a move into an existing library scans it (found by folder), the inbox re-post replaces Moonbase's message in place, a rename Jellyfin
/// does not show is rolled back, the NFO warning names the target main library by folder, and options are locked again right after a change.
/// </summary>
public sealed class Pilot15FixTests
{
    private static void Seed(FakeLibraryPeer p)
    {
        p.Add("Alien", 1979, tmdb: 348);
        p.Add("Heat", 1995, tmdb: 949);
    }

    [Fact]
    public async Task R2MOVE_AMoveIntoAnExistingLibrary_ScansThatLibraryByItsFolder_AndLocksOptionsAgain()
    {
        await using var rig = await JobRig.CreateAsync(Seed);
        await rig.State.UpdateAsync(s => s.Home.Peers.Add(new PeerState { PeerKey = StateSamples.PeerKeyB, DisplayName = "Barley", Libraries = { new LibraryMapping { MappingKey = "m", RemoteName = "M", Subfolder = "m" } } }));
        var key = await rig.NewLibraryAsync("Friend B movies", StateSamples.PeerKeyB);
        await rig.State.UpdateAsync(s => s.Home.Libraries.First(l => l.LibKey == key).JellyfinLibraryId = null); // what Pilot 15 had: no id stored
        rig.Job.Options = new LibraryOptionsGuard(rig.State, rig.Libraries, NullLogger.Instance);
        rig.Libraries.Unlocked.Add("Friend B movies");
        rig.Sync.Refresh.LibraryScans.Clear();

        Assert.Equal(LayoutJobError.None, await rig.Job.StartAsync([new MoveRequest(SyncRig.PeerKey, "movies", key)], rig.Admin.Id));
        await rig.Job.Running;

        Assert.Contains(rig.Sync.Refresh.LibraryScans, f => LibraryFolders.SamePath(f, rig.LibFolder(key)));
        Assert.Contains("Friend B movies", rig.Libraries.Locked);
        Assert.Empty(rig.Libraries.Unlocked);
    }

    [Fact]
    public void R4REPOST_ThePostBody_CarriesTheIdInMoonbasesOwnForm()
    {
        var id = Guid.Parse("18ed3f59-0000-4000-8000-0000000000aa");
        var user = Guid.Parse("3f2504e0-4f89-41d3-9a0c-0305e82c3301");

        using var replace = JsonDocument.Parse(LoopbackMoonbaseClient.PostPayload(new MoonbasePost(id, "T", "B", [user])));
        using var fresh = JsonDocument.Parse(LoopbackMoonbaseClient.PostPayload(new MoonbasePost(null, "T", "B", [user])));

        Assert.Equal(id.ToString("N"), replace.RootElement.GetProperty("id").GetString());
        Assert.Equal("users", replace.RootElement.GetProperty("audience").GetString());
        Assert.Equal(user.ToString("N"), replace.RootElement.GetProperty("targetUserIds")[0].GetString());
        Assert.False(fresh.RootElement.TryGetProperty("id", out _)); // a new message: no id, Moonbase makes one
    }

    [Fact]
    public async Task R4REPOST_ANewAccount_RePostReplacesTheMessageInPlace_NeverASecondOne()
    {
        using var dir = new TempDirectory();
        using var state = new StateStore(new ShoalPaths(dir.Path), NullLogger.Instance, TimeProvider.System, null);
        var accounts = new FakeAccounts();
        accounts.Add("admin", admin: true);
        accounts.Add("alice");
        var moonbase = new MoonbaseSim();
        var inbox = new InboxExplainerService(state, accounts, moonbase, TimeProvider.System);

        var (_, first) = await inbox.PostAsync("t", default);
        var dave = accounts.Add("dave");
        var (card, second) = await inbox.PostAsync("t", default);

        Assert.True(first);
        Assert.True(second);
        var message = Assert.Single(moonbase.Messages);
        Assert.Contains(dave.Id.ToString("N"), message.TargetUserIds);
        Assert.Equal(3, message.TargetUserIds.Count);
        Assert.Equal(Guid.Parse(message.Id), state.Current.Home.Inbox.ExplainerMessageId);
        Assert.Equal(0, card.MissingRecipients);
    }

    [Fact]
    public async Task R4REPOST_WithNoStoredId_TheMessageIsFoundByTitle_AndReplaced()
    {
        using var dir = new TempDirectory();
        using var state = new StateStore(new ShoalPaths(dir.Path), NullLogger.Instance, TimeProvider.System, null);
        var accounts = new FakeAccounts();
        accounts.Add("admin", admin: true);
        var moonbase = new MoonbaseSim();
        moonbase.Messages.Add(new SimMessage { Id = Guid.NewGuid().ToString("N"), Title = InboxExplainerService.Title, Audience = "users", TargetUserIds = [] });
        var inbox = new InboxExplainerService(state, accounts, moonbase, TimeProvider.System);

        var (_, sent) = await inbox.PostAsync("t", default);

        Assert.True(sent);
        Assert.Single(moonbase.Messages);
        Assert.Single(moonbase.Messages[0].TargetUserIds);
    }

    [Fact]
    public async Task R5RENAME_ARenameJellyfinDoesNotShow_IsRolledBack_SoBothKeepTheOldName()
    {
        using var rig = new LayoutRig();
        var key = (await rig.Layout.CreateAsync(new LibraryCreateRequest("Friend B movies", "movies", LibraryPlacement.Shoal), rig.Admin.Id)).LibKey!;
        rig.Libraries.HideRenamed = true;
        rig.Libraries.RefreshShows = false;

        Assert.Equal(LayoutError.Failed, (await rig.Layout.RenameAsync(key, "Friends mix")).Error);

        Assert.Equal("Friend B movies", rig.Library(key).Name);
        Assert.Contains(rig.Libraries.All, l => l.Name == "Friend B movies");
        Assert.DoesNotContain(rig.Libraries.All, l => l.Name == "Friends mix");
        Assert.Equal(["rename:Friend B movies>Friends mix", "rename:Friends mix>Friend B movies"], rig.Libraries.Operations.Where(static o => o.StartsWith("rename:", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task R5RENAME_ALibraryWithoutAnIdYet_IsFoundAfterARefresh_AndBothGetTheNewName()
    {
        using var rig = new LayoutRig();
        var key = (await rig.Layout.CreateAsync(new LibraryCreateRequest("Friend B movies", "movies", LibraryPlacement.Shoal), rig.Admin.Id)).LibKey!;
        rig.Libraries.Hidden.Add("Friend B movies");

        Assert.Equal(LayoutError.None, (await rig.Layout.RenameAsync(key, "Friends mix")).Error);

        Assert.Equal("Friends mix", rig.Library(key).Name);
        Assert.Contains(rig.Libraries.List(), l => l.Name == "Friends mix" && l.Id == rig.Library(key).JellyfinLibraryId);
    }

    [Fact]
    public async Task R5RENAME_ALibraryJellyfinNeverLists_IsNotRenamedInShoalAlone()
    {
        using var rig = new LayoutRig();
        var key = (await rig.Layout.CreateAsync(new LibraryCreateRequest("Friend B movies", "movies", LibraryPlacement.Shoal), rig.Admin.Id)).LibKey!;
        rig.Libraries.Hidden.Add("Friend B movies");
        rig.Libraries.RefreshShows = false;

        Assert.Equal(LayoutError.Failed, (await rig.Layout.RenameAsync(key, "Friends mix 2")).Error);

        Assert.Equal("Friend B movies", rig.Library(key).Name);
        Assert.DoesNotContain(rig.Libraries.Operations, static o => o.StartsWith("rename:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task R6MAINWARN_TheTargetMainLibrary_IsFoundByTheShoalFolder_EvenWithAStaleId()
    {
        using var rig = new LayoutRig();
        var key = (await rig.Layout.CreateAsync(new LibraryCreateRequest("Friends in Movies", "movies", LibraryPlacement.Main, rig.Main.Id), rig.Admin.Id)).LibKey!;
        await rig.State.UpdateAsync(s => s.Home.Libraries.First(l => l.LibKey == key).JellyfinLibraryId = Guid.NewGuid());
        rig.Libraries.SetSavesNfo(rig.Main.Id, true);
        var guard = new LibraryOptionsGuard(rig.State, rig.Libraries, NullLogger.Instance);

        Assert.Equal([rig.Main.Name], guard.MainLibrariesSavingNfo());
    }

    [Fact]
    public async Task R6_AShoalLibraryWithoutAStoredId_IsLockedAgain_ByItsFolder()
    {
        using var rig = new LayoutRig();
        var key = (await rig.Layout.CreateAsync(new LibraryCreateRequest("Friends' movies", "movies", LibraryPlacement.Shoal), rig.Admin.Id)).LibKey!;
        await rig.State.UpdateAsync(s => s.Home.Libraries.First(l => l.LibKey == key).JellyfinLibraryId = null);
        rig.Libraries.Unlocked.Add("Friends' movies");

        Assert.Equal(1, new LibraryOptionsGuard(rig.State, rig.Libraries, NullLogger.Instance).Enforce());
    }

    [Fact]
    public async Task R6_SavedOptions_AreLockedAgainAfterAShortDelay_ManySavesMakeOnePass()
    {
        var passes = 0;
        using var watch = new LibraryOptionsWatch(() => Interlocked.Increment(ref passes), NullLogger.Instance, TimeProvider.System, TimeSpan.FromMilliseconds(300));

        watch.OnChanged();
        var running = watch.Running;
        watch.OnChanged();
        watch.OnChanged();
        Assert.Equal(0, passes);

        await running;
        Assert.Equal(1, passes);

        watch.OnChanged(); // the pass's own saves: one more pass, which finds nothing
        await watch.Running;
        Assert.Equal(2, passes);
    }

    // Moonbase 2.4.0's SaveMessage, decompiled: string ids, compared ordinal ignore-case; no match makes a new message with a 32-hex id. The answers use the
    // shapes Pilot 13 S1/S4 saw ({ success, item { Id, ... } } and { items: [ { Id, ... } ] }, PascalCase).
    private sealed class MoonbaseSim : IMoonbaseClient
    {
        private static readonly JsonSerializerOptions Read = new(JsonSerializerDefaults.Web);

        public List<SimMessage> Messages { get; } = [];

        public Task<(MoonbaseOutcome Outcome, IReadOnlyList<MoonbaseMessage> Items)> ListAsync(string callerToken, CancellationToken cancellationToken)
        {
            var body = JsonSerializer.SerializeToUtf8Bytes(new { items = Messages });
            return Task.FromResult(LoopbackMoonbaseClient.ParseList(body));
        }

        public Task<(MoonbaseOutcome Outcome, Guid? Id)> PostAsync(string callerToken, MoonbasePost message, CancellationToken cancellationToken)
        {
            var sent = JsonSerializer.Deserialize<SimMessage>(LoopbackMoonbaseClient.PostPayload(message), Read)!;
            var existing = Messages.FirstOrDefault(m => string.Equals(m.Id, sent.Id, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                Messages[Messages.IndexOf(existing)] = sent;
            }
            else
            {
                sent.Id = Guid.NewGuid().ToString("N");
                Messages.Insert(0, sent);
            }

            var answer = JsonSerializer.SerializeToUtf8Bytes(new { success = true, item = sent });
            return Task.FromResult<(MoonbaseOutcome, Guid?)>((MoonbaseOutcome.Ok, LoopbackMoonbaseClient.ParsePostId(answer, message.Id)));
        }
    }

    private sealed class SimMessage
    {
        public string Id { get; set; } = string.Empty;

        public string Title { get; set; } = string.Empty;

        public string Body { get; set; } = string.Empty;

        public string Audience { get; set; } = "all";

        public List<string> TargetUserIds { get; set; } = [];
    }
}
