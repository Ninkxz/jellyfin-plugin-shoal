using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.Authorization;
using Jellyfin.Plugin.Shoal.Configuration;
using Jellyfin.Plugin.Shoal.Playback;
using Jellyfin.Plugin.Shoal.Registrations;
using Jellyfin.Plugin.Shoal.State;
using Jellyfin.Plugin.Shoal.Storage;
using Jellyfin.Plugin.Shoal.Sync;
using MediaBrowser.Model.Dto;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Shoal.Tests;

/// <summary>
/// K24 (DESIGN v2.6.9): the foundation of phases 5 and 6 changes no behavior. These tests pin the additive state fields, the new settings, the
/// seams and their defaults, and the registrations the later tasks fill in.
/// </summary>
public sealed class V26FoundationTests
{
    private static readonly Guid PeerSource = Guid.Parse("11111111-2222-3333-4444-555555555555");

    // --- additive state fields

    [Fact]
    public void TheNewShareAndPeerFields_AreKept_WhenTheyAreInRange()
    {
        var state = new ShoalState();
        StateSamples.Fill(state);
        var peer = state.Home.Peers[0];
        peer.RotationDays = 90;
        peer.LastRotatedUtc = new DateTime(2026, 10, 15, 8, 0, 0, DateTimeKind.Utc);
        var share = state.PeerRole.Shares[0];
        share.HomeServerIdPrefix = "1a2b3c4d5e6f";
        share.LastRotatedUtc = new DateTime(2026, 10, 15, 9, 0, 0, DateTimeKind.Utc);
        share.RotationCount = 3;
        share.RotationFailedUtc = new DateTime(2026, 10, 15, 10, 0, 0, DateTimeKind.Utc);
        var notes = new List<string>();

        Assert.Null(StateValidator.Validate(state, notes));

        Assert.Empty(notes);
        Assert.Equal(90, peer.RotationDays);
        Assert.Equal("1a2b3c4d5e6f", share.HomeServerIdPrefix);
        Assert.Equal(3, share.RotationCount);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    [InlineData(45)]
    [InlineData(365)]
    public void ARotationScheduleThatIsNotOff30_60Or90_IsSwitchedOff_WithoutRefusingTheFile(int days)
    {
        var state = new ShoalState();
        StateSamples.Fill(state);
        state.Home.Peers[0].RotationDays = days;
        var notes = new List<string>();

        Assert.Null(StateValidator.Validate(state, notes));

        Assert.Equal(0, state.Home.Peers[0].RotationDays);
        Assert.Contains(notes, static n => n.Contains("rotation schedule", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("1A2B3C4D5E6F")]
    [InlineData("1a2b3c4d5e6")]
    [InlineData("1a2b3c4d5e6f0")]
    [InlineData("zzzzzzzzzzzz")]
    public void AHomeServerIdPrefixThatIsNotTwelveLowerCaseHex_IsCleared_NotFatal(string prefix)
    {
        var state = new ShoalState();
        StateSamples.Fill(state);
        state.PeerRole.Shares[0].HomeServerIdPrefix = prefix;
        state.PeerRole.Shares[0].RotationCount = int.MaxValue;
        var notes = new List<string>();

        Assert.Null(StateValidator.Validate(state, notes));

        Assert.Equal(string.Empty, state.PeerRole.Shares[0].HomeServerIdPrefix);
        Assert.Equal(1_000_000, state.PeerRole.Shares[0].RotationCount);
    }

    [Fact]
    public async Task TheNewStateFields_SurviveASaveAndAReload_AtTheCurrentSchema()
    {
        using var dir = new TempDirectory();
        var paths = new ShoalPaths(dir.Path);
        using (var store = new StateStore(paths, NullLogger.Instance, TimeProvider.System, null))
        {
            await store.UpdateAsync(static s =>
            {
                StateSamples.Fill(s);
                s.Home.Peers[0].RotationDays = 30;
                s.PeerRole.Shares[0].HomeServerIdPrefix = "1a2b3c4d5e6f";
                s.PeerRole.Shares[0].RotationCount = 2;
            });
        }

        using var reopened = new StateStore(paths, NullLogger.Instance, TimeProvider.System, null);

        Assert.Equal(StateStatus.Ready, reopened.Status);
        Assert.Equal(ShoalState.CurrentSchema, reopened.Current.Schema);
        Assert.Equal(30, reopened.Current.Home.Peers[0].RotationDays);
        Assert.Equal("1a2b3c4d5e6f", reopened.Current.PeerRole.Shares[0].HomeServerIdPrefix);
        Assert.Equal(2, reopened.Current.PeerRole.Shares[0].RotationCount);
    }

    // --- settings (T-REG-2 extended)

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(21, 20)]
    [InlineData(int.MaxValue, 20)]
    [InlineData(5, 5)]
    public void TheTrickplayCache_IsClamped_AndNeverThrows(int value, int expected)
    {
        var config = new PluginConfiguration { TrickplayCacheGb = value };

        var notes = SettingsValidator.Apply(config);

        Assert.Equal(expected, config.TrickplayCacheGb);
        Assert.Equal(value == expected ? 0 : 1, notes.Count);
    }

    // --- counters and the Status block

    [Fact]
    public void TheCounters_AreSplit_AndEveryNameIsListed()
    {
        Assert.Equal(["nonstatic-stream", "audio-route", "core-hls-route", "forbidden-route", "long-query", "trickplay-m3u8", "no-credentials"], InterceptCounters.Names);
        Assert.DoesNotContain("bypass-route", InterceptCounters.Names);
        var time = new ManualTimeProvider();
        var counters = new InterceptCounters(time);

        counters.Increment(InterceptCounters.AudioRoute);

        Assert.Equal(time.GetUtcNow().UtcDateTime, counters.SinceUtc);
        Assert.Equal(1, counters.Get(InterceptCounters.AudioRoute));
        Assert.Equal(0, counters.Get(InterceptCounters.NonStaticStream));
    }

    // --- sanitizer hooks

    [Fact]
    public void TheDefaultHooks_ChangeNothing_AndTheCustomOnesAreCalledFromTheSanitizer()
    {
        var dto = new BaseItemDto
        {
            Id = Guid.NewGuid(),
            Name = "A",
            MediaSources = [new MediaSourceInfo { Id = PeerSource.ToString("N"), Size = 100, Container = "mkv" }]
        };
        var plain = new MetadataSanitizer(new AllRatings()).Sanitize(dto)!;
        Assert.Single(plain.Sources);
        Assert.Null(plain.Sources[0].Trickplay);

        var level = new SanitizedTrickplay(320, 180, 10, 10, 100, 10_000, 1_000_000);
        var hooked = new MetadataSanitizer(new AllRatings(), new DropAllSources(), new OneLevel(level)).Sanitize(dto)!;
        Assert.Empty(hooked.Sources);

        var kept = new MetadataSanitizer(new AllRatings(), KeepSources.Instance, new OneLevel(level)).Sanitize(dto)!;
        Assert.Equal([level], kept.Sources[0].Trickplay);
    }

    // --- version resolver and the other defaults

    [Fact]
    public async Task TheDefaultVersionResolver_AnswersTheRouteItemsOwnSource_AndRefusesAnyOtherId()
    {
        using var dir = new TempDirectory();
        using var state = new StateStore(new ShoalPaths(dir.Path), NullLogger.Instance, TimeProvider.System, null);
        await state.UpdateAsync(StateSamples.Fill);
        var item = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
        var path = Path.Combine(state.Current.StubRoot, StateSamples.PeerKeyA, "movies", "Title 0001", "Title 0001.strm");
        var index = new ShoalIndex(new OneItemPaths(item, path), state, NullLogger<ShoalIndex>.Instance);
        await index.RebuildAsync();
        var store = new CatalogStore(new ShoalPaths(dir.Path), NullLogger.Instance);
        var peerItem = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001");
        store.Save(new CatalogDocument
        {
            PeerKey = StateSamples.PeerKeyA,
            Entries =
            [
                new CatalogEntry { StubKey = "k3m9q2x7v1tz8c4n", PeerItemId = peerItem.ToString("N"), PeerMediaSourceId = PeerSource.ToString("N"), MappingKey = "movies-1", State = EntryState.Active, StubRelPath = "Title 0001/Title 0001.strm" }
            ]
        });
        var resolver = new OwnSourceVersionResolver(index, new StubCatalog(store));

        var own = resolver.Resolve(item, null);
        var named = resolver.Resolve(item, item.ToString("N"));

        Assert.Equal(new ResolvedVersion(item, "k3m9q2x7v1tz8c4n", StateSamples.PeerKeyA, peerItem, PeerSource, "k3m9q2x7v1tz8c4n"), own);
        Assert.Equal(own, named);
        Assert.Null(resolver.Resolve(item, Guid.NewGuid().ToString("N"))); // outside the set: a 404 with no Peer call
        Assert.Null(resolver.Resolve(item, "not-a-guid"));
        Assert.Null(resolver.Resolve(Guid.NewGuid(), null));
    }

    [Fact]
    public async Task ThePassThroughImageSanitizer_ReturnsTheBytesItWasGiven_AndNoAttachmentsAreListed()
    {
        var bytes = new byte[] { 0xFF, 0xD8, 1, 2, 3 };

        var result = await new PassThroughImageSanitizer().SanitizeAsync(bytes, new ImageSpec(ImageKind.Poster), CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal(bytes, result.Bytes.ToArray());
        var rebuilt = NoAttachments.Instance.Rebuild(new MediaSourceInfo(), Guid.NewGuid(), Guid.NewGuid(), "token");
        Assert.Empty(rebuilt.Attachments);
        Assert.Empty(rebuilt.KeptIndexes);
    }

    // --- registrations

    [Fact]
    public void EveryV26Seam_ResolvesToItsDefault_WithoutAnyFeatureRegistered()
    {
        var services = new ServiceCollection();
        services.AddSingleton(new InterceptCounters());
        services.AddLogging();
        services.AddSingleton(new ShoalPaths(Path.Combine(Path.GetTempPath(), "shoal-reg-img-" + Guid.NewGuid().ToString("N"))));
        services.AddSingleton<IEncoderLocator>(new FixedEncoder(null));
        services.AddSingleton<Jellyfin.Plugin.Shoal.Peers.IPeerEndpoints>(new NoPeers());
        services.AddSingleton(new GrantStore(TimeProvider.System, new StreamSlots(TimeProvider.System, () => 2)));
        services.AddSingleton(new PeerExtractionGate(TimeProvider.System));
        var registry = new Jellyfin.Plugin.Shoal.Peers.PeerRegistry();
        var peerClient = new Jellyfin.Plugin.Shoal.Peers.PeerClient(() => "Home", () => "0.1.0.0", TimeProvider.System);
        services.AddSingleton(registry);
        services.AddSingleton(peerClient);
        services.AddSingleton(new Jellyfin.Plugin.Shoal.Peers.PeerTrust(peerClient, registry));
        services.AddSingleton<Jellyfin.Plugin.Shoal.State.IStateStore>(new StateStore(new ShoalPaths(Path.Combine(Path.GetTempPath(), "shoal-reg-st-" + Guid.NewGuid().ToString("N"))), NullLogger.Instance, TimeProvider.System, null));
        services.AddSingleton(new ShoalIndex(new OneItemPaths(Guid.NewGuid(), "x"), new StateStore(new ShoalPaths(Path.Combine(Path.GetTempPath(), "shoal-reg-" + Guid.NewGuid().ToString("N"))), NullLogger.Instance, TimeProvider.System, null), NullLogger<ShoalIndex>.Instance));
        services.AddSingleton(new StubCatalog(new CatalogStore(new ShoalPaths(Path.GetTempPath()), NullLogger.Instance)));

        services.AddShoalFoundation();
        services.AddShoalRotation();
        services.AddShoalAlternateVersions();
        services.AddShoalTrickplay();
        services.AddShoalAttachments();
        services.AddShoalImageSandbox();
        using var provider = services.BuildServiceProvider();

        Assert.IsType<TrickplayHandler>(provider.GetRequiredService<ITrickplayHandler>()); // K30 replaced the default
        Assert.IsType<AttachmentHandler>(provider.GetRequiredService<IAttachmentHandler>()); // K31 replaced the default
        Assert.IsType<ShoalVersionResolver>(provider.GetRequiredService<IVersionResolver>()); // K28 replaces the default
        Assert.IsType<AttachmentRebuilder>(provider.GetRequiredService<IAttachmentRebuilder>()); // K31 replaced the default
        Assert.IsType<ChildImageSanitizer>(provider.GetRequiredService<IImageSanitizer>()); // K32 replaced the default
        Assert.IsType<AlternateVersionSourceSanitizer>(provider.GetRequiredService<ISourceSanitizer>()); // K28 replaces the default
        Assert.IsType<TrickplaySanitizer>(provider.GetRequiredService<ITrickplaySanitizer>()); // K30 replaced the default
    }

    [Fact]
    public void ThePluginRegistrator_CallsEveryFeatureRegistration_SoLaterTasksNeverEditIt()
    {
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "Jellyfin.Plugin.Shoal", "PluginServiceRegistrator.cs"));

        foreach (var call in new[] { "AddShoalFoundation", "AddShoalRotation", "AddShoalAlternateVersions", "AddShoalTrickplay", "AddShoalAttachments", "AddShoalImageSandbox" })
        {
            Assert.Contains("serviceCollection." + call + "();", source, StringComparison.Ordinal);
        }
    }

    private sealed class AllRatings : IRatingTable
    {
        public bool IsRecognized(string rating) => true;
    }

    private sealed class DropAllSources : ISourceSanitizer
    {
        public IReadOnlyList<SanitizedSource> Refine(BaseItemDto dto, IReadOnlyList<SanitizedSource> sources) => [];
    }

    private sealed class OneLevel : ITrickplaySanitizer
    {
        private readonly SanitizedTrickplay _level;

        public OneLevel(SanitizedTrickplay level) => _level = level;

        public IReadOnlyList<SanitizedTrickplay> Read(BaseItemDto dto, Guid peerSourceId) => [_level];
    }

    private sealed class OneItemPaths : IItemPaths
    {
        private readonly Guid _item;
        private readonly string _path;

        public OneItemPaths(Guid item, string path)
        {
            _item = item;
            _path = path;
        }

#pragma warning disable CS0067
        public event EventHandler<ItemPathEventArgs>? Added;

        public event EventHandler<ItemPathEventArgs>? Removed;
#pragma warning restore CS0067

        public string? PathOf(Guid itemId) => itemId == _item ? _path : null;

        public IReadOnlyList<ItemPathEventArgs> ItemsBelow(string stubRoot) => [new ItemPathEventArgs(_item, _path)];
    }
}

internal sealed class NoPeers : Jellyfin.Plugin.Shoal.Peers.IPeerEndpoints
{
    public Jellyfin.Plugin.Shoal.Peers.PeerEndpoint? Find(string peerKey) => null;
}
