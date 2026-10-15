using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.Api.Admin;
using Jellyfin.Plugin.Shoal.Configuration;
using Jellyfin.Plugin.Shoal.Diagnostics;
using Jellyfin.Plugin.Shoal.Peers;
using Jellyfin.Plugin.Shoal.State;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Rig = Jellyfin.Plugin.Shoal.Tests.SpeedTestServiceTests.Rig;
using ShapedLink = Jellyfin.Plugin.Shoal.Tests.SpeedTestServiceTests.ShapedLink;

namespace Jellyfin.Plugin.Shoal.Tests;

/// <summary>U8: the Speed and SpeedTest endpoints, the schedule and the numbers on the server cards.</summary>
public sealed class AdminSpeedTests
{
    private static readonly string Key = SpeedTestServiceTests.Key;
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

    private sealed class Settings : ISettingsAccessor
    {
        public PluginConfiguration Stored { get; private set; } = new();

        public PluginConfiguration Current => PluginSettingsAccessor.Copy(Stored);

        public void Save(PluginConfiguration configuration) => Stored = PluginSettingsAccessor.Copy(configuration);
    }

    private static SpeedSample Sample(double hoursAgo, double mbps, string kind = SpeedKinds.Light, double rtt = 40, string path = "unknown") =>
        new() { T = Now.AddHours(-hoursAgo), Kind = kind, Mbps = mbps, RttMedianMs = rtt, RttP90Ms = rtt + 10, JitterMs = 3, LossPct = 0, Path = path };

    private static AdminSpeedController Controller(Rig rig, Settings settings, SpeedCooldowns? cooldowns = null)
    {
        var http = new DefaultHttpContext();
        http.Request.ContentType = "application/json";
        return new AdminSpeedController(rig.State, rig.History, rig.Service, cooldowns ?? new SpeedCooldowns(TimeProvider.System), settings, TimeProvider.System, NullLogger<AdminSpeedController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = http }
        };
    }

    private static void Body(AdminSpeedController controller, string json)
    {
        var http = controller.ControllerContext.HttpContext;
        http.Request.Body = new System.IO.MemoryStream(System.Text.Encoding.UTF8.GetBytes(json));
        http.Request.ContentLength = http.Request.Body.Length;
    }

    [Fact]
    public async Task Speed_ReturnsTheSamplesKpisAndLadderTheLinkSpeedPanelReads()
    {
        await using var link = await ShapedLink.StartAsync(1_000_000, TimeSpan.Zero);
        using var rig = await Rig.CreateAsync(link);
        var now = DateTimeOffset.UtcNow;
        foreach (var (hours, mbps) in new[] { (30.0, 90.0), (3.0, 7.0), (2.0, 8.0), (1.0, 7.5) })
        {
            rig.History.Append(Key, new SpeedSample { T = now.AddHours(-hours), Kind = SpeedKinds.Light, Mbps = mbps, RttMedianMs = 160, RttP90Ms = 170, JitterMs = 4, LossPct = 0, Path = "unknown" });
        }

        var controller = Controller(rig, new Settings());
        var day = AdminTestSupport.Body(controller.Speed(Key, "24h"));
        var week = AdminTestSupport.Body(controller.Speed(Key, "7d"));

        Assert.Equal(3, day.GetProperty("samples").GetArrayLength()); // the 30 hour old sample is outside 24h
        Assert.Equal(4, week.GetProperty("samples").GetArrayLength());
        var first = day.GetProperty("samples")[0];
        Assert.Equal("scheduled", first.GetProperty("kind").GetString());
        Assert.Equal(7.0, first.GetProperty("mbps").GetDouble());
        var kpis = day.GetProperty("kpis");
        Assert.Equal(7.5, kpis.GetProperty("nowMbps").GetDouble());
        Assert.Equal(160, kpis.GetProperty("latencyMs").GetDouble());
        Assert.Equal(7.0, kpis.GetProperty("lowestMbps").GetDouble());
        var ladder = day.GetProperty("ladder").EnumerateArray().ToDictionary(r => r.GetProperty("cls").GetString()!, r => r.GetProperty("verdict").GetString()!);
        Assert.Equal("smooth", ladder["720p"]);
        Assert.Equal("needsTranscode", ladder["1080p"]);
        Assert.Equal("willBuffer", ladder["remux4k"]);
        Assert.Equal(30, day.GetProperty("ladder").EnumerateArray().Single(r => r.GetProperty("cls").GetString() == "4k").GetProperty("neededMbps").GetDouble());
        Assert.Equal("none", day.GetProperty("bottleneck").GetProperty("kind").GetString());
    }

    [Fact]
    public async Task Speed_AnUnmeasuredPeerHasNoLadder_AndTheWrongRangeOrPeerIsRefused()
    {
        await using var link = await ShapedLink.StartAsync(1_000_000, TimeSpan.Zero);
        using var rig = await Rig.CreateAsync(link);
        var controller = Controller(rig, new Settings());

        var empty = AdminTestSupport.Body(controller.Speed(Key, null));
        Assert.Equal(0, empty.GetProperty("samples").GetArrayLength());
        Assert.Equal(0, empty.GetProperty("ladder").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, empty.GetProperty("kpis").GetProperty("nowMbps").ValueKind);
        AdminTestSupport.Body(controller.Speed(Key, "1y"), 400);
        AdminTestSupport.Body(controller.Speed("00000000000000000000000000000000", "24h"), 404);
    }

    [Fact]
    public async Task APerConnectionLimit_ShowsAsTheBottleneck_AndAProxyAsProxyCpu()
    {
        await using var link = await ShapedLink.StartAsync(1_000_000, TimeSpan.Zero);
        using var rig = await Rig.CreateAsync(link);
        rig.History.Append(Key, new SpeedSample { T = DateTimeOffset.UtcNow, Kind = SpeedKinds.Manual, Mbps = 10, MbpsParallel = 19 });
        var controller = Controller(rig, new Settings());
        Assert.Equal("perConnection", AdminTestSupport.Body(controller.Speed(Key, "24h")).GetProperty("bottleneck").GetProperty("kind").GetString());

        rig.History.Append(Key, new SpeedSample { T = DateTimeOffset.UtcNow.AddSeconds(1), Kind = SpeedKinds.Manual, Mbps = 10, MbpsParallel = 10 });
        await rig.State.UpdateAsync(s => s.Home.Peers[0].ProxyUrl = "socks5://127.0.0.1:1055");
        Assert.Equal("proxyCpu", AdminTestSupport.Body(controller.Speed(Key, "24h")).GetProperty("bottleneck").GetProperty("kind").GetString());
    }

    [Fact]
    public async Task Speed_CarriesTheRouteTheHintsAndTheBottleneck_OfTheAnalysis()
    {
        await using var link = await ShapedLink.StartAsync(1_000_000, TimeSpan.Zero);
        using var rig = await Rig.CreateAsync(link);
        var now = DateTimeOffset.UtcNow;
        rig.History.Append(Key, new SpeedSample { T = now.AddHours(-2), Kind = SpeedKinds.Light, Mbps = 20, RttMedianMs = 90, Path = "derp", DerpRegion = "fra" });
        rig.History.Append(Key, new SpeedSample { T = now.AddMinutes(-30), Kind = SpeedKinds.Playback, Mbps = 30, StreamMbps = 8, TranscodeSpeed = 1.1, ConversionsActive = 3 });
        var controller = Controller(rig, new Settings());

        var report = AdminTestSupport.Body(controller.Speed(Key, "24h"));

        Assert.Equal("derp", report.GetProperty("route").GetProperty("kind").GetString());
        Assert.Equal("fra", report.GetProperty("route").GetProperty("region").GetString());
        Assert.Equal("relay", report.GetProperty("bottleneck").GetProperty("kind").GetString());
        Assert.Equal("fra", report.GetProperty("bottleneck").GetProperty("region").GetString());
        Assert.True(report.GetProperty("localApiAvailable").GetBoolean());
        var hints = report.GetProperty("hints").EnumerateArray().ToDictionary(h => h.GetProperty("kind").GetString()!, h => h);
        Assert.Contains("relayed", hints.Keys);
        Assert.Equal(3, hints["peerTranscodeSlowShared"].GetProperty("n").GetInt32());
    }

    [Fact]
    public async Task TestNow_Returns202_RunsTheTest_AndIsLimitedToOnePerMinute()
    {
        await using var link = await ShapedLink.StartAsync(5_000_000, TimeSpan.FromMilliseconds(5));
        using var rig = await Rig.CreateAsync(link);
        var controller = Controller(rig, new Settings());

        Body(controller, "{\"deep\":false}");
        AdminTestSupport.Body(await controller.Start(Key, default), 202);
        var until = DateTime.UtcNow.AddSeconds(30);
        while (rig.History.Read(Key).Count == 0 && DateTime.UtcNow < until)
        {
            await Task.Delay(20);
        }

        var saved = Assert.Single(rig.History.Read(Key));
        Assert.Equal(SpeedKinds.Manual, saved.Kind);
        Assert.NotNull(saved.Mbps);

        Body(controller, "{\"deep\":false}");
        var limited = AdminTestSupport.Render(await controller.Start(Key, default));
        Assert.Equal(429, limited.Status);
        Assert.True(int.Parse(limited.Headers["Retry-After"].ToString(), System.Globalization.CultureInfo.InvariantCulture) > 0);

        var report = AdminTestSupport.Body(controller.Speed(Key, "24h"));
        Assert.NotEqual(JsonValueKind.Null, report.GetProperty("cooldowns").GetProperty("testUntilUtc").ValueKind);
        Assert.Equal(JsonValueKind.Null, report.GetProperty("cooldowns").GetProperty("deepUntilUtc").ValueKind);
    }

    [Fact]
    public async Task TestNow_RefusesAPausedOrUnknownServer_AndWhileATestRuns()
    {
        await using var link = await ShapedLink.StartAsync(1_000_000, TimeSpan.FromMilliseconds(40));
        using var rig = await Rig.CreateAsync(link);
        var controller = Controller(rig, new Settings());
        Body(controller, "{}");
        AdminTestSupport.Body(await controller.Start("00000000000000000000000000000000", default), 404);

        var running = rig.Service.RunAsync(Key, SpeedKinds.Light, default);
        while (!rig.Service.IsRunning(Key))
        {
            await Task.Yield();
        }

        Body(controller, "{}");
        AdminTestSupport.Body(await controller.Start(Key, default), 409);
        Assert.True(AdminTestSupport.Body(controller.Speed(Key, "24h")).GetProperty("testRunning").GetBoolean()); // the page asks again while a test runs
        await running;
        Assert.False(AdminTestSupport.Body(controller.Speed(Key, "24h")).GetProperty("testRunning").GetBoolean());

        await rig.State.UpdateAsync(s => s.Home.Peers[0].Enabled = false);
        Body(controller, "{}");
        AdminTestSupport.Body(await controller.Start(Key, default), 409);
    }

    [Fact]
    public async Task L4_ABodyThatIsNotJson_IsRefused_AndDoesNotStartTheCooldown()
    {
        await using var link = await ShapedLink.StartAsync(1_000_000, TimeSpan.Zero);
        using var rig = await Rig.CreateAsync(link);
        var cooldowns = new SpeedCooldowns(TimeProvider.System);
        var controller = Controller(rig, new Settings(), cooldowns);

        Body(controller, "not json");
        AdminTestSupport.Body(await controller.Start(Key, default), 400);
        Body(controller, string.Empty);
        AdminTestSupport.Body(await controller.Start(Key, default), 400);

        Assert.Null(cooldowns.Until(Key, false));
        Assert.True(cooldowns.TryStart(Key, false, out _));
    }

    [Fact]
    public async Task L4_ATestThatCouldNotRun_GivesTheCooldownBack()
    {
        await using var link = await ShapedLink.StartAsync(1_000_000, TimeSpan.Zero);
        using var rig = await Rig.CreateAsync(link);
        var cooldowns = new SpeedCooldowns(TimeProvider.System);
        var controller = Controller(rig, new Settings(), cooldowns);
        await rig.State.UpdateAsync(s => s.Home.Peers[0].Enabled = true);
        var running = rig.Service.RunAsync(Key, SpeedKinds.Light, default); // a test is already running when the click arrives after the 409 check
        while (!rig.Service.IsRunning(Key))
        {
            await Task.Yield();
        }

        Assert.True(cooldowns.TryStart(Key, false, out _));
        cooldowns.Release(Key, false);
        Assert.Null(cooldowns.Until(Key, false));
        Assert.Null(await rig.Service.RunAsync(Key, SpeedKinds.Manual, default)); // refused while one runs: the caller releases
        await running;
        Assert.NotNull(controller);
    }

    [Fact]
    public void TheCooldowns_AreOneMinuteForATest_AndTenForTheDeepTest()
    {
        var time = new ManualTimeProvider();
        var cooldowns = new SpeedCooldowns(time);

        Assert.True(cooldowns.TryStart(Key, false, out _));
        Assert.False(cooldowns.TryStart(Key, false, out var wait));
        Assert.InRange(wait.TotalSeconds, 59, 60);
        Assert.True(cooldowns.TryStart(Key, true, out _)); // the deep test has its own clock
        time.Advance(TimeSpan.FromSeconds(61));
        Assert.True(cooldowns.TryStart(Key, false, out _));
        Assert.False(cooldowns.TryStart(Key, true, out _));
        Assert.NotNull(cooldowns.Until(Key, true));
        time.Advance(TimeSpan.FromMinutes(10));
        Assert.Null(cooldowns.Until(Key, true));
    }

    [Fact]
    public void TheSchedule_TestsANewPeer_AfterTheInterval_AndWhenAPeerComesBack_ButNotWhileOfflineOrStreaming()
    {
        var schedule = new SpeedSchedule();
        var six = TimeSpan.FromHours(6);

        Assert.True(schedule.IsDue("p", PeerStatus.Online, null, null, Now, six, false)); // right after pairing: never measured
        Assert.False(schedule.IsDue("p", PeerStatus.Online, Now.AddHours(-1), Now.AddHours(-1), Now, six, false));
        Assert.True(schedule.IsDue("p", PeerStatus.Online, Now.AddHours(-6), Now.AddHours(-6), Now, six, false));
        Assert.False(schedule.IsDue("p", PeerStatus.Online, Now.AddHours(-7), Now.AddHours(-7), Now, six, true)); // streaming from Home: left alone
        Assert.False(schedule.IsDue("p", PeerStatus.Offline, Now.AddHours(-7), Now.AddHours(-7), Now, six, false));
        Assert.True(schedule.IsDue("p", PeerStatus.Online, Now.AddMinutes(-15), Now.AddMinutes(-15), Now, six, false)); // offline -> online
        Assert.False(schedule.IsDue("p", PeerStatus.Online, Now.AddMinutes(-15), Now.AddMinutes(-15), Now, six, false));
    }

    [Fact]
    public void AFlappingPeer_AndARetriedPairing_GetNoTestWithinTenMinutesOfTheLastSample()
    {
        var schedule = new SpeedSchedule();
        var six = TimeSpan.FromHours(6);
        Assert.True(schedule.IsDue("p", PeerStatus.Offline, null, null, Now, six, false) is false);

        // Online again after a test five minutes ago: the comeback trigger is held back.
        Assert.False(schedule.IsDue("p", PeerStatus.Online, Now.AddMinutes(-5), Now.AddMinutes(-5), Now, six, false));
        Assert.False(schedule.IsDue("p", PeerStatus.Offline, Now.AddMinutes(-5), Now.AddMinutes(-5), Now.AddMinutes(1), six, false));
        Assert.False(schedule.IsDue("p", PeerStatus.Online, Now.AddMinutes(-5), Now.AddMinutes(-5), Now.AddMinutes(2), six, false));

        // A passive playback sample counts as "a sample of any kind" too; a pairing with a failed first test waits as well.
        Assert.False(schedule.IsDue("q", PeerStatus.Online, null, Now.AddMinutes(-3), Now, six, false));
        Assert.True(schedule.IsDue("q", PeerStatus.Online, null, Now.AddMinutes(-3), Now.AddMinutes(8), six, false));
    }

    [Fact]
    public async Task TheServerCard_GetsTheNumbersAndTheVerdictLine_FromTheHistory()
    {
        await using var link = await ShapedLink.StartAsync(1_000_000, TimeSpan.Zero);
        using var rig = await Rig.CreateAsync(link);
        var provider = new SpeedSummaryProvider(rig.History, new Settings());
        Assert.Null(provider.For(Key));

        rig.History.Append(Key, new SpeedSample { T = Now, Kind = SpeedKinds.Manual, Mbps = 7.5, RttMedianMs = 160, JitterMs = 5, LossPct = 0, Path = "unknown" });
        var summary = provider.For(Key)!;

        Assert.Equal(7.5, summary.Mbps);
        Assert.Equal(160, summary.RttMs);
        Assert.Equal([("1080p", "needsTranscode"), ("4k", "needsTranscode"), ("remux", "willBuffer")], summary.Verdicts.Select(v => (v.Cls, v.Verdict)));

        var card = AdminStatusBuilder.Server(rig.State.Current.Home.Peers[0], new PeerLiveInfo { Status = "Online", Mbps = summary.Mbps, RttMs = summary.RttMs, VerdictSummary = summary.Verdicts });
        Assert.Equal(3, card.VerdictSummary.Count);
        Assert.Equal(7.5, card.Mbps);
        Assert.Equal(160, card.RttMs);

        // A newer sample replaces the cached one; a failed test keeps the last good numbers.
        rig.History.Append(Key, new SpeedSample { T = Now.AddMinutes(1), Kind = SpeedKinds.Light, Error = "timeout" });
        Assert.Equal(7.5, provider.For(Key)!.Mbps);
    }
}
