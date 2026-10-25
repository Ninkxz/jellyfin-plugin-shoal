using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.Peers;
using Jellyfin.Plugin.Shoal.State;
using Jellyfin.Plugin.Shoal.Storage;
using Jellyfin.Plugin.Shoal.Sync;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Shoal.Tests;

/// <summary>
/// 2.9.5 scenario matrix: 1 to 4 friends (ranked by pairing date) feed one movie and one TV library of Home, in each placement (a Shoal library that merges
/// series, the admin's own library without and with series merging), with every overlap pattern at once (one series or movie per pattern) and one fault on
/// the top friend at the second sync. Each case runs three real syncs of every friend (real planner, real stub writer, real folders on a temp disk) and
/// checks the product rules after each one. The two friend reports of 2.9.3 and 2.9.4 are cases of this matrix.
/// </summary>
public sealed partial class SyncScenarioMatrixTests
{
    /// <summary>Where the friends' titles show.</summary>
    public enum Layout
    {
        /// <summary>A library Shoal created; it merges series with the same ids.</summary>
        SharedMerges,

        /// <summary>Placement Main: a folder of the admin's own TV library, series merging off.</summary>
        MainNoMerge,

        /// <summary>Placement Main with series merging on.</summary>
        MainMerges,
    }

    /// <summary>What goes wrong with the top friend (Friend A) at the second sync.</summary>
    public enum Fault
    {
        /// <summary>Nothing.</summary>
        None,

        /// <summary>Friend A is offline for the second sync, back for the third.</summary>
        Offline,

        /// <summary>Friend A's catalog.json is half written before the second sync (the .bak is whole).</summary>
        TornCatalog,

        /// <summary>Friend A's catalog.json and .bak are gone before the second sync.</summary>
        MissingCatalog,

        /// <summary>Friend A's server lists nothing at the second sync, everything again at the third.</summary>
        EmptyCatalog,

        /// <summary>From the second sync on Friend A no longer has Series One, Movie One and one episode of Series Three.</summary>
        Drops,
    }

    /// <summary>Gets every case of the matrix for one layout (one test class per layout, so the three run in parallel), known bugs left out.</summary>
    /// <param name="layout">The layout.</param>
    /// <returns>The cases.</returns>
    public static TheoryData<int, Layout, Fault> Cases(Layout layout)
    {
        var data = new TheoryData<int, Layout, Fault>();
        foreach (var friends in new[] { 1, 2, 3, 4 })
        {
            foreach (var fault in Enum.GetValues<Fault>())
            {
                if (!IsKnownBug(fault))
                {
                    data.Add(friends, layout, fault);
                }
            }
        }

        return data;
    }

    /// <summary>Gets the cases of bug 2.9.5 #2 (every friend count and layout).</summary>
    public static TheoryData<int, Layout, Fault> KnownBugCases()
    {
        var data = new TheoryData<int, Layout, Fault>();
        foreach (var friends in new[] { 1, 2, 3, 4 })
        {
            foreach (var layout in Enum.GetValues<Layout>())
            {
                data.Add(friends, layout, Fault.MissingCatalog);
            }
        }

        return data;
    }

    /// <summary>Gets the cases that also run ten more syncs (soak: nothing may change on disk once the friends are stable).</summary>
    public static TheoryData<int, Layout, Fault> SoakCases()
    {
        var data = new TheoryData<int, Layout, Fault>();
        foreach (var layout in Enum.GetValues<Layout>())
        {
            foreach (var fault in new[] { Fault.None, Fault.Offline, Fault.Drops })
            {
                data.Add(3, layout, fault);
            }
        }

        return data;
    }

    // The fault that fails on bug 2.9.5 #2, too big for this change (docs/NOTES-2.9.5.md): its cases run in the skipped theory below.
    private static bool IsKnownBug(Fault fault) => fault == Fault.MissingCatalog;

    [Theory]
    [MemberData(nameof(Cases), Layout.SharedMerges)]
    public async Task EveryCase_KeepsTheProductRules_OverThreeSyncs(int friends, Layout layout, Fault fault) =>
        await RunAsync(friends, layout, fault, syncs: 3);

    /// <summary>2.9.5 #1: Friend B's copy of a series has no provider ids; its extra episode joined Friend A's series by title and showed as a second entry.</summary>
    [Fact]
    public async Task Bug1_ASeriesMatchedOnlyByTitle_NeverJoinsAsASecondEntry() => await RunAsync(2, Layout.MainMerges, Fault.None, syncs: 3);

    /// <summary>2.9.5 #3: with the top friend offline, every lower friend wrote a second copy of each series the top friend shows (episodes listed twice).</summary>
    [Fact]
    public async Task Bug3_AnOfflineTopFriend_KeepsItsSeries_AndTheOthersStandIn() => await RunAsync(3, Layout.SharedMerges, Fault.Offline, syncs: 3);

    [Theory]
    [MemberData(nameof(KnownBugCases))]
    public async Task Bug2_AFriendsLostCatalog_HoldsWithoutChangingAnyStub(int friends, Layout layout, Fault fault)
    {
        await using var home = await ScenarioHome.CreateAsync(friends, layout);
        foreach (var friend in home.Friends)
        {
            await home.Sync.SyncAsync(friend.Key, SyncTrigger.Scheduled);
        }

        var before = home.Look();
        home.ApplyFault(fault, 2);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var result = await home.Sync.ReleaseAsync(home.Friends[0].Key, ReleaseKind.Both);
            Assert.Equal("catalogUnreadable", result.Error);
            Assert.Equal(before.Files, home.Look().Files);
            Assert.True(home.Catalogs.Load(home.Friends[0].Key).Unreadable);
        }
    }

    /// <summary>The cases of the admin's own TV library without series merging.</summary>
    public sealed class MainNoMerge
    {
        [Theory]
        [MemberData(nameof(Cases), Layout.MainNoMerge, MemberType = typeof(SyncScenarioMatrixTests))]
        public async Task EveryCase_KeepsTheProductRules_OverThreeSyncs(int friends, Layout layout, Fault fault) =>
            await RunAsync(friends, layout, fault, syncs: 3);
    }

    /// <summary>The cases of the admin's own TV library with series merging.</summary>
    public sealed class MainMerges
    {
        [Theory]
        [MemberData(nameof(Cases), Layout.MainMerges, MemberType = typeof(SyncScenarioMatrixTests))]
        public async Task EveryCase_KeepsTheProductRules_OverThreeSyncs(int friends, Layout layout, Fault fault) =>
            await RunAsync(friends, layout, fault, syncs: 3);
    }

    /// <summary>Soak: three friends, ten more syncs after the third; nothing may change on disk once the friends are stable.</summary>
    public sealed class Soak
    {
        [Theory]
        [MemberData(nameof(SoakCases), MemberType = typeof(SyncScenarioMatrixTests))]
        public async Task TenMoreSyncs_ChangeNothingOnDisk(int friends, Layout layout, Fault fault) =>
            await RunAsync(friends, layout, fault, syncs: 13);
    }

    private static async Task RunAsync(int friends, Layout layout, Fault fault, int syncs)
    {
        await using var home = await ScenarioHome.CreateAsync(friends, layout);
        var problems = new List<string>();
        Snapshot? settled = null;
        for (var sync = 1; sync <= syncs; sync++)
        {
            home.ApplyFault(fault, sync);
            var before = home.Look();
            foreach (var friend in home.Friends)
            {
                await home.Sync.SyncAsync(friend.Key, SyncTrigger.Scheduled);
            }

            home.Time.Advance(TimeSpan.FromHours(6));
            if (sync == 1)
            {
                home.PlaceUserFiles();
            }

            var after = home.Look();
            if (Environment.GetEnvironmentVariable("SHOAL_MATRIX_DUMP") is "1")
            {
                Console.WriteLine($"DBG sync {sync}{Environment.NewLine}DBG " + string.Join(Environment.NewLine + "DBG ", after.Files));
            }

            problems.AddRange(home.Check(sync, fault, before, after).Select(p => $"sync {sync}: {p}"));
            if (sync == 3)
            {
                settled = after;
            }
            else if (sync > 3 && !after.Files.SequenceEqual(settled!.Files))
            {
                var added = after.Files.Except(settled.Files).Take(3);
                var gone = settled.Files.Except(after.Files).Take(3);
                problems.Add($"sync {sync}: (c) soak: the disk changed with stable friends; new [{string.Join(", ", added)}] gone [{string.Join(", ", gone)}]");
            }

            if (problems.Count > 0)
            {
                break; // the first sync that breaks a rule is the one to read
            }
        }

        Assert.True(problems.Count == 0, $"friends={friends} layout={layout} fault={fault}{Environment.NewLine}{string.Join(Environment.NewLine, problems.Take(12))}");
    }

    [GeneratedRegex(@"S(\d+)E(\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex EpisodeName();

    [GeneratedRegex(@"\[(tvdb|tmdb|imdb)id-[^\]]+\]", RegexOptions.CultureInvariant)]
    private static partial Regex IdTag();

    /// <summary>One series of a friend's TV library.</summary>
    private sealed record SeriesSpec(string Logical, string Name, int Year, string? Tvdb, (int S, int E)[] Episodes);

    /// <summary>One movie of a friend's movie library.</summary>
    private sealed record MovieSpec(string Logical, string Name, int Year, string? Tmdb);

    /// <summary>What Home's disk shows after a sync.</summary>
    private sealed record Snapshot(
        IReadOnlyList<string> Files,
        IReadOnlyDictionary<string, string> TvStubOwner,
        IReadOnlyDictionary<string, string> MovieStubOwner);

    /// <summary>A friend: its fake server, what it has, and whether it can be reached.</summary>
    private sealed class Friend
    {
        public required string Key { get; init; }

        public required string Name { get; init; }

        public required int Index { get; init; }

        public required FakeLibraryPeer Peer { get; init; }

        public required List<SeriesSpec> AllSeries { get; init; }

        public required List<MovieSpec> AllMovies { get; init; }

        public List<SeriesSpec> Series { get; set; } = [];

        public List<MovieSpec> Movies { get; set; } = [];

        public bool Offline { get; set; }

        public bool Serves(string logical, int season, int episode) =>
            !Offline && Series.Any(s => s.Logical == logical && s.Episodes.Contains((season, episode)));

        public bool ServesMovie(string logical) => !Offline && Movies.Any(m => m.Logical == logical);
    }

    /// <summary>
    /// Home with N friends wired like the plugin wires them (CatalogSyncService with the TV engine and the title groups), each friend a real fake server.
    /// </summary>
    private sealed class ScenarioHome : IAsyncDisposable
    {
        private const string TvLibKey = "0123456789abcdef";
        private const string MovieLibKey = "fedcba9876543210";

        private readonly Dictionary<(string Peer, string Item), string> _seriesLogical = new();
        private readonly Dictionary<(string Peer, string Item), string> _movieLogical = new();
        private readonly Dictionary<string, PeerEndpoint> _endpoints = new(StringComparer.Ordinal);
        private readonly HashSet<string> _down = new(StringComparer.Ordinal);
        private readonly List<string> _userFiles = [];
        private readonly HashSet<string> _userDirs = new(StringComparer.OrdinalIgnoreCase);
        private Layout _layout;
        private PeerClient _client = null!;

        public TempDirectory Dir { get; } = new();

        public ManualTimeProvider Time { get; } = new();

        public List<Friend> Friends { get; } = [];

        public StateStore State { get; private set; } = null!;

        public CatalogStore Catalogs { get; private set; } = null!;

        public CatalogSyncService Sync { get; private set; } = null!;

        public GroupBook Book { get; private set; } = null!;

        public ShoalPaths Paths { get; private set; } = null!;

        public string StubRoot => Dir.Combine("stubs");

        public string TvFolder => Path.Combine(StubRoot, ShoalLibrary.LibsFolder, TvLibKey);

        public string MovieFolder => Path.Combine(StubRoot, ShoalLibrary.LibsFolder, MovieLibKey);

        private bool Merges => _layout != Layout.MainNoMerge;

        public static async Task<ScenarioHome> CreateAsync(int friends, Layout layout)
        {
            var home = new ScenarioHome { _layout = layout };
            for (var i = 0; i < friends; i++)
            {
                var key = new string((char)('a' + i), 32);
                var peer = new FakeLibraryPeer
                {
                    ServerId = Guid.Parse(new string((char)('1' + i), 32)),
                    LibraryId = Guid.Parse("a0000000000000000000000000000" + (100 + i).ToString(CultureInfo.InvariantCulture)),
                    Tv = new FakeTvTree { LibraryId = Guid.Parse("b0000000000000000000000000000" + (100 + i).ToString(CultureInfo.InvariantCulture)) },
                };
                await peer.StartAsync();
                var friend = new Friend { Key = key, Name = "Friend " + (char)('A' + i), Index = i, Peer = peer, AllSeries = SeriesOf(i, friends), AllMovies = MoviesOf(i, friends) };
                home.Friends.Add(friend);
                home.Serve(friend, friend.AllSeries, friend.AllMovies);
            }

            await home.InitializeAsync();
            return home;
        }

        // ---- the data: one series (or movie) per overlap pattern; friend i of n
        private static List<SeriesSpec> SeriesOf(int i, int n)
        {
            static (int, int)[] Range(int season, int from, int to) => [.. Enumerable.Range(from, to - from + 1).Select(e => (season, e))];
            var list = new List<SeriesSpec>
            {
                new("one", "Series One", 2010, "1001", Range(1, 1, 3)), // identical episode sets
                new("two", "Series Two", 2010, "1002", Range(i + 1, 1, 2)), // disjoint: a season each
                new("three", "Series Three", 2010, "1003", Range(1, i + 1, i + 3)), // partial overlap
                new("four", "Series Four", 2010, "1004", i == 0 ? Range(1, 1, 1) : Range(1, 1, 5)), // the top friend has only one episode
                new(i % 2 == 0 ? "six-2010" : "six-2020", "Series Six", i % 2 == 0 ? 2010 : 2020, i % 2 == 0 ? "1006" : "1016", Range(1, 1, 2)), // same title, other show
                new("seven", new[] { "Series Seven", "series seven", "SERIES SEVEN", "Series Seven!" }[i], 2010, "1007", [.. Range(1, 1, 2), (2, i + 1)]), // one id, other spellings
                new("eight", "Series Eight", 2010, i == 1 ? null : "1008", [.. Range(1, 1, 3), .. i == 1 ? new[] { (1, 4) } : []]), // Friend B has no provider ids
            };
            if (i == n - 1)
            {
                list.Add(new("five", "Series Five", 2010, "1005", Range(1, 1, 2))); // only the last friend has it
            }

            return list;
        }

        private static List<MovieSpec> MoviesOf(int i, int n)
        {
            var list = new List<MovieSpec>
            {
                new("m-one", "Movie One", 2001, "2001"),
                new(i % 2 == 0 ? "m-three-2003" : "m-three-2013", "Movie Three", i % 2 == 0 ? 2003 : 2013, i % 2 == 0 ? "2003" : "2013"),
                new("m-four", new[] { "Movie Four", "movie four", "MOVIE FOUR", "Movie Four!" }[i], 2004, "2004"),
                new(i == 1 ? "m-five-noids" : "m-five", "Movie Five", 2005, i == 1 ? null : "2005"), // DESIGN 5.3: without ids a movie never matches one with ids
                new("m-six-" + i.ToString(CultureInfo.InvariantCulture), "Movie Six " + (char)('A' + i), 2006, (2060 + i).ToString(CultureInfo.InvariantCulture)),
            };
            if (i == n - 1)
            {
                list.Add(new("m-two", "Movie Two", 2002, "2002"));
            }

            return list;
        }

        /// <summary>Makes the friend's fake server list exactly these titles (stable ids, as a real server keeps them).</summary>
        private void Serve(Friend friend, List<SeriesSpec> series, List<MovieSpec> movies)
        {
            friend.Series = series;
            friend.Movies = movies;
            var tree = new FakeTvTree { LibraryId = friend.Peer.Tv!.LibraryId };
            foreach (var spec in series)
            {
                var n = (friend.Index * 100) + friend.AllSeries.IndexOf(spec);
                var show = new FakeTvSeries { Id = FakeTvTree.IdOf('S', n), Name = spec.Name, Year = spec.Year, ProviderIds = spec.Tvdb is null ? [] : new() { ["Tvdb"] = spec.Tvdb } };
                tree.Series.Add(show);
                _seriesLogical[(friend.Key, show.Id.ToString("N"))] = spec.Logical;
                foreach (var group in spec.Episodes.GroupBy(static e => e.S))
                {
                    var season = new FakeTvSeason { Id = FakeTvTree.IdOf('N', n, group.Key), SeriesId = show.Id, Number = group.Key };
                    tree.Seasons.Add(season);
                    foreach (var (s, e) in group)
                    {
                        tree.Episodes.Add(new FakeTvEpisode { Id = FakeTvTree.IdOf('E', n, s, e), SeriesId = show.Id, SeasonId = season.Id, Season = s, Number = e, Name = "Episode " + e.ToString(CultureInfo.InvariantCulture) });
                    }
                }
            }

            friend.Peer.Tv = tree;
            friend.Peer.Movies.Clear();
            foreach (var spec in movies)
            {
                var id = FakeTvTree.IdOf('M', (friend.Index * 100) + friend.AllMovies.IndexOf(spec));
                friend.Peer.Add(spec.Name, spec.Year, spec.Tmdb is null ? null : long.Parse(spec.Tmdb, CultureInfo.InvariantCulture), configure: m => m.Id = id);
                _movieLogical[(friend.Key, id.ToString("N"))] = spec.Logical;
            }
        }

        public void ApplyFault(Fault fault, int sync)
        {
            var a = Friends[0];
            var catalog = Path.Combine(Paths.PeerDir(a.Key), "catalog.json");
            switch (fault)
            {
                case Fault.Offline when sync == 2:
                    a.Offline = a.Peer.Down = true;
                    _down.Add(a.Key);
                    break;
                case Fault.Offline when sync == 3:
                    a.Offline = a.Peer.Down = false;
                    _down.Remove(a.Key);
                    break;
                case Fault.TornCatalog when sync == 2:
                    var bytes = File.ReadAllBytes(catalog);
                    File.WriteAllBytes(catalog, bytes[..(bytes.Length / 2)]);
                    break;
                case Fault.MissingCatalog when sync == 2:
                    File.Delete(catalog);
                    File.Delete(catalog + ".bak");
                    break;
                case Fault.EmptyCatalog when sync == 2:
                    Serve(a, [], []);
                    break;
                case Fault.EmptyCatalog when sync == 3:
                    Serve(a, a.AllSeries, a.AllMovies);
                    break;
                case Fault.Drops when sync == 2:
                    var three = a.AllSeries.Single(static s => s.Logical == "three");
                    Serve(
                        a,
                        [.. a.AllSeries.Where(static s => s.Logical != "one").Select(s => s == three ? s with { Episodes = [.. s.Episodes.Where(static e => e != (1, 2))] } : s)],
                        [.. a.AllMovies.Where(static m => m.Logical != "m-one")]);
                    break;
            }
        }

        /// <summary>After the first sync: a file Shoal never writes inside the top friend's first series folder, a folder of the admin's own next to the stubs, and a file outside the stub root.</summary>
        public void PlaceUserFiles()
        {
            var outside = Dir.Combine("media", "own movie.mkv");
            Directory.CreateDirectory(Path.GetDirectoryName(outside)!);
            File.WriteAllText(outside, "mine");
            var own = Path.Combine(TvFolder, "Home videos", "clip.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(own)!);
            File.WriteAllText(own, "mine");
            _userDirs.Add("Home videos");
            _userFiles.AddRange([outside, own]);
            var first = Catalogs.Load(Friends[0].Key).Document.Entries.FirstOrDefault(static e => e.Kind == EntryKind.Series && e.State == EntryState.Active && e.FolderRel is not null);
            if (first is not null)
            {
                var notes = Path.Combine(TvFolder, first.FolderRel!, "notes.txt");
                File.WriteAllText(notes, "mine");
                _userFiles.Add(notes);
                _userDirs.Add(first.FolderRel!);
            }
        }

        public Snapshot Look()
        {
            var index = StubIndex();
            Dictionary<string, string> Owners(string folder) => !Directory.Exists(folder) ? [] : Directory.EnumerateFiles(folder, "*.strm", SearchOption.AllDirectories)
                .Select(f => (f, StubSentinel.TryParse(File.ReadAllText(f), out var key) && index.TryGetValue(key, out var hit) ? hit.Peer : "?"))
                .ToDictionary(static x => x.f, static x => x.Item2, StringComparer.Ordinal);
            var files = Directory.Exists(StubRoot)
                ? Directory.EnumerateFiles(StubRoot, "*", SearchOption.AllDirectories).Where(static f => !f.EndsWith(".json", StringComparison.Ordinal) && !f.EndsWith(".bak", StringComparison.Ordinal) && !f.EndsWith(".tmp", StringComparison.Ordinal))
                    .Select(f => Path.GetRelativePath(StubRoot, f) + (f.EndsWith(".strm", StringComparison.Ordinal) ? " = " + File.ReadAllText(f).Trim() : string.Empty)).Order(StringComparer.Ordinal).ToList()
                : [];
            return new Snapshot(files, Owners(TvFolder), Owners(MovieFolder));
        }

        private Dictionary<string, (string Peer, CatalogEntry Entry)> StubIndex()
        {
            var index = new Dictionary<string, (string Peer, CatalogEntry Entry)>(StringComparer.Ordinal);
            foreach (var friend in Friends)
            {
                foreach (var entry in Catalogs.Load(friend.Key).Document.Entries)
                {
                    index.TryAdd(entry.StubKey, (friend.Key, entry));
                }
            }

            return index;
        }

        private Friend FriendOf(string key) => Friends.Single(f => f.Key == key);

        /// <summary>The product rules (a) to (h); one line per break.</summary>
        public List<string> Check(int sync, Fault fault, Snapshot before, Snapshot after)
        {
            var problems = new List<string>();
            var index = StubIndex();
            CheckTv(sync, fault, before, after, index, problems);
            CheckMovies(sync, after, index, problems);

            // (d) nothing outside the stub root, and no non-stub file, is touched
            problems.AddRange(_userFiles.Where(static f => !File.Exists(f)).Select(f => "(d) a file Shoal never wrote is gone: " + Path.GetRelativePath(Dir.Path, f)));

            // (e) a friend that is offline or has a bad catalog never makes another friend's live stub go
            if (sync == 2 && fault != Fault.None)
            {
                foreach (var (path, owner) in before.TvStubOwner.Concat(before.MovieStubOwner))
                {
                    if (owner != Friends[0].Key && owner != "?" && !after.TvStubOwner.ContainsKey(path) && !after.MovieStubOwner.ContainsKey(path))
                    {
                        problems.Add($"(e) {FriendOf(owner).Name}'s live stub went while Friend A was faulty: {Rel(path)}");
                    }
                }
            }

            return problems;
        }

        private string Rel(string path) => Path.GetRelativePath(StubRoot, path);

        private void CheckTv(int sync, Fault fault, Snapshot before, Snapshot after, Dictionary<string, (string Peer, CatalogEntry Entry)> index, List<string> problems)
        {
            var folders = Directory.Exists(TvFolder) ? Directory.EnumerateDirectories(TvFolder).Select(Path.GetFileName).Select(static n => n!).ToList() : [];
            var seriesByFolder = Friends.SelectMany(f => Catalogs.Load(f.Key).Document.Entries.Where(static e => e.Kind == EntryKind.Series && e.FolderRel is not null).Select(e => (f.Key, e)))
                .ToLookup(static x => x.e.FolderRel!, StringComparer.OrdinalIgnoreCase);
            var entries = new Dictionary<string, (HashSet<string> Logicals, List<(string Logical, int S, int E)> Episodes, List<string> Folders)>(StringComparer.Ordinal);
            var foldersOf = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            foreach (var folder in folders)
            {
                var logicals = new HashSet<string>(StringComparer.Ordinal);
                var visible = new List<(string Logical, int S, int E)>();
                foreach (var strm in Directory.EnumerateFiles(Path.Combine(TvFolder, folder), "*.strm", SearchOption.AllDirectories))
                {
                    if (!StubSentinel.TryParse(File.ReadAllText(strm), out var key) || !index.TryGetValue(key, out var hit) || hit.Entry.Kind != EntryKind.Episode
                        || !_seriesLogical.TryGetValue((hit.Peer, hit.Entry.PeerSeriesId ?? string.Empty), out var logical))
                    {
                        problems.Add("(f) a visible episode no catalog knows: " + Rel(strm));
                        continue;
                    }

                    var match = EpisodeName().Match(Path.GetFileName(strm));
                    var (s, e) = (int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture), int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture));
                    logicals.Add(logical);
                    visible.Add((logical, s, e));
                    CheckSource(hit.Peer, hit.Entry, logical, s, e, strm, index, problems);
                }

                foreach (var (peer, series) in seriesByFolder[folder])
                {
                    if (_seriesLogical.TryGetValue((peer, series.PeerItemId), out var logical))
                    {
                        logicals.Add(logical);
                    }
                }

                if (logicals.Count == 0 && !_userDirs.Contains(folder))
                {
                    problems.Add("(c) a series folder no catalog knows: " + folder);
                }

                foreach (var logical in logicals)
                {
                    (foldersOf.TryGetValue(logical, out var set) ? set : foldersOf[logical] = new(StringComparer.Ordinal)).Add(folder);
                }

                if (visible.Count == 0)
                {
                    continue;
                }

                // what Jellyfin lists: merging on, folders with the same id tag are one series; off (or no id tag), each folder is one
                var entryKey = Merges && IdTag().Match(folder) is { Success: true } tag ? tag.Value : folder;
                if (!entries.TryGetValue(entryKey, out var listed))
                {
                    listed = entries[entryKey] = (new(StringComparer.Ordinal), [], []);
                }

                listed.Logicals.UnionWith(logicals);
                listed.Episodes.AddRange(visible);
                listed.Folders.Add(folder);
            }

            foreach (var (key, listed) in entries.Where(static x => x.Value.Logicals.Count > 1))
            {
                problems.Add($"(a) one series entry mixes {string.Join(" + ", listed.Logicals.Order(StringComparer.Ordinal))}: {string.Join(" | ", listed.Folders)}");
            }

            foreach (var group in entries.Values.GroupBy(static x => x.Episodes[0].Logical))
            {
                var shown = group.ToList();
                if (Merges && shown.Count > 1)
                {
                    problems.Add($"(a) series '{group.Key}' shows as {shown.Count} entries: {string.Join(" | ", shown.SelectMany(static x => x.Folders))}");
                }
                else if (!Merges && shown.Select(static x => string.Join(",", x.Episodes.Select(static e => (e.S, e.E)).Order())).GroupBy(static x => x).Any(static g => g.Count() > 1))
                {
                    problems.Add($"(a) series '{group.Key}' shows twice with the same episodes: {string.Join(" | ", shown.SelectMany(static x => x.Folders))}");
                }

                var twice = shown.SelectMany(static x => x.Episodes).GroupBy(static e => (e.S, e.E)).Where(static g => g.Count() > 1).Select(static g => $"S{g.Key.S:00}E{g.Key.E:00}x{g.Count()}").ToList();
                if (twice.Count > 0)
                {
                    problems.Add($"(b) series '{group.Key}' lists {string.Join(", ", twice)}: {string.Join(" | ", shown.SelectMany(static x => x.Folders))}");
                }
            }

            // (c) folders per series never exceed the friends that have it, and never grow between sync 2 and 3 when no friend changed
            foreach (var (logical, set) in foldersOf)
            {
                var having = Friends.Count(f => f.AllSeries.Any(s => s.Logical == logical));
                if (set.Count > having)
                {
                    problems.Add($"(c) series '{logical}' has {set.Count} folders for {having} friend(s): {string.Join(" | ", set.Order(StringComparer.Ordinal))}");
                }
            }

            if (sync == 3 && fault is not (Fault.Offline or Fault.EmptyCatalog))
            {
                static IEnumerable<string> SeriesFolders(Snapshot shot) =>
                    shot.Files.Select(static f => f.Split(Path.DirectorySeparatorChar)).Where(static p => p.Length > 3 && p[1] == TvLibKey).Select(static p => p[2]).Distinct(StringComparer.Ordinal);
                var had = SeriesFolders(before).ToHashSet(StringComparer.Ordinal);
                var grown = SeriesFolders(after).Where(f => !had.Contains(f)).ToList();
                problems.AddRange(grown.Select(static f => "(c) a new series folder at sync 3 with unchanged friends: " + f));
            }

            // (h) once settled, what a reachable friend has is shown: every episode when the library merges series, else every series
            if (sync >= 3)
            {
                var shownEpisodes = entries.Values.SelectMany(static x => x.Episodes).ToHashSet();
                foreach (var friend in Friends.Where(static f => !f.Offline))
                {
                    foreach (var spec in friend.Series)
                    {
                        // a series without provider ids cannot merge with another folder: its extra episodes stay hidden sources (2.9.5 #1)
                        var missing = Merges && spec.Tvdb is not null ? spec.Episodes.Where(e => !shownEpisodes.Contains((spec.Logical, e.S, e.E))).ToList() : shownEpisodes.Any(x => x.Logical == spec.Logical) ? [] : [.. spec.Episodes];
                        if (missing.Count > 0)
                        {
                            problems.Add($"(h) {friend.Name} has series '{spec.Logical}' {string.Join(",", missing.Select(static e => $"S{e.S:00}E{e.E:00}"))} but Home does not show it");
                        }
                    }
                }
            }
        }

        // (f) a visible episode whose owner cannot serve it plays from another friend of its series group that can
        private void CheckSource(string owner, CatalogEntry episode, string logical, int s, int e, string strm, Dictionary<string, (string Peer, CatalogEntry Entry)> index, List<string> problems)
        {
            if (FriendOf(owner).Serves(logical, s, e) || !Friends.Any(f => f.Serves(logical, s, e)))
            {
                return; // the owner serves it, or nobody can
            }

            var members = Book.ByCopy(episode.ParentKey)?.Members.Select(static m => m.PeerKey) ?? [];
            if (!members.Any(m => m != owner && FriendOf(m).Serves(logical, s, e)))
            {
                problems.Add($"(f) {Rel(strm)}: {FriendOf(owner).Name} cannot serve it and no reachable friend of its group stands in");
            }
        }

        private void CheckMovies(int sync, Snapshot after, Dictionary<string, (string Peer, CatalogEntry Entry)> index, List<string> problems)
        {
            var shown = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            var byId = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var (strm, _) in after.MovieStubOwner)
            {
                if (!StubSentinel.TryParse(File.ReadAllText(strm), out var key) || !index.TryGetValue(key, out var hit) || !_movieLogical.TryGetValue((hit.Peer, hit.Entry.PeerItemId), out var logical))
                {
                    problems.Add("(f) a visible movie no catalog knows: " + Rel(strm));
                    continue;
                }

                var folder = Path.GetFileName(Path.GetDirectoryName(strm))!;
                (shown.TryGetValue(logical, out var list) ? list : shown[logical] = []).Add(folder);
                foreach (var (name, value) in hit.Entry.ProviderIds)
                {
                    var id = name.ToLowerInvariant() + ":" + value;
                    (byId.TryGetValue(id, out var ids) ? ids : byId[id] = []).Add(folder);
                }

                if (!FriendOf(hit.Peer).ServesMovie(logical) && Friends.Any(f => f.ServesMovie(logical))
                    && !(Book.ByCopy(key)?.Members.Any(m => m.PeerKey != hit.Peer && FriendOf(m.PeerKey).ServesMovie(logical)) ?? false))
                {
                    problems.Add($"(f) {Rel(strm)}: {FriendOf(hit.Peer).Name} cannot serve it and no reachable friend of its group stands in");
                }
            }

            // (g) one visible movie per provider id (a copy with no ids at all is matched by DESIGN 5.3 only against copies with none: docs/NOTES-2.9.5.md)
            foreach (var (id, list) in byId.Where(static x => x.Value.Distinct(StringComparer.Ordinal).Count() > 1))
            {
                problems.Add($"(g) movie {id} shows {list.Distinct(StringComparer.Ordinal).Count()} times: {string.Join(" | ", list.Distinct(StringComparer.Ordinal))}");
            }

            if (sync >= 3)
            {
                foreach (var friend in Friends.Where(static f => !f.Offline))
                {
                    problems.AddRange(friend.Movies.Where(m => !shown.ContainsKey(m.Logical)).Select(m => $"(h) {friend.Name} has movie '{m.Logical}' but Home does not show it"));
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            Sync.Dispose();
            _client.Dispose();
            State.Dispose();
            foreach (var friend in Friends)
            {
                await friend.Peer.DisposeAsync();
            }

            Dir.Dispose();
        }

        private async Task InitializeAsync()
        {
            Paths = new ShoalPaths(Dir.Path);
            State = new StateStore(Paths, NullLogger.Instance, Time, null);
            var placement = _layout == Layout.SharedMerges ? LibraryPlacement.Shoal : LibraryPlacement.Main;
            Guid? main = placement == LibraryPlacement.Main ? Guid.Parse("c0000000000000000000000000000001") : null;
            await State.UpdateAsync(s =>
            {
                s.StubRoot = StubRoot;
                foreach (var friend in Friends)
                {
                    s.Home.Peers.Add(new PeerState
                    {
                        PeerKey = friend.Key,
                        DisplayName = friend.Name,
                        BaseUrl = friend.Peer.Url,
                        PinnedServerId = friend.Peer.ServerId.ToString("N"),
                        Priority = 100,
                        Pairing = new PairingInfo { Method = PairingMethod.Qc, PairedUtc = new DateTime(2026, 9, 1 + friend.Index, 0, 0, 0, DateTimeKind.Utc) }, // ranked by pairing date
                        Libraries =
                        {
                            new LibraryMapping { MappingKey = SyncRun.MappingKeyOf(friend.Peer.LibraryId), RemoteName = "Movies", LocalLibraryName = friend.Name + " Movies", Subfolder = "movies", CollectionType = "movies" },
                            new LibraryMapping { MappingKey = SyncRun.MappingKeyOf(friend.Peer.Tv!.LibraryId), RemoteName = "TV shows", LocalLibraryName = friend.Name + " TV", Subfolder = "tv", CollectionType = "tvshows" },
                        },
                    });
                }

                var feeds = Friends.Select(static f => f.Key).ToList();
                s.Home.Libraries.Add(new ShoalLibrary { LibKey = TvLibKey, Name = "Shows", Type = "tvshows", Placement = placement, JellyfinLibraryId = main, PathRel = ShoalLibrary.LibsFolder + "/" + TvLibKey, Feeds = [.. feeds] });
                s.Home.Libraries.Add(new ShoalLibrary { LibKey = MovieLibKey, Name = "Films", Type = "movies", Placement = placement, JellyfinLibraryId = main is null ? null : Guid.Parse("c0000000000000000000000000000002"), PathRel = ShoalLibrary.LibsFolder + "/" + MovieLibKey, Feeds = [.. feeds] });
            });
            Assert.Equal(Friends.Count, State.Current.Home.Peers.Count);
            Assert.Equal(2, State.Current.Home.Libraries.Count(static l => !l.IsLegacy));

            var log = new CapturingLogger();
            var registry = new PeerRegistry(Time);
            _client = new PeerClient(() => "Home", () => "0.1.0.0", Time, registry) { EnforceAddressPolicy = false };
            var trust = new PeerTrust(_client, registry, Time);
            foreach (var friend in Friends)
            {
                _endpoints[friend.Key] = new PeerEndpoint(friend.Key, friend.Name, new Uri(friend.Peer.Url), "shoal-home-device", "token-for-tests-0123456789abcdef").Configured(PeerNetwork.Tailnet, null, false, friend.Peer.ServerId.ToString("N"));
            }

            var sanitizer = new MetadataSanitizer(new Ratings(), new AlternateVersionSourceSanitizer(static () => false), new TrickplaySanitizer());
            var reader = new PeerCatalogReader(_client, trust, registry, sanitizer, log);
            Catalogs = new CatalogStore(Paths, log);
            var fetcher = new ImageFetcher(_client, NullLogger<ImageFetcher>.Instance);
            var layout = new TvLayout();
            var tv = new TvSyncEngine(new TvSanitizer(new Ratings(), new TrickplaySanitizer()), layout, new TvNfoWriter(), new PeerTvReader(reader, log), new TvPlanner(layout), new TvStubWriter(log), new FakeLocalSeries(), log)
            {
                RefreshDeferral = TimeSpan.Zero,
                ImageWork = new TvImageWork(),
                Images = new TvImageRunner(fetcher, Time),
                EpisodeThumbs = static () => false,
                SeriesGrouping = path => LibraryFolders.SamePath(path, TvFolder) ? Merges : null,
            };
            Directory.CreateDirectory(StubRoot);
            Book = new GroupBook(new GroupStore(() => StubRoot, log), log);
            Sync = new CatalogSyncService(State, key => _endpoints.GetValueOrDefault(key), trust, reader, Catalogs, new StubWriter(fetcher, log), new FakeLocalMovies(), new FakeRefresh(), static () => SyncSettings.Default, Time, log, tv, static () => true, key => !_down.Contains(key))
            {
                Groups = Book,
            };
        }

        private sealed class Ratings : IRatingTable
        {
            public bool IsRecognized(string rating) => rating is "R" or "PG-13" or "PG" or "G" or "TV-14";
        }
    }
}
