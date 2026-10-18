using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.State;
using Jellyfin.Plugin.Shoal.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Shoal.Tests;

/// <summary>
/// K41 (PLAN v2.9): <c>state.json</c> schema 3 (libraries, layout, library order, priority tier, auto rank), the in-memory upgrade from schema 2
/// and its validation.
/// </summary>
public sealed class StateSchema3Tests
{
    private static readonly DateTime Paired = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>A state as the 1.2.x build has it: two friends, one with a movie and a TV library, the other with a movie library.</summary>
    private static ShoalState Current()
    {
        var state = new ShoalState();
        StateSamples.Fill(state); // Hops: movies-1 -> "movies", Everyone
        state.Home.Peers[0].Libraries.Add(new LibraryMapping
        {
            MappingKey = "tv-1",
            RemoteName = "Shows",
            CollectionType = "tvshows",
            LocalLibraryName = "Hops TV shows",
            Subfolder = "tv",
            Access = new LibraryAccess { Mode = AccessMode.Selected, SelectedUserIds = { StateSamples.OtherUser } },
        });
        state.Home.Peers.Add(new PeerState
        {
            PeerKey = StateSamples.PeerKeyB,
            DisplayName = "Barley",
            BaseUrl = "https://barley.example.org",
            Network = PeerNetwork.Public,
            Priority = 50,
            Pairing = new PairingInfo { Method = PairingMethod.Invite, PairedUtc = Paired, PairedByUserId = StateSamples.AdminUser },
            Libraries = { new LibraryMapping { MappingKey = "m-b", RemoteName = "Films", LocalLibraryName = "Barley Movies", Subfolder = "movies" } },
        });
        return state;
    }

    /// <summary>The bytes the 1.2.x build writes for <see cref="Current"/>: schema 2, none of the schema 3 members.</summary>
    private static byte[] Schema2Bytes(ShoalState? state = null)
    {
        var node = JsonNode.Parse(StateJson.Serialize(state ?? Current()))!.AsObject();
        node["schema"] = 2;
        var home = node["home"]!.AsObject();
        home.Remove("libraries");
        home.Remove("layout");
        home.Remove("libraryOrder");
        foreach (var peer in home["peers"]!.AsArray())
        {
            var p = peer!.AsObject();
            p.Remove("priorityTier");
            p.Remove("autoRank");
            p.Remove("qualityLimit");
            p.Remove("dismissed");
        }

        return Encoding.UTF8.GetBytes(node.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    }

    private static Dictionary<string, (byte[] Bytes, DateTime Written)> Snapshot(string root) =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).ToDictionary(static f => f, static f => (File.ReadAllBytes(f), File.GetLastWriteTimeUtc(f)), StringComparer.Ordinal);

    private static void AssertSame(Dictionary<string, (byte[] Bytes, DateTime Written)> before, Dictionary<string, (byte[] Bytes, DateTime Written)> after)
    {
        Assert.Equal(before.Keys.Order(StringComparer.Ordinal), after.Keys.Order(StringComparer.Ordinal));
        foreach (var (path, value) in before)
        {
            Assert.Equal(value.Bytes, after[path].Bytes);
            Assert.Equal(value.Written, after[path].Written);
        }
    }

    [Fact]
    public void TheSchema2Sample_HasNoneOfTheNewMembers()
    {
        var text = Encoding.UTF8.GetString(Schema2Bytes());
        Assert.Contains("\"schema\": 2", text, StringComparison.Ordinal);
        foreach (var member in new[] { "libraryOrder", "layout", "priorityTier", "autoRank", "qualityLimit", "dismissed", "pathRel" })
        {
            Assert.DoesNotContain("\"" + member + "\"", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Upgrade_MapsEveryFriendLibrary_ToOneLibraryWithOneFeed_AndItsOwnFolder()
    {
        var state = StateJson.Deserialize(Schema2Bytes());
        var notes = StateUpgrade.Upgrade(state);

        Assert.Equal(3, state.Schema);
        Assert.Single(notes, static n => n.Contains("3 friend libraries", StringComparison.Ordinal));
        Assert.Equal(LayoutPreset.PerFriend, state.Home.Layout.Default);
        Assert.Empty(state.Home.Layout.DefaultTargets);
        var libraries = state.Home.Libraries;
        Assert.Equal(3, libraries.Count);
        Assert.Equal(libraries.Select(static l => l.LibKey), state.Home.LibraryOrder);

        var movies = libraries[0];
        Assert.Equal(StateUpgrade.LegacyLibKey(StateSamples.PeerKeyA, "movies-1"), movies.LibKey);
        Assert.Matches("^[0-9a-f]{16}$", movies.LibKey);
        Assert.Equal("Hops Movies", movies.Name);
        Assert.Equal("movies", movies.Type);
        Assert.Equal(LibraryPlacement.Shoal, movies.Placement);
        Assert.Null(movies.JellyfinLibraryId);
        Assert.Equal([StateSamples.PeerKeyA], movies.Feeds);
        Assert.Equal(StateSamples.PeerKeyA + "/movies", movies.PathRel);
        Assert.True(movies.IsLegacy);

        var tv = libraries[1];
        Assert.Equal("tvshows", tv.Type);
        Assert.Equal(StateSamples.PeerKeyA + "/tv", tv.PathRel);
        Assert.Equal(AccessMode.Selected, tv.Access.Mode);
        Assert.Equal([StateSamples.OtherUser], tv.Access.SelectedUserIds);

        Assert.Equal([StateSamples.PeerKeyB], libraries[2].Feeds);
        Assert.Equal(StateSamples.PeerKeyB + "/movies", libraries[2].PathRel);

        Assert.All(state.Home.Peers, static p =>
        {
            Assert.Equal(PriorityTier.Normal, p.PriorityTier);
            Assert.Equal(0, p.AutoRank.Score);
            Assert.Null(p.AutoRank.PendingChallenger);
            Assert.Equal(QualityClass.Off, p.QualityLimit.MaxClass);
        });
    }

    [Fact]
    public void Upgrade_LeavesAccessUnchanged_AndCopiesIt()
    {
        var state = StateJson.Deserialize(Schema2Bytes());
        StateUpgrade.Upgrade(state);
        var mapping = state.Home.Peers[0].Libraries[0];
        var library = state.Home.Libraries[0];

        Assert.Equal(AccessMode.Everyone, mapping.Access.Mode);
        Assert.Equal(mapping.Access.Mode, library.Access.Mode);
        Assert.Equal(mapping.Access.SelectedUserIds, library.Access.SelectedUserIds);
        Assert.Equal(mapping.Access.IncludeRestricted, library.Access.IncludeRestricted);
        Assert.Equal(mapping.Access.ChangedUtc, library.Access.ChangedUtc);
        Assert.Equal(mapping.Access.ChangedByUserId, library.Access.ChangedByUserId);
        Assert.NotSame(mapping.Access, library.Access);
        Assert.NotSame(mapping.Access.SelectedUserIds, library.Access.SelectedUserIds);
    }

    [Fact]
    public void Upgrade_IsDeterministic_AndLeavesASchema3StateAlone()
    {
        var first = StateJson.Deserialize(Schema2Bytes());
        var second = StateJson.Deserialize(Schema2Bytes());
        StateUpgrade.Upgrade(first);
        StateUpgrade.Upgrade(second);
        Assert.Equal(first.Home.Libraries.Select(static l => l.LibKey), second.Home.Libraries.Select(static l => l.LibKey));

        first.Home.Layout.Default = LayoutPreset.Custom;
        Assert.Empty(StateUpgrade.Upgrade(first));
        Assert.Equal(LayoutPreset.Custom, first.Home.Layout.Default);
    }

    [Fact]
    public void Upgrade_SkipsAMappingOfAnotherType_WithoutRefusingTheFile()
    {
        var old = Current();
        old.Home.Peers[1].Libraries.Add(new LibraryMapping { MappingKey = "odd", CollectionType = "music", LocalLibraryName = "Odd", Subfolder = "music" });
        var state = StateJson.Deserialize(Schema2Bytes(old));
        var notes = new List<string>(StateUpgrade.Upgrade(state));

        Assert.Null(StateValidator.Validate(state, notes));
        Assert.Equal(3, state.Home.Libraries.Count);
        Assert.Contains(notes, static n => n.Contains("other than movies or TV shows", StringComparison.Ordinal));
        Assert.Equal(2, state.Home.Peers[1].Libraries.Count); // the mapping itself stays
    }

    [Fact]
    public async Task LoadingASchema2File_ChangesZeroFiles_AndGivesTheUpgradedState()
    {
        using var dir = new TempDirectory();
        var paths = new ShoalPaths(dir.Path);
        SecureDirectory.Ensure(paths.Root);
        SecureDirectory.Ensure(paths.BackupsDir);
        var bytes = Schema2Bytes();
        File.WriteAllBytes(paths.StateFile, bytes);
        File.WriteAllBytes(paths.StateBackup, bytes);
        File.WriteAllBytes(Path.Combine(paths.BackupsDir, "state-20261017.json"), bytes);

        // The stub tree of a legacy install: per friend, per library.
        var stubs = dir.Combine("stubs");
        foreach (var rel in new[] { StateSamples.PeerKeyA + "/movies/A (2000)/A (2000).strm", StateSamples.PeerKeyA + "/movies/A (2000)/A (2000).nfo", StateSamples.PeerKeyA + "/tv/Show/Season 01/S01E01.strm", StateSamples.PeerKeyB + "/movies/B (2001)/B (2001).strm" })
        {
            var file = Path.Combine(stubs, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, "x");
        }

        var before = Snapshot(dir.Path);
        await Task.Delay(20); // a write would show in the timestamps too

        using (var store = new StateStore(paths, NullLogger.Instance, TimeProvider.System, null))
        {
            Assert.Equal(StateStatus.Ready, store.Status);
            Assert.Equal(StateSource.Primary, store.LoadInfo.Source);
            Assert.False(store.LoadInfo.AccessReviewRequired);
            Assert.Equal(3, store.Current.Schema);
            Assert.Equal(3, store.Current.Home.Libraries.Count);
            Assert.Equal(LayoutPreset.PerFriend, store.Current.Home.Layout.Default);
            Assert.Contains(store.LoadInfo.Notes, static n => n.Contains("upgraded to schema 3", StringComparison.Ordinal));
        }

        using (var again = new StateStore(paths, NullLogger.Instance, TimeProvider.System, null))
        {
            Assert.Equal(StateStatus.Ready, again.Status); // the second load upgrades the same file the same way
        }

        AssertSame(before, Snapshot(dir.Path));
    }

    [Fact]
    public async Task TheFirstChangeAfterTheUpgrade_WritesSchema3_AndTheReloadNeedsNoUpgrade()
    {
        using var dir = new TempDirectory();
        var paths = new ShoalPaths(dir.Path);
        SecureDirectory.Ensure(paths.Root);
        File.WriteAllBytes(paths.StateFile, Schema2Bytes());
        IReadOnlyList<string> keys;
        using (var store = new StateStore(paths, NullLogger.Instance, TimeProvider.System, null))
        {
            keys = [.. store.Current.Home.Libraries.Select(static l => l.LibKey)];
            await store.UpdateAsync(static s => s.Home.Peers[1].PriorityTier = PriorityTier.Backup);
        }

        Assert.Equal(3, StateJson.PeekSchema(File.ReadAllBytes(paths.StateFile)));
        using var reopened = new StateStore(paths, NullLogger.Instance, TimeProvider.System, null);
        Assert.Equal(StateStatus.Ready, reopened.Status);
        Assert.DoesNotContain(reopened.LoadInfo.Notes, static n => n.Contains("upgraded", StringComparison.Ordinal));
        Assert.Equal(keys, reopened.Current.Home.Libraries.Select(static l => l.LibKey));
        Assert.Equal(PriorityTier.Backup, reopened.Current.Home.Peers[1].PriorityTier);
        Assert.Equal(LayoutPreset.PerFriend, reopened.Current.Home.Layout.Default);
    }

    [Fact]
    public void ANewInstall_StartsAllMixed_WithNoLibraries()
    {
        using var dir = new TempDirectory();
        using var store = new StateStore(new ShoalPaths(dir.Path), NullLogger.Instance, TimeProvider.System, null);

        Assert.Equal(StateSource.Fresh, store.LoadInfo.Source);
        Assert.Equal(LayoutPreset.AllMixed, store.Current.Home.Layout.Default);
        Assert.Empty(store.Current.Home.Libraries);
        Assert.Empty(store.Current.Home.LibraryOrder);
    }

    // --- validation

    private static ShoalState Upgraded()
    {
        var state = StateJson.Deserialize(Schema2Bytes());
        StateUpgrade.Upgrade(state);
        return state;
    }

    private static ShoalLibrary NewLibrary(string libKey, string type = "movies", params string[] feeds) => new()
    {
        LibKey = libKey,
        Name = "Friends " + type,
        Type = type,
        Feeds = [.. feeds],
        PathRel = ShoalLibrary.LibsFolder + "/" + libKey,
    };

    [Fact]
    public void TheUpgradedState_IsValid_WithoutNotes()
    {
        var state = Upgraded();
        var notes = new List<string>();
        Assert.Null(StateValidator.Validate(state, notes));
        Assert.Empty(notes);
    }

    [Theory]
    [InlineData("ABCDEF0123456789")]
    [InlineData("abcdef012345678")]
    [InlineData("../../etc/passwd")]
    [InlineData("")]
    public void ABadLibraryKey_RefusesTheFile(string libKey)
    {
        var state = Upgraded();
        state.Home.Libraries.Add(new ShoalLibrary { LibKey = libKey, Type = "movies", PathRel = "libs/" + libKey });
        Assert.Equal("a library key is not valid", StateValidator.Validate(state, new List<string>()));
    }

    [Theory]
    [InlineData("libs/../x")]
    [InlineData("libs/0123456789abcdee")]
    [InlineData("../outside/movies")]
    [InlineData("libs")]
    [InlineData("libs/0123456789abcdef/sub")]
    public void ALibraryFolderThatIsNotItsOwn_RefusesTheFile(string pathRel)
    {
        var state = Upgraded();
        state.Home.Libraries.Add(new ShoalLibrary { LibKey = "0123456789abcdef", Type = "movies", PathRel = pathRel });
        Assert.Equal("a library folder is not valid", StateValidator.Validate(state, new List<string>()));
    }

    [Fact]
    public void ADuplicateKey_OrABadType_RefusesTheFile()
    {
        var dup = Upgraded();
        dup.Home.Libraries.Add(NewLibrary("0123456789abcdef"));
        dup.Home.Libraries.Add(NewLibrary("0123456789abcdef"));
        Assert.Equal("a library key is used twice", StateValidator.Validate(dup, new List<string>()));

        var type = Upgraded();
        type.Home.Libraries.Add(NewLibrary("0123456789abcdef", "music"));
        Assert.Equal("a library type is not valid", StateValidator.Validate(type, new List<string>()));
    }

    [Fact]
    public void MoreThan50NewLibraries_RefusesTheFile_ButLegacyOnesDoNotCount()
    {
        var state = Upgraded();
        for (var i = 0; i < StateValidator.MaxLibraries; i++)
        {
            state.Home.Libraries.Add(NewLibrary(i.ToString("x16", System.Globalization.CultureInfo.InvariantCulture)));
        }

        Assert.Null(StateValidator.Validate(state, new List<string>())); // 50 new + 3 legacy

        state.Home.Libraries.Add(NewLibrary("ffffffffffffffff"));
        Assert.Equal("there are too many libraries", StateValidator.Validate(state, new List<string>()));
    }

    [Fact]
    public void AConnection_FeedsAtMostOneNewLibraryPerType_AndUnknownFeedsAreDropped()
    {
        var state = Upgraded();
        state.Home.Libraries.Add(NewLibrary("aaaaaaaaaaaaaaaa", "movies", StateSamples.PeerKeyA, StateSamples.PeerKeyB, StateSamples.PeerKeyA));
        state.Home.Libraries.Add(NewLibrary("bbbbbbbbbbbbbbbb", "movies", StateSamples.PeerKeyB, "ffffffffffffffffffffffffffffffff"));
        state.Home.Libraries.Add(NewLibrary("cccccccccccccccc", "tvshows", StateSamples.PeerKeyB));
        var notes = new List<string>();

        Assert.Null(StateValidator.Validate(state, notes));

        var byKey = state.Home.Libraries.ToDictionary(static l => l.LibKey);
        Assert.Equal([StateSamples.PeerKeyA, StateSamples.PeerKeyB], byKey["aaaaaaaaaaaaaaaa"].Feeds);
        Assert.Empty(byKey["bbbbbbbbbbbbbbbb"].Feeds);
        Assert.Equal([StateSamples.PeerKeyB], byKey["cccccccccccccccc"].Feeds); // another type is fine
        Assert.Contains(notes, static n => n.Contains("feed", StringComparison.Ordinal));
    }

    [Fact]
    public void ALegacyLibrary_IsFedOnlyByItsOwnServer()
    {
        var state = Upgraded();
        state.Home.Libraries[0].Feeds.Add(StateSamples.PeerKeyB);
        Assert.Null(StateValidator.Validate(state, new List<string>()));
        Assert.Equal([StateSamples.PeerKeyA], state.Home.Libraries[0].Feeds);
    }

    [Fact]
    public async Task RemovingAServer_StillWorks_AndTakesItsLegacyLibrariesAlong()
    {
        using var dir = new TempDirectory();
        var paths = new ShoalPaths(dir.Path);
        SecureDirectory.Ensure(paths.Root);
        File.WriteAllBytes(paths.StateFile, Schema2Bytes());
        using var store = new StateStore(paths, NullLogger.Instance, TimeProvider.System, null);

        var after = await store.UpdateAsync(static s => s.Home.Peers.RemoveAll(static p => p.PeerKey == StateSamples.PeerKeyA));

        Assert.Equal([StateSamples.PeerKeyB + "/movies"], after.Home.Libraries.Select(static l => l.PathRel));
        Assert.Equal(after.Home.Libraries.Select(static l => l.LibKey), after.Home.LibraryOrder);

        var unmapped = await store.UpdateAsync(static s => s.Home.Peers[0].Libraries.Clear());
        Assert.Empty(unmapped.Home.Libraries);
    }

    [Fact]
    public void TheOrder_IsRepaired_AndADefaultTargetThatIsGoneIsCleared()
    {
        var state = Upgraded();
        var keys = state.Home.Libraries.Select(static l => l.LibKey).ToList();
        state.Home.Libraries.Add(NewLibrary("dddddddddddddddd", "tvshows"));
        state.Home.LibraryOrder = [keys[2], "eeeeeeeeeeeeeeee", keys[2], keys[0]];
        state.Home.Layout.DefaultTargets["movies"] = "eeeeeeeeeeeeeeee"; // gone
        state.Home.Layout.DefaultTargets["tvshows"] = "dddddddddddddddd";
        state.Home.Layout.DefaultTargets["music"] = "dddddddddddddddd";
        var notes = new List<string>();

        Assert.Null(StateValidator.Validate(state, notes));

        Assert.Equal([keys[2], keys[0], keys[1], "dddddddddddddddd"], state.Home.LibraryOrder);
        Assert.Equal(new Dictionary<string, string> { ["tvshows"] = "dddddddddddddddd" }, state.Home.Layout.DefaultTargets);
    }

    [Fact]
    public void AutoRank_IsClamped_AndAChallengerThatIsItselfOrGoneIsCleared()
    {
        var state = Upgraded();
        state.Home.Peers[0].AutoRank = new AutoRank { Score = double.NaN, Streak = 5000, PendingChallenger = StateSamples.PeerKeyA };
        state.Home.Peers[1].AutoRank = new AutoRank { Score = 42.5, Streak = 1, PendingChallenger = StateSamples.PeerKeyA };

        Assert.Null(StateValidator.Validate(state, new List<string>()));

        Assert.Equal(0, state.Home.Peers[0].AutoRank.Score);
        Assert.Null(state.Home.Peers[0].AutoRank.PendingChallenger);
        Assert.Equal(0, state.Home.Peers[0].AutoRank.Streak);
        Assert.Equal(StateSamples.PeerKeyA, state.Home.Peers[1].AutoRank.PendingChallenger);
        Assert.Equal(42.5, state.Home.Peers[1].AutoRank.Score);
    }

    [Fact]
    public void TheNewMembers_AreWrittenWithPlainNames()
    {
        var state = Upgraded();
        state.Home.Libraries.Add(new ShoalLibrary { LibKey = "aaaaaaaaaaaaaaaa", Type = "movies", Placement = LibraryPlacement.Main, PathRel = "libs/aaaaaaaaaaaaaaaa" });
        var text = Encoding.UTF8.GetString(StateJson.Serialize(state));

        Assert.Contains("\"placement\": \"Main\"", text, StringComparison.Ordinal);
        Assert.Contains("\"default\": \"PerFriend\"", text, StringComparison.Ordinal);
        Assert.Contains("\"priorityTier\": \"Normal\"", text, StringComparison.Ordinal);
        Assert.Contains("\"pathRel\": \"libs/aaaaaaaaaaaaaaaa\"", text, StringComparison.Ordinal);
        Assert.DoesNotContain("isLegacy", text, StringComparison.Ordinal);
    }
}
