using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.Diagnostics;
using Jellyfin.Plugin.Shoal.Peers;
using Jellyfin.Plugin.Shoal.State;
using Jellyfin.Plugin.Shoal.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Shoal.Tests;

/// <summary>
/// PLAN v2.9 K54: the automatic ranking with hysteresis (tiers, up, stable, T, pairing order; 25 percent on two evaluations 12 hours apart; offline over
/// 30 minutes demotes at once; back, promoted after 2 hours), the per-title quality-limit fit within a tier, and the ranker that stores it.
/// </summary>
public sealed class RankingTests
{
    private static readonly DateTime T0 = new(2026, 10, 18, 0, 0, 0, DateTimeKind.Utc);

    private static RankInput In(string key, double? t, PriorityTier tier = PriorityTier.Normal, bool online = true, bool unstable = false, int pairing = 0, AutoRank? state = null, DateTimeOffset? since = null, bool trusted = true) =>
        new(key, tier, online, trusted, unstable, t, since ?? new DateTimeOffset(T0.AddDays(-1)), pairing, state ?? new AutoRank());

    // Feeds an outcome's states back in, as the ranker does between evaluations.
    private static List<RankInput> Next(RankOutcome outcome, params RankInput[] inputs) => [.. inputs.Select(i => i with { State = outcome.States[i.PeerKey] })];

    [Fact]
    public void TheFirstEvaluation_OrdersByTierThenUpThenStableThenTThenPairing_WithNoHysteresis()
    {
        var outcome = VerdictEngine.RankConnections(
            [
                In("slow", 20, pairing: 0),
                In("fast", 100, pairing: 1),
                In("tie", 20, pairing: 2),
                In("shaky", 500, unstable: true, pairing: 3),
                In("backup", 900, PriorityTier.Backup, pairing: 4),
                In("prefer", 5, PriorityTier.Prefer, pairing: 5),
                In("gone", 900, online: false, pairing: 6, since: new DateTimeOffset(T0.AddHours(-2))),
            ],
            T0);

        Assert.Equal(["prefer", "fast", "slow", "tie", "shaky", "gone", "backup"], outcome.Order);
        Assert.True(outcome.Changed);
        Assert.Equal(1, outcome.States["fast"].Position);
        Assert.Equal(100, outcome.States["fast"].Score);
        Assert.Equal(T0, outcome.States["gone"].DemotedUtc);
    }

    [Fact]
    public void ABackupServer_StaysLast_HoweverFastItIs()
    {
        var outcome = VerdictEngine.RankConnections([In("backup", 1000, PriorityTier.Backup), In("normal", 1)], T0);

        Assert.Equal(["normal", "backup"], outcome.Order);
    }

    [Fact]
    public void AChallengerNeeds25Percent_OnTwoEvaluationsAtLeast12HoursApart()
    {
        var first = VerdictEngine.RankConnections([In("a", 100), In("b", 50, pairing: 1)], T0);
        Assert.Equal(["a", "b"], first.Order);

        // b is now 30 percent faster: one win is not enough, a second win 6 hours later is too soon, the one at 12 hours swaps.
        var win1 = VerdictEngine.RankConnections(Next(first, In("a", 100), In("b", 130, pairing: 1)), T0.AddHours(1));
        Assert.Equal(["a", "b"], win1.Order);
        Assert.Equal("b", win1.States["a"].PendingChallenger);
        Assert.Equal(1, win1.States["a"].Streak);

        var soon = VerdictEngine.RankConnections(Next(win1, In("a", 100), In("b", 130, pairing: 1)), T0.AddHours(7));
        Assert.Equal(["a", "b"], soon.Order);

        var swap = VerdictEngine.RankConnections(Next(soon, In("a", 100), In("b", 130, pairing: 1)), T0.AddHours(13));
        Assert.Equal(["b", "a"], swap.Order);
        Assert.Null(swap.States["a"].PendingChallenger);
        Assert.Equal(0, swap.States["b"].Position);
        Assert.Equal(T0.AddHours(13), swap.States["b"].Since);
    }

    [Fact]
    public void Under25Percent_NeverSwaps_SoTheOrderDoesNotFlap()
    {
        var outcome = VerdictEngine.RankConnections([In("a", 100), In("b", 50, pairing: 1)], T0);
        for (var hour = 1; hour <= 72; hour += 6)
        {
            outcome = VerdictEngine.RankConnections(Next(outcome, In("a", 100), In("b", hour % 12 == 1 ? 124 : 80, pairing: 1)), T0.AddHours(hour));
            Assert.Equal(["a", "b"], outcome.Order);
            Assert.Null(outcome.States["a"].PendingChallenger);
        }
    }

    [Fact]
    public void ALostEvaluation_StartsTheChallengeOver()
    {
        var outcome = VerdictEngine.RankConnections([In("a", 100), In("b", 50, pairing: 1)], T0);
        outcome = VerdictEngine.RankConnections(Next(outcome, In("a", 100), In("b", 130, pairing: 1)), T0.AddHours(1));
        outcome = VerdictEngine.RankConnections(Next(outcome, In("a", 100), In("b", 110, pairing: 1)), T0.AddHours(7)); // lost
        Assert.Null(outcome.States["a"].PendingChallenger);

        outcome = VerdictEngine.RankConnections(Next(outcome, In("a", 100), In("b", 130, pairing: 1)), T0.AddHours(14)); // a first win again
        Assert.Equal(["a", "b"], outcome.Order);
        outcome = VerdictEngine.RankConnections(Next(outcome, In("a", 100), In("b", 130, pairing: 1)), T0.AddHours(26));
        Assert.Equal(["b", "a"], outcome.Order);
    }

    [Fact]
    public void OfflineOver30Minutes_DemotesAtOnce_ButAShortBlipDoesNot()
    {
        var outcome = VerdictEngine.RankConnections([In("a", 100), In("b", 50, pairing: 1)], T0);

        var blip = VerdictEngine.RankConnections(Next(outcome, In("a", 100, online: false, since: new DateTimeOffset(T0.AddMinutes(-20))), In("b", 50, pairing: 1)), T0);
        Assert.Equal(["a", "b"], blip.Order);
        Assert.Null(blip.States["a"].DemotedUtc);

        var down = VerdictEngine.RankConnections(Next(blip, In("a", 100, online: false, since: new DateTimeOffset(T0.AddMinutes(-31))), In("b", 50, pairing: 1)), T0);
        Assert.Equal(["b", "a"], down.Order);
        Assert.Equal(T0, down.States["a"].DemotedUtc);
    }

    [Fact]
    public void ADemotedServer_IsPromotedOnlyAfterTwoHoursOnline()
    {
        var outcome = VerdictEngine.RankConnections([In("a", 100, online: false, since: new DateTimeOffset(T0.AddHours(-1))), In("b", 50, pairing: 1)], T0);
        Assert.Equal(["b", "a"], outcome.Order);

        var back = VerdictEngine.RankConnections(Next(outcome, In("a", 100, since: new DateTimeOffset(T0.AddHours(1))), In("b", 50, pairing: 1)), T0.AddHours(2));
        Assert.Equal(["b", "a"], back.Order); // online for one hour: still demoted
        Assert.NotNull(back.States["a"].DemotedUtc);

        var stable = VerdictEngine.RankConnections(Next(back, In("a", 100, since: new DateTimeOffset(T0.AddHours(1))), In("b", 50, pairing: 1)), T0.AddHours(3));
        Assert.Null(stable.States["a"].DemotedUtc);
        Assert.Equal("b", stable.Order[0]); // back in the up group, below the one that held the place; speed then challenges as usual
    }

    [Fact]
    public void AnUnknownStatusTime_NeitherDemotesNorPromotes()
    {
        var input = In("a", 100, online: false) with { StatusSinceUtc = null };
        var outcome = VerdictEngine.RankConnections([input], T0);

        Assert.Null(outcome.States["a"].DemotedUtc);
    }

    [Fact]
    public void ANewConnection_StartsAtTheEndOfItsGroup()
    {
        var outcome = VerdictEngine.RankConnections([In("a", 10), In("b", 5, pairing: 1)], T0);

        var added = VerdictEngine.RankConnections([.. Next(outcome, In("a", 10), In("b", 5, pairing: 1)), In("c", 500, pairing: 2)], T0.AddHours(1));

        Assert.Equal(["a", "b", "c"], added.Order);
    }

    [Fact]
    public void ANotTrustedServer_RanksAsDown()
    {
        var outcome = VerdictEngine.RankConnections([In("a", 100, trusted: false, since: new DateTimeOffset(T0.AddHours(-1))), In("b", 5, pairing: 1)], T0);

        Assert.Equal(["b", "a"], outcome.Order);
    }

    [Fact]
    public void NothingNew_ChangesNothing()
    {
        var first = VerdictEngine.RankConnections([In("a", 100), In("b", 50, pairing: 1)], T0);

        var again = VerdictEngine.RankConnections(Next(first, In("a", 100), In("b", 50, pairing: 1)), T0.AddHours(1));

        Assert.False(again.Changed);
        Assert.Equal(first.States["a"].Since, again.States["a"].Since);
    }

    [Fact]
    public void TheInputStates_AreNotChanged()
    {
        var state = new AutoRank();
        VerdictEngine.RankConnections([In("a", 100, state: state)], T0);

        Assert.Null(state.Since);
        Assert.Equal(0, state.Score);
    }

    [Fact]
    public void AQualityLimitFit_MovesACopyUpOnlyWithinItsTier()
    {
        var tiers = new Dictionary<string, PriorityTier> { ["p"] = PriorityTier.Prefer, ["n1"] = PriorityTier.Normal, ["n2"] = PriorityTier.Normal, ["b"] = PriorityTier.Backup };
        var fits = new HashSet<string> { "n2", "b" };

        var order = VerdictEngine.OrderForTitle(["p", "n1", "n2", "b"], k => tiers[k], fits.Contains);

        Assert.Equal(["p", "n2", "n1", "b"], order);
    }

    [Fact]
    public void AFit_NeedsTheLimitAndTOfOneAndAHalfTimesTheBitrate()
    {
        var off = new QualityLimit();
        var hd = new QualityLimit { MaxClass = QualityClass.Hd1080 };
        var capped = new QualityLimit { MaxClass = QualityClass.Off, MaxMbps = 10 };

        Assert.True(VerdictEngine.FitsTitle(off, 3840, 2160, 20_000_000, 30));
        Assert.False(VerdictEngine.FitsTitle(off, 3840, 2160, 20_000_000, 29));
        Assert.False(VerdictEngine.FitsTitle(hd, 3840, 2160, 20_000_000, 100));
        Assert.True(VerdictEngine.FitsTitle(hd, 1920, 1080, 8_000_000, 100));
        Assert.False(VerdictEngine.FitsTitle(capped, 1920, 1080, 12_000_000, 100));
        Assert.False(VerdictEngine.FitsTitle(off, 1920, 1080, 8_000_000, null));
        Assert.False(VerdictEngine.FitsTitle(off, 1920, 1080, 0, 100));
    }

    [Fact]
    public void K52_TheFitRank_IsFitWithHeadroom_ThenInsideTheLimit_ThenOver()
    {
        var hd = new QualityLimit { MaxClass = QualityClass.Hd1080, MaxMbps = 20 };

        Assert.Equal(0, VerdictEngine.FitRank(hd, 1920, 1080, 8_000_000, 50));
        Assert.Equal(1, VerdictEngine.FitRank(hd, 1920, 1080, 8_000_000, null)); // never measured: inside the limit still beats a conversion
        Assert.Equal(1, VerdictEngine.FitRank(hd, 1920, 1080, 8_000_000, 5));
        Assert.Equal(2, VerdictEngine.FitRank(hd, 3840, 2160, 8_000_000, 500));
        Assert.Equal(2, VerdictEngine.FitRank(hd, 1920, 1080, 30_000_000, 500));
        Assert.Equal(2, VerdictEngine.FitRank(hd, 0, 0, 8_000_000, 500)); // an unknown size is never assumed small
        Assert.Equal(1, VerdictEngine.FitRank(new QualityLimit(), 3840, 2160, 40_000_000, null));
    }

    [Fact]
    public void K52_ARankOrder_KeepsTheConnectionOrderForEqualRanks_AndNeverCrossesATier()
    {
        var tiers = new Dictionary<string, PriorityTier> { ["a"] = PriorityTier.Normal, ["b"] = PriorityTier.Normal, ["c"] = PriorityTier.Normal, ["z"] = PriorityTier.Backup };
        var ranks = new Dictionary<string, int> { ["a"] = 2, ["b"] = 1, ["c"] = 1, ["z"] = 0 };

        Assert.Equal(["b", "c", "a", "z"], VerdictEngine.OrderForTitle(["a", "b", "c", "z"], k => tiers[k], k => ranks[k]));
    }

    [Fact]
    public async Task TheRanker_StoresTheStates_AndAnswersTheOrder()
    {
        using var dir = new TempDirectory();
        var time = new ManualTimeProvider();
        using var state = new StateStore(new ShoalPaths(dir.Path), NullLogger.Instance, time, null);
        await state.UpdateAsync(s =>
        {
            s.Home.Peers.Add(new PeerState { PeerKey = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", DisplayName = "A", BaseUrl = "http://a.tail1234.ts.net:8096", Priority = 100 });
            s.Home.Peers.Add(new PeerState { PeerKey = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", DisplayName = "B", BaseUrl = "http://b.tail1234.ts.net:8096", Priority = 200 });
        });
        var registry = new PeerRegistry(time);
        registry.SetStatus("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", PeerStatus.Online);
        registry.SetStatus("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", PeerStatus.Online);
        var speeds = new Dictionary<string, IReadOnlyList<SpeedSample>>
        {
            ["aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"] = [new SpeedSample { T = time.GetUtcNow(), Kind = SpeedKinds.Light, Mbps = 10 }],
            ["bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"] = [new SpeedSample { T = time.GetUtcNow(), Kind = SpeedKinds.Light, Mbps = 90 }],
        };
        using var ranker = new ConnectionRanker(state, registry, k => speeds.GetValueOrDefault(k) ?? [], time, NullLogger.Instance);

        Assert.Equal(["aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"], ranker.Order()); // not evaluated yet: the old rank rule

        var outcome = await ranker.EvaluateAsync();

        Assert.NotNull(outcome);
        Assert.Equal(["bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"], ranker.Order());
        Assert.Equal(90, ranker.Throughput("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"));
        var stored = state.Current.Home.Peers.Single(static p => p.PeerKey == "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb").AutoRank;
        Assert.Equal(0, stored.Position);
        Assert.Equal(90, stored.Score);
        Assert.NotNull(stored.Since);
        var generation = state.Current.Generation;

        await ranker.EvaluateAsync();
        Assert.Equal(generation, state.Current.Generation); // nothing changed: nothing written
    }
}
