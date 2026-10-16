using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.Peers;
using Jellyfin.Plugin.Shoal.Sync;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Shoal.Tests;

/// <summary>K37 (DESIGN v2.7.3): the images of a TV library: order, widths, caps, file names, the budget, the quota and the follow-up (T-TV-6).</summary>
public sealed class TvImageTests : IAsyncLifetime
{
    private readonly TempDirectory _temp = new();
    private readonly ManualTimeProvider _time = new();
    private readonly PeerClient _client = new(() => "Home", () => "0.1.0.0", TimeProvider.System) { EnforceAddressPolicy = false };
    private FakeLibraryPeer _peer = null!;
    private PeerEndpoint _endpoint = null!;
    private ImageFetcher _fetcher = null!;
    private string _root = string.Empty;

    public async Task InitializeAsync()
    {
        _peer = new FakeLibraryPeer { Tv = FakeTvTree.Generate(2, 2, 3) };
        await _peer.StartAsync();
        _endpoint = new PeerEndpoint("00000000000000000000000000000abc", "Hops", new Uri(_peer.Url), "shoal-1a2b3c4d5e6f-4b0c1d2e", "9f2c4d6e8a0b1c3d5e7f9a0b2c4d6e8f");
        _fetcher = new ImageFetcher(_client, NullLogger<ImageFetcher>.Instance);
        _root = _temp.Combine("stubs");
        Directory.CreateDirectory(_root);
    }

    public async Task DisposeAsync()
    {
        await _peer.DisposeAsync();
        _fetcher.Dispose();
        _client.Dispose();
        _temp.Dispose();
    }

    private static CatalogEntry Entry(EntryKind kind, string key, Guid peerId, Dictionary<string, string> tags, string? parent = null, int? season = null, int? episode = null, DateTime? seen = null) => new()
    {
        Kind = kind,
        StubKey = key,
        PeerItemId = peerId.ToString("N"),
        State = EntryState.Active,
        ImageTags = tags,
        ParentKey = parent,
        Season = season,
        Episode = episode,
        FirstSeenUtc = seen ?? DateTime.UtcNow,
    };

    private static string Key(string kind, int n) => kind + n.ToString("D15", System.Globalization.CultureInfo.InvariantCulture);

    private static Dictionary<string, string> Tags(params string[] types) => types.ToDictionary(static t => t, static t => t.ToLowerInvariant() + "1");

    // --- the order and the shape of the work

    [Fact]
    public void TheQueue_IsPosters_SeasonPosters_ThenBackdropsBannersLandscapes_ThenThumbsNewestSeriesFirst()
    {
        var oldSeries = Entry(EntryKind.Series, Key("a", 1), FakeTvTree.IdOf('S', 1), Tags("Primary", "Backdrop", "Banner", "Thumb"), seen: new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var newSeries = Entry(EntryKind.Series, Key("a", 2), FakeTvTree.IdOf('S', 2), Tags("Primary", "Backdrop", "Banner", "Thumb"), seen: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var season = Entry(EntryKind.Season, Key("b", 1), FakeTvTree.IdOf('N', 1), Tags("Primary"), oldSeries.StubKey, 1);
        var oldEpisodes = Enumerable.Range(1, 2).Select(n => Entry(EntryKind.Episode, Key("c", n), FakeTvTree.IdOf('E', 1, 1, n), Tags("Primary"), oldSeries.StubKey, 1, n));
        var newEpisodes = Enumerable.Range(1, 2).Select(n => Entry(EntryKind.Episode, Key("d", n), FakeTvTree.IdOf('E', 2, 1, n), Tags("Primary"), newSeries.StubKey, 1, n));
        CatalogEntry[] all = [.. oldEpisodes, season, oldSeries, .. newEpisodes, newSeries];

        var jobs = new TvImageWork().Queue(all, all.Select(static e => e.StubKey).ToHashSet(), episodeThumbs: true);

        Assert.Equal([TvImageKind.SeriesPoster, TvImageKind.SeriesPoster, TvImageKind.SeasonPoster], jobs.Take(3).Select(static j => j.Kind));
        Assert.All(jobs.Skip(3).Take(6), static j => Assert.True(j.Kind is TvImageKind.SeriesBackdrop or TvImageKind.SeriesBanner or TvImageKind.SeriesLandscape));
        var thumbs = jobs.Skip(9).ToList();
        Assert.All(thumbs, static j => Assert.Equal(TvImageKind.EpisodeThumb, j.Kind));
        Assert.Equal([Key("d", 1), Key("d", 2), Key("c", 1), Key("c", 2)], thumbs.Select(static j => j.StubKey)); // the newest series first, episodes in order
        Assert.Equal(13, jobs.Count);
    }

    [Fact]
    public void TheWidthsAndTheCaps_AreTheDesigns()
    {
        var series = Entry(EntryKind.Series, Key("a", 1), FakeTvTree.IdOf('S', 1), Tags("Primary", "Backdrop", "Banner", "Thumb"));
        var season = Entry(EntryKind.Season, Key("b", 1), FakeTvTree.IdOf('N', 1), Tags("Primary"), series.StubKey, 1);
        var episode = Entry(EntryKind.Episode, Key("c", 1), FakeTvTree.IdOf('E', 1), Tags("Primary"), series.StubKey, 1, 1);
        CatalogEntry[] all = [series, season, episode];

        var jobs = new TvImageWork().Queue(all, all.Select(static e => e.StubKey).ToHashSet(), true).ToDictionary(static j => j.Kind);

        Assert.Equal((600, 2L << 20), (jobs[TvImageKind.SeriesPoster].MaxWidth, jobs[TvImageKind.SeriesPoster].MaxBytes));
        Assert.Equal(400, jobs[TvImageKind.SeasonPoster].MaxWidth);
        Assert.Equal((1280, 4L << 20), (jobs[TvImageKind.SeriesBackdrop].MaxWidth, jobs[TvImageKind.SeriesBackdrop].MaxBytes));
        Assert.Equal((1000, 1_572_864L), (jobs[TvImageKind.SeriesBanner].MaxWidth, jobs[TvImageKind.SeriesBanner].MaxBytes));
        Assert.Equal(1000, jobs[TvImageKind.SeriesLandscape].MaxWidth);
        Assert.Equal((480, 2L << 20), (jobs[TvImageKind.EpisodeThumb].MaxWidth, jobs[TvImageKind.EpisodeThumb].MaxBytes));
    }

    [Fact]
    public void OnlyPendingEntries_WithAValidTag_GetAJob_AndThumbsCanBeTurnedOff()
    {
        var series = Entry(EntryKind.Series, Key("a", 1), FakeTvTree.IdOf('S', 1), new Dictionary<string, string> { ["Primary"] = "ok1", ["Banner"] = "bad tag!", ["Backdrop"] = new string('x', 65) });
        var episode = Entry(EntryKind.Episode, Key("c", 1), FakeTvTree.IdOf('E', 1), Tags("Primary"), series.StubKey, 1, 1);
        var done = Entry(EntryKind.Episode, Key("c", 2), FakeTvTree.IdOf('E', 2), Tags("Primary"), series.StubKey, 1, 2);
        CatalogEntry[] all = [series, episode, done];
        var pending = new HashSet<string> { series.StubKey, episode.StubKey };

        var withThumbs = new TvImageWork().Queue(all, pending, true);
        var without = new TvImageWork().Queue(all, pending, false);

        Assert.Equal([TvImageKind.SeriesPoster, TvImageKind.EpisodeThumb], withThumbs.Select(static j => j.Kind));
        Assert.Equal([TvImageKind.SeriesPoster], without.Select(static j => j.Kind));
        Assert.Empty(new TvImageWork().Queue(all, new HashSet<string>(), true));
    }

    [Fact]
    public void TheFileNames_ComeFromFixedWordsAndIntegers()
    {
        var layout = new TvLayout();

        Assert.Equal(["poster", "poster", "backdrop", "banner", "landscape"], new[] { TvImageKind.SeriesPoster, TvImageKind.SeasonPoster, TvImageKind.SeriesBackdrop, TvImageKind.SeriesBanner, TvImageKind.SeriesLandscape }.Select(TvImageNames.For));
        Assert.Equal("S01E02-E03-thumb", TvImageNames.Thumb(layout, 1, 2, 3));
        Assert.Equal("S00E01-thumb", TvImageNames.Thumb(layout, 0, 1, null));
        Assert.Throws<ArgumentOutOfRangeException>(() => TvImageNames.For(TvImageKind.EpisodeThumb));
        Assert.All(new[] { "poster", "backdrop", "banner", "landscape", "S01E01-thumb", "S2024E1000-E1005-thumb" }, static n => Assert.True(TvImageNames.IsSafeBaseName(n), n));
        Assert.All(new[] { "", "../x", "poster/..", "a\\b", "Show-thumb", "S01E01.jpg", "S01E01-thumb/../x", null }, static n => Assert.False(TvImageNames.IsSafeBaseName(n), n));
    }

    // --- fetching

    private (string Folder, string BaseName) Folder(string sub, string baseName)
    {
        var folder = Path.Combine(_root, sub);
        Directory.CreateDirectory(folder);
        return (folder, baseName);
    }

    private TvImageJob Job(TvImageKind kind, int n = 0, long maxBytes = 2 << 20, int width = 480) => new(Key("k", n), kind, FakeTvTree.IdOf('E', n), "tag1", width, maxBytes);

    [Theory]
    [InlineData(TvImageKind.SeriesBanner, "Banner", "banner")]
    [InlineData(TvImageKind.SeriesLandscape, "Thumb", "landscape")]
    [InlineData(TvImageKind.SeriesBackdrop, "Backdrop", "backdrop")]
    [InlineData(TvImageKind.SeriesPoster, "Primary", "poster")]
    [InlineData(TvImageKind.SeasonPoster, "Primary", "poster")]
    [InlineData(TvImageKind.EpisodeThumb, "Primary", "S01E01-thumb")]
    public async Task EachKind_AsksForItsOwnImageType_AndIsStoredUnderItsOwnName(TvImageKind kind, string type, string name)
    {
        var (folder, _) = Folder("show", name);

        var result = await _fetcher.FetchTvAsync(_endpoint, Job(kind, 1, width: 777), name, _root, folder, new DiskQuota(1L << 30), CancellationToken.None);

        Assert.Equal(ImageOutcome.Stored, result.Outcome);
        Assert.Equal(Path.Combine(folder, name + ".jpg"), result.Path);
        var request = Assert.Single(_peer.Requests, static r => r.Contains("/Images/", StringComparison.Ordinal));
        Assert.Contains($"/Images/{type}?format=Jpg&maxWidth=777&quality=", request, StringComparison.Ordinal);
        Assert.EndsWith("&tag=tag1", request, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("webp")]
    [InlineData("exifbomb")]
    [InlineData("html")]
    [InlineData("wrongtype")]
    [InlineData("status500")]
    [InlineData("hugebanner")]
    public async Task AHostileImage_IsRefused_AndNothingIsWritten(string kind)
    {
        _peer.TvImageOverride = (_, _) => kind switch
        {
            "webp" => (200, "image/jpeg", ImageBuilder.WebP()),
            "exifbomb" => (200, "image/jpeg", ImageBuilder.Jpeg(8000, 8000, 4000, withExifThumbnail: true)),
            "html" => (200, "image/jpeg", "<html>"u8.ToArray()),
            "wrongtype" => (200, "text/html", ImageBuilder.Jpeg(480, 270, 3000)),
            "status500" => (500, "image/jpeg", ImageBuilder.Jpeg(480, 270, 3000)),
            _ => (200, "image/jpeg", ImageBuilder.Jpeg(1000, 200, 1_700_000)), // over the 1.5 MB cap of a banner, under the transport's 2 MB
        };
        var (folder, _) = Folder("show", "banner");
        var quota = new DiskQuota(1L << 30);

        var result = await _fetcher.FetchTvAsync(_endpoint, Job(TvImageKind.SeriesBanner, 1, 1_572_864, 1000), "banner", _root, folder, quota, CancellationToken.None);

        Assert.NotEqual(ImageOutcome.Stored, result.Outcome);
        Assert.Empty(Directory.GetFiles(folder));
        Assert.Equal(0, quota.UsedBytes);
    }

    [Fact]
    public async Task AJobWithAnUnsafeName_OrTag_IsSkipped_WithoutACall()
    {
        var (folder, _) = Folder("show", "poster");
        var before = _peer.Requests.Count;

        var badName = await _fetcher.FetchTvAsync(_endpoint, Job(TvImageKind.SeriesPoster), "../evil", _root, folder, new DiskQuota(1L << 30), CancellationToken.None);
        var badTag = await _fetcher.FetchTvAsync(_endpoint, Job(TvImageKind.SeriesPoster) with { Tag = "x y" }, "poster", _root, folder, new DiskQuota(1L << 30), CancellationToken.None);
        var badWidth = await _fetcher.FetchTvAsync(_endpoint, Job(TvImageKind.SeriesPoster, width: 99999), "poster", _root, folder, new DiskQuota(1L << 30), CancellationToken.None);

        Assert.All(new[] { badName, badTag, badWidth }, static r => Assert.Equal(ImageOutcome.Skipped, r.Outcome));
        Assert.Equal(before, _peer.Requests.Count);
        Assert.Empty(Directory.GetFiles(folder));
    }

    // --- the stage: budget, quota, peer errors

    private IReadOnlyList<CatalogEntry> Thumbs(int count)
    {
        var series = Entry(EntryKind.Series, Key("a", 1), FakeTvTree.IdOf('S', 1), []);
        return [series, .. Enumerable.Range(1, count).Select(n => Entry(EntryKind.Episode, Key("e", n), FakeTvTree.IdOf('E', 1, 1, n), Tags("Primary"), series.StubKey, 1, n))];
    }

    private Func<TvImageJob, (string Folder, string BaseName)?> Locate(IReadOnlyList<CatalogEntry> entries) => job =>
    {
        var entry = entries.Single(e => e.StubKey == job.StubKey);
        return Folder("Show/Season 01", TvImageNames.Thumb(new TvLayout(), entry.Season!.Value, entry.Episode!.Value, null));
    };

    [Fact]
    public async Task ABigSetOfThumbs_FinishesOverSeveralRuns_EachWithinItsBudget_InAtMostTwelve()
    {
        var entries = Thumbs(120);
        _peer.TvImageOverride = (_, _) =>
        {
            _time.Advance(TimeSpan.FromMinutes(1)); // every image takes a minute: ten fit in one run's budget
            return null;
        };
        var runner = new TvImageRunner(_fetcher, _time);
        var work = new TvImageWork();
        IReadOnlyCollection<string> pending = entries.Select(static e => e.StubKey).ToList();
        var runs = 0;
        var stored = 0;

        while (pending.Count > 0 && runs < 20)
        {
            var jobs = work.Queue(entries, pending, true);
            var run = await runner.RunAsync(_endpoint, jobs, Locate(entries), _root, new DiskQuota(1L << 30), null, CancellationToken.None);
            stored += run.Stored;
            pending = run.Pending;
            runs++;
            Assert.True(run.Stored <= 11, $"run {runs} stored {run.Stored}"); // never far past the ten minutes
        }

        Assert.Equal(120, stored);
        Assert.Equal(12, runs);
        Assert.Equal(120, Directory.GetFiles(Path.Combine(_root, "Show", "Season 01"), "*-thumb.jpg").Length);
    }

    [Fact]
    public async Task AFullQuota_StopsTheStage_WithPlaceholders_AndNothingPending()
    {
        var entries = Thumbs(5);
        var runner = new TvImageRunner(_fetcher, _time);
        var jobs = new TvImageWork().Queue(entries, entries.Select(static e => e.StubKey).ToHashSet(), true);

        var run = await runner.RunAsync(_endpoint, jobs, Locate(entries), _root, new DiskQuota(4_000), null, CancellationToken.None);

        Assert.True(run.QuotaReached);
        Assert.Empty(run.Pending);
        Assert.Equal(1, run.Stored); // 3,000 bytes fit, the next does not
    }

    [Fact]
    public async Task APeerThatStopsAnswering_LeavesEverythingPending_ForTheNextRun()
    {
        var entries = Thumbs(4);
        var dead = new PeerEndpoint(_endpoint.PeerKey, "Hops", new Uri("http://127.0.0.1:1"), "shoal-1a2b3c4d5e6f-4b0c1d2e", "9f2c4d6e8a0b1c3d5e7f9a0b2c4d6e8f");
        var jobs = new TvImageWork().Queue(entries, entries.Select(static e => e.StubKey).ToHashSet(), true);

        var run = await new TvImageRunner(_fetcher, _time).RunAsync(dead, jobs, Locate(entries), _root, new DiskQuota(1L << 30), null, CancellationToken.None);

        Assert.Equal(0, run.Stored);
        Assert.Equal(4, run.Pending.Count);
        Assert.False(run.QuotaReached);
    }

    [Fact]
    public async Task ABadImage_IsCounted_NotRetried_AndTheRestGoesOn()
    {
        var entries = Thumbs(3);
        var bad = entries.First(static e => e.Episode == 2).PeerItemId;
        _peer.TvImageOverride = (id, _) => id.ToString("N") == bad ? (200, "image/jpeg", ImageBuilder.WebP()) : null;
        var jobs = new TvImageWork().Queue(entries, entries.Select(static e => e.StubKey).ToHashSet(), true);

        var run = await new TvImageRunner(_fetcher, _time).RunAsync(_endpoint, jobs, Locate(entries), _root, new DiskQuota(1L << 30), null, CancellationToken.None);

        Assert.Equal((2, 1), (run.Stored, run.Refused));
        Assert.Empty(run.Pending);
    }

    [Fact]
    public async Task AStubThatIsGone_IsSkipped()
    {
        var entries = Thumbs(2);
        var jobs = new TvImageWork().Queue(entries, entries.Select(static e => e.StubKey).ToHashSet(), true);

        var run = await new TvImageRunner(_fetcher, _time).RunAsync(_endpoint, jobs, _ => null, _root, new DiskQuota(1L << 30), null, CancellationToken.None);

        Assert.Equal(0, run.Stored);
        Assert.Empty(run.Pending);
    }

    // --- the follow-up

    [Fact]
    public void TheScheduler_FollowsUpIn10Minutes_AtMostTwelveTimesInARow()
    {
        var regular = TimeSpan.FromHours(6);
        var pending = new[] { new SyncReport { ImagesPending = true } };
        var inARow = 0;
        var waits = new List<TimeSpan>();
        for (var i = 0; i < 13; i++)
        {
            (var wait, inARow) = SyncFollowUp.Next(pending, regular, inARow);
            waits.Add(wait);
        }

        Assert.Equal(12, waits.Count(static w => w == TimeSpan.FromMinutes(10)));
        Assert.Equal(regular, waits[12]);
        Assert.Equal((regular, 0), SyncFollowUp.Next([new SyncReport()], regular, 5));
        Assert.Equal((regular, 0), SyncFollowUp.Next([], regular, 0));
    }
}
