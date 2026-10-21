using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.Authorization;
using Jellyfin.Plugin.Shoal.Diagnostics;
using Jellyfin.Plugin.Shoal.Pairing;
using Jellyfin.Plugin.Shoal.Peers;
using Jellyfin.Plugin.Shoal.Playback;
using Jellyfin.Plugin.Shoal.State;
using Jellyfin.Plugin.Shoal.Storage;
using Jellyfin.Plugin.Shoal.Sync;
using MediaBrowser.Model.Dlna;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.Shoal.Tests;

/// <summary>
/// The real PlaybackInfo stack with fakes only at the edges (K14): the grants, the limiter, the registry, the catalog and the peer.
/// </summary>
internal sealed class PlaybackParts
{
    public static readonly Guid PeerItem = Guid.Parse("11111111-1111-1111-1111-111111111111");
    public static readonly Guid PeerSource = Guid.Parse("22222222-2222-2222-2222-222222222222");
    public static readonly Guid PeerItemB = Guid.Parse("33333333-3333-3333-3333-333333333333");
    public static readonly Guid PeerItemC = Guid.Parse("44444444-4444-4444-4444-444444444444");
    public const string StubKey = "k3m9q2x7v1tz8c4n";

    public PlaybackParts(GrantStore grants, RateLimiter limiter, PeerRegistry registry, FakeDevices devices, FakeNotice notice, FakeAdmins admins, PeerEndpoint endpoint, FakeSlots slots, ManualTimeProvider time, StreamSlots streamSlots)
    {
        Grants = grants;
        Limiter = limiter;
        Registry = registry;
        Devices = devices;
        Notice = notice;
        Admins = admins;
        Endpoint = endpoint;
        Slots = slots;
        Time = time;
        StreamSlots = streamSlots;
    }

    public GrantStore Grants { get; }

    public RateLimiter Limiter { get; }

    public PeerRegistry Registry { get; }

    public FakeDevices Devices { get; }

    public FakeNotice Notice { get; }

    public FakeAdmins Admins { get; }

    public PeerEndpoint Endpoint { get; }

    public FakeSlots Slots { get; }

    public ManualTimeProvider Time { get; }

    public StreamSlots StreamSlots { get; }

    public PeerExtractionGate? Broker { get; set; }

    public int MaxStreams { get; set; } = 2;

    public int HlsBufferAhead { get; set; } = 3;

    public HlsReadAhead ReadAhead { get; set; } = null!;

    public PassiveTelemetry Telemetry { get; set; } = null!;

    public ReadAheadBudget StaticReadAhead { get; } = new(64L * 1024 * 1024);

    public bool HlsSubtitles { get; set; } = true;

    public TrickplayCache TrickplayCache { get; set; } = null!;

    public bool TrickplayEnabled { get; set; } = true;

    public bool AttachmentsEnabled { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether the offline card is on (the plugin default; a test that pins the plain refusal turns it off).</summary>
    public bool CardsEnabled { get; set; } = true;

    public string CardServiceName { get; set; } = "Seerr";

    public FakeCardEncoder CardEncoder { get; } = new();

    public OfflineCards Cards { get; set; } = null!;

    public IVersionResolver? VersionOverride { get; set; }

    /// <summary>Gets or sets the group pick (PLAN v2.9 K42) the PlaybackInfo and static handlers ask; null: no title is grouped.</summary>
    public IGroupPlayback? GroupOverride { get; set; }

    /// <summary>Gets or sets the PlaybackInfo handler (tests change its attempt budget).</summary>
    public PlaybackInfoHandler Handler { get; set; } = null!;

    public RateLimiter AttachmentLimiter { get; set; } = null!;

    public long TrickplayCapBytes { get; set; } = 1L << 30;

    public IImageSanitizer Sanitizer { get; set; } = new PassThroughImageSanitizer();

    public BitrateLimits Limits { get; set; } = new(0, null);

    internal static PlaybackParts Build(WebApplicationBuilder builder, StateStore state, PeerClient client, PeerEndpoint endpoint, TempDirectory dir, CapturingLogger log)
    {
        var paths = new ShoalPaths(dir.Path);
        var store = new CatalogStore(paths, log);
        var document = new CatalogDocument { PeerKey = endpoint.PeerKey, CompletedOnce = true };
        foreach (var (folder, peerItem, key) in new[] { ("Title 0001", PeerItem, StubKey), ("Title 0002", PeerItemB, "b3m9q2x7v1tz8c4n"), ("Title 0003", PeerItemC, "c3m9q2x7v1tz8c4n") })
        {
            document.Entries.Add(new CatalogEntry
            {
                StubKey = key,
                PeerItemId = peerItem.ToString("N"),
                PeerMediaSourceId = PeerSource.ToString("N"),
                MappingKey = "movies-1",
                PeerEtag = "etag-1",
                Title = "The Matrix",
                Year = 1999,
                SizeBytes = 4_000_000_000,
                Bitrate = 8_000_000,
                Container = folder == "Title 0002" ? "mov,mp4,m4a,3gp,3g2,mj2" : "mkv",
                RuntimeTicks = 72_000_000_000,
                State = EntryState.Active,
                StubRelPath = $"{folder}/{folder}.strm",
                Sources =
                [
                    new CatalogSource
                    {
                        StubKey = key,
                        PeerMediaSourceId = PeerSource.ToString("N"),
                        Trickplay = [new TrickplayLevel { Width = 320, Height = 180, TileWidth = 10, TileHeight = 10, ThumbnailCount = 250, IntervalMs = 10_000, Bandwidth = 500_000 }],
                    },
                ],
            });
        }

        TvIds.AddCatalog(document);
        store.Save(document);
        var registry = new PeerRegistry();
        var trust = new PeerTrust(client, registry);
        var slots = new FakeSlots();
        var time = new ManualTimeProvider();
        var grants = new GrantStore(time, slots);
        var limiter = new RateLimiter(time, 30, 15);
        var devices = new FakeDevices();
        var notice = new FakeNotice();
        var admins = new FakeAdmins();
        PlaybackParts? partsBox = null;
        var streamSlots = new StreamSlots(time, () => partsBox?.MaxStreams ?? 2);
        slots.Real = streamSlots;
        var parts = new PlaybackParts(grants, limiter, registry, devices, notice, admins, endpoint, slots, time, streamSlots);
        partsBox = parts;
        parts.Cards = new OfflineCards(() => new OfflineCardSettings(parts.CardsEnabled, parts.CardServiceName, string.Empty), parts.CardEncoder, dir.Combine("offline-cards"), time);
        var handler = new PlaybackInfoHandler(
            grants,
            limiter,
            new FakeEndpoints(endpoint),
            client,
            trust,
            registry,
            new StubCatalog(store),
            state,
            devices,
            new FakeLimits(parts),
            notice,
            admins,
            slots,
            () => parts.HlsSubtitles,
            time,
            new TypedCapture<PlaybackInfoHandler>(log),
            new AttachmentRebuilder(() => parts.AttachmentsEnabled),
            cards: parts.Cards,
            groups: new LiveGroups(() => parts.GroupOverride),
            maxStreamsPerUser: () => parts.MaxStreams);
        parts.Handler = handler;
        builder.Services.AddSingleton<IPlaybackInfoHandler>(handler);
        builder.Services.AddSingleton(parts.Cards);
        var staticHandler = new StaticStreamHandler(new StubCatalog(store), new FakeEndpoints(endpoint), client, trust, registry, streamSlots, new RateLimiter(time, 1200, 100), state, notice, new TypedCapture<StaticStreamHandler>(log), cards: parts.Cards, readAhead: parts.StaticReadAhead, groups: new LiveGroups(() => parts.GroupOverride), grants: grants);
        builder.Services.AddSingleton<IStaticStreamHandler>(staticHandler);
        var jobs = new HlsJobControl(new FakeEndpoints(endpoint), client, new TypedCapture<HlsJobControl>(log));
        builder.Services.AddSingleton(jobs);
        builder.Services.AddSingleton<IActiveEncodingsHandler>(new ActiveEncodingsHandler(grants, jobs, streamSlots, new RateLimiter(time, 10, 10)));
        var broker = new PeerExtractionGate(time);
        var subtitles = new SubtitleHandler(grants, new StubCatalog(store), new FakeEndpoints(endpoint), client, trust, registry, state, notice, broker, new RateLimiter(time, 30, 15), time, new TypedCapture<SubtitleHandler>(log));
        parts.Broker = broker;
        builder.Services.AddSingleton(broker);
        builder.Services.AddSingleton<ISubtitleHandler>(subtitles);
        var telemetry = new PassiveTelemetry(new SpeedHistoryStore(new ShoalPaths(dir.Combine("speed-root")), Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance), streamSlots, time);
        parts.Telemetry = telemetry;
        var readAhead = new HlsReadAhead(() => parts.HlsBufferAhead, id => grants.Get(id) is { } held && streamSlots.HoldsOwnSlot(held), time, new TypedCapture<HlsReadAhead>(log)) { Enabled = false };
        streamSlots.HlsReleased += readAhead.Release;
        registry.PeerStopped += readAhead.ReleasePeer;
        parts.ReadAhead = readAhead;
        builder.Services.AddSingleton(new HlsProxyHandler(grants, new FakeEndpoints(endpoint), client, trust, registry, streamSlots, state, notice, subtitles, new RateLimiter(time, 600, 40), time, new TypedCapture<HlsProxyHandler>(log), telemetry: telemetry, readAhead: readAhead));
        parts.AttachmentLimiter = new RateLimiter(time, 60, 60);
        // The filter asks the same resolver whether a named media source belongs to the title (Warden 10 L5).
        builder.Services.AddSingleton<IVersionResolver>(sp => new LiveResolver(() => parts.VersionOverride, new OwnSourceVersionResolver(sp.GetRequiredService<ShoalIndex>(), new StubCatalog(store))));
        builder.Services.AddSingleton<IAttachmentHandler>(sp => new AttachmentHandler(
            grants,
            new LiveResolver(() => parts.VersionOverride, new OwnSourceVersionResolver(sp.GetRequiredService<ShoalIndex>(), new StubCatalog(store))),
            new FakeEndpoints(endpoint),
            client,
            trust,
            registry,
            state,
            broker,
            sp.GetRequiredService<InterceptCounters>(),
            parts.AttachmentLimiter,
            () => parts.AttachmentsEnabled,
            time,
            new TypedCapture<AttachmentHandler>(log)));
        var cache = new TrickplayCache(dir.Combine("trickplay-cache"), () => partsBox!.TrickplayCapBytes, time, new TypedCapture<TrickplayCache>(log));
        parts.TrickplayCache = cache;
        builder.Services.AddSingleton(cache);
        builder.Services.AddSingleton<ITrickplayHandler>(sp => new TrickplayHandler(
            sp.GetRequiredService<ShoalIndex>(),
            new StubCatalog(store),
            new OwnSourceVersionResolver(sp.GetRequiredService<ShoalIndex>(), new StubCatalog(store)),
            new FakeEndpoints(endpoint),
            client,
            trust,
            registry,
            state,
            cache,
            new LiveSanitizer(() => parts.Sanitizer),
            sp.GetRequiredService<InterceptCounters>(),
            new RateLimiter(time, 2400, 100),
            () => parts.TrickplayEnabled,
            new TypedCapture<TrickplayHandler>(log)));
        builder.Services.AddControllers().ConfigureApplicationPartManager(manager => { manager.ApplicationParts.Add(new SingleControllerPart(typeof(Jellyfin.Plugin.Shoal.Api.ShoalHlsController))); manager.ApplicationParts.Add(new SingleControllerPart(typeof(Jellyfin.Plugin.Shoal.Api.ShoalSubtitlesController))); });
        return parts;
    }
}

/// <summary>
/// Stands in for ffmpeg: writes a structurally valid MP4 (ftyp, moov, mdat) and remembers what it was asked to draw.
/// </summary>
internal sealed class FakeCardEncoder : IOfflineCardEncoder
{
    public bool Available { get; set; } = true;

    public bool Works { get; set; } = true;

    public int Calls => Texts.Count;

    public List<string> Texts { get; } = [];

    public int DelayMs { get; set; }

    public static byte[] Mp4(int padding = 4000)
    {
        static byte[] Box(string type, byte[] body)
        {
            var size = body.Length + 8;
            return [(byte)(size >> 24), (byte)(size >> 16), (byte)(size >> 8), (byte)size, .. System.Text.Encoding.ASCII.GetBytes(type), .. body];
        }

        return [.. Box("ftyp", [.. System.Text.Encoding.ASCII.GetBytes("isom"), 0, 0, 2, 0, .. System.Text.Encoding.ASCII.GetBytes("isomiso2avc1mp41")]), .. Box("moov", new byte[100]), .. Box("mdat", new byte[padding])];
    }

    public async Task<bool> EncodeAsync(string text, string outputPath, CancellationToken cancellationToken)
    {
        lock (Texts)
        {
            Texts.Add(text);
        }

        if (DelayMs > 0)
        {
            await Task.Delay(DelayMs, cancellationToken);
        }

        if (!Works)
        {
            return false;
        }

        await System.IO.File.WriteAllBytesAsync(outputPath, Mp4(), cancellationToken);
        return true;
    }
}

internal sealed class FakeDevices : IDeviceCapabilities
{
    public Dictionary<(Guid User, string Device), DeviceProfile> Profiles { get; } = [];

    public DeviceProfile? ProfileFor(Guid userId, string deviceId) => Profiles.GetValueOrDefault((userId, deviceId));
}

internal sealed class FakeNotice : IOfflineNotice
{
    public List<(Guid User, string Peer)> Notices { get; } = [];

    public List<(Guid User, string Peer)> QualityRefusals { get; } = [];

    public void PeerOffline(Guid userId, string peerKey, string peerName) => Notices.Add((userId, peerKey));

    public void QualityRefused(Guid userId, string peerKey) => QualityRefusals.Add((userId, peerKey));

    public List<(Guid User, string Peer, string Name, BusyReason Reason, int Playing)> Busies { get; } = [];

    public void Busy(Guid userId, string peerKey, string peerName, BusyReason reason, int playing = 0) => Busies.Add((userId, peerKey, peerName, reason, playing));
}

internal sealed class FakeAdmins : IAdminNotifier
{
    public List<string> Messages { get; } = [];

    public Task NotifyAdminsAsync(string header, string text, CancellationToken cancellationToken = default)
    {
        Messages.Add(header + ": " + text);
        return Task.CompletedTask;
    }
}

internal sealed class FakeSlots : ISlotLedger
{
    public HashSet<string> Holding { get; } = [];

    public StreamSlots? Real { get; set; }

    public bool HoldsSlot(Grant grant) => Holding.Contains(grant.GrantId) || (Real?.HoldsSlot(grant) ?? false);

    public bool HoldsOwnSlot(Grant grant) => Holding.Contains(grant.GrantId) || (Real?.HoldsOwnSlot(grant) ?? false);

    public bool CanStart(Guid userId, Guid itemId, string peerKey, int peerMax) => Real?.CanStart(userId, itemId, peerKey, peerMax) ?? true;

    public bool CanStart(Guid userId, Guid itemId, string peerKey, int peerMax, SlotOrigin origin) => Real?.CanStart(userId, itemId, peerKey, peerMax, origin) ?? true;

    public bool ConversionsFull(string peerKey, int peerMaxConversions) => Real?.ConversionsFull(peerKey, peerMaxConversions) ?? false;

    public bool ConversionsFull(string peerKey, int peerMaxConversions, Guid userId, Guid itemId, SlotOrigin origin) =>
        Real?.ConversionsFull(peerKey, peerMaxConversions, userId, itemId, origin) ?? false;

    public int ActiveSlots(Guid userId) => Real?.ActiveSlots(userId) ?? 0;
}

internal sealed class FakeLimits : ISystemLimits
{
    private readonly PlaybackParts _parts;

    public FakeLimits(PlaybackParts parts) => _parts = parts;

    public BitrateLimits Current() => _parts.Limits;
}

internal sealed class FakeEndpoints : IPeerEndpoints
{
    private readonly PeerEndpoint _endpoint;

    public FakeEndpoints(PeerEndpoint endpoint) => _endpoint = endpoint;

    public PeerEndpoint? Find(string peerKey) => peerKey == _endpoint.PeerKey ? _endpoint : null;
}

/// <summary>
/// Lets a typed logger parameter write to the shared capturing logger.
/// </summary>
internal sealed class TypedCapture<T> : Microsoft.Extensions.Logging.ILogger<T>
{
    private readonly CapturingLogger _inner;

    public TypedCapture(CapturingLogger inner) => _inner = inner;

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

    public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        _inner.Log(logLevel, eventId, state, exception, formatter);
}

/// <summary>An MVC application part that exposes exactly one controller type of the plugin (the others need services the harness does not have).</summary>
internal sealed class SingleControllerPart : Microsoft.AspNetCore.Mvc.ApplicationParts.ApplicationPart, Microsoft.AspNetCore.Mvc.ApplicationParts.IApplicationPartTypeProvider
{
    private readonly System.Reflection.TypeInfo _type;

    public SingleControllerPart(Type type) => _type = System.Reflection.IntrospectionExtensions.GetTypeInfo(type);

    public override string Name => _type.Name;

    public IEnumerable<System.Reflection.TypeInfo> Types => [_type];
}

/// <summary>
/// Forwards to whichever sanitizer the test has set now.
/// </summary>
internal sealed class LiveSanitizer : IImageSanitizer
{
    private readonly Func<IImageSanitizer> _current;

    public LiveSanitizer(Func<IImageSanitizer> current) => _current = current;

    public Task<ImageSanitizeResult> SanitizeAsync(ReadOnlyMemory<byte> input, ImageSpec spec, CancellationToken cancellationToken) =>
        _current().SanitizeAsync(input, spec, cancellationToken);
}

/// <summary>
/// Uses the resolver a test sets, else the real one.
/// </summary>
internal sealed class LiveResolver : IVersionResolver
{
    private readonly Func<IVersionResolver?> _override;
    private readonly IVersionResolver _real;

    public LiveResolver(Func<IVersionResolver?> over, IVersionResolver real)
    {
        _override = over;
        _real = real;
    }

    public ResolvedVersion? Resolve(Guid routeItemId, string? requestedMediaSourceId) => (_override() ?? _real).Resolve(routeItemId, requestedMediaSourceId);
}

/// <summary>
/// The group pick of the parts, read on every call; none means no title is grouped.
/// </summary>
internal sealed class LiveGroups : IGroupPlayback
{
    private readonly Func<IGroupPlayback?> _override;

    public LiveGroups(Func<IGroupPlayback?> over) => _override = over;

    public IReadOnlyList<MemberChoice>? Candidates(string stubPeerKey, CatalogEntry stubEntry, CallerUser user) => _override()?.Candidates(stubPeerKey, stubEntry, user);

    public IReadOnlyList<MemberChoice> CandidatesForCopy(string stubPeerKey, CatalogEntry stubEntry, CallerUser user, string copyLibKey) => _override()?.CandidatesForCopy(stubPeerKey, stubEntry, user, copyLibKey) ?? [];

    public MemberChoice? Member(string stubPeerKey, CatalogEntry stubEntry, string grantPeerKey, Guid grantPeerItemId) => _override()?.Member(stubPeerKey, stubEntry, grantPeerKey, grantPeerItemId);

    public IReadOnlyList<Guid> PeerIds(CatalogEntry stubEntry) => _override()?.PeerIds(stubEntry) ?? [];

    public bool Admits(string stubPeerKey, CatalogEntry stubEntry, MemberChoice member, CallerUser user, string? copyLibKey) => _override()?.Admits(stubPeerKey, stubEntry, member, user, copyLibKey) ?? true;
}
