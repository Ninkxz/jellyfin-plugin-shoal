using System;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.Api.Admin;
using Jellyfin.Plugin.Shoal.Sync;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Shoal.Tests;

/// <summary>
/// <c>GET Shoal/Admin/Peers/{h}/Overlap</c> (DESIGN 5.8, 9.6): the report of the last complete sync, by quality, from the catalog. The admin page asked for
/// it from the start and got a 404 on a real server.
/// </summary>
public sealed class AdminOverlapTests
{
    private static CatalogEntry Entry(int n, EntryState state, int width, int height, long peerBytes, long localBytes = 0) => new()
    {
        StubKey = "stub" + n.ToString("x8", System.Globalization.CultureInfo.InvariantCulture),
        PeerItemId = n.ToString("x32", System.Globalization.CultureInfo.InvariantCulture),
        StubRelPath = state == EntryState.Active ? "movies/title" + n.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".strm" : null,
        MappingKey = "movies1",
        State = state,
        Width = width,
        Height = height,
        SizeBytes = peerBytes,
        LocalSizeBytes = localBytes,
    };

    private static async Task<(PairingRig Rig, AdminOverlapController Controller, CatalogStore Catalogs)> SetUpAsync()
    {
        var rig = new PairingRig();
        await rig.State.UpdateAsync(StateSamples.Fill);
        var catalogs = new CatalogStore(rig.Paths, NullLogger<CatalogStore>.Instance);
        return (rig, new AdminOverlapController(rig.State, catalogs), catalogs);
    }

    [Fact]
    public async Task BeforeTheFirstCompleteSync_TheAnswerHasNoTime_AndTheUiShowsItsEmptyLine()
    {
        var (rig, controller, _) = await SetUpAsync();
        using (rig)
        {
            var body = AdminTestSupport.Body(controller.Overlap(StateSamples.PeerKeyA));
            Assert.True(!body.TryGetProperty("computedUtc", out var at) || at.ValueKind == System.Text.Json.JsonValueKind.Null);
            Assert.True(!body.TryGetProperty("both", out var both) || both.ValueKind == System.Text.Json.JsonValueKind.Null);
        }
    }

    [Fact]
    public async Task AfterASync_TheReportAddsUpByQuality()
    {
        var (rig, controller, catalogs) = await SetUpAsync();
        using (rig)
        {
            var load = catalogs.Load(StateSamples.PeerKeyA);
            load.Document.Entries.AddRange(
            [
                Entry(1, EntryState.DuplicateLocal, 3840, 2160, 9_000, 8_000),
                Entry(2, EntryState.DuplicateLocal, 1920, 1080, 4_000, 3_500),
                Entry(3, EntryState.DuplicateLocal, 1280, 720, 1_000, 900),
                Entry(4, EntryState.Active, 1920, 800, 5_000),
                Entry(5, EntryState.Active, 640, 480, 300),
                Entry(6, EntryState.Active, 0, 0, 100),
                Entry(7, EntryState.DuplicatePeer, 1920, 1080, 777),
            ]);
            load.Document.Report = new OverlapReport
            {
                OverlapCount = 3,
                OverlapLocalBytes = 12_400,
                OverlapPeerBytes = 14_000,
                GainCount = 3,
                GainBytes = 5_400,
                ComputedUtc = new DateTime(2026, 10, 15, 8, 0, 0, DateTimeKind.Utc),
            };
            catalogs.Save(load.Document);

            var body = AdminTestSupport.Body(controller.Overlap(StateSamples.PeerKeyA));
            Assert.Equal(new DateTimeOffset(2026, 10, 15, 8, 0, 0, TimeSpan.Zero), body.GetProperty("computedUtc").GetDateTimeOffset());
            Assert.Equal(3, body.GetProperty("both").GetProperty("count").GetInt32());
            Assert.Equal(12_400, body.GetProperty("both").GetProperty("localBytes").GetInt64());
            Assert.Equal(3, body.GetProperty("gain").GetProperty("count").GetInt32());
            Assert.Equal(5_400, body.GetProperty("gain").GetProperty("peerBytes").GetInt64());
            var rows = body.GetProperty("byQuality").EnumerateArray().ToDictionary(r => r.GetProperty("q").GetString()!);
            Assert.Equal(["4k", "1080p", "720p", "sd"], body.GetProperty("byQuality").EnumerateArray().Select(r => r.GetProperty("q").GetString()!).ToArray());
            Assert.Equal(1, rows["4k"].GetProperty("bothCount").GetInt32());
            Assert.Equal(8_000, rows["4k"].GetProperty("localBytes").GetInt64());
            Assert.Equal(1, rows["1080p"].GetProperty("bothCount").GetInt32());
            Assert.Equal(1, rows["1080p"].GetProperty("gainCount").GetInt32()); // a 1920 by 800 scope title is 1080p by width
            Assert.Equal(5_000, rows["1080p"].GetProperty("peerBytes").GetInt64());
            Assert.Equal(1, rows["720p"].GetProperty("bothCount").GetInt32());
            Assert.Equal(2, rows["sd"].GetProperty("gainCount").GetInt32());
            Assert.Equal(400, rows["sd"].GetProperty("peerBytes").GetInt64());
            Assert.Equal(body.GetProperty("both").GetProperty("count").GetInt32(), rows.Values.Sum(r => r.GetProperty("bothCount").GetInt32()));
        }
    }

    [Fact]
    public async Task AnUnknownServer_Is404()
    {
        var (rig, controller, _) = await SetUpAsync();
        using (rig)
        {
            AdminTestSupport.Body(controller.Overlap("nope"), 404);
        }
    }

    [Theory]
    [InlineData(3840, 2160, "4k")]
    [InlineData(3840, 1600, "4k")]
    [InlineData(1920, 1080, "1080p")]
    [InlineData(1920, 800, "1080p")]
    [InlineData(1280, 720, "720p")]
    [InlineData(854, 480, "sd")]
    [InlineData(0, 0, "sd")]
    public void QualityBuckets(int width, int height, string expected) => Assert.Equal(expected, AdminOverlapController.QualityOf(width, height));
}
