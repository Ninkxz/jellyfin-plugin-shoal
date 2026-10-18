using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.Authorization;
using Jellyfin.Plugin.Shoal.Diagnostics;
using Jellyfin.Plugin.Shoal.Peers;
using Jellyfin.Plugin.Shoal.Playback;
using Jellyfin.Plugin.Shoal.State;
using Jellyfin.Plugin.Shoal.Storage;
using Jellyfin.Plugin.Shoal.Sync;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Shoal.Tests;

/// <summary>
/// PLAN v2.9 K43: a series another (higher-ranked) server shows is grouped: episodes that overlap that server's (the owner's numbering wins) are hidden sources,
/// the other episodes join the series in a shared library (series grouping) or stay hidden in another folder, and a play of the owner's episode can go to a
/// member's hidden source of exactly the same episode.
/// </summary>
public sealed class TvGroupTests
{
    private static readonly Guid SeriesId = Guid.Parse("a0000000000000000000000000000001");

    private static TvPlanner Planner() => new(new TvLayout());

    private static Guid Ep(int n) => Guid.Parse("b" + n.ToString("D31", System.Globalization.CultureInfo.InvariantCulture));

    private static SanitizedSeries Series() => new() { PeerItemId = SeriesId, Title = "Show", Year = 2008, ProviderIds = new CanonicalIds(null, 81189, null), Etag = "e", ContentHash = "h" };

    private static SanitizedEpisode Episode(int season, int number, int? end = null, int id = 0) =>
        new() { PeerItemId = Ep(id == 0 ? (season * 100) + number : id), PeerSeriesId = SeriesId, Season = season, Number = number, NumberEnd = end, Etag = "e", ContentHash = "h" };

    private static TvOwnedSeries Owned(bool sameFolder, params (int Season, int Number, int End)[] episodes) =>
        new("ownerpeer", "ownerseries00001", sameFolder, [.. episodes.Select(static (e, i) => new TvOwnedEpisode(e.Season, e.Number, e.End, "ownerep" + i.ToString("D9", System.Globalization.CultureInfo.InvariantCulture)))]);

    private static TvPlanInput Input(TvOwnedSeries owned, IReadOnlyList<CatalogEntry> existing, params SanitizedEpisode[] episodes) =>
        new("tv-1", existing, [Series()], [], episodes, true, true, new HashSet<string>(), new TvGuardSettings(25, 10, 500, 20), true, true, PeerOwned: new Dictionary<string, TvOwnedSeries> { ["tvdb:81189"] = owned });

    [Fact]
    public void InASharedLibrary_OverlappingEpisodesAreHiddenSources_AndTheOthersJoinTheSeries()
    {
        var owned = Owned(true, (1, 1, 1), (1, 2, 3));

        var plan = Planner().Plan(Input(owned, [], Episode(1, 1), Episode(1, 3), Episode(1, 4), Episode(2, 1)));

        Assert.Equal([(1, 4), (2, 1)], plan.AddEpisodes.Select(static e => (e.Season, e.Number)).OrderBy(static x => x));
        Assert.Equal([(1, 1, "ownerep000000000"), (1, 3, "ownerep000000001")], plan.HiddenEpisodes!.Select(static h => (h.Episode.Season, h.Episode.Number, h.DuplicateOf!)).OrderBy(static x => x)); // E03 overlaps the owner's E02-E03
        Assert.Equal("ownerseries00001", plan.PeerOwnedSeries![SeriesId.ToString("N")]);
        Assert.Empty(plan.DuplicateLocalSeriesKeys);
    }

    [Fact]
    public void InAnotherFolder_EveryEpisodeStaysHidden_AndOnlyOverlapsCanStandIn()
    {
        var plan = Planner().Plan(Input(Owned(false, (1, 1, 1)), [], Episode(1, 1), Episode(1, 2)));

        Assert.Empty(plan.AddEpisodes);
        Assert.Equal(2, plan.HiddenEpisodes!.Count);
        Assert.Equal("ownerep000000000", plan.HiddenEpisodes.Single(h => h.Episode.Number == 1).DuplicateOf);
        Assert.Null(plan.HiddenEpisodes.Single(h => h.Episode.Number == 2).DuplicateOf);
    }

    [Fact]
    public void AnExistingStubThatNowOverlapsTheOwner_Goes_LikeADemotedTitle()
    {
        var series = new CatalogEntry { Kind = EntryKind.Series, StubKey = "myseries00000001", PeerItemId = SeriesId.ToString("N"), MappingKey = "tv-1", State = EntryState.Active, FolderRel = "Show (2008)", PeerEtag = "e", ContentHash = "h", LabelVersion = FriendLabel.Version };
        var mine = new CatalogEntry { Kind = EntryKind.Episode, StubKey = "myepisode0000001", ParentKey = series.StubKey, PeerItemId = Ep(101).ToString("N"), PeerSeriesId = SeriesId.ToString("N"), MappingKey = "tv-1", Season = 1, Episode = 1, State = EntryState.Active, StubRelPath = "Show (2008)/Season 01/S01E01.strm", PeerEtag = "e", ContentHash = "h", LabelVersion = FriendLabel.Version };

        var plan = Planner().Plan(Input(Owned(true, (1, 1, 1)), [series, mine], Episode(1, 1)));

        Assert.Contains("myepisode0000001", plan.RemoveKeys);
        Assert.Single(plan.HiddenEpisodes!);
    }

    [Fact]
    public void ADuplicatePeerHiddenRecord_DoesNotBlockTheEpisodeFromJoining_WhenTheOwnerLosesIt()
    {
        var series = new CatalogEntry { Kind = EntryKind.Series, StubKey = "myseries00000001", PeerItemId = SeriesId.ToString("N"), MappingKey = "tv-1", State = EntryState.DuplicatePeer, DuplicateOf = "ownerseries00001" };
        var hidden = new CatalogEntry { Kind = EntryKind.Episode, StubKey = "hidden0000000001", ParentKey = series.StubKey, PeerItemId = Ep(101).ToString("N"), PeerSeriesId = SeriesId.ToString("N"), MappingKey = "tv-1", Season = 1, Episode = 1, State = EntryState.DuplicatePeer };

        var plan = Planner().Plan(Input(Owned(true), [series, hidden], Episode(1, 1))); // the owner no longer has S01E01

        Assert.Single(plan.AddEpisodes);
        Assert.Empty(plan.HiddenEpisodes!);
    }

    [Fact]
    public void TheGroupPlanner_MakesASeriesGroup_FromAMembersLink_AndEndsItWhenTheOwnersFolderGoes()
    {
        var document = new GroupsDocument();
        var ownerSeries = new CatalogEntry { Kind = EntryKind.Series, StubKey = "ownerseries00001", PeerItemId = new string('1', 32), MappingKey = "tv-1", State = EntryState.Active, FolderRel = "Show (2008)" };
        var memberSeries = new CatalogEntry { Kind = EntryKind.Series, StubKey = "memberseries0001", PeerItemId = new string('2', 32), MappingKey = "tv-1", State = EntryState.DuplicatePeer, DuplicateOf = "ownerseries00001" };
        var known = new HashSet<string> { "ownerpeer", "memberpeer" };

        Assert.True(GroupPlanner.ApplyTvPeer(document, "memberpeer", [memberSeries], static _ => "lib1", k => k == ownerSeries.StubKey ? new OwnedCopy("ownerpeer", ownerSeries, "lib0") : null, known, static () => "grp0000000000001"));
        var group = Assert.Single(document.Groups);
        Assert.Equal(GroupKind.Series, group.Kind);
        Assert.Equal("ownerpeer", group.MetaOwner);
        Assert.Equal(["ownerpeer", "memberpeer"], group.Members.Select(static m => m.PeerKey));
        Assert.Equal("ownerseries00001", group.Copies.Single().StubKey);
        Assert.False(GroupPlanner.ApplyTvPeer(document, "memberpeer", [memberSeries], static _ => "lib1", static _ => null, known, static () => "x")); // stable

        Assert.True(GroupPlanner.ApplyTvPeer(document, "ownerpeer", [], static _ => "lib0", static _ => null, known, static () => "x")); // the owner's series folder is gone
        Assert.Empty(document.Groups);
    }

    [Fact]
    public async Task APlayOfTheOwnersEpisode_CanGoToAMembersHiddenSourceOfTheSameEpisode_ButNeverToAPartOfATwoPartFile()
    {
        using var dir = new TempDirectory();
        var time = new ManualTimeProvider();
        using var state = new StateStore(new ShoalPaths(dir.Path), NullLogger.Instance, time, null);
        Directory.CreateDirectory(dir.Combine("stubs"));
        const string Owner = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        const string Member = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        var user = Guid.NewGuid();
        await state.UpdateAsync(s =>
        {
            s.StubRoot = dir.Combine("stubs");
            foreach (var key in new[] { Owner, Member })
            {
                s.Home.Peers.Add(new PeerState { PeerKey = key, DisplayName = key[..4], BaseUrl = "http://" + key[..4] + ".tail1234.ts.net:8096", Libraries = { new LibraryMapping { MappingKey = "tv-1", RemoteName = "TV", LocalLibraryName = key[..4], Subfolder = "tv", CollectionType = "tvshows", Access = new LibraryAccess { Mode = AccessMode.Everyone } } } });
            }
        });
        var catalogs = new CatalogStore(new ShoalPaths(dir.Path), NullLogger.Instance);
        var ownerSeries = new CatalogEntry { Kind = EntryKind.Series, StubKey = "ownerseries00001", PeerItemId = new string('1', 32), MappingKey = "tv-1", State = EntryState.Active, FolderRel = "Show (2008)" };
        CatalogEntry OwnerEp(string key, int number, int? end, char id) => new() { Kind = EntryKind.Episode, StubKey = key, ParentKey = ownerSeries.StubKey, PeerItemId = new string(id, 32), PeerMediaSourceId = new string(id, 32), PeerSeriesId = ownerSeries.PeerItemId, MappingKey = "tv-1", Season = 1, Episode = number, EpisodeEnd = end, State = EntryState.Active, StubRelPath = $"Show (2008)/Season 01/S01E0{number}.strm" };
        var single = OwnerEp("ownerep000000001", 1, null, '3');
        var twoPart = OwnerEp("ownerep000000002", 2, 3, '4');
        var ownerDoc = new CatalogDocument { PeerKey = Owner, CompletedOnce = true };
        ownerDoc.Entries.AddRange([ownerSeries, single, twoPart]);
        catalogs.Save(ownerDoc);
        var memberSeries = new CatalogEntry { Kind = EntryKind.Series, StubKey = "memberseries0001", PeerItemId = new string('2', 32), MappingKey = "tv-1", State = EntryState.DuplicatePeer, DuplicateOf = ownerSeries.StubKey };
        CatalogEntry Hidden(string key, int number, char id) => new() { Kind = EntryKind.Episode, StubKey = key, ParentKey = memberSeries.StubKey, PeerItemId = new string(id, 32), PeerMediaSourceId = new string(id, 32), PeerSeriesId = memberSeries.PeerItemId, MappingKey = "tv-1", Season = 1, Episode = number, State = EntryState.DuplicatePeer };
        var memberDoc = new CatalogDocument { PeerKey = Member, CompletedOnce = true };
        memberDoc.Entries.AddRange([memberSeries, Hidden("hiddenep00000001", 1, '5'), Hidden("hiddenep00000002", 2, '6')]);
        catalogs.Save(memberDoc);
        var book = new GroupBook(new MemoryGroupStore(), NullLogger.Instance);
        book.Update(d =>
        {
            d.Groups.Add(new TitleGroup { GroupKey = "grp0000000000001", Kind = GroupKind.Series, MetaOwner = Owner, Members = [new GroupMember { PeerKey = Owner, PeerItemId = ownerSeries.PeerItemId }, new GroupMember { PeerKey = Member, PeerItemId = memberSeries.PeerItemId }], Copies = [new GroupCopy { StubKey = ownerSeries.StubKey, FolderRel = "Show (2008)" }] });
            return true;
        });
        var registry = new PeerRegistry(time);
        registry.SetStatus(Owner, PeerStatus.Offline);
        registry.SetStatus(Member, PeerStatus.Online);
        var playback = new GroupPlayback(book, new StubCatalog(catalogs), state, registry, new FixedRanking([Owner, Member]));
        var caller = new CallerUser(user, false, false, true, true, true, false, null);

        var candidates = playback.Candidates(Owner, single, caller)!;
        Assert.Equal([Member], candidates.Select(static c => c.PeerKey)); // the owner is down: the member's S01E01 plays
        Assert.Equal(Guid.ParseExact(new string('5', 32), "N"), candidates[0].PeerItemId);
        Assert.Contains(Guid.ParseExact(new string('5', 32), "N"), playback.PeerIds(single));

        Assert.Empty(playback.Candidates(Owner, twoPart, caller)!); // the member's E02 is half of the owner's E02-E03
        Assert.NotNull(playback.Member(Owner, single, Member, Guid.ParseExact(new string('5', 32), "N")));
    }

    [Fact]
    public async Task TheTvSync_KeepsAHigherRankedServersSeriesAsGroupMember_WithHiddenSources_AndWritesNoStubOfIt()
    {
        await using var rig = await SyncRig.CreateAsync(null, realTv: true);
        rig.Peer.Tv = FakeTvTree.Generate(series: 2, seasonsPerSeries: 2, episodesPerSeason: 3);
        await rig.AddTvMappingAsync();
        const string Other = "cccccccccccccccccccccccccccccccc";
        await rig.State.UpdateAsync(s => s.Home.Peers.Add(new PeerState { PeerKey = Other, DisplayName = "Barley", BaseUrl = "http://barley.tail1234.ts.net:8096", Priority = 10, Libraries = { new LibraryMapping { MappingKey = "tv-9", RemoteName = "TV", LocalLibraryName = "Barley TV", Subfolder = "tv", CollectionType = "tvshows" } } }));
        var otherSeries = new CatalogEntry { Kind = EntryKind.Series, StubKey = "barleyseries0001", PeerItemId = new string('9', 32), MappingKey = "tv-9", State = EntryState.Active, FolderRel = "Show 0", Title = "Show 0", ProviderIds = new Dictionary<string, string> { ["Tvdb"] = "100000" } };
        var otherDoc = new CatalogDocument { PeerKey = Other, CompletedOnce = true };
        otherDoc.Entries.Add(otherSeries);
        for (var n = 1; n <= 3; n++)
        {
            otherDoc.Entries.Add(new CatalogEntry { Kind = EntryKind.Episode, StubKey = "barleyep0000000" + n, ParentKey = otherSeries.StubKey, PeerItemId = new string((char)('a' + n), 32), PeerSeriesId = otherSeries.PeerItemId, MappingKey = "tv-9", Season = 1, Episode = n, State = EntryState.Active, StubRelPath = $"Show 0/Season 01/S01E0{n}.strm" });
        }

        rig.Catalogs.Save(otherDoc);

        await rig.SyncAsync();

        var entries = rig.Catalog().Entries;
        var mine = entries.Single(e => e.Kind == EntryKind.Series && e.Title == "Show 0");
        Assert.Equal(EntryState.DuplicatePeer, mine.State);
        Assert.Equal("barleyseries0001", mine.DuplicateOf);
        var hidden = entries.Where(e => e.Kind == EntryKind.Episode && e.ParentKey == mine.StubKey).ToList();
        Assert.Equal(6, hidden.Count);
        Assert.All(hidden, static e => Assert.Equal(EntryState.DuplicatePeer, e.State));
        Assert.All(hidden, static e => Assert.Null(e.StubRelPath));
        Assert.Equal(3, hidden.Count(static e => e.DuplicateOf is not null)); // season 1 stands in for Barley's episodes; season 2 stays hidden (another library folder)
        Assert.Equal(6, entries.Count(e => e.Kind == EntryKind.Episode && e.State == EntryState.Active)); // Show 1 is written as before

        await rig.SyncAsync(); // stable: no record churn
        Assert.Equal(hidden.Select(static e => e.StubKey).Order(), rig.Catalog().Entries.Where(e => e.Kind == EntryKind.Episode && e.ParentKey == mine.StubKey).Select(static e => e.StubKey).Order());
    }

    private sealed class FixedRanking : IConnectionRanking
    {
        private readonly IReadOnlyList<string> _order;

        public FixedRanking(IReadOnlyList<string> order) => _order = order;

        public IReadOnlyList<string> Order() => _order;

        public double? Throughput(string peerKey) => null;
    }
}
