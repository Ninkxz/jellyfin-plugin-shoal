using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.Api.Admin;
using Jellyfin.Plugin.Shoal.Diagnostics;
using Jellyfin.Plugin.Shoal.Playback;
using Jellyfin.Plugin.Shoal.State;
using Jellyfin.Plugin.Shoal.Storage;
using Jellyfin.Plugin.Shoal.Sync;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Shoal.Tests;

/// <summary>
/// PLAN v2.9 K53, the pure parts: the class of a picture, the class bitrate (p75 or reference), the show rule and the dismiss rules, and the rank reasons.
/// </summary>
public sealed class RecommendationEngineTests
{
    private static readonly DateTime Now = new(2026, 10, 18, 12, 0, 0, DateTimeKind.Utc);

    private static List<TitleSize> Catalog(int uhd, int hd, int sd = 0, int uhdMbps = 20, int hdMbps = 8) =>
    [
        .. Enumerable.Range(0, uhd).Select(_ => new TitleSize(3840, 2160, uhdMbps * 1_000_000)),
        .. Enumerable.Range(0, hd).Select(_ => new TitleSize(1920, 1080, hdMbps * 1_000_000)),
        .. Enumerable.Range(0, sd).Select(static _ => new TitleSize(720, 480, 1_500_000)),
    ];

    [Theory]
    [InlineData(854, 480, QualityClass.Sd480)]
    [InlineData(1280, 534, QualityClass.Hd720)]
    [InlineData(1920, 800, QualityClass.Hd1080)]
    [InlineData(3840, 2160, QualityClass.Uhd2160)]
    [InlineData(4096, 2160, QualityClass.Uhd2160)]
    [InlineData(0, 1080, QualityClass.Off)]
    public void TheClassOfAPicture_IsTheSmallestBoxItFits(int width, int height, QualityClass expected) =>
        Assert.Equal(expected, VerdictEngine.ClassOf(width, height));

    [Fact]
    public void TheClassBitrate_IsThe75thPercentile_OrTheReference()
    {
        var titles = new List<TitleSize> { new(3840, 2160, 10_000_000), new(3840, 2160, 20_000_000), new(3840, 2160, 30_000_000), new(3840, 2160, 60_000_000), new(1920, 1080, 0) };

        Assert.Equal(30, VerdictEngine.ClassBitrateMbps(titles, QualityClass.Uhd2160));
        Assert.Equal(10, VerdictEngine.ClassBitrateMbps(titles, QualityClass.Hd1080)); // no known bitrate: the reference
        Assert.Equal(2, VerdictEngine.ClassBitrateMbps(titles, QualityClass.Sd480));
    }

    [Fact]
    public void ASlowLinkWithA4kCatalog_IsToldToLimitTo1080p()
    {
        var card = VerdictEngine.Recommend(new QualityLimit(), 15, false, Catalog(uhd: 30, hd: 70), [], Now)!;

        Assert.Equal(QualityClass.Hd1080, card.SuggestedClass); // 15 >= 1.5 x 8, < 1.5 x 20
        Assert.Equal(QualityClass.Uhd2160, card.SlowForClass);
        Assert.Equal(30, card.TitlesAbove);
        Assert.Equal(100, card.TitlesKnown);
        Assert.Equal("This link is slow for 4K. Limit Bob's server to 1080p?", card.Text("Bob's server"));
    }

    [Fact]
    public void NoCard_WhenTheLinkCarriesIt_TheLimitIsAlreadyThere_TheLinkIsUnstable_OrItWasNeverMeasured()
    {
        var titles = Catalog(uhd: 30, hd: 70);

        Assert.Null(VerdictEngine.Recommend(new QualityLimit(), 31, false, titles, [], Now)); // 4K holds
        Assert.Null(VerdictEngine.Recommend(new QualityLimit { MaxClass = QualityClass.Hd1080 }, 15, false, titles, [], Now));
        Assert.Null(VerdictEngine.Recommend(new QualityLimit { MaxClass = QualityClass.Hd720 }, 15, false, titles, [], Now)); // already lower
        Assert.Null(VerdictEngine.Recommend(new QualityLimit(), 15, true, titles, [], Now));
        Assert.Null(VerdictEngine.Recommend(new QualityLimit(), null, false, titles, [], Now));
        Assert.NotNull(VerdictEngine.Recommend(new QualityLimit { MaxClass = QualityClass.Uhd2160 }, 15, false, titles, [], Now));
    }

    [Fact]
    public void TheCard_NeedsTenPercentOrTwentyTitlesAbove()
    {
        Assert.Null(VerdictEngine.Recommend(new QualityLimit(), 15, false, Catalog(uhd: 9, hd: 91), [], Now)); // 9 percent, 9 titles
        Assert.NotNull(VerdictEngine.Recommend(new QualityLimit(), 15, false, Catalog(uhd: 10, hd: 90), [], Now)); // 10 percent
        Assert.NotNull(VerdictEngine.Recommend(new QualityLimit(), 15, false, Catalog(uhd: 20, hd: 980), [], Now)); // 2 percent, but 20 titles
        Assert.Null(VerdictEngine.Recommend(new QualityLimit(), 15, false, Catalog(uhd: 0, hd: 100), [], Now)); // nothing above
    }

    [Fact]
    public void AVerySlowLink_IsToldTheHighestClassThatHolds_Or480p()
    {
        Assert.Equal(QualityClass.Hd720, VerdictEngine.Recommend(new QualityLimit(), 7, false, Catalog(uhd: 30, hd: 70), [], Now)!.SuggestedClass); // 720p reference 4: 6 needed
        var card = VerdictEngine.Recommend(new QualityLimit(), 1, false, Catalog(uhd: 30, hd: 70), [], Now)!;
        Assert.Equal(QualityClass.Sd480, card.SuggestedClass);
        Assert.Equal(QualityClass.Hd720, card.SlowForClass);
    }

    [Fact]
    public void ADismissal_HoldsUntilTheClassDrops_TheLinkGetsMoreThan30PercentSlower_Or30DaysPass()
    {
        var titles = Catalog(uhd: 30, hd: 70);
        var dismissed = new[] { new DismissedRecommendation { SuggestedClass = QualityClass.Hd1080, ThroughputMbps = 15, DismissedUtc = Now.AddDays(-1) } };

        Assert.Null(VerdictEngine.Recommend(new QualityLimit(), 15, false, titles, dismissed, Now));
        Assert.Null(VerdictEngine.Recommend(new QualityLimit(), 12.5, false, titles, dismissed, Now)); // 17 percent slower, still 1080p
        Assert.NotNull(VerdictEngine.Recommend(new QualityLimit(), 15, false, titles, dismissed, Now.AddDays(30)));
        Assert.Equal(QualityClass.Hd720, VerdictEngine.Recommend(new QualityLimit(), 7, false, titles, dismissed, Now)!.SuggestedClass); // the class dropped

        var high = new[] { new DismissedRecommendation { SuggestedClass = QualityClass.Hd1080, ThroughputMbps = 25, DismissedUtc = Now.AddDays(-1) } };
        Assert.NotNull(VerdictEngine.Recommend(new QualityLimit(), 15, false, titles, high, Now)); // 40 percent below the T at dismissal
    }

    [Fact]
    public void TheRankReasons_ReadAsTheAdminSeesThem()
    {
        var inputs = new List<RankInput>
        {
            new("fast", PriorityTier.Normal, true, true, false, 80, null, 0, new AutoRank()),
            new("slow", PriorityTier.Normal, true, true, false, 20, null, 1, new AutoRank()),
            new("down", PriorityTier.Normal, false, true, false, 90, null, 2, new AutoRank()),
            new("pref", PriorityTier.Prefer, true, true, false, null, null, 3, new AutoRank()),
            new("shaky", PriorityTier.Backup, true, true, true, 50, null, 4, new AutoRank()),
        };
        var outcome = VerdictEngine.RankConnections(inputs, Now);

        var why = VerdictEngine.ExplainRanks(outcome, inputs);

        Assert.Equal("Always prefer: 1st of 5 (not measured yet)", why["pref"].Label);
        Assert.Equal("Automatic: 2nd of 5 (offline)", why["down"].Label); // offline for less than 30 minutes: not demoted yet, a play skips it
        Assert.Equal("Automatic: 3rd of 5 (fastest link)", why["fast"].Label);
        Assert.Equal("Automatic: 4th of 5 (slower link)", why["slow"].Label);
        Assert.Equal("Backup only: 5th of 5 (unstable link)", why["shaky"].Label);
        Assert.Equal(4, why["slow"].Position);
    }
}

/// <summary>
/// PLAN v2.9 K51-K53 admin side: the PUT with expectedCurrent and clamps, the dismiss, the panel, the play order, the backup counts, Playing now, and the routes'
/// elevation. No throughput of a dismissal, admin id, peer id or token in any answer.
/// </summary>
public sealed class QualityAdminTests : IAsyncLifetime
{
    private const string A = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string B = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private static readonly Guid Admin = Guid.Parse("a0000000-0000-0000-0000-000000000001");

    private readonly TempDirectory _dir = new();
    private readonly ManualTimeProvider _time = new();
    private readonly FakeRank _ranking = new();
    private readonly Dictionary<string, List<SpeedSample>> _speed = new(StringComparer.Ordinal);
    private StateStore _state = null!;
    private CatalogStore _catalogs = null!;
    private GroupBook _book = null!;
    private GrantStore _grants = null!;
    private readonly HoldingSlots _slots = new();
    private QualityAdmin _quality = null!;

    public async Task InitializeAsync()
    {
        _state = new StateStore(new ShoalPaths(_dir.Path), NullLogger.Instance, _time, null);
        await _state.UpdateAsync(s =>
        {
            s.Home.Peers.Add(new PeerState { PeerKey = A, DisplayName = "Alice", Alias = "Alice's place", BaseUrl = "http://a.tail1234.ts.net:8096", Priority = 100 });
            s.Home.Peers.Add(new PeerState { PeerKey = B, DisplayName = "Bob", BaseUrl = "http://b.tail1234.ts.net:8096", Priority = 110, PriorityTier = PriorityTier.Backup });
        });
        _catalogs = new CatalogStore(new ShoalPaths(_dir.Path), NullLogger.Instance);
        var doc = new CatalogDocument { PeerKey = A, CompletedOnce = true };
        for (var i = 0; i < 40; i++)
        {
            doc.Entries.Add(new CatalogEntry { StubKey = "s" + i.ToString("D15", System.Globalization.CultureInfo.InvariantCulture), PeerItemId = Guid.NewGuid().ToString("N"), MappingKey = "movies-1", Title = "T" + i, StubRelPath = i == 39 ? null : $"T{i}/T{i}.strm", State = i == 39 ? EntryState.DuplicateLocal : EntryState.Active, Width = i < 20 ? 3840 : 1920, Height = i < 20 ? 2160 : 1080, Bitrate = i < 20 ? 20_000_000 : 8_000_000 });
        }

        _catalogs.Save(doc);
        _speed[A] = [new SpeedSample { T = _time.GetUtcNow(), Kind = SpeedKinds.Light, Mbps = 15, JitterMs = 2, LossPct = 0 }];
        _book = new GroupBook(new MemoryGroupStore(), NullLogger.Instance);
        _grants = new GrantStore(_time, _slots);
        _ranking.Keys = [A, B];
        _quality = new QualityAdmin(_state, _catalogs, k => _speed.GetValueOrDefault(k) ?? [], _ranking, _time, _book, _grants, _slots);
    }

    public Task DisposeAsync()
    {
        _state.Dispose();
        _dir.Dispose();
        return Task.CompletedTask;
    }

    private static QualityLimitRequest Put(QualityClass cls, int? mbps, QualityClass expected, int? expectedMbps = null, bool rec = false) =>
        new() { MaxClass = cls, MaxMbps = mbps, FromRecommendation = rec, ExpectedCurrent = new QualityLimitExpected { MaxClass = expected, MaxMbps = expectedMbps } };

    [Fact]
    public async Task ThePut_SavesOnlyWhenTheStoredLimitIsTheOneTheAdminSaw_AndClampsTheMbps()
    {
        var saved = await _quality.SetLimitAsync(A, Put(QualityClass.Hd1080, 5000, QualityClass.Off), Admin, CancellationToken.None);
        Assert.Equal(QualityChangeOutcome.Saved, saved.Outcome);
        Assert.Equal(1000, _state.Current.Home.Peers[0].QualityLimit.MaxMbps);
        Assert.Equal(Admin, _state.Current.Home.Peers[0].QualityLimit.SetByUserId);

        var stale = await _quality.SetLimitAsync(A, Put(QualityClass.Hd720, null, QualityClass.Off), Admin, CancellationToken.None);
        Assert.Equal(QualityChangeOutcome.Changed, stale.Outcome);
        Assert.Equal(QualityClass.Hd1080, stale.Limit!.MaxClass); // the one there now
        Assert.Equal(QualityClass.Hd1080, _state.Current.Home.Peers[0].QualityLimit.MaxClass);

        Assert.Equal(QualityChangeOutcome.Invalid, (await _quality.SetLimitAsync(A, new QualityLimitRequest { MaxClass = QualityClass.Hd720 }, Admin, CancellationToken.None)).Outcome);
        Assert.Equal(QualityChangeOutcome.NotFound, (await _quality.SetLimitAsync("cccccccccccccccccccccccccccccccc", Put(QualityClass.Hd720, null, QualityClass.Off), Admin, CancellationToken.None)).Outcome);
        Assert.Equal(QualityChangeOutcome.Saved, (await _quality.SetLimitAsync(A, Put(QualityClass.Hd720, 0, QualityClass.Hd1080, 1000, rec: true), Admin, CancellationToken.None)).Outcome);
        Assert.Equal(1, _state.Current.Home.Peers[0].QualityLimit.MaxMbps);
        Assert.Equal(QualityLimitOrigin.Recommendation, _state.Current.Home.Peers[0].QualityLimit.Origin);
    }

    [Fact]
    public async Task ThePanel_ShowsTheCard_AndADismissHidesIt_AndKeepsTheThroughputOnTheServer()
    {
        var panel = _quality.Panel(A)!;
        Assert.Equal(QualityClass.Hd1080, panel.Recommendation!.SuggestedClass);
        Assert.Equal(20, panel.Recommendation.TitlesAbove); // Home's own copy is not counted
        Assert.Equal("This link is slow for 4K. Limit Alice's place to 1080p?", panel.Recommendation.Text);
        Assert.Null(panel.CostText);

        Assert.Equal(QualityChangeOutcome.Stale, await _quality.DismissAsync(A, QualityClass.Hd720, Admin, CancellationToken.None));
        Assert.Equal(QualityChangeOutcome.Invalid, await _quality.DismissAsync(A, null, Admin, CancellationToken.None));
        Assert.Equal(QualityChangeOutcome.Saved, await _quality.DismissAsync(A, QualityClass.Hd1080, Admin, CancellationToken.None));

        var after = _quality.Panel(A)!;
        Assert.Null(after.Recommendation);
        Assert.Single(after.Dismissed);
        Assert.Equal(15, _state.Current.Home.Peers[0].Dismissed[0].ThroughputMbps);
        Assert.Equal(QualityChangeOutcome.Stale, await _quality.DismissAsync(A, QualityClass.Hd1080, Admin, CancellationToken.None)); // nothing shown now

        var json = AdminTestSupport.Text(AdminReply.Json(StatusCodes.Status200OK, after));
        Assert.DoesNotContain(Admin.ToString("N"), json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Admin.ToString("D"), json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("throughput", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ThePanel_CarriesTheEffectiveLimitAndTheCostLine()
    {
        await _state.UpdateAsync(s => s.Home.Peers[0].QualityLimit = new QualityLimit { MaxClass = QualityClass.Hd720, MaxMbps = 5 });

        var panel = _quality.Panel(A)!;

        Assert.Equal("Up to 720p, 5 Mbps", panel.Effective.Text);
        Assert.Equal((1280, 720), (panel.Effective.MaxWidth, panel.Effective.MaxHeight));
        Assert.Equal("Titles above this quality will be converted by Alice's place. That uses their computer's power.", panel.CostText);
        var json = AdminTestSupport.Text(AdminReply.Json(StatusCodes.Status200OK, panel));
        Assert.Contains("\"maxClass\":\"720\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void ThePlayOrder_ListsEveryConnectionWithItsReason()
    {
        _ranking.Why[A] = new RankExplanation(1, 2, PriorityTier.Normal, "fastest link", "Automatic: 1st of 2 (fastest link)");

        var rows = _quality.PlayOrder();

        Assert.Equal([A, B], rows.Select(static r => r.PeerKey));
        Assert.Equal("Automatic: 1st of 2 (fastest link)", rows[0].Label);
        Assert.Equal("Alice's place", rows[0].Name);
        Assert.Equal(PriorityTier.Backup, rows[1].Tier);
        Assert.Null(rows[1].Label);
        Assert.Equal("No limit", rows[1].Effective.Text);
    }

    [Fact]
    public void TheBackupCounts_SayWhichTitlesAConnectionPlaysFirst_AndWhichItBacksUp()
    {
        _book.Update(d =>
        {
            d.Groups.Add(new TitleGroup { GroupKey = "grp0000000000001", MetaOwner = A, Members = [new GroupMember { PeerKey = A, PeerItemId = new string('1', 32) }, new GroupMember { PeerKey = B, PeerItemId = new string('2', 32) }] });
            d.Groups.Add(new TitleGroup { GroupKey = "grp0000000000002", MetaOwner = B, Members = [new GroupMember { PeerKey = B, PeerItemId = new string('3', 32) }, new GroupMember { PeerKey = A, PeerItemId = new string('4', 32) }] });
            return true;
        });
        _ranking.Keys = [B, A]; // B ranks first, but it is Backup only

        Assert.Equal(new BackupCountsDto(2, 2, 0), _quality.Backups(A));
        Assert.Equal(new BackupCountsDto(2, 0, 2), _quality.Backups(B));
    }

    [Fact]
    public void PlayingNow_ListsTheRunningStreamsPerFriend_WithMethodQualityAndTheLimit()
    {
        var running = NewGrant("g1", PlayMethod.Transcode, 3840, 2160, new QualityCap(1920, 1080, 0), held: true);
        _grants.TryAdd(running);
        _grants.TryAdd(NewGrant("g2", PlayMethod.DirectPlay, 1920, 1080, null, held: false)); // no slot: not playing
        _slots.Holding.Add("g1");

        var friends = _quality.PlayingNow();

        var friend = Assert.Single(friends);
        Assert.Equal("Alice's place", friend.Name);
        var stream = Assert.Single(friend.Streams);
        Assert.Equal(("converted", "4K", true, "Up to 1080p"), (stream.Method, stream.SourceQuality, stream.HeldByLimit, stream.LimitText));
        Assert.Equal("Film", stream.Title);
        var json = AdminTestSupport.Text(AdminReply.Json(StatusCodes.Status200OK, new { friends }));
        Assert.DoesNotContain(new string('9', 32), json, StringComparison.Ordinal); // the friend's item id
        Assert.DoesNotContain("peer-session", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ThePriorityTier_IsSetOnlyFromTheTierTheAdminSaw_AndTheOrderIsEvaluatedAgain()
    {
        var saved = await _quality.SetTierAsync(A, new PriorityTierRequest { Tier = PriorityTier.Backup, ExpectedCurrent = PriorityTier.Normal }, default);
        Assert.Equal((QualityChangeOutcome.Saved, (PriorityTier?)PriorityTier.Backup), (saved.Outcome, saved.Tier));
        Assert.Equal(PriorityTier.Backup, _state.Current.Home.Peers[0].PriorityTier);
        Assert.Equal(1, _ranking.Recomputes);

        var stale = await _quality.SetTierAsync(A, new PriorityTierRequest { Tier = PriorityTier.Prefer, ExpectedCurrent = PriorityTier.Normal }, default);
        Assert.Equal((QualityChangeOutcome.Changed, (PriorityTier?)PriorityTier.Backup), (stale.Outcome, stale.Tier));
        Assert.Equal(1, _ranking.Recomputes);

        Assert.Equal(QualityChangeOutcome.Invalid, (await _quality.SetTierAsync(A, new PriorityTierRequest { Tier = PriorityTier.Prefer }, default)).Outcome);
        Assert.Equal(QualityChangeOutcome.Invalid, (await _quality.SetTierAsync(A, new PriorityTierRequest { Tier = (PriorityTier)9, ExpectedCurrent = PriorityTier.Backup }, default)).Outcome);
        Assert.Equal(QualityChangeOutcome.NotFound, (await _quality.SetTierAsync("cccccccccccccccccccccccccccccccc", new PriorityTierRequest { Tier = PriorityTier.Prefer, ExpectedCurrent = PriorityTier.Normal }, default)).Outcome);
    }

    [Fact]
    public async Task ThePriorityRoute_AnswersTheTierAndRank_And409WhenItChanged()
    {
        _ranking.Why[A] = new RankExplanation(2, 2, PriorityTier.Backup, "slower link", "Backup only: 2nd of 2 (slower link)");
        var ok = new AdminQualityController(_state, _quality, NullLogger<AdminQualityController>.Instance) { ControllerContext = Context("""{"tier":"Backup","expectedCurrent":"Normal"}""", Admin) };
        var body = AdminTestSupport.Body(await ok.SetPriority(A, default));
        Assert.Equal("Backup", body.GetProperty("tier").GetString());
        Assert.Equal("Backup only: 2nd of 2 (slower link)", body.GetProperty("rank").GetProperty("label").GetString());

        var stale = new AdminQualityController(_state, _quality, NullLogger<AdminQualityController>.Instance) { ControllerContext = Context("""{"tier":"Prefer","expectedCurrent":"Normal"}""", Admin) };
        var conflict = AdminTestSupport.Body(await stale.SetPriority(A, default), StatusCodes.Status409Conflict);
        Assert.Equal("Backup", conflict.GetProperty("current").GetString());

        var bad = new AdminQualityController(_state, _quality, NullLogger<AdminQualityController>.Instance) { ControllerContext = Context("""{"tier":"Sometimes","expectedCurrent":"Backup"}""", Admin) };
        AdminTestSupport.Body(await bad.SetPriority(A, default), StatusCodes.Status400BadRequest);
        var routes = AdminRouteTests.AllRoutes().Where(static r => r.Method.Name == nameof(AdminQualityController.SetPriority)).Select(static r => r.Route).ToList();
        Assert.Equal(["Shoal/Admin/Peers/{peerKey}/Priority", "Shoal/Admin/Peers/{peerKey}/PriorityTier"], routes.Order());
    }

    [Fact]
    public void TheQualityRoutes_RequireElevation()
    {
        var type = typeof(AdminQualityController);
        var policy = ((Microsoft.AspNetCore.Authorization.AuthorizeAttribute)Attribute.GetCustomAttribute(type, typeof(Microsoft.AspNetCore.Authorization.AuthorizeAttribute))!).Policy;
        Assert.Equal(MediaBrowser.Common.Api.Policies.RequiresElevation, policy);
        var routes = AdminRouteTests.AllRoutes().Where(r => r.Method.DeclaringType == type).Select(static r => r.Verb + " " + r.Route).ToList();
        Assert.Contains("PUT Shoal/Admin/Peers/{peerKey}/QualityLimit", routes);
        Assert.Contains("POST Shoal/Admin/Peers/{peerKey}/Recommendations/Dismiss", routes);
        Assert.DoesNotContain(type.GetMethods(), static m => m.GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AllowAnonymousAttribute), true).Length > 0);
    }

    [Fact]
    public async Task TheController_AnswersAChangedLimitWith409AndTheCurrentOne_AndNeedsAnAdminId()
    {
        var controller = new AdminQualityController(_state, _quality, NullLogger<AdminQualityController>.Instance) { ControllerContext = Context("""{"maxClass":"720","expectedCurrent":{"maxClass":"1080"}}""", Admin) };

        var body = AdminTestSupport.Body(await controller.SetLimit(A, CancellationToken.None), StatusCodes.Status409Conflict);
        Assert.Equal("changed", body.GetProperty("error").GetString());
        Assert.Equal("Off", body.GetProperty("current").GetProperty("maxClass").GetString());

        var ok = new AdminQualityController(_state, _quality, NullLogger<AdminQualityController>.Instance) { ControllerContext = Context("""{"maxClass":"720","maxMbps":8,"expectedCurrent":{"maxClass":"Off"}}""", Admin) };
        var saved = AdminTestSupport.Body(await ok.SetLimit(A, CancellationToken.None));
        Assert.Equal("Up to 720p, 8 Mbps", saved.GetProperty("effective").GetProperty("text").GetString());

        var anonymous = new AdminQualityController(_state, _quality, NullLogger<AdminQualityController>.Instance) { ControllerContext = Context("{}", null) };
        Assert.Equal("adminUserRequired", AdminTestSupport.Body(await anonymous.SetLimit(A, CancellationToken.None), StatusCodes.Status400BadRequest).GetProperty("error").GetString());
    }

    private static ControllerContext Context(string body, Guid? user)
    {
        var http = new DefaultHttpContext();
        http.Request.ContentType = "application/json";
        http.Request.Body = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(body));
        if (user is { } id)
        {
            http.User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity([new System.Security.Claims.Claim("Jellyfin-UserId", id.ToString())], "test"));
        }

        return new ControllerContext { HttpContext = http };
    }

    private Grant NewGrant(string id, PlayMethod method, int width, int height, QualityCap? cap, bool held) => new()
    {
        GrantId = id,
        LocalUserId = Admin,
        LocalItemId = Guid.NewGuid(),
        CallerDeviceId = "dev-" + id,
        LocalPlaySessionId = "0123456789abcdef0123456789abcdef",
        PeerKey = A,
        PeerItemId = Guid.ParseExact(new string('9', 32), "N"),
        PeerMediaSourceId = Guid.ParseExact(new string('9', 32), "N"),
        PeerPlaySessionId = "peer-session-1",
        Method = method,
        Cap = cap,
        HeldByLimit = held,
        SourceWidth = width,
        SourceHeight = height,
        SourceBitrate = 40_000_000,
        Title = "Film",
        CreatedUtc = _time.GetUtcNow(),
        LastAccessUtc = _time.GetUtcNow(),
        LastAuthorizedUtc = _time.GetUtcNow(),
    };

    private sealed class FakeRank : IConnectionRanking
    {
        public IReadOnlyList<string> Keys { get; set; } = [];

        public Dictionary<string, RankExplanation> Why { get; } = new(StringComparer.Ordinal);

        public IReadOnlyList<string> Order() => Keys;

        public double? Throughput(string peerKey) => null;

        public RankExplanation? Explain(string peerKey) => Why.GetValueOrDefault(peerKey);

        public int Recomputes { get; private set; }

        public Task RecomputeAsync(System.Threading.CancellationToken cancellationToken)
        {
            Recomputes++;
            return Task.CompletedTask;
        }
    }

    private sealed class HoldingSlots : ISlotLedger
    {
        public HashSet<string> Holding { get; } = [];

        public bool HoldsSlot(Grant grant) => Holding.Contains(grant.GrantId);
    }
}
