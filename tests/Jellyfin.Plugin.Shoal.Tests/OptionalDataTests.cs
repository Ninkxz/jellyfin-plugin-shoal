using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Jellyfin.Extensions.Json;
using Jellyfin.Plugin.Shoal.Sync;
using MediaBrowser.Model.Dto;
using Xunit;

namespace Jellyfin.Plugin.Shoal.Tests;

/// <summary>
/// Pilot round 7 (high): a Peer item with a trickplay manifest was silently dropped from the sync, because Jellyfin 12.1's <c>TrickplayInfoDto</c> cannot be
/// deserialized. Optional data (a trickplay manifest, attachments) must never cost a title.
/// </summary>
public sealed class OptionalDataTests
{
    private static readonly Guid Source = Guid.Parse("5a5a5a5a5a5a5a5a5a5a5a5a5a5a5a5a");

    // What a Jellyfin 12.1 server writes for BaseItemDto.Trickplay with Fields=Trickplay: media source id, then thumbnail width, then the TrickplayInfoDto
    // members in PascalCase (the same names the plugin reads from the raw JSON). The numbers are those the lab Peer reported (320 wide, 10 by 10 tiles, 60
    // thumbnails, one every 10 seconds). Nothing here is a secret or an id of a real server.
    private static Dictionary<string, object?> Manifest(string sourceId) => new()
    {
        ["Trickplay"] = new Dictionary<string, object?>
        {
            [sourceId] = new Dictionary<string, object?>
            {
                ["320"] = new Dictionary<string, object?>
                {
                    ["Width"] = 320,
                    ["Height"] = 180,
                    ["TileWidth"] = 10,
                    ["TileHeight"] = 10,
                    ["ThumbnailCount"] = 60,
                    ["Interval"] = 10000,
                    ["Bandwidth"] = 351207,
                },
            },
        },
    };

    [Fact]
    public void TheRealShape_CannotBeDeserializedIntoBaseItemDto_WhichIsWhyItIsReadByHand()
    {
        var json = JsonSerializer.Serialize(new Dictionary<string, object?> { ["Id"] = Guid.NewGuid().ToString("N"), ["Name"] = "Movie", ["Trickplay"] = Manifest("abc")["Trickplay"] });

        Assert.ThrowsAny<Exception>(() => JsonSerializer.Deserialize<BaseItemDto>(json, JsonDefaults.Options));
    }

    [Fact]
    public async Task ATitleWithATrickplayManifest_IsSynced_AndKeepsItsLevels()
    {
        await using var rig = await SyncRig.CreateAsync(p => p.Add("The Matrix", 1999, tmdb: 603, configure: m =>
        {
            m.Id = Source;
            m.ExtraDetail = Manifest(Source.ToString("N"));
        }));

        var report = await rig.SyncAsync();

        Assert.True(report.Added == 1, $"{report.Result} {report.Error} added {report.Added} skipped {report.SkippedItems}");
        Assert.Equal(0, report.SkippedItems);
        var level = Assert.Single(Assert.Single(rig.Entries).Sources[0].Trickplay);
        Assert.Equal((320, 180, 10, 10, 60, 10000, 351207), (level.Width, level.Height, level.TileWidth, level.TileHeight, level.ThumbnailCount, level.IntervalMs, level.Bandwidth));
        Assert.Equal(0, rig.Reader.OptionalDataDropped);
    }

    [Theory]
    [InlineData("array")]
    [InlineData("string")]
    [InlineData("level-is-a-string")]
    [InlineData("width-is-a-string")]
    [InlineData("huge-number")]
    [InlineData("deep")]
    [InlineData("null")]
    public async Task AManifestThatIsWrong_CostsTheThumbnails_NeverTheTitle(string kind)
    {
        object? manifest = kind switch
        {
            "array" => new[] { 1, 2 },
            "string" => "no",
            "level-is-a-string" => new Dictionary<string, object?> { [Source.ToString("N")] = new Dictionary<string, object?> { ["320"] = "x" } },
            "width-is-a-string" => new Dictionary<string, object?> { [Source.ToString("N")] = new Dictionary<string, object?> { ["320"] = new Dictionary<string, object?> { ["Width"] = "320", ["Height"] = 180 } } },
            "huge-number" => new Dictionary<string, object?> { [Source.ToString("N")] = new Dictionary<string, object?> { ["320"] = new Dictionary<string, object?> { ["Width"] = 320, ["Height"] = 180, ["TileWidth"] = 10, ["TileHeight"] = 10, ["ThumbnailCount"] = 99999999999L, ["Interval"] = 1, ["Bandwidth"] = 1 } } },
            "deep" => new Dictionary<string, object?> { ["a"] = new Dictionary<string, object?> { ["b"] = new Dictionary<string, object?> { ["c"] = new Dictionary<string, object?> { ["d"] = new[] { 1 } } } } },
            _ => null,
        };
        await using var rig = await SyncRig.CreateAsync(p => p.Add("The Matrix", 1999, tmdb: 603, configure: m =>
        {
            m.Id = Source;
            m.ExtraDetail = new Dictionary<string, object?> { ["Trickplay"] = manifest };
        }));

        var report = await rig.SyncAsync();

        Assert.True(report.Added == 1, $"{report.Result} {report.Error} added {report.Added} skipped {report.SkippedItems}");
        Assert.Equal(0, report.SkippedItems);
        Assert.Empty(Assert.Single(rig.Entries).Sources[0].Trickplay);
    }

    [Fact]
    public async Task AnOutOfRangeLevel_IsDroppedByTheSanitizer_AndTheTitleIsKept()
    {
        var manifest = Manifest(Source.ToString("N"));
        ((Dictionary<string, object?>)((Dictionary<string, object?>)((Dictionary<string, object?>)manifest["Trickplay"]!)[Source.ToString("N")]!)["320"]!)["ThumbnailCount"] = 0;
        await using var rig = await SyncRig.CreateAsync(p => p.Add("The Matrix", 1999, tmdb: 603, configure: m =>
        {
            m.Id = Source;
            m.ExtraDetail = manifest;
        }));

        var report = await rig.SyncAsync();

        Assert.True(report.Added == 1, $"{report.Result} {report.Error} added {report.Added} skipped {report.SkippedItems}");
        Assert.Empty(Assert.Single(rig.Entries).Sources[0].Trickplay);
    }

    [Fact]
    public async Task UnreadableAttachments_AreLeftOut_AndTheTitleIsKept_AndCounted()
    {
        await using var rig = await SyncRig.CreateAsync(p => p.Add("The Matrix", 1999, tmdb: 603, configure: m =>
        {
            m.Id = Source;
            m.ExtraDetail = Manifest(Source.ToString("N"));
            m.Sources = [new PeerSource { Id = Source }];
            m.SourceExtra = new Dictionary<string, object?> { ["MediaAttachments"] = new[] { new Dictionary<string, object?> { ["Index"] = new Dictionary<string, object?> { ["x"] = 1 }, ["FileName"] = "a.ttf" } } };
        }));

        var report = await rig.SyncAsync();

        Assert.True(report.Added == 1, $"{report.Result} {report.Error} added {report.Added} skipped {report.SkippedItems}");
        Assert.Equal(0, report.SkippedItems);
        Assert.Equal(1, rig.Reader.OptionalDataDropped);
        Assert.Empty(Assert.Single(rig.Entries).Sources[0].Trickplay); // the retry leaves every optional part out
    }

    [Fact]
    public async Task AnItemThatIsBrokenInItself_IsStillSkipped_NotHiddenByTheRetry()
    {
        await using var rig = await SyncRig.CreateAsync(p => p.Add("The Matrix", 1999, tmdb: 603, configure: m =>
        {
            m.Id = Source;
            m.ExtraDetail = new Dictionary<string, object?> { ["Genres"] = new Dictionary<string, object?> { ["not"] = "a list" }, ["MediaSources"] = "nope" };
        }));

        var report = await rig.SyncAsync();

        Assert.Equal(0, report.Added);
        Assert.Equal(1, report.SkippedItems);
    }

    // --- a manifest the Peer generates later reaches titles that are already synced

    [Fact]
    public async Task AManifestThePeerGeneratesLater_ReachesAnExistingTitle_WithinADay_EvenWithTheSameEtag()
    {
        PeerMovie? movie = null;
        await using var rig = await SyncRig.CreateAsync(p => movie = p.Add("The Matrix", 1999, tmdb: 603, configure: m => m.Id = Source));
        await rig.SyncAsync();
        Assert.Empty(Assert.Single(rig.Entries).Sources[0].Trickplay);
        movie!.ExtraDetail = Manifest(Source.ToString("N")); // the etag stays e1

        rig.Time.Advance(TimeSpan.FromHours(3));
        await rig.SyncAsync();
        Assert.Empty(Assert.Single(rig.Entries).Sources[0].Trickplay); // not yet a day

        rig.Time.Advance(TimeSpan.FromHours(22));
        var report = await rig.SyncAsync();

        Assert.Equal(0, report.SkippedItems);
        var level = Assert.Single(Assert.Single(rig.Entries).Sources[0].Trickplay);
        Assert.Equal((320, 60), (level.Width, level.ThumbnailCount));
    }

    [Fact]
    public async Task ATitleWithoutTrickplay_IsLookedAtOnceADay_NotOnEverySync()
    {
        await using var rig = await SyncRig.CreateAsync(p => p.Add("The Matrix", 1999, tmdb: 603));
        await rig.SyncAsync();
        rig.Time.Advance(TimeSpan.FromHours(25));
        await rig.SyncAsync(); // looked at (the day is up)
        var detailCalls = rig.Peer.Requests.Count(static r => r.Contains("Ids=", StringComparison.Ordinal));

        rig.Time.Advance(TimeSpan.FromHours(2));
        await rig.SyncAsync();
        rig.Time.Advance(TimeSpan.FromHours(2));
        await rig.SyncAsync();

        Assert.Equal(detailCalls, rig.Peer.Requests.Count(static r => r.Contains("Ids=", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task TheDailyLook_IsBoundedPerSync()
    {
        await using var rig = await SyncRig.CreateAsync(p =>
        {
            for (var i = 0; i < 70; i++)
            {
                p.Add("Movie " + i.ToString(System.Globalization.CultureInfo.InvariantCulture), 2000, tmdb: 1000 + i);
            }
        });
        await rig.SyncAsync();
        rig.Time.Advance(TimeSpan.FromHours(25));
        var before = rig.Peer.Requests.Count(static r => r.Contains("Ids=", StringComparison.Ordinal));

        await rig.SyncAsync();

        var asked = rig.Peer.Requests.Where(static r => r.Contains("Ids=", StringComparison.Ordinal)).Skip(before).SelectMany(static r => r.Split("Ids=")[1].Split('&')[0].Split(',')).Count();
        Assert.Equal(50, asked);
        rig.Time.Advance(TimeSpan.FromMinutes(30));
        await rig.SyncAsync(); // the other 20 follow on the next sync
        Assert.Equal(70, rig.Entries.Count(static e => e.TrickplayCheckedUtc is not null));
    }

    [Fact]
    public async Task WithTheSwitchOff_NoTitleIsLookedAtAgain()
    {
        await using var rig = await SyncRig.CreateAsync(p => p.Add("The Matrix", 1999, tmdb: 603));
        rig.Settings = new SyncSettings(25, 10, 500, 20, 2, Jellyfin.Plugin.Shoal.Configuration.ArtQuality.Standard) { TrickplayRefresh = false };
        await rig.SyncAsync();
        var before = rig.Peer.Requests.Count(static r => r.Contains("Ids=", StringComparison.Ordinal));

        rig.Time.Advance(TimeSpan.FromHours(30));
        await rig.SyncAsync();

        Assert.Equal(before, rig.Peer.Requests.Count(static r => r.Contains("Ids=", StringComparison.Ordinal)));
    }

    [Fact]
    public void TheTrickplaySanitizer_FailsSoft_WhenTheManifestIsTheWrongShape()
    {
        var sanitizer = new MetadataSanitizer(new Ratings(), null, new ThrowingTrickplay());
        var dto = new BaseItemDto { Id = Source, Name = "Movie", MediaSources = [new MediaBrowser.Model.Dto.MediaSourceInfo { Id = Source.ToString("N") }] };

        var movie = sanitizer.Sanitize(dto);

        Assert.NotNull(movie);
        Assert.All(movie!.Sources, static s => Assert.Null(s.Trickplay));
    }

    private sealed class ThrowingTrickplay : ITrickplaySanitizer
    {
        public IReadOnlyList<SanitizedTrickplay> Read(BaseItemDto dto, Guid peerSourceId) => throw new InvalidOperationException("boom");
    }

    private sealed class Ratings : Jellyfin.Plugin.Shoal.Sync.IRatingTable
    {
        public bool IsRecognized(string rating) => true;
    }
}
