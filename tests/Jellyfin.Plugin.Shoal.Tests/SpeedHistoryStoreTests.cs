using System;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.Shoal.Diagnostics;
using Jellyfin.Plugin.Shoal.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Shoal.Tests;

/// <summary>U8 (DESIGN 3.6): the speed history file.</summary>
public sealed class SpeedHistoryStoreTests
{
    private const string Peer = "0123456789abcdef0123456789abcdef";
    private static readonly DateTimeOffset T0 = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

    private static SpeedSample Sample(int minutes, double mbps = 10) => new() { T = T0.AddMinutes(minutes), Kind = SpeedKinds.Light, Mbps = mbps, RttMedianMs = 20 };

    [Fact]
    public void ASampleRoundTrips_AndTheFileHoldsOneObjectPerLine()
    {
        using var dir = new TempDirectory();
        var store = new SpeedHistoryStore(new ShoalPaths(dir.Path), NullLogger.Instance);

        store.Append(Peer, Sample(0, 87.3));
        store.Append(Peer, new SpeedSample { T = T0.AddMinutes(1), Kind = SpeedKinds.Manual, Error = "timeout" });

        var read = store.Read(Peer);
        Assert.Equal(2, read.Count);
        Assert.Equal(87.3, read[0].Mbps);
        Assert.True(read[0].HasSpeed);
        Assert.Equal("timeout", read[1].Error);
        Assert.False(read[1].HasSpeed);
        var lines = File.ReadAllLines(Path.Combine(dir.Path, "shoal", "peers", Peer, "speed.jsonl"));
        Assert.Equal(2, lines.Length);
        Assert.Contains("\"kind\":\"light\"", lines[0], StringComparison.Ordinal);
        Assert.Contains("\"rttMedianMs\":20", lines[0], StringComparison.Ordinal);
    }

    [Fact]
    public void TheFile_IsCompactedToTwoThousandLines_WhenItPassesTwoThousandFiveHundred()
    {
        using var dir = new TempDirectory();
        var store = new SpeedHistoryStore(new ShoalPaths(dir.Path), NullLogger.Instance);

        for (var i = 0; i < SpeedHistoryStore.CompactAt + 1; i++)
        {
            store.Append(Peer, Sample(i));
        }

        var read = store.Read(Peer);
        Assert.Equal(SpeedHistoryStore.KeepLines, read.Count);
        Assert.Equal(T0.AddMinutes(SpeedHistoryStore.CompactAt + 1 - SpeedHistoryStore.KeepLines), read[0].T); // the newest are kept
        Assert.Equal(T0.AddMinutes(SpeedHistoryStore.CompactAt), read[^1].T);
    }

    [Fact]
    public void ManyPlaybackSamples_NeverPushTheTestsOutOfTheFile()
    {
        using var dir = new TempDirectory();
        var store = new SpeedHistoryStore(new ShoalPaths(dir.Path), NullLogger.Instance);
        for (var i = 0; i < 100; i++)
        {
            store.Append(Peer, Sample(i, 8 + i));
        }

        for (var i = 0; i < SpeedHistoryStore.CompactAt + 200; i++)
        {
            store.Append(Peer, new SpeedSample { T = T0.AddMinutes(1000 + i), Kind = SpeedKinds.Playback, Mbps = 30 });
        }

        var read = store.Read(Peer);
        Assert.Equal(100, read.Count(s => s.Kind == SpeedKinds.Light)); // every test is still there
        Assert.InRange(read.Count(s => s.Kind == SpeedKinds.Playback), 1, SpeedHistoryStore.CompactAt);
        Assert.NotNull(Jellyfin.Plugin.Shoal.Diagnostics.VerdictEngine.Throughput(read));
        Assert.True(read.Count <= SpeedHistoryStore.CompactAt);
    }

    [Fact]
    public void TheCompaction_KeepsTheNewestThreeHundredPlaybacks_AndTheNewestOfTheRest()
    {
        using var dir = new TempDirectory();
        var store = new SpeedHistoryStore(new ShoalPaths(dir.Path), NullLogger.Instance);
        for (var i = 0; i < 2501; i++)
        {
            store.Append(Peer, i % 2 == 0
                ? new SpeedSample { T = T0.AddMinutes(i), Kind = SpeedKinds.Playback, Mbps = 1 }
                : new SpeedSample { T = T0.AddMinutes(i), Kind = SpeedKinds.Light, Mbps = 2 });
        }

        var read = store.Read(Peer);
        Assert.Equal(SpeedHistoryStore.PlaybackKeep, read.Count(s => s.Kind == SpeedKinds.Playback));
        Assert.Equal(1250 - 0, read.Count(s => s.Kind == SpeedKinds.Light)); // fewer than 1,700: all of them
        Assert.Equal(T0.AddMinutes(2500), read[^1].T); // the newest line is kept
    }

    [Fact]
    public void ADamagedLine_IsSkipped_AndTheRestIsRead()
    {
        using var dir = new TempDirectory();
        var store = new SpeedHistoryStore(new ShoalPaths(dir.Path), NullLogger.Instance);
        store.Append(Peer, Sample(0));
        File.AppendAllText(Path.Combine(dir.Path, "shoal", "peers", Peer, "speed.jsonl"), "{not json\n\n[1,2]\n");
        store.Append(Peer, Sample(1));

        Assert.Equal(2, store.Read(Peer).Count);
    }

    [Fact]
    public void Read_CanStartAtATime_AndAPeerWithoutAFileIsEmpty()
    {
        using var dir = new TempDirectory();
        var store = new SpeedHistoryStore(new ShoalPaths(dir.Path), NullLogger.Instance);
        Assert.Empty(store.Read(Peer));
        for (var i = 0; i < 5; i++)
        {
            store.Append(Peer, Sample(i * 60));
        }

        Assert.Equal(2, store.Read(Peer, T0.AddHours(3)).Count);
        store.Delete(Peer);
        Assert.Empty(store.Read(Peer));
    }

    [Fact]
    public void AnInvalidPeerKey_IsRefused()
    {
        using var dir = new TempDirectory();
        var store = new SpeedHistoryStore(new ShoalPaths(dir.Path), NullLogger.Instance);

        Assert.Throws<ArgumentException>(() => store.Append("../evil", Sample(0)));
        Assert.Throws<ArgumentException>(() => store.Read("a/b"));
    }
}
