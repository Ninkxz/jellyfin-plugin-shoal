using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.Api.Admin;
using Jellyfin.Plugin.Shoal.Configuration;
using Jellyfin.Plugin.Shoal.Security;
using Jellyfin.Plugin.Shoal.State;
using Jellyfin.Plugin.Shoal.Storage;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Shoal.Tests;

/// <summary>
/// U6 (DESIGN 11.2): the settings form, "Protect your shared videos", state copies and recovery, over the real state store.
/// </summary>
public sealed class AdminSettingsControllerTests
{
    private sealed class Settings : ISettingsAccessor
    {
        public PluginConfiguration Stored { get; private set; } = new();

        public PluginConfiguration Current => PluginSettingsAccessor.Copy(Stored);

        public void Save(PluginConfiguration configuration) => Stored = PluginSettingsAccessor.Copy(configuration);
    }

    private sealed class Checker : IStubRootChecker
    {
        public StubRootCheck Result { get; set; } = StubRootCheck.Ok;

        public StubRootCheck Check(string path) => Result;
    }

    private sealed class Guard : IGuardCounters
    {
        public IReadOnlyList<GuardCounter> WouldHaveBeenBlocked { get; } = [new GuardCounter("Kodi", "tailnet", "stream", 14)];

        public int GuardErrors => 1;

        public int TranscodeCapHits => 2;
    }

    private sealed class Findings : IFindingsSource
    {
        public IReadOnlyList<ReconcileFinding> Compute() =>
        [
            new ReconcileFinding(ReconcileKind.PeerWithoutSecret, StateSamples.PeerKeyA, null),
            new ReconcileFinding(ReconcileKind.LibraryWithoutPeer, null, "Old Friend Movies")
        ];
    }

    private sealed class Secrets : ISecretStore
    {
        public SecretStoreStatus Status => SecretStoreStatus.Ready;

        public SecretStoreInfo Info => new() { Status = SecretStoreStatus.Ready, KeySource = KeySource.Environment, VolumeWarning = "x" };

        public IReadOnlyCollection<string>? PeerKeys => [];

        public PeerCredential? TryGet(string peerKey) => null;

        public Task PutAsync(string peerKey, PeerCredential credential, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<bool> RemoveAsync(string peerKey, CancellationToken cancellationToken = default) => Task.FromResult(false);

        public Task ResetAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed record Rig(AdminSettingsController Controller, StateStore Store, Settings Settings, Checker Checker, ManualTimeProvider Time, TempDirectory Dir) : IDisposable
    {
        public void Dispose()
        {
            Store.Dispose();
            Dir.Dispose();
        }
    }

    private sealed class RecordingSandbox : Jellyfin.Plugin.Shoal.Sync.IImageSandboxStatus
    {
        public int SelfTests { get; private set; }

        public Jellyfin.Plugin.Shoal.Sync.ImageSandboxStatus GetStatus() => new("active", "full", null, 0);

        public void RequestSelfTest() => SelfTests++;
    }

    private static Rig Create(Action<TempDirectory>? beforeOpen = null, Jellyfin.Plugin.Shoal.Sync.IImageSandboxStatus? sandbox = null, Jellyfin.Plugin.Shoal.Playback.TrickplayCache? trickplay = null)
    {
        var dir = new TempDirectory();
        beforeOpen?.Invoke(dir);
        var time = new ManualTimeProvider();
        var store = new StateStore(new ShoalPaths(dir.Path), NullLogger.Instance, time, null);
        var settings = new Settings();
        var checker = new Checker();
        var controller = new AdminSettingsController(store, new Secrets(), settings, checker, new Guard(), new Findings(), time, NullLogger<AdminSettingsController>.Instance, null, sandbox, trickplay)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        return new Rig(controller, store, settings, checker, time, dir);
    }

    private static void Send(ControllerBase controller, string json)
    {
        controller.Request.ContentType = "application/json";
        controller.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(json));
    }

    private static JsonElement Body(IActionResult result, int expectedStatus = 200) => AdminTestSupport.Body(result, expectedStatus);

    [Fact]
    public async Task K33_TheFeatureSwitches_RoundTrip_WithTheirDefaults()
    {
        using var rig = Create();
        await rig.Store.UpdateAsync(s => s.StubRoot = "/data/shoal");

        var before = Body(rig.Controller.GetSettings());
        Assert.False(before.GetProperty("alternateVersionsEnabled").GetBoolean());
        Assert.True(before.GetProperty("trickplayEnabled").GetBoolean());
        Assert.Equal(1, before.GetProperty("trickplayCacheGb").GetInt32());
        Assert.True(before.GetProperty("attachmentsEnabled").GetBoolean());
        Assert.True(before.GetProperty("reencodeImages").GetBoolean());

        Send(rig.Controller, """{"alternateVersionsEnabled":true,"trickplayEnabled":false,"trickplayCacheGb":5,"attachmentsEnabled":false,"reencodeImages":false}""");
        var after = Body(await rig.Controller.PutSettings(default));

        Assert.True(after.GetProperty("alternateVersionsEnabled").GetBoolean());
        Assert.False(after.GetProperty("trickplayEnabled").GetBoolean());
        Assert.Equal(5, after.GetProperty("trickplayCacheGb").GetInt32());
        Assert.False(after.GetProperty("attachmentsEnabled").GetBoolean());
        Assert.False(after.GetProperty("reencodeImages").GetBoolean());
        Assert.Equal(5, rig.Settings.Stored.TrickplayCacheGb);
        Assert.Equal(6, after.GetProperty("syncIntervalHours").GetInt32()); // what was not sent stays
    }

    [Theory]
    [InlineData(0)]
    [InlineData(21)]
    [InlineData(-3)]
    public async Task K33_ACacheSizeOutsideOneToTwenty_IsRefused_AndNothingIsSaved(int gb)
    {
        using var rig = Create();
        await rig.Store.UpdateAsync(s => s.StubRoot = "/data/shoal");

        Send(rig.Controller, "{\"trickplayCacheGb\":" + gb + ",\"trickplayEnabled\":false}");
        Body(await rig.Controller.PutSettings(default), 400);

        Assert.Equal(1, rig.Settings.Stored.TrickplayCacheGb);
        Assert.True(rig.Settings.Stored.TrickplayEnabled);
    }

    [Fact]
    public async Task K33_TurningTheImageSwitchOn_AsksTheSandboxToTestItself_OnceAndOnlyThen()
    {
        var sandbox = new RecordingSandbox();
        using var rig = Create(sandbox: sandbox);
        await rig.Store.UpdateAsync(s => s.StubRoot = "/data/shoal");

        Send(rig.Controller, """{"reencodeImages":true}""");
        Body(await rig.Controller.PutSettings(default));
        Assert.Equal(0, sandbox.SelfTests); // it was on already

        Send(rig.Controller, """{"reencodeImages":false}""");
        Body(await rig.Controller.PutSettings(default));
        Assert.Equal(0, sandbox.SelfTests);

        Send(rig.Controller, """{"reencodeImages":true,"syncIntervalHours":12}""");
        var json = Body(await rig.Controller.PutSettings(default));
        Assert.Equal(1, sandbox.SelfTests);
        Assert.Equal("active", json.GetProperty("imageSandbox").GetProperty("state").GetString());

        Send(rig.Controller, """{"syncIntervalHours":6}""");
        Body(await rig.Controller.PutSettings(default));
        Assert.Equal(1, sandbox.SelfTests);
    }

    [Fact]
    public async Task K33_TheTrickplayCache_ShowsItsSize_AndClearEmptiesIt()
    {
        using var cacheDir = new TempDirectory();
        var cache = new Jellyfin.Plugin.Shoal.Playback.TrickplayCache(cacheDir.Path, () => 1L << 30, TimeProvider.System, NullLogger<Jellyfin.Plugin.Shoal.Playback.TrickplayCache>.Instance);
        const string Peer = "4b0c1d2e3f405162738495a6b7c8d9e0";
        const string Stub = "bbbbbbbbbbbbbbbb";
        cache.EnsureSignature(Peer, Stub, "sig");
        Assert.True(cache.Store(Peer, Stub, 320, 0, new byte[4096]));
        using var rig = Create(trickplay: cache);
        await rig.Store.UpdateAsync(s => s.StubRoot = "/data/shoal");

        Assert.True(Body(rig.Controller.GetSettings()).GetProperty("trickplayCacheBytes").GetInt64() >= 4096);
        var cleared = Body(rig.Controller.ClearTrickplayCache());

        Assert.True(cleared.GetProperty("freedBytes").GetInt64() >= 4096);
        Assert.Equal(0, Body(rig.Controller.GetSettings()).GetProperty("trickplayCacheBytes").GetInt64());
    }

    [Fact]
    public async Task K33_WithoutACache_ClearIs404_AndNoSizeIsShown()
    {
        using var rig = Create();
        await rig.Store.UpdateAsync(s => s.StubRoot = "/data/shoal");

        Body(rig.Controller.ClearTrickplayCache(), 404);
        Assert.Equal(JsonValueKind.Null, Body(rig.Controller.GetSettings()).GetProperty("trickplayCacheBytes").ValueKind);
    }

    [Fact]
    public async Task GetSettings_ReadsTheTogglesTheFolderAndTheKeySource()
    {
        using var rig = Create();
        await rig.Store.UpdateAsync(s => s.StubRoot = "/data/shoal");

        var json = Body(rig.Controller.GetSettings());

        Assert.Equal("/data/shoal", json.GetProperty("stubRoot").GetString());
        Assert.Equal("ok", json.GetProperty("stubRootCheck").GetString());
        Assert.Equal(6, json.GetProperty("syncIntervalHours").GetInt32());
        Assert.Equal(500, json.GetProperty("newItemsMinCount").GetInt32());
        Assert.Equal(2, json.GetProperty("diskQuotaGbPerPeer").GetInt32());
        Assert.Equal("Standard", json.GetProperty("artQuality").GetString());
        Assert.Equal("auto", json.GetProperty("tailscaleSocket").GetString());
        Assert.Equal("env", json.GetProperty("keySource").GetString());
        Assert.True(json.GetProperty("secretsVolumeUnsafe").GetBoolean());
    }

    [Fact]
    public async Task Contract_GetSettings_MatchesTheGoldenJson()
    {
        using var rig = Create();
        await rig.Store.UpdateAsync(s => s.StubRoot = "/data/shoal");

        var rendered = AdminTestSupport.Render(rig.Controller.GetSettings());

        Assert.Equal(200, rendered.Status);
        ContractGolden.Match("settings.get", rendered.Text);
    }

    [Fact]
    public async Task PutSettings_SavesTheTogglesAndTheFolder_AndAnswersWithTheSavedValues()
    {
        using var rig = Create();
        Send(rig.Controller, """{"stubRoot":"/data/shoal","syncIntervalHours":12,"diskQuotaGbPerPeer":5,"artQuality":"High","notifyClientsWhenOffline":false,"tailscaleSocket":"off"}""");

        var json = Body(await rig.Controller.PutSettings(CancellationToken.None));

        Assert.Equal(12, rig.Settings.Stored.SyncIntervalHours);
        Assert.Equal(5, rig.Settings.Stored.DiskQuotaGbPerPeer);
        Assert.Equal(ArtQuality.High, rig.Settings.Stored.ArtQuality);
        Assert.False(rig.Settings.Stored.NotifyClientsWhenOffline);
        Assert.Equal("off", rig.Settings.Stored.TailscaleSocket);
        Assert.Equal("/data/shoal", rig.Store.Current.StubRoot);
        Assert.Equal(12, json.GetProperty("syncIntervalHours").GetInt32());
        Assert.Equal(4, rig.Settings.Stored.MaxStreamsPerUser); // a field that was not sent stays as it was
    }

    [Theory]
    [InlineData("""{"syncIntervalHours":0}""")]
    [InlineData("""{"massDeletionPercent":101}""")]
    [InlineData("""{"diskQuotaGbPerPeer":51}""")]
    [InlineData("""{"maxStreamsPerUser":11}""")]
    [InlineData("""{"tailscaleSocket":"relative/socket"}""")]
    [InlineData("""{"artQuality":"Ultra"}""")]
    [InlineData("not json")]
    public async Task PutSettings_RefusesWhatIsOutOfRange_AndSavesNothing(string payload)
    {
        using var rig = Create();
        Send(rig.Controller, payload);

        var json = Body(await rig.Controller.PutSettings(CancellationToken.None), 400);

        Assert.Equal("invalid", json.GetProperty("error").GetString());
        Assert.Equal(6, rig.Settings.Stored.SyncIntervalHours);
        Assert.Equal(string.Empty, rig.Store.Current.StubRoot);
    }

    [Fact]
    public async Task PutSettings_AFolderInsideALibrary_IsRefusedWithItsReason()
    {
        using var rig = Create();
        rig.Checker.Result = StubRootCheck.InsideLibrary;
        Send(rig.Controller, """{"stubRoot":"/media/movies/shoal","syncIntervalHours":12}""");

        var json = Body(await rig.Controller.PutSettings(CancellationToken.None), 400);

        Assert.Equal("stubRoot", json.GetProperty("error").GetString());
        Assert.Equal("insideLibrary", json.GetProperty("detail").GetString());
        Assert.Equal(6, rig.Settings.Stored.SyncIntervalHours);
        Assert.Equal(string.Empty, rig.Store.Current.StubRoot);
    }

    [Fact]
    public async Task PutSettings_ANetworkShare_NeedsTheAcknowledgement()
    {
        using var rig = Create();
        rig.Checker.Result = StubRootCheck.Network;
        Send(rig.Controller, """{"stubRoot":"/mnt/nas/shoal"}""");

        var refused = Body(await rig.Controller.PutSettings(CancellationToken.None), 400);

        Assert.Equal("network", refused.GetProperty("detail").GetString());
        Assert.Equal(string.Empty, rig.Store.Current.StubRoot);

        Send(rig.Controller, """{"stubRoot":"/mnt/nas/shoal","stubRootNetworkAck":true}""");

        Body(await rig.Controller.PutSettings(CancellationToken.None));

        Assert.Equal("/mnt/nas/shoal", rig.Store.Current.StubRoot);
    }

    [Fact]
    public void EveryPropertyOfThePluginConfiguration_IsKnownToTheSettingsApi()
    {
        // A setting the page edits but the controller does not know cannot be saved (Pilot 09 F1: TvShowsEnabled). SchemaVersion is the file's own.
        var dto = typeof(SettingsDto).GetProperties().Select(static p => p.Name).ToHashSet(StringComparer.Ordinal);
        var missing = typeof(PluginConfiguration).GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly)
            .Select(static p => p.Name)
            .Where(name => name != nameof(PluginConfiguration.SchemaVersion) && !dto.Contains(name))
            .ToList();

        Assert.Empty(missing);
    }

    [Fact]
    public void EveryFieldThePageSaves_IsKnownToTheSettingsApi()
    {
        using var stream = typeof(Plugin).Assembly.GetManifestResourceStream("Jellyfin.Plugin.Shoal.Configuration.shoal.js");
        Assert.NotNull(stream);
        using var reader = new StreamReader(stream);
        var script = reader.ReadToEnd();
        var start = script.LastIndexOf("function collect() {", script.IndexOf("tvEpisodeThumbs: f.tvThumbs", StringComparison.Ordinal), StringComparison.Ordinal); // the settings page's own
        var body = script[start..script.IndexOf("function validate(", start, StringComparison.Ordinal)];
        var posted = System.Text.RegularExpressions.Regex.Matches(body, @"^\s{12}(\w+):", System.Text.RegularExpressions.RegexOptions.Multiline).Select(static m => m.Groups[1].Value).ToList();
        var known = typeof(SettingsDto).GetProperties().Select(static p => p.Name.ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);

        Assert.NotEmpty(posted);
        Assert.All(posted, field => Assert.Contains(field.ToLowerInvariant(), known));
    }

    [Fact]
    public async Task V291_TvIsAlwaysOn_AnOldPageSendingTvShowsEnabledFalseChangesNothing_ThumbsStillRoundTrip()
    {
        using var rig = Create();
        await rig.Store.UpdateAsync(s => s.StubRoot = "/data/shoal");
        var before = Body(rig.Controller.GetSettings());
        Assert.True(before.GetProperty("tvShowsEnabled").GetBoolean());
        Assert.True(before.GetProperty("tvEpisodeThumbs").GetBoolean());

        Send(rig.Controller, """{"tvShowsEnabled":false,"tvEpisodeThumbs":false}""");
        var after = Body(await rig.Controller.PutSettings(default));

        Assert.True(after.GetProperty("tvShowsEnabled").GetBoolean());
        Assert.False(after.GetProperty("tvEpisodeThumbs").GetBoolean());
        Assert.True(rig.Settings.Current.TvShowsEnabled);
        Assert.False(rig.Settings.Current.TvEpisodeThumbs);
        Assert.True(Body(rig.Controller.GetSettings()).GetProperty("tvShowsEnabled").GetBoolean());
    }

    [Fact]
    public async Task Hardening_StartsInEnforce_AndTheModeChangesAtOnce()
    {
        using var rig = Create();

        var before = Body(rig.Controller.GetHardening());
        Assert.Equal("Enforce", before.GetProperty("mode").GetString()); // D18: a fresh install protects
        Assert.Equal(new[] { "LogOnly", "Enforce", "Off" }, before.GetProperty("modes").EnumerateArray().Select(m => m.GetString()).ToArray());
        var counter = Assert.Single(before.GetProperty("counters").EnumerateArray());
        Assert.Equal("Kodi", counter.GetProperty("app").GetString());
        Assert.False(before.TryGetProperty("shieldHits", out _)); // the Initiate shield went with Quick Connect (D17 B8)
        Assert.Equal(1, before.GetProperty("guardErrors").GetInt32());

        rig.Time.Advance(TimeSpan.FromDays(1));
        Send(rig.Controller, """{"mode":"LogOnly"}""");
        var after = Body(await rig.Controller.PutHardening(CancellationToken.None));

        Assert.Equal("LogOnly", after.GetProperty("mode").GetString());
        Assert.Equal(HardeningMode.LogOnly, rig.Store.Current.PeerRole.HardeningMode);
        Assert.Equal(rig.Time.GetUtcNow().UtcDateTime, rig.Store.Current.PeerRole.HardeningSinceUtc);
    }

    [Theory]
    [InlineData("""{"mode":"Everything"}""")]
    [InlineData("""{"mode":7}""")]
    [InlineData("""{}""")]
    [InlineData("nope")]
    public async Task Hardening_AnUnknownMode_IsRefused(string payload)
    {
        using var rig = Create();
        Send(rig.Controller, payload);

        Body(await rig.Controller.PutHardening(CancellationToken.None), 400);

        Assert.Equal(HardeningMode.Enforce, rig.Store.Current.PeerRole.HardeningMode);
    }

    [Fact]
    public async Task D18_AnExistingStateKeepsItsStoredMode_AcrossARestart()
    {
        using var dir = new TempDirectory();
        var paths = new ShoalPaths(dir.Path);
        using (var first = new StateStore(paths, NullLogger.Instance, TimeProvider.System, null))
        {
            await first.UpdateAsync(s => s.PeerRole.HardeningMode = HardeningMode.LogOnly);
        }

        using var again = new StateStore(paths, NullLogger.Instance, TimeProvider.System, null);

        Assert.Equal(HardeningMode.LogOnly, again.Current.PeerRole.HardeningMode); // no silent flip on upgrade
        using var fresh = new TempDirectory();
        using var brandNew = new StateStore(new ShoalPaths(fresh.Path), NullLogger.Instance, TimeProvider.System, null);
        Assert.Equal(HardeningMode.Enforce, brandNew.Current.PeerRole.HardeningMode);
    }

    [Fact]
    public async Task GetState_ListsRestorableCopiesNewestFirst_AndTheStartupFindingsInPlainKinds()
    {
        using var rig = Create();
        await rig.Store.UpdateAsync(StateSamples.Fill);
        await rig.Store.UpdateAsync(s => s.Home.Peers[0].Priority = 50); // the second write leaves a .bak

        var json = Body(rig.Controller.GetState());

        Assert.Equal("Normal", json.GetProperty("mode").GetString());
        var files = json.GetProperty("files").EnumerateArray().ToList();
        Assert.NotEmpty(files);
        Assert.DoesNotContain(files, f => f.GetProperty("file").GetString() == "state.json");
        Assert.Contains(files, f => f.GetProperty("kind").GetString() == "bak");
        var findings = json.GetProperty("findings").EnumerateArray().ToList();
        Assert.Equal("missingKey", findings[0].GetProperty("kind").GetString());
        Assert.Equal("Hops", findings[0].GetProperty("serverName").GetString());
        Assert.Equal("orphanLibrary", findings[1].GetProperty("kind").GetString());
        Assert.Equal("Old Friend Movies", findings[1].GetProperty("library").GetString());
    }

    [Fact]
    public async Task Restore_OnlyAcceptsAFileFromTheList()
    {
        using var rig = Create();
        await rig.Store.UpdateAsync(StateSamples.Fill);
        await rig.Store.UpdateAsync(s => s.Home.Peers[0].Priority = 50);

        foreach (var bad in new[] { """{"file":"../../etc/passwd"}""", """{"file":"state.json.corrupt-1"}""", """{"file":null}""", "{}", "nope"})
        {
            Send(rig.Controller, bad);
            Body(await rig.Controller.Restore(CancellationToken.None), 400);
        }

        var listed = Body(rig.Controller.GetState()).GetProperty("files")[0].GetProperty("file").GetString();
        Send(rig.Controller, JsonSerializer.Serialize(new { file = listed }));

        Body(await rig.Controller.Restore(CancellationToken.None));
    }

    [Fact]
    public async Task RecoveryMode_CanStartOver_AndTheSettingsRefuseChangesUntilThen()
    {
        using var rig = Create(d =>
        {
            var paths = new ShoalPaths(d.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(paths.StateFile)!);
            File.WriteAllText(paths.StateFile, "{ damaged");
        });
        Assert.Equal(StateStatus.Recovery, rig.Store.Status);

        Send(rig.Controller, """{"syncIntervalHours":12}""");
        Body(await rig.Controller.PutSettings(CancellationToken.None), 409);
        Assert.Equal("Recovery", Body(rig.Controller.GetState()).GetProperty("mode").GetString());

        Send(rig.Controller, """{"confirmName":"nope"}""");
        Body(await rig.Controller.StartOver(CancellationToken.None), 400);
        Assert.Equal(StateStatus.Recovery, rig.Store.Status);

        Send(rig.Controller, """{"confirmName":"start over"}""");
        Body(await rig.Controller.StartOver(CancellationToken.None));

        Assert.Equal(StateStatus.Ready, rig.Store.Status);
        Assert.Equal("Normal", Body(rig.Controller.GetState()).GetProperty("mode").GetString());
        Assert.Contains(Directory.EnumerateFiles(Path.GetDirectoryName(new ShoalPaths(rig.Dir.Path).StateFile)!), f => f.Contains("corrupt", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DeleteAll_NeedsTheTypedPhrase_ThenRefusesUntilTheServicesItNeedsExist()
    {
        using var rig = Create();

        Send(rig.Controller, "{}");
        Assert.Equal("confirmName", Body(await rig.Controller.DeleteAll(CancellationToken.None), 400).GetProperty("error").GetString());

        Send(rig.Controller, """{"confirmName":"delete everything"}""");
        var json = Body(await rig.Controller.DeleteAll(CancellationToken.None), 501);

        Assert.Equal("notImplemented", json.GetProperty("error").GetString());
    }

    [Theory]
    [InlineData(5)]
    [InlineData(10)]
    [InlineData(0)]
    public async Task StreamsPerPerson_AboveFourOrBelowOne_IsRefused(int value)
    {
        using var rig = Create();
        Send(rig.Controller, "{\"maxStreamsPerUser\":" + value.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}");

        Body(await rig.Controller.PutSettings(CancellationToken.None), 400);

        Assert.Equal(4, rig.Settings.Stored.MaxStreamsPerUser);
    }

    [Fact]
    public async Task StartOver_OnAHealthyServer_IsRefused_AndNothingIsMoved()
    {
        using var rig = Create();
        await rig.Store.UpdateAsync(StateSamples.Fill);
        Send(rig.Controller, """{"confirmName":"start over"}""");

        Assert.Equal("notInRecovery", Body(await rig.Controller.StartOver(CancellationToken.None), 409).GetProperty("error").GetString());

        Assert.NotEmpty(rig.Store.Current.Home.Peers);
        Assert.DoesNotContain(Directory.EnumerateFiles(Path.GetDirectoryName(new ShoalPaths(rig.Dir.Path).StateFile)!), f => f.Contains("corrupt", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ABodyThatIsNotJson_IsA400_NotA500()
    {
        using var rig = Create();
        rig.Controller.Request.ContentType = "text/plain";
        rig.Controller.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("hello"));

        Body(await rig.Controller.PutSettings(CancellationToken.None), 400);
        rig.Controller.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("hello"));
        Body(await rig.Controller.PutHardening(CancellationToken.None), 400);
        rig.Controller.Request.Body = new MemoryStream();
        rig.Controller.Request.ContentType = null;
        Body(await rig.Controller.Restore(CancellationToken.None), 400);
        Body(await rig.Controller.DeleteAll(CancellationToken.None), 400);
    }

    [Fact]
    public void Hardening_CarriesTheWrongInviteCounterAndTheProxyFlag()
    {
        using var rig = Create();

        var json = Body(rig.Controller.GetHardening());

        Assert.Equal(0, json.GetProperty("inviteFailures").GetProperty("lastHour").GetInt32());
        Assert.False(json.GetProperty("inviteFailures").GetProperty("notice").GetBoolean());
        Assert.False(json.GetProperty("proxySuspected").GetBoolean());
        Assert.False(Body(rig.Controller.GetState()).GetProperty("backupByCopy").GetBoolean());
    }

    [Fact]
    public async Task F5_NoSettingsResponseCarriesATokenAnInviteOrAnId()
    {
        using var rig = Create();
        await rig.Store.UpdateAsync(StateSamples.Fill);
        var guid = new Regex(@"[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}", RegexOptions.IgnoreCase);

        foreach (var result in new[] { rig.Controller.GetSettings(), rig.Controller.GetHardening(), rig.Controller.GetState() })
        {
            var text = AdminTestSupport.Text(result);
            Assert.DoesNotMatch(guid, text);
            Assert.DoesNotContain("token", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("shoal1-", text, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void EveryAction_RequiresTheAdminPolicy()
    {
        var type = typeof(AdminSettingsController);

        Assert.Equal("RequiresElevation", type.GetCustomAttribute<AuthorizeAttribute>()!.Policy);
        Assert.Equal("Shoal/Admin", type.GetCustomAttribute<RouteAttribute>()!.Template);
        Assert.Null(type.GetCustomAttribute<AllowAnonymousAttribute>());
        foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
        {
            Assert.Null(method.GetCustomAttribute<AllowAnonymousAttribute>());
        }
    }
}
