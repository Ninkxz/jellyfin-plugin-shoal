using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.Authorization;
using Jellyfin.Plugin.Shoal.Configuration;
using Jellyfin.Plugin.Shoal.Peers;
using Jellyfin.Plugin.Shoal.Playback;
using Xunit;

namespace Jellyfin.Plugin.Shoal.Tests;

/// <summary>
/// The offline card (owner request 2026-10-16): an offline friend's title plays a short clip that says so, through Shoal's own static route, with no peer call, no
/// stream slot, no grant, and nothing written to the real item.
/// </summary>
public sealed class OfflineCardTests : IClassFixture<PlaybackFixture>, IAsyncLifetime
{
    private static readonly string Item = MatrixWorld.Item.ToString("N");
    private static readonly string CardSource = OfflineCards.SourceIdOf(MatrixWorld.Item).ToString("N");

    private readonly PlaybackFixture _fixture;
    private CircuitBreaker? _breaker;

    public OfflineCardTests(PlaybackFixture fixture) => _fixture = fixture;

    private MatrixHarness H => _fixture.H;

    private PlaybackParts P => H.Filter!.Playback!;

    public async Task InitializeAsync()
    {
        await H.ResetAsync();
        await H.Filter!.Index.RebuildAsync();
        P.Time.Advance(TimeSpan.FromHours(1));
        foreach (var user in new[] { MatrixWorld.Admin, MatrixWorld.Selected, MatrixWorld.Other })
        {
            foreach (var grant in P.Grants.ForUser(user))
            {
                P.Grants.Remove(grant.GrantId);
            }
        }

        P.Slots.Holding.Clear();
        P.Registry.SetStatus(H.World.PeerKey, PeerStatus.Online);
        P.Registry.RecordPinVerified(H.World.PeerKey);
        P.Notice.Notices.Clear();
        P.Cards.Clear();
        P.CardsEnabled = true;
        P.CardServiceName = "Seerr";
        P.CardEncoder.Works = true;
        P.CardEncoder.Available = true;
        P.CardEncoder.DelayMs = 0;
        P.CardEncoder.Texts.Clear();
        _breaker = H.Client.BreakerOf(P.Endpoint);
        for (var i = 0; i < 3; i++)
        {
            _breaker.RecordFailure(); // the friend is offline: the circuit is open
        }

        H.Peer.Requests.Clear();
    }

    public Task DisposeAsync()
    {
        _breaker?.RecordSuccess();
        P.CardsEnabled = true;
        H.State.UpdateAsync(static s => s.Home.Peers[0].Alias = string.Empty).GetAwaiter().GetResult();
        return Task.CompletedTask;
    }

    private string Sign(Guid user, params Claim[] extra) => H.World.Sign("oc-" + user.ToString("N") + extra.Length, MatrixWorld.Principal(user, extra));

    private async Task<HttpResponseMessage> PlaybackInfoAsync(Guid? user = null)
    {
        await H.Filter!.Index.RebuildAsync();
        return await H.SendAsync(Sign(user ?? MatrixWorld.Admin), HttpMethod.Post, $"/Items/{Item}/PlaybackInfo", r => r.Content = new StringContent("{}", Encoding.UTF8, "application/json"));
    }

    private static async Task<JsonObject> ReadAsync(HttpResponseMessage response) => (JsonObject)JsonNode.Parse(await response.Content.ReadAsStringAsync())!;

    private Task<HttpResponseMessage> GetClipAsync(string? principal, Action<HttpRequestMessage>? configure = null, string? source = null) =>
        H.SendAsync(principal, HttpMethod.Get, $"/Videos/{Item}/stream?static=true&mediaSourceId={source ?? CardSource}", configure);

    private async Task<string> CardSessionAsync(Guid? user = null)
    {
        using var response = await PlaybackInfoAsync(user);
        return (string)(await ReadAsync(response))["PlaySessionId"]!;
    }

    // ---------------- PlaybackInfo ----------------

    [Fact]
    public async Task AnOfflineFriend_GetsTheCardAsAnOrdinaryDirectPlaySource_WithNoPeerCallNoGrantAndNoSlot()
    {
        using var response = await PlaybackInfoAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ReadAsync(response);
        Assert.Null((string?)body["ErrorCode"]);
        var source = body["MediaSources"]!.AsArray().Single()!;
        Assert.Equal("Server offline", (string?)source["Name"]);
        Assert.Equal(CardSource, (string?)source["Id"]);
        Assert.Equal("mp4", (string?)source["Container"]);
        Assert.Equal(80_000_000L, (long)source["RunTimeTicks"]!);
        Assert.True((bool)source["SupportsDirectPlay"]!);
        Assert.False((bool)source["SupportsDirectStream"]!);
        Assert.False((bool)source["SupportsTranscoding"]!);
        Assert.Null((string?)source["TranscodingUrl"]);
        var streams = source["MediaStreams"]!.AsArray();
        Assert.Equal(["Video", "Audio"], streams.Select(static s => (string)s!["Type"]!).ToArray());
        Assert.Equal((1280, 720, "h264"), ((int)streams[0]!["Width"]!, (int)streams[0]!["Height"]!, (string)streams[0]!["Codec"]!));
        Assert.StartsWith("oc", (string)body["PlaySessionId"]!, StringComparison.Ordinal);
        Assert.Empty(H.Peer.Requests); // no peer call
        Assert.Empty(P.Grants.ForUser(MatrixWorld.Admin)); // no grant, so nothing for the slots or the session reporter to find
        Assert.Equal(0, P.StreamSlots.ActiveSlotsOnPeer(H.World.PeerKey));
        Assert.Single(P.Notice.Notices); // the toast still goes out
        var text = await Task.FromResult(body.ToJsonString());
        Assert.DoesNotContain(PlaybackParts.PeerItem.ToString("N"), text, StringComparison.Ordinal); // no peer id
        Assert.DoesNotContain(Item, text.Replace(CardSource, string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithTheCardOff_TheOldRefusalIsKept()
    {
        P.CardsEnabled = false;

        using var response = await PlaybackInfoAsync();

        Assert.Equal("NotAllowed", (string?)(await ReadAsync(response))["ErrorCode"]);
        Assert.Equal(0, P.CardEncoder.Calls);
    }

    [Fact]
    public async Task WithoutFfmpeg_OrWhenTheClipCannotBeMade_TheOldRefusalIsKept_AndTheEncoderIsNotAskedAgainAtOnce()
    {
        P.CardEncoder.Works = false;

        using var first = await PlaybackInfoAsync();
        using var second = await PlaybackInfoAsync();

        Assert.Equal("NotAllowed", (string?)(await ReadAsync(first))["ErrorCode"]);
        Assert.Equal("NotAllowed", (string?)(await ReadAsync(second))["ErrorCode"]);
        Assert.Equal(1, P.CardEncoder.Calls); // a failed clip is not retried for five minutes

        P.CardEncoder.Available = false;
        P.Time.Advance(TimeSpan.FromMinutes(6));
        using var third = await PlaybackInfoAsync();
        Assert.Equal("NotAllowed", (string?)(await ReadAsync(third))["ErrorCode"]);
        Assert.Equal(1, P.CardEncoder.Calls);
    }

    [Fact]
    public async Task TheClip_IsMadeOncePerNameAndWording_AndAnswersFastWhenWarm()
    {
        (await PlaybackInfoAsync()).Dispose(); // makes it
        var watch = Stopwatch.StartNew();
        using var warm = await PlaybackInfoAsync();
        watch.Stop();

        Assert.Equal(HttpStatusCode.OK, warm.StatusCode);
        Assert.True(watch.ElapsedMilliseconds < 100, "took " + watch.ElapsedMilliseconds + " ms");
        Assert.Equal(1, P.CardEncoder.Calls);

        P.CardServiceName = "Overseerr";
        (await PlaybackInfoAsync()).Dispose(); // other wording, other clip
        Assert.Equal(2, P.CardEncoder.Calls);
        Assert.Contains("Overseerr", P.CardEncoder.Texts[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheCardSaysWhoIsOffline_InTheFriendsDisplayName()
    {
        (await PlaybackInfoAsync()).Dispose();

        Assert.StartsWith("Hops is offline right now.", P.CardEncoder.Texts.Single(), StringComparison.Ordinal);
    }

    // ---------------- the clip ----------------

    [Fact]
    public async Task TheClip_IsServedByShoal_WithShoalsHeaders_NoPeerCall_NoSlot_AndRanges()
    {
        (await PlaybackInfoAsync()).Dispose();
        var bytes = FakeCardEncoder.Mp4();

        using var full = await GetClipAsync(Sign(MatrixWorld.Admin));
        var body = await full.Content.ReadAsByteArrayAsync();

        Assert.Equal(HttpStatusCode.OK, full.StatusCode);
        Assert.Equal("video/mp4", full.Content.Headers.ContentType!.MediaType);
        Assert.Equal(bytes, body);
        Assert.True(OfflineCards.LooksLikeMp4(body));
        Assert.Equal("nosniff", full.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.False(full.Headers.Contains(FakeCoreController.Header));
        Assert.Empty(H.Peer.Requests);
        Assert.Equal(0, P.StreamSlots.ActiveSlotsOnPeer(H.World.PeerKey));

        using var part = await GetClipAsync(Sign(MatrixWorld.Admin), r => r.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(100, 199));
        Assert.Equal(HttpStatusCode.PartialContent, part.StatusCode);
        Assert.Equal(bytes[100..200], await part.Content.ReadAsByteArrayAsync());
        Assert.Equal($"bytes 100-199/{bytes.Length}", part.Content.Headers.ContentRange!.ToString());

        using var tail = await GetClipAsync(Sign(MatrixWorld.Admin), r => r.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(null, 50));
        Assert.Equal(bytes[^50..], await tail.Content.ReadAsByteArrayAsync());

        using var beyond = await GetClipAsync(Sign(MatrixWorld.Admin), r => r.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(bytes.Length + 10, null));
        Assert.Equal(HttpStatusCode.RequestedRangeNotSatisfiable, beyond.StatusCode);

        using var head = await H.SendAsync(Sign(MatrixWorld.Admin), HttpMethod.Head, $"/Videos/{Item}/stream?static=true&mediaSourceId={CardSource}");
        Assert.Equal(HttpStatusCode.OK, head.StatusCode);
        Assert.Equal(bytes.Length, head.Content.Headers.ContentLength);
    }

    [Fact]
    public async Task TheClip_FollowsTheSameAuthRulesAsPlayback()
    {
        (await PlaybackInfoAsync()).Dispose();

        using var anonymous = await GetClipAsync(null);
        using var apiKey = await GetClipAsync(Sign(MatrixWorld.Admin, new Claim(CallerContext.IsApiKeyClaim, "True")));
        using var outsider = await GetClipAsync(Sign(MatrixWorld.Other));
        using var otherItem = await H.SendAsync(Sign(MatrixWorld.Admin), HttpMethod.Get, $"/Videos/{MatrixWorld.ItemB:N}/stream?static=true&mediaSourceId={CardSource}");

        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, apiKey.StatusCode);
        Assert.NotEqual(HttpStatusCode.OK, outsider.StatusCode);
        Assert.False(outsider.Headers.Contains(FakeCoreController.Header));
        Assert.NotEqual(HttpStatusCode.OK, otherItem.StatusCode); // the card id belongs to one item
        Assert.Empty(H.Peer.Requests);
    }

    [Fact]
    public async Task ADifferentMediaSource_IsNotTheCard()
    {
        using var response = await GetClipAsync(Sign(MatrixWorld.Admin), source: Guid.NewGuid().ToString("N"));

        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
    }

    // ---------------- reports ----------------

    private Task<HttpResponseMessage> ReportAsync(string path, string principal, object? body)
    {
        return H.SendAsync(principal, HttpMethod.Post, path, r =>
        {
            if (body is not null)
            {
                r.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            }
        });
    }

    [Fact]
    public async Task ReportsForTheCard_AreSwallowed_AndNeverReachCore()
    {
        var session = await CardSessionAsync();
        var admin = Sign(MatrixWorld.Admin);

        foreach (var path in new[] { "/Sessions/Playing", "/Sessions/Playing/Progress" })
        {
            using var response = await ReportAsync(path, admin, new { ItemId = Item, MediaSourceId = CardSource, PlaySessionId = session, PositionTicks = 12_345_678L });
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
            Assert.False(response.Headers.Contains(FakeCoreController.Header), path + ": core's report ran");
        }

        using var camel = await ReportAsync("/Sessions/Playing/Progress", admin, new { itemId = Item, playSessionId = session });
        using var ping = await ReportAsync("/Sessions/Playing/Ping?playSessionId=" + session, admin, null);
        using var legacy = await ReportAsync($"/PlayingItems/{Item}/Progress?playSessionId={session}&mediaSourceId={CardSource}", admin, null);

        Assert.Equal(HttpStatusCode.NoContent, camel.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, ping.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, legacy.StatusCode);
        using var stopped = await ReportAsync("/Sessions/Playing/Stopped", admin, new { ItemId = Item, MediaSourceId = CardSource, PlaySessionId = session });
        Assert.Equal(HttpStatusCode.NoContent, stopped.StatusCode);
        Assert.False(stopped.Headers.Contains(FakeCoreController.Header));
        Assert.Empty(P.Grants.ForUser(MatrixWorld.Admin));
    }

    [Fact]
    public async Task OtherReports_StillReachCore()
    {
        var session = await CardSessionAsync();

        using var other = await ReportAsync("/Sessions/Playing/Progress", Sign(MatrixWorld.Admin), new { ItemId = Item, PlaySessionId = "0123456789abcdef0123456789abcdef", PositionTicks = 1 });
        using var theirs = await ReportAsync("/Sessions/Playing/Progress", Sign(MatrixWorld.Selected), new { ItemId = Item, PlaySessionId = session }); // another user's use of the id
        using var empty = await ReportAsync("/Sessions/Playing/Progress", Sign(MatrixWorld.Admin), null);

        Assert.True(other.Headers.Contains(FakeCoreController.Header));
        Assert.True(theirs.Headers.Contains(FakeCoreController.Header));
        Assert.True(empty.Headers.Contains(FakeCoreController.Header));
    }

    [Fact]
    public void ASessionOfTheCard_IsBoundToItsUserItsItemAndItsTime()
    {
        var cards = P.Cards;
        var session = cards.NewSession(MatrixWorld.Admin, MatrixWorld.Item)!;

        Assert.True(cards.IsCardSession(session, MatrixWorld.Admin, MatrixWorld.Item));
        Assert.True(cards.IsCardSession(session, MatrixWorld.Admin, null)); // a ping names no item
        Assert.False(cards.IsCardSession(session, MatrixWorld.Admin, MatrixWorld.ItemB)); // not another item
        Assert.False(cards.IsCardSession(session, MatrixWorld.Selected, MatrixWorld.Item));
        Assert.False(cards.IsCardSession("0123456789abcdef0123456789abcdef", MatrixWorld.Admin, MatrixWorld.Item));
        Assert.False(cards.IsCardSession(null, MatrixWorld.Admin, MatrixWorld.Item));
        P.Time.Advance(TimeSpan.FromHours(7));
        Assert.False(cards.IsCardSession(session, MatrixWorld.Admin, MatrixWorld.Item)); // sessions expire
    }

    [Fact]
    public async Task ACardSession_CannotSilenceTheReportsOfAnotherItem()
    {
        var session = await CardSessionAsync();
        var admin = Sign(MatrixWorld.Admin);
        var other = MatrixWorld.ItemB.ToString("N");

        using var body = await ReportAsync("/Sessions/Playing/Progress", admin, new { ItemId = other, PlaySessionId = session, PositionTicks = 5 });
        using var legacy = await ReportAsync($"/PlayingItems/{other}/Progress?playSessionId={session}", admin, null);
        using var noItem = await ReportAsync("/Sessions/Playing/Progress", admin, new { PlaySessionId = session });
        using var mine = await ReportAsync("/Sessions/Playing/Progress", admin, new { ItemId = Item, PlaySessionId = session });

        Assert.True(body.Headers.Contains(FakeCoreController.Header), "a report for another item was swallowed");
        Assert.True(legacy.Headers.Contains(FakeCoreController.Header));
        Assert.True(noItem.Headers.Contains(FakeCoreController.Header));
        Assert.Equal(HttpStatusCode.NoContent, mine.StatusCode);
    }

    [Fact]
    public async Task ASwallowedStopped_EndsTheSession()
    {
        var session = await CardSessionAsync();
        var admin = Sign(MatrixWorld.Admin);

        using var stopped = await ReportAsync("/Sessions/Playing/Stopped", admin, new { ItemId = Item, PlaySessionId = session });
        using var after = await ReportAsync("/Sessions/Playing/Progress", admin, new { ItemId = Item, PlaySessionId = session });

        Assert.Equal(HttpStatusCode.NoContent, stopped.StatusCode);
        Assert.True(after.Headers.Contains(FakeCoreController.Header), "the session outlived its Stopped");
        Assert.False(P.Cards.IsCardSession(session, MatrixWorld.Admin, MatrixWorld.Item));
    }

    [Fact]
    public void AUserHoldsAtMostEightSessions_AndNeverEvictsAnotherUsers()
    {
        var cards = P.Cards;
        var theirs = cards.NewSession(MatrixWorld.Selected, MatrixWorld.Item)!;
        var mine = new List<string>();
        for (var i = 0; i < 12; i++)
        {
            P.Time.Advance(TimeSpan.FromSeconds(1));
            mine.Add(cards.NewSession(MatrixWorld.Admin, MatrixWorld.Item)!);
        }

        Assert.Equal(8, mine.Count(id => cards.IsCardSession(id, MatrixWorld.Admin, MatrixWorld.Item)));
        Assert.All(mine.Take(4), id => Assert.False(cards.IsCardSession(id, MatrixWorld.Admin, MatrixWorld.Item))); // the oldest of the user's own went
        Assert.All(mine.Skip(4), id => Assert.True(cards.IsCardSession(id, MatrixWorld.Admin, MatrixWorld.Item)));
        Assert.True(cards.IsCardSession(theirs, MatrixWorld.Selected, MatrixWorld.Item));
    }

    // ---------------- one clip at a time ----------------

    [Fact]
    public async Task WhileOneClipIsBeingMade_AnotherNameGetsTheRefusalAtOnce_AndTheSameNameWaits()
    {
        P.CardEncoder.DelayMs = 400;
        var first = P.Cards.GetAsync("Hops", default);
        await Task.Delay(100);

        var watch = Stopwatch.StartNew();
        var other = await P.Cards.GetAsync("Another friend", default);
        watch.Stop();
        var same = P.Cards.GetAsync("Hops", default);

        Assert.Null(other);
        Assert.True(watch.ElapsedMilliseconds < 150, "took " + watch.ElapsedMilliseconds + " ms");
        Assert.NotNull(await first);
        Assert.NotNull(await same);
        Assert.Equal(1, P.CardEncoder.Calls);
    }

    // ---------------- the alias ----------------

    [Fact]
    public async Task TheAlias_IsWhatTheCardAndTheMessageSay_NotTheNameTheFriendChose()
    {
        await H.State.UpdateAsync(static s => s.Home.Peers[0].Alias = "Pat's server");

        (await PlaybackInfoAsync()).Dispose();

        Assert.StartsWith("Pat's server is offline right now.", P.CardEncoder.Texts.Single(), StringComparison.Ordinal);
        Assert.Equal("Pat's server", H.State.Current.Home.Peers[0].ShownName());
    }

    [Fact]
    public async Task WithoutAnAlias_TheDisplayNameIsUsed()
    {
        Assert.Equal(H.State.Current.Home.Peers[0].DisplayName, H.State.Current.Home.Peers[0].ShownName());
        await Task.CompletedTask;
    }

    // ---------------- the words ----------------

    [Fact]
    public void TheCopy_HasNoDashes_AndIsInSimpleWords()
    {
        var all = string.Join('\n', OfflineCardCopy.Paragraphs("Hops", "Seerr")) + "\n" + OfflineCardCopy.Toast("Hops", "Seerr", "https://seerr.example.org/") + "\n" + OfflineCardCopy.GenericFriend;

        Assert.DoesNotContain('-', all);
        Assert.DoesNotContain('\u2013', all);
        Assert.DoesNotContain('\u2014', all);
        Assert.Equal(
            [
                "Hops is offline right now.",
                "This is not a problem with this server. Their server is down.",
                "To watch this title now, request it on Seerr and it will be downloaded here. Strongly recommended.",
                "Or try again later.",
            ],
            OfflineCardCopy.Paragraphs("Hops", "Seerr"));
        Assert.Equal("This friend's server is offline right now.", OfflineCardCopy.Paragraphs(string.Empty, "Seerr")[0]);
    }

    [Fact]
    public void TheToast_CarriesTheRequestAddress_OnlyWhenThereIsOne()
    {
        Assert.Equal("Hops is offline right now. Try again later. Request it on Seerr: https://seerr.example.org/", OfflineCardCopy.Toast("Hops", "Seerr", "https://seerr.example.org/"));
        Assert.Equal("Hops is offline right now. Try again later.", OfflineCardCopy.Toast("Hops", "Seerr", string.Empty));
    }

    [Fact]
    public void TheLayout_WrapsAtWordsAndKeepsParagraphsApart()
    {
        var text = OfflineCardCopy.Layout(OfflineCardCopy.Paragraphs("Hops", "Seerr"));
        var lines = text.Split('\n');

        Assert.All(lines, l => Assert.True(l.Length <= 44, l));
        Assert.Contains(string.Empty, lines); // blank lines between paragraphs
        Assert.Equal(OfflineCardCopy.Paragraphs("Hops", "Seerr").Select(static p => p.Replace(" ", string.Empty, StringComparison.Ordinal)), text.Split("\n\n").Select(static p => p.Replace("\n", string.Empty, StringComparison.Ordinal).Replace(" ", string.Empty, StringComparison.Ordinal)));
    }

    // ---------------- the real thing ----------------

    [Fact]
    public void TheFfmpegCommand_UsesAListAndAFile_NeverTheFriendsNameOnTheCommandLine()
    {
        var arguments = FfmpegCardEncoder.Arguments("card.txt", "card.mp4", "C:\\Windows\\Fonts\\arialbd.ttf");
        var filter = arguments[arguments.ToList().IndexOf("-vf") + 1];

        Assert.Contains("expansion=none", filter, StringComparison.Ordinal);
        Assert.Contains("textfile='card.txt'", filter, StringComparison.Ordinal); // relative: no host path goes through filter escaping
        Assert.DoesNotContain("work", filter, StringComparison.Ordinal);
        Assert.Contains("fontfile='C\\:/Windows/Fonts/arialbd.ttf'", filter, StringComparison.Ordinal);
        Assert.Contains("s=1280x720", string.Join(' ', arguments), StringComparison.Ordinal);
        Assert.Equal(["-profile:v", "main"], arguments.SkipWhile(static a => a != "-profile:v").Take(2).ToArray());
        Assert.Contains("+faststart", arguments);
        Assert.Contains("libx264", arguments);
        Assert.Contains("aac", arguments);
        Assert.Contains("font='Sans'", string.Join(' ', FfmpegCardEncoder.Arguments("/t/c.txt", "/t/o.mp4", null)), StringComparison.Ordinal);
    }

    [FfmpegFact]
    public async Task TheRealFfmpeg_MakesAnMp4OfTheRightShape()
    {
        var ffmpeg = FfmpegFactAttribute.FindFfmpeg()!;
        using var dir = new TempDirectory();
        var encoder = new FfmpegCardEncoder(new FixedLocator(ffmpeg), Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        var output = Path.Combine(dir.Combine("it's a folder"), "card.mp4"); // a quote in the data path must not matter (Warden 15 L2)
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);

        var made = await encoder.EncodeAsync(OfflineCardCopy.Layout(OfflineCardCopy.Paragraphs("Hops", "Seerr")), output, default);

        Assert.True(made, "ffmpeg could not draw the card (is a font installed?)");
        var bytes = await File.ReadAllBytesAsync(output);
        Assert.True(OfflineCards.LooksLikeMp4(bytes));
        Assert.True(bytes.Length < 600_000, "the clip is " + bytes.Length + " bytes");
        var probe = Path.Combine(Path.GetDirectoryName(ffmpeg)!, OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe");
        if (File.Exists(probe))
        {
            var start = new ProcessStartInfo(probe) { RedirectStandardOutput = true, UseShellExecute = false };
            foreach (var argument in new[] { "-v", "error", "-show_entries", "stream=codec_name,profile,width,height,pix_fmt:format=duration", "-of", "json", output })
            {
                start.ArgumentList.Add(argument);
            }

            using var process = Process.Start(start)!;
            var json = JsonNode.Parse(await process.StandardOutput.ReadToEndAsync())!;
            await process.WaitForExitAsync();
            var streams = json["streams"]!.AsArray();
            var video = streams.Single(static s => (string?)s!["codec_name"] == "h264")!;
            Assert.Equal((1280, 720, "yuv420p"), ((int)video["width"]!, (int)video["height"]!, (string)video["pix_fmt"]!));
            Assert.Contains((string)video["profile"]!, new[] { "Main", "Constrained Baseline", "Baseline" });
            Assert.Contains(streams, static s => (string?)s!["codec_name"] == "aac");
            Assert.InRange(double.Parse((string)json["format"]!["duration"]!, System.Globalization.CultureInfo.InvariantCulture), 7.5, 8.6);
        }
    }

    private sealed class FixedLocator : Jellyfin.Plugin.Shoal.Sync.IEncoderLocator
    {
        public FixedLocator(string path) => EncoderPath = path;

        public string? EncoderPath { get; }
    }

    // ---------------- settings ----------------

    [Theory]
    [InlineData("Seerr", true)]
    [InlineData("Overseerr 2", true)]
    [InlineData("my.requests", true)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("Seerr: now", false)]
    [InlineData("has-a-dash", false)]
    [InlineData("A very very very very long service name", false)]
    public void TheServiceName_IsPlainWords(string name, bool valid) => Assert.Equal(valid, SettingsValidator.IsValidServiceName(name));

    [Theory]
    [InlineData("", true)]
    [InlineData("https://seerr.example.org", true)]
    [InlineData("http://192.168.1.5:5055/", true)]
    [InlineData("ftp://seerr.example.org", false)]
    [InlineData("https://user:pass@seerr.example.org", false)]
    [InlineData("https://seerr.example.org/a b", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("seerr.example.org", false)]
    public void TheServiceUrl_IsAnHttpAddressWithoutALogin(string url, bool valid) => Assert.Equal(valid, SettingsValidator.IsValidServiceUrl(url));

    [Fact]
    public void TheValidator_ResetsABadServiceNameAndUrl()
    {
        var configuration = new PluginConfiguration { RequestServiceName = "<b>x</b>", RequestServiceUrl = "javascript:alert(1)" };

        var notes = SettingsValidator.Apply(configuration);

        Assert.Equal("Seerr", configuration.RequestServiceName);
        Assert.Equal(string.Empty, configuration.RequestServiceUrl);
        Assert.Equal(2, notes.Count);
        Assert.True(new PluginConfiguration().OfflineCardEnabled);
    }
}
