using System;
using System.Linq;
using Jellyfin.Plugin.Shoal.Diagnostics;
using Xunit;

namespace Jellyfin.Plugin.Shoal.Tests;

/// <summary>U8 (DESIGN 7.2): verdicts from throughput.</summary>
public sealed class VerdictEngineTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

    private static SpeedSample S(int minutes, double? mbps, string kind = SpeedKinds.Light, string? error = null) =>
        new() { T = T0.AddMinutes(minutes), Kind = kind, Mbps = mbps, Error = error };

    private static string Verdict(double t, string cls, bool autoFit = true) => VerdictEngine.Ladder(t, autoFit).Single(r => r.Cls == cls).Verdict;

    [Fact]
    public void T_IsTheMedianOfTheLastThreeSamplesWithASpeed_ErrorsAndPlaybackNotCounted()
    {
        var history = new[]
        {
            S(0, 100), S(1, 5), S(2, 50), S(3, null, error: "timeout"), S(4, 7, SpeedKinds.Manual), S(5, 1, SpeedKinds.Playback)
        };

        Assert.Equal(7, VerdictEngine.Throughput(history)); // 5, 50, 7 -> median 7
        Assert.Null(VerdictEngine.Throughput([S(0, null, error: "unreachable")]));
        Assert.Equal(15, VerdictEngine.Throughput([S(0, 10), S(1, 20)]));
    }

    [Fact]
    public void AtEightMbit_720pIsSmooth_1080pAndUpNeedATranscode_AndRemuxWillBuffer()
    {
        const double T = 7.5;

        Assert.Equal("smooth", Verdict(T, "720p")); // 7.5 >= 1.5 x 4
        Assert.Equal("willBuffer", Verdict(T, "1080pTranscode")); // the client asks for 8 Mbps and the link has 7.5
        Assert.Equal("needsTranscode", Verdict(T, "1080p"));
        Assert.Equal("needsTranscode", Verdict(T, "4k"));
        Assert.Equal("willBuffer", Verdict(T, "remux1080"));
        Assert.Equal("willBuffer", Verdict(T, "remux4k"));
        Assert.Equal(5, VerdictEngine.AutoFitMbps(T));
        Assert.Equal(
            [("1080p", "needsTranscode"), ("4k", "needsTranscode"), ("remux", "willBuffer")],
            VerdictEngine.Summary(T, true).Select(r => (r.Cls, r.Verdict)));
    }

    [Theory]
    [InlineData(6, "smooth")]
    [InlineData(5.9, "mayBuffer")]
    [InlineData(4, "mayBuffer")]
    [InlineData(3.9, "willBuffer")]
    public void ThePrecise720pThresholds(double t, string expected) => Assert.Equal(expected, Verdict(t, "720p"));

    [Fact]
    public void TheSmoothThresholds_AreOneAndAHalfTimesTheReference()
    {
        Assert.Equal(
            [("remux4k", 120.0), ("remux1080", 52.5), ("4k", 30.0), ("1080p", 15.0), ("1080pTranscode", 12.0), ("720p", 6.0)],
            VerdictEngine.Ladder(1, true).Select(r => (r.Cls, r.NeededMbps)));
        Assert.Equal("smooth", Verdict(15, "1080p"));
        Assert.Equal("mayBuffer", Verdict(14.9, "1080p"));
        Assert.Equal("mayBuffer", Verdict(10, "1080p"));
    }

    [Fact]
    public void WithAutoFitOff_ATitleThatDoesNotFitWillBuffer()
    {
        Assert.Equal("willBuffer", Verdict(7.5, "1080p", autoFit: false));
        Assert.Equal("willBuffer", VerdictEngine.TitleVerdict(7.5, 20, autoFit: false));
        Assert.Equal("needsTranscode", VerdictEngine.TitleVerdict(7.5, 20, autoFit: true));
        Assert.Equal("willBuffer", VerdictEngine.TitleVerdict(5, 20, autoFit: true)); // 3.3 Mbps is not worth transcoding down to
    }

    [Theory]
    [InlineData(15, 10, "smooth")]
    [InlineData(14.9, 10, "mayBuffer")]
    [InlineData(10, 10, "mayBuffer")]
    [InlineData(9.9, 10, "needsTranscode")]
    public void PerTitle_UsesTheActualBitrate(double t, double b, string expected) => Assert.Equal(expected, VerdictEngine.TitleVerdict(t, b, true));

    [Theory]
    [InlineData(23, "good")]
    [InlineData(50, "good")]
    [InlineData(51, "ok")]
    [InlineData(150, "ok")]
    [InlineData(160, "slow")]
    [InlineData(300, "slow")]
    [InlineData(301, "poor")]
    public void TheLatencyWords(double rtt, string expected) => Assert.Equal(expected, VerdictEngine.LatencyVerdict(rtt));

    [Fact]
    public void AnUnstableLink_HasOver30MsJitterOrOver2PercentLoss()
    {
        Assert.False(VerdictEngine.Unstable(30, 2));
        Assert.True(VerdictEngine.Unstable(31, 0));
        Assert.True(VerdictEngine.Unstable(1, 2.1));
        Assert.False(VerdictEngine.Unstable(null, null));
        Assert.Empty(VerdictEngine.Summary(null, true));
    }
}
