using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Shoal.Diagnostics;
using Jellyfin.Plugin.Shoal.State;
using Xunit;

namespace Jellyfin.Plugin.Shoal.Tests;

/// <summary>U9 (DESIGN 7.3): each hint fires under its own link profile, and only then.</summary>
public sealed class LinkAnalyzerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);
    private static readonly PeerState Peer = new() { PeerKey = "4b0c1d2e3f405162738495a6b7c8d9e0", DisplayName = "Fern", Network = PeerNetwork.Tailnet };
    private static readonly IReadOnlyDictionary<string, IReadOnlyList<SpeedSample>> NoOthers = new Dictionary<string, IReadOnlyList<SpeedSample>>();

    private static SpeedSample Test(int hours, double mbps, double rtt = 30, string path = "unknown", double? parallel = null, double? drop = null) =>
        new() { T = T0.AddHours(hours), Kind = SpeedKinds.Light, Mbps = mbps, RttMedianMs = rtt, Path = path, MbpsParallel = parallel, RttDropPct = drop };

    private static List<SpeedSample> Good(int count = 4, double mbps = 80, double rtt = 30) => [.. Enumerable.Range(0, count).Select(i => Test(i, mbps, rtt))];

    private static IReadOnlyList<string> Kinds(LinkAnalysis a) => [.. a.Hints.Select(h => h.Kind)];

    [Fact]
    public void AHealthyLink_HasNoHints()
    {
        var a = LinkAnalyzer.Analyze(Peer, Good(), NoOthers);

        Assert.Empty(a.Hints);
        Assert.Equal("none", a.Bottleneck);
        Assert.Equal("unknown", a.Route);
    }

    [Fact]
    public void ARelayTheLocalApiConfirmed_IsRelayed_AndIsTheFirstBottleneck()
    {
        var history = Good();
        history.Add(Test(5, 20, 90, path: "derp"));

        var a = LinkAnalyzer.Analyze(Peer, history, NoOthers);

        Assert.Equal("derp", a.Route);
        Assert.Contains("relayed", Kinds(a));
        Assert.Equal("relay", a.Bottleneck);
    }

    [Fact]
    public void TheRelayHeuristic_NeedsTwiceTheRtt_MoreThan20msAbove_AndUnderHalfTheSpeed()
    {
        var before = Good(4, 80, 30);

        Assert.True(LinkAnalyzer.PossiblyRelayed(before, Test(9, 30, 95))); // 95 > 60, 65 above, 30 < 40
        Assert.False(LinkAnalyzer.PossiblyRelayed(before, Test(9, 50, 95))); // the speed is not under half
        Assert.False(LinkAnalyzer.PossiblyRelayed(before, Test(9, 30, 55))); // not twice the best
        Assert.False(LinkAnalyzer.PossiblyRelayed(Good(4, 80, 8), Test(9, 30, 18))); // twice, but not 20 ms above
        Assert.False(LinkAnalyzer.PossiblyRelayed([], Test(9, 30, 95))); // nothing to compare with
    }

    [Fact]
    public void ARoundTripThatFellByMoreThan40PercentInOneBurst_IsPossiblyRelayed()
    {
        Assert.True(LinkAnalyzer.PossiblyRelayed([], Test(0, 80, 40, drop: 45)));
        Assert.False(LinkAnalyzer.PossiblyRelayed([], Test(0, 80, 40, drop: 40)));
    }

    [Fact]
    public void APossiblyRelayedLink_IsAHintButNotAFact()
    {
        var history = Good();
        history.Add(Test(5, 30, 95, path: "derp?"));

        var a = LinkAnalyzer.Analyze(Peer, history, NoOthers);

        Assert.Equal("maybe", a.Route);
        Assert.Contains("maybeRelayed", Kinds(a));
        Assert.DoesNotContain("relayed", Kinds(a));
        Assert.Equal("none", a.Bottleneck);
    }

    [Fact]
    public void SlowTranscoding_WhileTheLinkIsFastEnough_IsTheHint_AndNamesTheSettingWhenTwoConversionsRan()
    {
        var history = Good();
        history.Add(new SpeedSample { T = T0.AddHours(6), Kind = SpeedKinds.Playback, Mbps = 30, StreamMbps = 8, TranscodeSpeed = 1.1, ConversionsActive = 1 });
        var single = LinkAnalyzer.Analyze(Peer, history, NoOthers);
        Assert.Contains(single.Hints, h => h.Kind == "peerTranscodeSlow");
        Assert.Equal("peerTranscode", single.Bottleneck);

        history.Add(new SpeedSample { T = T0.AddHours(7), Kind = SpeedKinds.Playback, Mbps = 30, StreamMbps = 8, TranscodeSpeed = 1.1, ConversionsActive = 3 });
        var shared = LinkAnalyzer.Analyze(Peer, history, NoOthers);
        var hint = Assert.Single(shared.Hints, h => h.Kind == "peerTranscodeSlowShared");
        Assert.Equal(3, hint.N);

        history.Add(new SpeedSample { T = T0.AddHours(8), Kind = SpeedKinds.Playback, Mbps = 30, StreamMbps = 8, TranscodeSpeed = 3.0, ConversionsActive = 3 });
        Assert.DoesNotContain("peerTranscodeSlow", Kinds(LinkAnalyzer.Analyze(Peer, history, NoOthers))); // quick enough
        history.Add(new SpeedSample { T = T0.AddHours(9), Kind = SpeedKinds.Playback, Mbps = 9, StreamMbps = 8, TranscodeSpeed = 1.1, ConversionsActive = 1 });
        Assert.DoesNotContain("peerTranscodeSlow", Kinds(LinkAnalyzer.Analyze(Peer, history, NoOthers))); // the link, not the transcode, held it back
    }

    [Fact]
    public void WhenEveryPeerIsUnderHalfOfItsBest_TheLikelyBottleneckIsYourOwnServer()
    {
        var slow = new List<SpeedSample> { Test(0, 80), Test(1, 80), Test(2, 20), Test(3, 20), Test(4, 20) };
        var others = new Dictionary<string, IReadOnlyList<SpeedSample>> { ["b"] = new List<SpeedSample> { Test(0, 50), Test(1, 50), Test(2, 10), Test(3, 10), Test(4, 10) } };

        var a = LinkAnalyzer.Analyze(Peer, slow, others);

        Assert.Contains("homeDownload", Kinds(a));
        Assert.Equal("homeDownload", a.Bottleneck);
    }

    [Fact]
    public void WhenOnlyThisPeerIsSlowAndTheRoundTripIsNormal_TheLikelyBottleneckIsThePeersUpload()
    {
        var slow = new List<SpeedSample> { Test(0, 80), Test(1, 80), Test(2, 20), Test(3, 20), Test(4, 20) };
        var others = new Dictionary<string, IReadOnlyList<SpeedSample>> { ["b"] = new List<SpeedSample>(Good(5, 50)) };

        var a = LinkAnalyzer.Analyze(Peer, slow, others);

        Assert.Equal(["peerUpload"], Kinds(a));
        Assert.Equal("peerUpload", a.Bottleneck);

        var slowAndFar = new List<SpeedSample> { Test(0, 80, 30), Test(1, 80, 30), Test(2, 20, 200), Test(3, 20, 200), Test(4, 20, 200) };
        Assert.DoesNotContain("peerUpload", Kinds(LinkAnalyzer.Analyze(Peer, slowAndFar, others))); // the latency moved too: not an upload problem
    }

    [Fact]
    public void AParallelRunOneAndAHalfTimesTheSingleOne_IsAPerConnectionLimit()
    {
        var history = Good();
        history.Add(Test(5, 40, parallel: 70));
        Assert.Contains("perConnection", Kinds(LinkAnalyzer.Analyze(Peer, history, NoOthers)));

        history.Add(Test(6, 40, parallel: 44));
        Assert.DoesNotContain("perConnection", Kinds(LinkAnalyzer.Analyze(Peer, history, NoOthers)));
    }

    [Fact]
    public void AProxy_AddsTheCpuHint()
    {
        var proxied = new PeerState { PeerKey = Peer.PeerKey, DisplayName = "Fern", Network = PeerNetwork.Tailnet, ProxyUrl = "socks5://127.0.0.1:1055" };

        var a = LinkAnalyzer.Analyze(proxied, Good(), NoOthers);

        Assert.Equal(["proxyCpu"], Kinds(a));
        Assert.Equal("proxyCpu", a.Bottleneck);
    }
}
