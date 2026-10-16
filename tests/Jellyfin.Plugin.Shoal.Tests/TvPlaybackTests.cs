using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.Authorization;
using Jellyfin.Plugin.Shoal.Peers;
using Jellyfin.Plugin.Shoal.Playback;
using Jellyfin.Plugin.Shoal.State;
using Jellyfin.Plugin.Shoal.Sync;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Shoal.Tests;

/// <summary>
/// The ids and catalog entries of the TV items of the playback harness: one series with a season and three episodes, and one episode of another series, in a
/// <c>tvshows</c> library next to the movie libraries.
/// </summary>
internal static class TvIds
{
    public const string MappingKey = "tv-1";
    public const string SeriesA = "55555555555555555555555555555555";
    public const string SeriesB = "99999999999999999999999999999999";

    public static readonly Guid Series = Guid.Parse("d0000000-0000-0000-0000-000000000001");
    public static readonly Guid Season = Guid.Parse("d0000000-0000-0000-0000-000000000002");
    public static readonly Guid Ep1 = Guid.Parse("d0000000-0000-0000-0000-000000000003");
    public static readonly Guid Ep2 = Guid.Parse("d0000000-0000-0000-0000-000000000004");
    public static readonly Guid Ep3 = Guid.Parse("d0000000-0000-0000-0000-000000000005");
    public static readonly Guid Other = Guid.Parse("d0000000-0000-0000-0000-000000000006");

    public static readonly Guid PeerEp1 = Guid.Parse("61616161-6161-6161-6161-616161616161");
    public static readonly Guid SourceEp1 = Guid.Parse("62626262-6262-6262-6262-626262626262");
    public static readonly Guid PeerEp2 = Guid.Parse("63636363-6363-6363-6363-636363636363");
    public static readonly Guid SourceEp2 = Guid.Parse("64646464-6464-6464-6464-646464646464");
    public static readonly Guid PeerEp3 = Guid.Parse("65656565-6565-6565-6565-656565656565");
    public static readonly Guid SourceEp3 = Guid.Parse("66666666-6666-6666-6666-666666666667");
    public static readonly Guid PeerOther = Guid.Parse("67676767-6767-6767-6767-676767676767");
    public static readonly Guid SourceOther = Guid.Parse("68686868-6868-6868-6868-686868686868");

    public static IReadOnlyList<Guid> All { get; } = [Series, Season, Ep1, Ep2, Ep3, Other];

    public static Guid SourceOf(string peerItem) =>
        peerItem == PeerEp1.ToString("N") ? SourceEp1
        : peerItem == PeerEp2.ToString("N") ? SourceEp2
        : peerItem == PeerEp3.ToString("N") ? SourceEp3
        : peerItem == PeerOther.ToString("N") ? SourceOther
        : PlaybackParts.PeerSource;

    internal static void AddCatalog(CatalogDocument document)
    {
        document.Entries.Add(Container(EntryKind.Series, "tvseriesaaaaaaaa", SeriesA, null, "Show (2020)", null));
        document.Entries.Add(Container(EntryKind.Series, "tvseriesbbbbbbbb", SeriesB, null, "Other Show (2019)", null));
        document.Entries.Add(Container(EntryKind.Season, "tvseasonaaaaaaaa", "56565656565656565656565656565656", "tvseriesaaaaaaaa", "Show (2020)/Season 01", SeriesA));
        document.Entries.Add(Episode("tveponeaaaaaaaaa", PeerEp1, SourceEp1, "tvseriesaaaaaaaa", SeriesA, "Show (2020)", 1));
        document.Entries.Add(Episode("tvepoteaaaaaaaaa", PeerEp2, SourceEp2, "tvseriesaaaaaaaa", SeriesA, "Show (2020)", 2));
        document.Entries.Add(Episode("tveptreeaaaaaaaa", PeerEp3, SourceEp3, "tvseriesaaaaaaaa", SeriesA, "Show (2020)", 3));
        document.Entries.Add(Episode("tvepotherbbbbbbb", PeerOther, SourceOther, "tvseriesbbbbbbbb", SeriesB, "Other Show (2019)", 1));
    }

    internal static async Task EnsureAsync(MatrixHarness h)
    {
        await h.State.UpdateAsync(s =>
        {
            var peer = s.Home.Peers[0];
            if (peer.Libraries.All(static l => l.MappingKey != MappingKey))
            {
                peer.Libraries.Add(new LibraryMapping
                {
                    MappingKey = MappingKey,
                    RemoteName = "Shows",
                    LocalLibraryName = "Hops Shows",
                    Subfolder = "tv",
                    CollectionType = "tvshows",
                    Access = new LibraryAccess { Mode = AccessMode.Selected, SelectedUserIds = { MatrixWorld.Admin, MatrixWorld.Selected } },
                });
            }
        });
        var paths = new Dictionary<Guid, string>
        {
            [Series] = "Show (2020)",
            [Season] = "Show (2020)/Season 01",
            [Ep1] = "Show (2020)/Season 01/S01E01.strm",
            [Ep2] = "Show (2020)/Season 01/S01E02.strm",
            [Ep3] = "Show (2020)/Season 01/S01E03.strm",
            [Other] = "Other Show (2019)/Season 01/S01E01.strm",
        };
        foreach (var (id, path) in paths)
        {
            h.World.Federated[id] = new FederatedItem(h.World.PeerKey, MappingKey);
            h.World.TvPaths[id] = path;
            foreach (var user in new[] { MatrixWorld.Admin, MatrixWorld.Selected, MatrixWorld.Other, MatrixWorld.NewUser, MatrixWorld.ShareUser, MatrixWorld.Child })
            {
                h.World.Visible[(user, id)] = true;
            }
        }
    }

    private static CatalogEntry Container(EntryKind kind, string key, string peerId, string? parent, string folder, string? seriesId) => new()
    {
        Kind = kind,
        StubKey = key,
        PeerItemId = peerId,
        MappingKey = MappingKey,
        ParentKey = parent,
        Season = kind == EntryKind.Season ? 1 : null,
        PeerSeriesId = seriesId ?? string.Empty,
        FolderRel = folder,
        Title = "Show",
        State = EntryState.Active,
        PeerEtag = "etag-1",
    };

    private static CatalogEntry Episode(string key, Guid peerItem, Guid source, string parent, string series, string folder, int number) => new()
    {
        Kind = EntryKind.Episode,
        StubKey = key,
        PeerItemId = peerItem.ToString("N"),
        PeerMediaSourceId = source.ToString("N"),
        MappingKey = MappingKey,
        ParentKey = parent,
        PeerSeriesId = series,
        Season = 1,
        Episode = number,
        PeerEtag = "etag-1",
        Title = "Episode " + number.ToString(CultureInfo.InvariantCulture),
        SizeBytes = 1_000_000_000,
        Bitrate = 4_000_000,
        Container = "mkv",
        RuntimeTicks = 24_000_000_000,
        State = EntryState.Active,
        StubRelPath = $"{folder}/Season 01/S01E{number.ToString("00", CultureInfo.InvariantCulture)}.strm",
        Sources = [new CatalogSource { StubKey = key, PeerMediaSourceId = source.ToString("N") }],
    };
}

/// <summary>
/// K39 acceptance (DESIGN v2.7.8, T-TV-8): a series or season id is never played (404 on every route of 6.1, no Peer request), the T-SEC-15 rows hold for
/// the TV ids as for a movie, PlaybackInfo of an episode has the 6.2.1 shape, the binge handover allowance, session reports of an episode, and the trickplay
/// recheck rotation.
/// </summary>
public sealed class TvPlaybackTests : IClassFixture<PlaybackFixture>, IAsyncLifetime
{
    private const string PeerToken = FakeJellyfinPeer.DefaultToken;

    private static readonly string Movie = MatrixWorld.Item.ToString("N");
    private static readonly string AdminDevice = "device-" + MatrixWorld.Admin.ToString("N")[^4..];

    private readonly PlaybackFixture _fixture;

    public TvPlaybackTests(PlaybackFixture fixture) => _fixture = fixture;

    private MatrixHarness H => _fixture.H;

    private PlaybackParts P => H.Filter!.Playback!;

    public static TheoryData<string> Routes() =>
    [
        "static-get", "static-head", "static-container", "subtitle", "subtitle-ticks", "playbackinfo-post", "playbackinfo-get", "trickplay", "attachment",
    ];

    public async Task InitializeAsync()
    {
        await H.ResetAsync();
        await TvIds.EnsureAsync(H);
        await H.State.UpdateAsync(s =>
        {
            s.Home.Peers[0].MaxConcurrentStreams = 2;
            s.Home.Peers[0].MaxConcurrentTranscodes = 2;
        });
        await H.Filter!.Index.RebuildAsync();
        P.Time.Advance(TimeSpan.FromHours(1)); // refills every bucket, ends every slot and ages every grant
        foreach (var user in new[] { MatrixWorld.Admin, MatrixWorld.Selected, MatrixWorld.Other })
        {
            foreach (var grant in P.Grants.ForUser(user))
            {
                P.Grants.Remove(grant.GrantId);
            }
        }

        P.MaxStreams = 2;
        P.Slots.Holding.Clear();
        P.Registry.SetStatus(H.World.PeerKey, PeerStatus.Online);
        P.Registry.RecordPinVerified(H.World.PeerKey);
        P.Registry.RecordAuthVerified(H.World.PeerKey);
        H.Peer.PlaybackInfoReply = (id, _) => Reply.Json(Answer(id, transcode: false));
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static string PathFor(string route, Guid id)
    {
        var n = id.ToString("N");
        return route switch
        {
            "static-get" or "static-head" => $"/Videos/{n}/stream?Static=true&MediaSourceId={n}",
            "static-container" => $"/Videos/{n}/stream.mkv?static=true&mediaSourceId={n}",
            "subtitle" => $"/Videos/{n}/{n}/Subtitles/2/Stream.vtt",
            "subtitle-ticks" => $"/Videos/{n}/{n}/Subtitles/2/0/Stream.srt",
            "playbackinfo-post" or "playbackinfo-get" => $"/Items/{n}/PlaybackInfo",
            "trickplay" => $"/Videos/{n}/Trickplay/320/0.jpg?mediaSourceId={n}",
            "attachment" => $"/Videos/{n}/{n}/Attachments/1",
            _ => throw new ArgumentOutOfRangeException(nameof(route)),
        };
    }

    private static HttpMethod MethodFor(string route) => route switch
    {
        "static-head" => HttpMethod.Head,
        "playbackinfo-post" => HttpMethod.Post,
        _ => HttpMethod.Get,
    };

    private static Dictionary<string, object?> Answer(string peerItem, bool transcode)
    {
        var source = TvIds.SourceOf(peerItem).ToString("N");
        return new Dictionary<string, object?>
        {
            ["MediaSources"] = new[]
            {
                new Dictionary<string, object?>
                {
                    ["Id"] = source,
                    ["Protocol"] = "File",
                    ["Path"] = "/media/tv/Show (2020)/Season 01/Show - S01E01.mkv",
                    ["Container"] = "mkv",
                    ["Size"] = 1_000_000_000L,
                    ["RunTimeTicks"] = 24_000_000_000L,
                    ["SupportsDirectPlay"] = !transcode,
                    ["SupportsDirectStream"] = !transcode,
                    ["SupportsTranscoding"] = true,
                    ["DefaultAudioStreamIndex"] = 1,
                    ["TranscodingUrl"] = transcode ? $"/videos/{peerItem}/master.m3u8?DeviceId=abc&MediaSourceId={source}&VideoCodec=h264&ApiKey={PeerToken}" : null,
                    ["TranscodingSubProtocol"] = transcode ? "hls" : null,
                    ["TranscodingContainer"] = transcode ? "ts" : null,
                    ["MediaStreams"] = new object[]
                    {
                        new Dictionary<string, object?> { ["Index"] = 0, ["Type"] = "Video", ["Codec"] = "h264", ["Width"] = 1920, ["Height"] = 1080, ["IsDefault"] = true },
                        new Dictionary<string, object?> { ["Index"] = 1, ["Type"] = "Audio", ["Codec"] = "aac", ["Language"] = "eng", ["Channels"] = 2, ["IsDefault"] = true },
                    },
                },
            },
            ["PlaySessionId"] = "peer-play-session-0123456789",
        };
    }

    private string Sign(Guid user, params Claim[] extra) => H.World.Sign("tv-" + user.ToString("N") + extra.Length.ToString(CultureInfo.InvariantCulture), MatrixWorld.Principal(user, extra));

    private async Task<HttpResponseMessage> SendAsync(string? principal, string route, Guid id)
    {
        await H.Filter!.Index.RebuildAsync();
        return await H.SendAsync(principal, MethodFor(route), PathFor(route, id), r =>
        {
            if (route == "playbackinfo-post")
            {
                r.Content = new StringContent("{}", Encoding.UTF8, "application/json");
            }
        });
    }

    private async Task<HttpResponseMessage> PlaybackInfoAsync(Guid item, Guid? user = null)
    {
        await H.Filter!.Index.RebuildAsync();
        return await H.SendAsync(Sign(user ?? MatrixWorld.Admin), HttpMethod.Post, $"/Items/{item:N}/PlaybackInfo", r => r.Content = new StringContent("{}", Encoding.UTF8, "application/json"));
    }

    private static async Task<string?> ErrorCodeAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = (JsonObject)JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        return (string?)json["ErrorCode"];
    }

    // ---------------- the container ids (T-TV-8) ----------------

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task ASeriesOrSeasonId_IsNeverPlayed_OnAnyRouteOf61_AndTheFakePeerSeesNothing(string route)
    {
        foreach (var id in new[] { TvIds.Series, TvIds.Season })
        {
            var before = H.Peer.Requests.Count;

            using var response = await SendAsync(Sign(MatrixWorld.Admin), route, id);

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.False(response.Headers.Contains(FakeCoreController.Header), "core's action ran for a federated container");
            Assert.Equal(before, H.Peer.Requests.Count);
            if (route != "static-head")
            {
                Assert.Empty(await response.Content.ReadAsByteArrayAsync());
            }
        }
    }

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task T_SEC_15_TheRefusalsForTvIds_AreThoseOfAMovie_AndCoreNeverRuns(string route)
    {
        var apiKey = Sign(MatrixWorld.Admin, new Claim(CallerContext.IsApiKeyClaim, "True"));
        var principals = new (string Name, string? Key)[]
        {
            ("no credentials", null),
            ("an API key", apiKey),
            ("a user outside the library", Sign(MatrixWorld.Other)),
            ("a restricted user", Sign(MatrixWorld.Child)),
        };
        foreach (var (name, key) in principals)
        {
            var before = H.Peer.Requests.Count;
            using var baseline = await SendAsync(key, route, MatrixWorld.Item);
            var expected = baseline.StatusCode;
            Assert.False(baseline.Headers.Contains(FakeCoreController.Header), name + ": core ran for the movie baseline");
            foreach (var id in TvIds.All)
            {
                using var response = await SendAsync(key, route, id);

                Assert.False(response.Headers.Contains(FakeCoreController.Header), $"{name}: core's action ran for {id}");
                Assert.True(expected == response.StatusCode, $"{name}, {route}: the movie answers {(int)expected}, {id} answers {(int)response.StatusCode}");
            }

            Assert.Equal(before, H.Peer.Requests.Count); // every row here is a refusal: not one request reached the Peer
        }
    }

    // ---------------- PlaybackInfo of an episode (T-SEC-20) ----------------

    [Fact]
    public async Task T_SEC_20_PlaybackInfoOfAnEpisode_HasTheShapeOf621_AndHomesEveryPeerId()
    {
        using var response = await PlaybackInfoAsync(TvIds.Ep1);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var call = H.Peer.Requests.Last(static r => r.Path.EndsWith("/PlaybackInfo", StringComparison.Ordinal));
        Assert.Equal($"/Items/{TvIds.PeerEp1:N}/PlaybackInfo", call.Path);
        Assert.Equal(string.Empty, call.Query);
        var body = (JsonObject)JsonNode.Parse(call.Body)!;
        Assert.Equal(TvIds.SourceEp1.ToString("N"), (string?)body["MediaSourceId"]);
        Assert.False((bool)body["AutoOpenLiveStream"]!);
        var text = await response.Content.ReadAsStringAsync();
        var json = (JsonObject)JsonNode.Parse(text)!;
        Assert.Null((string?)json["ErrorCode"]);
        var source = json["MediaSources"]!.AsArray().Single()!;
        Assert.Equal(TvIds.Ep1.ToString("N"), (string?)source["Id"]);
        foreach (var leak in new[] { TvIds.PeerEp1.ToString("N"), TvIds.SourceEp1.ToString("N"), TvIds.SeriesA, "peer-play-session", PeerToken, "/media/tv" })
        {
            Assert.DoesNotContain(leak, text, StringComparison.Ordinal);
        }

        var grant = P.Grants.ForUser(MatrixWorld.Admin).Single(g => g.LocalItemId == TvIds.Ep1);
        Assert.Equal(TvIds.SeriesA, grant.SeriesKey);
        Assert.Equal(TvIds.PeerEp1, grant.PeerItemId);
        Assert.Equal(TvIds.SourceEp1, grant.PeerMediaSourceId);
    }

    [Fact]
    public async Task AMovieGrant_HasNoSeriesKey()
    {
        H.Peer.PlaybackInfoReply = (id, _) => Reply.Json(Answer(id, transcode: false));

        using var response = await PlaybackInfoAsync(MatrixWorld.Item);

        Assert.Null(await ErrorCodeAsync(response));
        Assert.Equal(string.Empty, P.Grants.ForUser(MatrixWorld.Admin).Single(g => g.LocalItemId == MatrixWorld.Item).SeriesKey);
    }

    // ---------------- the binge handover allowance ----------------

    private StaticLease Hold(Guid user, Guid item, string series, string device)
    {
        var lease = P.StreamSlots.TryOpenStatic(user, item, H.World.PeerKey, 2, out var denial, new SlotOrigin(series, device));
        Assert.True(lease is not null, "the setup stream was refused: " + denial);
        return lease;
    }

    [Fact]
    public async Task WithTwoStreamsAllowed_TheNextEpisodeOfTheSeriesOnTheSameDevice_Succeeds()
    {
        using var playing = Hold(MatrixWorld.Admin, TvIds.Ep1, TvIds.SeriesA, AdminDevice);

        using var response = await PlaybackInfoAsync(TvIds.Ep2);

        Assert.Null(await ErrorCodeAsync(response)); // the canary passed too: this is the normal answer
        Assert.Single(P.Grants.ForUser(MatrixWorld.Admin), g => g.LocalItemId == TvIds.Ep2);
    }

    [Fact]
    public async Task TheThirdEpisode_PlaysWhileTheFirstStillHoldsItsSlot_N9()
    {
        Hold(MatrixWorld.Admin, TvIds.Ep1, TvIds.SeriesA, AdminDevice).Dispose(); // the first episode ended: no connection is open, its stop report never came
        using var second = Hold(MatrixWorld.Admin, TvIds.Ep2, TvIds.SeriesA, AdminDevice);

        using var response = await PlaybackInfoAsync(TvIds.Ep3);

        Assert.Null(await ErrorCodeAsync(response));
    }

    [Fact]
    public async Task ADifferentSeries_OnTheSameUser_IsRefused()
    {
        using var playing = Hold(MatrixWorld.Admin, TvIds.Other, TvIds.SeriesB, AdminDevice);

        using var response = await PlaybackInfoAsync(TvIds.Ep2);

        Assert.Equal("RateLimitExceeded", await ErrorCodeAsync(response));
    }

    [Fact]
    public async Task TheSameSeriesOnAnotherDevice_IsRefused()
    {
        using var playing = Hold(MatrixWorld.Admin, TvIds.Ep1, TvIds.SeriesA, "another-device");

        using var response = await PlaybackInfoAsync(TvIds.Ep2);

        Assert.Equal("RateLimitExceeded", await ErrorCodeAsync(response));
    }

    [Fact]
    public async Task AMovieThatIsPlaying_GetsNoAllowance_ForTheNextMovie()
    {
        using var playing = Hold(MatrixWorld.Admin, MatrixWorld.ItemB, string.Empty, AdminDevice);

        using var response = await PlaybackInfoAsync(MatrixWorld.Item);

        Assert.Equal("RateLimitExceeded", await ErrorCodeAsync(response));
    }

    [Fact]
    public async Task TheAllowance_NeverExceedsThePeerTotal()
    {
        using var theirs = Hold(MatrixWorld.Selected, TvIds.Other, TvIds.SeriesB, "friend-device");
        using var more = Hold(MatrixWorld.Other, MatrixWorld.ItemB, string.Empty, "friend-device"); // the peer now carries 2 of 2, none of them mine to replace

        using var response = await PlaybackInfoAsync(TvIds.Ep2);

        Assert.Equal("RateLimitExceeded", await ErrorCodeAsync(response));
    }

    [Fact]
    public async Task TheAllowance_NeverExceedsTheStreamsPerUser()
    {
        P.MaxStreams = 1;
        using var playing = Hold(MatrixWorld.Admin, MatrixWorld.ItemB, string.Empty, AdminDevice); // a movie is not an episode of the series: nothing is replaced

        using var response = await PlaybackInfoAsync(TvIds.Ep2);

        Assert.Equal("RateLimitExceeded", await ErrorCodeAsync(response));
    }

    [Fact]
    public void TheNextEpisodeOfTheSeriesOnTheDevice_TakesThePlaceOfTheLastOne_NotAnotherPlace()
    {
        var slots = new StreamSlots(P.Time, () => 4);
        var user = MatrixWorld.Admin;
        var origin = new SlotOrigin(TvIds.SeriesA, "tv-1");
        slots.TryOpenStatic(user, TvIds.Ep1, "peer", 4, out _, origin)!.Dispose(); // each episode's stream has ended when the next one starts
        slots.TryOpenStatic(user, TvIds.Ep2, "peer", 4, out _, origin)!.Dispose();
        using var third = slots.TryOpenStatic(user, TvIds.Ep3, "peer", 4, out var denialThird, origin);

        Assert.NotNull(third);
        Assert.Equal(SlotDenial.None, denialThird);
        Assert.Equal(1, slots.ActiveSlots(user)); // one place however many episodes
        using var movie = slots.TryOpenStatic(user, MatrixWorld.Item, "peer", 4, out _, default); // a second stream of another kind is a place of its own
        Assert.NotNull(movie);
        Assert.Equal(2, slots.ActiveSlots(user));
    }

    [Fact]
    public void TheAllowance_AppliesToTheHlsSlotToo_AndToNothingElse()
    {
        var slots = new StreamSlots(P.Time, () => 4);
        Grant Make(Guid item, string series, string device) => new()
        {
            GrantId = Guid.NewGuid().ToString("N"),
            LocalUserId = MatrixWorld.Admin,
            LocalItemId = item,
            CallerDeviceId = device,
            SeriesKey = series,
            LocalPlaySessionId = Guid.NewGuid().ToString("N"),
            PeerKey = "peer",
            PeerItemId = Guid.NewGuid(),
            PeerMediaSourceId = Guid.NewGuid(),
        };

        // peerMax 2: half is 1.
        Assert.True(slots.TryTakeHls(Make(TvIds.Ep1, TvIds.SeriesA, "tv"), 2, 4, out _));
        Assert.False(slots.TryTakeHls(Make(TvIds.Other, TvIds.SeriesB, "tv"), 2, 4, out var otherSeries));
        Assert.Equal(SlotDenial.Peer, otherSeries);
        Assert.False(slots.TryTakeHls(Make(MatrixWorld.Item, string.Empty, "tv"), 2, 4, out _));
        Assert.False(slots.TryTakeHls(Make(TvIds.Ep2, TvIds.SeriesA, "phone"), 2, 4, out _));
        var ep2 = Make(TvIds.Ep2, TvIds.SeriesA, "tv");
        Assert.True(slots.TryTakeHls(ep2, 2, 4, out _)); // the handover: the half of the peer plus one, Ep1 is still counted
        Assert.False(slots.TryTakeHls(Make(TvIds.Ep3, TvIds.SeriesA, "tv"), 2, 4, out var total)); // the peer holds 2 of 2 and both are playing
        Assert.Equal(SlotDenial.Peer, total);
        P.Time.Advance(StreamSlots.HlsSupersedeAfter + TimeSpan.FromSeconds(1));
        slots.TouchHls(ep2.GrantId); // Ep2 still plays; Ep1 went quiet
        Assert.True(slots.TryTakeHls(Make(TvIds.Ep3, TvIds.SeriesA, "tv"), 2, 4, out _)); // it takes the place of the idle Ep1, not of the playing Ep2
        Assert.Equal(2, slots.ActiveSlots(MatrixWorld.Admin));
    }

    [Fact]
    public async Task WhenTheNextEpisodeTakesTheSlotOfAQuietHlsStream_ThePeersJobOfThatStreamIsStopped()
    {
        H.Peer.PlaybackInfoReply = (id, _) => Reply.Json(Answer(id, transcode: true));
        using var firstResponse = await PlaybackInfoAsync(TvIds.Ep1);
        using var secondResponse = await PlaybackInfoAsync(TvIds.Ep2);
        var first = P.Grants.ForUser(MatrixWorld.Admin).Single(g => g.LocalItemId == TvIds.Ep1);
        var second = P.Grants.ForUser(MatrixWorld.Admin).Single(g => g.LocalItemId == TvIds.Ep2);
        Assert.Equal(PlayMethod.Transcode, first.Method);
        first.PeerJobStarted = true;
        second.PeerJobStarted = true;
        var endpoints = new FakeEndpoints(P.Endpoint);
        var jobs = new HlsJobControl(endpoints, H.Client, NullLogger<HlsJobControl>.Instance);
        await using var reporter = new PeerSessionReporter(P.Grants, endpoints, H.Client, jobs, P.StreamSlots, P.Time, NullLogger<PeerSessionReporter>.Instance);
        reporter.Start();
        Assert.True(P.StreamSlots.TryTakeHls(first, 4, 4, out _));
        H.Peer.Requests.Clear();

        P.Time.Advance(StreamSlots.HlsSupersedeAfter + TimeSpan.FromSeconds(1)); // the first stream went quiet
        Assert.True(P.StreamSlots.TryTakeHls(second, 4, 4, out _));

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline && !H.Peer.Requests.Any(static r => r.Method == "DELETE"))
        {
            await Task.Delay(10);
        }

        var delete = Assert.Single(H.Peer.Requests, static r => r.Method == "DELETE");
        Assert.Equal("/Videos/ActiveEncodings", delete.Path);
        Assert.False(first.PeerJobStarted); // only the superseded stream's job was stopped
        Assert.True(second.PeerJobStarted);
        Assert.False(P.StreamSlots.HoldsOwnSlot(first));
        Assert.True(P.StreamSlots.HoldsOwnSlot(second));
    }

    // ---------------- session reports of an episode ----------------

    [Fact]
    public async Task TheSessionReports_OfAnEpisodeGrant_UseThePeersIds()
    {
        H.Peer.Requests.Clear();
        var endpoints = new FakeEndpoints(P.Endpoint);
        var jobs = new HlsJobControl(endpoints, H.Client, NullLogger<HlsJobControl>.Instance);
        await using var reporter = new PeerSessionReporter(P.Grants, endpoints, H.Client, jobs, P.StreamSlots, P.Time, NullLogger<PeerSessionReporter>.Instance);
        reporter.Start();
        using var created = await PlaybackInfoAsync(TvIds.Ep1);
        Assert.Null(await ErrorCodeAsync(created));
        var grant = P.Grants.ForUser(MatrixWorld.Admin).Single(g => g.LocalItemId == TvIds.Ep1);

        reporter.OnStart(new PlaybackNote(MatrixWorld.Admin, grant.LocalPlaySessionId, 600_000_000, false));
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline && !H.Peer.Requests.Any(static r => r.Path == "/Sessions/Playing"))
        {
            await Task.Delay(10);
        }

        var call = Assert.Single(H.Peer.Requests, static r => r.Path == "/Sessions/Playing");
        using var doc = JsonDocument.Parse(call.Body);
        var names = doc.RootElement.EnumerateObject().Select(static p => p.Name).ToList();
        Assert.Equal(["ItemId", "MediaSourceId", "PlaySessionId", "PlayMethod", "CanSeek", "PositionTicks", "IsPaused"], names);
        Assert.Equal(TvIds.PeerEp1.ToString("N"), doc.RootElement.GetProperty("ItemId").GetString());
        Assert.Equal(TvIds.SourceEp1.ToString("N"), doc.RootElement.GetProperty("MediaSourceId").GetString());
        Assert.Equal("peer-play-session-0123456789", doc.RootElement.GetProperty("PlaySessionId").GetString());
        var text = Encoding.UTF8.GetString(call.Body);
        Assert.DoesNotContain(grant.LocalPlaySessionId, text, StringComparison.Ordinal);
        Assert.DoesNotContain(TvIds.Ep1.ToString("N"), text, StringComparison.Ordinal);
        Assert.DoesNotContain(TvIds.SeriesA, text, StringComparison.Ordinal); // the series id is Home's business, never reported
    }

    // ---------------- the trickplay recheck rotation ----------------

    private static CatalogEntry Bare(EntryKind kind, DateTime seen) => new() { Kind = kind, FirstSeenUtc = seen, State = EntryState.Active, Sources = [new CatalogSource()] };

    [Fact]
    public void AMovieIsLookedAtOncePerDay_AtMostFiftyPerSync()
    {
        var now = new DateTime(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc);
        var movies = Enumerable.Range(0, 70).Select(_ => Bare(EntryKind.Movie, now.AddHours(-25))).ToList();
        var fresh = Bare(EntryKind.Movie, now.AddHours(-2));

        var recheck = new TrickplayRecheck(now);

        Assert.Equal(50, movies.Count(recheck.Due));
        Assert.False(recheck.Due(fresh));
        Assert.Equal(20, movies.Count(new TrickplayRecheck(now.AddMinutes(30)).Due)); // the ones not looked at yet; the 50 stamped ones wait a day
    }

    [Fact]
    public void AnEpisodeIsLookedAtOncePerWeek_AtMostTwoHundredPerSync()
    {
        var now = new DateTime(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc);
        var episodes = Enumerable.Range(0, 450).Select(_ => Bare(EntryKind.Episode, now.AddDays(-8))).ToList();
        var daily = Bare(EntryKind.Episode, now.AddDays(-2));

        var first = new TrickplayRecheck(now);
        Assert.Equal(200, episodes.Count(first.Due));
        Assert.False(first.Due(daily)); // two days is not a week
        var second = new TrickplayRecheck(now.AddMinutes(30));
        Assert.Equal(200, episodes.Count(second.Due));
        var third = new TrickplayRecheck(now.AddHours(1));
        Assert.Equal(50, episodes.Count(third.Due));
        Assert.Equal(0, episodes.Count(new TrickplayRecheck(now.AddHours(2)).Due)); // all stamped: nothing again for a week
    }

    [Fact]
    public void AnEpisodeWithTrickplay_ASeries_AndASeason_AreNeverDue()
    {
        var now = new DateTime(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc);
        var withLevels = Bare(EntryKind.Episode, now.AddDays(-30));
        withLevels.Sources[0].Trickplay = [new TrickplayLevel { Width = 320, Height = 180, TileWidth = 10, TileHeight = 10, ThumbnailCount = 100, IntervalMs = 10_000, Bandwidth = 500_000 }];
        var recheck = new TrickplayRecheck(now);

        Assert.False(recheck.Due(withLevels));
        Assert.False(recheck.Due(Bare(EntryKind.Series, now.AddDays(-30))));
        Assert.False(recheck.Due(Bare(EntryKind.Season, now.AddDays(-30))));
        Assert.Null(withLevels.TrickplayCheckedUtc);
    }

    [Fact]
    public async Task TheTvSync_AsksForTheEpisodesWithoutTrickplay_TwoHundredPerSync_OnceAWeek()
    {
        await using var rig = await SyncRig.CreateAsync();
        rig.Peer.Tv = FakeTvTree.Generate(series: 1, seasonsPerSeries: 1, episodesPerSeason: 230);
        await rig.AddTvMappingAsync();
        await rig.SyncAsync();
        Assert.Equal(230, rig.Entries.Count(static e => e.Kind == EntryKind.Episode));

        int Asked(int skip) => rig.Peer.Requests.Where(static r => r.Contains("Ids=", StringComparison.Ordinal)).Skip(skip).SelectMany(static r => r.Split("Ids=")[1].Split('&')[0].Split(',')).Count();
        var before = rig.Peer.Requests.Count(static r => r.Contains("Ids=", StringComparison.Ordinal));
        rig.Time.Advance(TimeSpan.FromDays(2));
        await rig.SyncAsync();
        Assert.Equal(0, Asked(before)); // two days is not a week

        rig.Time.Advance(TimeSpan.FromDays(6));
        await rig.SyncAsync();
        Assert.Equal(200, Asked(before));
        Assert.Equal(200, rig.Entries.Count(static e => e.Kind == EntryKind.Episode && e.TrickplayCheckedUtc is not null));

        var mid = rig.Peer.Requests.Count(static r => r.Contains("Ids=", StringComparison.Ordinal));
        rig.Time.Advance(TimeSpan.FromMinutes(30));
        await rig.SyncAsync();
        Assert.Equal(30, Asked(mid)); // the rest follow on the next sync
        Assert.Equal(230, rig.Entries.Count(static e => e.Kind == EntryKind.Episode && e.TrickplayCheckedUtc is not null));
    }
}
