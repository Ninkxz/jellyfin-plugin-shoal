using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.Configuration;
using Jellyfin.Plugin.Shoal.Peers;
using Jellyfin.Plugin.Shoal.State;
using Jellyfin.Plugin.Shoal.Storage;
using Jellyfin.Plugin.Shoal.Sync;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.Shoal.Tests;

/// <summary>
/// A movie on the fake peer.
/// </summary>
internal sealed class PeerSource
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public long Size { get; set; } = 4_000_000;

    public int Bitrate { get; set; } = 8_000_000;

    public string Container { get; set; } = "mkv";

    public string Codec { get; set; } = "h264";

    public int Width { get; set; } = 1920;

    public int Height { get; set; } = 1080;
}

internal sealed class PeerMovie
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = "Movie";

    public int? Year { get; set; } = 2000;

    public string Etag { get; set; } = "e1";

    public Dictionary<string, string> ProviderIds { get; set; } = [];

    public long Size { get; set; } = 4_000_000;

    public string Container { get; set; } = "mkv";

    public string? PosterTag { get; set; } = "p1";

    public string? BackdropTag { get; set; } = "b1";

    public string Overview { get; set; } = "A film.";

    public string[] Genres { get; set; } = ["Drama"];

    public string[] Actors { get; set; } = ["Actor One"];

    public string? Rating { get; set; } = "PG-13";

    public bool HideFromListing { get; set; }

    /// <summary>Gets or sets the media sources the details answer lists; null gives the one default source whose id is the item id.</summary>
    public List<PeerSource>? Sources { get; set; }

    /// <summary>Gets or sets extra members of the details answer, written as they are (a trickplay manifest, odd attachments).</summary>
    public Dictionary<string, object?>? ExtraDetail { get; set; }

    /// <summary>Gets or sets extra members written into every media source of the details answer.</summary>
    public Dictionary<string, object?>? SourceExtra { get; set; }

    public bool Deleted { get; set; }
}

/// <summary>
/// A scriptable peer that serves what the catalog sync asks for: identity, views, the cheap listing, details and images.
/// </summary>
internal sealed class FakeLibraryPeer : IAsyncDisposable
{
    private WebApplication? _app;

    public Guid ServerId { get; set; } = Guid.Parse("0123456789abcdef0123456789abcdef");

    public Guid LibraryId { get; set; } = Guid.Parse("aaaaaaaabbbbccccddddeeeeeeeeeeee");

    public string LibraryName { get; set; } = "Movies";

    /// <summary>Gets or sets the collection type the view reports (<c>movies</c>, or <c>tvshows</c> with the counts below).</summary>
    public string CollectionType { get; set; } = "movies";

    public int SeriesCount { get; set; }

    public int EpisodeCount { get; set; }

    /// <summary>Gets or sets the share user's <c>Policy.EnabledFolders</c> (2.9.2 K2); null means the ids of the movie and TV views (N form, as Pilot 21 G1 saw).</summary>
    public List<string>? EnabledFolders { get; set; }

    /// <summary>Gets or sets the share user's <c>Policy.EnableAllFolders</c>.</summary>
    public bool? EnableAllFolders { get; set; } = false;

    /// <summary>Gets or sets a value indicating whether <c>/UserViews</c> also lists core's Playlists view, which is never in EnabledFolders (Pilot 21 G1).</summary>
    public bool ListPlaylistsView { get; set; } = true;

    /// <summary>Gets the id of that Playlists view.</summary>
    public Guid PlaylistsViewId { get; } = Guid.Parse("d8e71732000040008000000000000001");

    /// <summary>Replaces the answer to <c>/Users/Me</c>: (status, body), or null for the policy above.</summary>
    public Func<(int Status, string Body)?>? MeOverride { get; set; }

    public List<PeerMovie> Movies { get; } = [];

    public ConcurrentQueue<string> Requests { get; } = new();

    public int ItemHits => Requests.Count(static r => r.StartsWith("/Items?", StringComparison.Ordinal) || r.StartsWith("/Items/", StringComparison.Ordinal));

    /// <summary>Replaces the answer to a listing page: (status, body), or null for the normal answer.</summary>
    public Func<int, (int Status, byte[] Body)?>? ListingOverride { get; set; }

    /// <summary>Replaces the total the listing announces.</summary>
    public int? TotalOverride { get; set; }

    /// <summary>Replaces the answer to an image request.</summary>
    public Func<PeerMovie, string, (int Status, string ContentType, byte[] Body)?>? ImageOverride { get; set; }

    /// <summary>Replaces the answer to an image request for a TV item: (peer item id, image type) to (status, content type, body), or null for the normal JPEG.</summary>
    public Func<Guid, string, (int Status, string ContentType, byte[] Body)?>? TvImageOverride { get; set; }

    /// <summary>Gets or sets a value indicating whether the peer is offline: every connection drops.</summary>
    public bool Down { get; set; }

    /// <summary>Delay before each answer.</summary>
    public int DelayMs { get; set; }

    /// <summary>Items to generate on the fly after the real ones (for scale tests).</summary>
    public int GeneratedCount { get; set; }

    /// <summary>Gets or sets the TV library this peer also has (series, seasons, episodes; DESIGN v2.7, K34), or null for a movies-only peer.</summary>
    public FakeTvTree? Tv { get; set; }

    public string Url { get; private set; } = string.Empty;

    public PeerMovie Add(string name, int? year = 2000, long? tmdb = null, string? imdb = null, Action<PeerMovie>? configure = null)
    {
        var movie = new PeerMovie { Name = name, Year = year };
        if (tmdb is not null)
        {
            movie.ProviderIds["Tmdb"] = tmdb.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        if (imdb is not null)
        {
            movie.ProviderIds["Imdb"] = imdb;
        }

        configure?.Invoke(movie);
        Movies.Add(movie);
        return movie;
    }

    public async Task StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        _app = builder.Build();
        _app.Map("/{**path}", HandleAsync);
        await _app.StartAsync();
        Url = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.DisposeAsync();
        }
    }

    private static Dictionary<string, object?> Line(PeerMovie m) => new()
    {
        ["Id"] = m.Id.ToString("N"),
        ["Name"] = m.Name,
        ["Etag"] = m.Etag,
        ["ProviderIds"] = m.ProviderIds,
        ["ProductionYear"] = m.Year,
        ["Type"] = "Movie",
    };

    private static Dictionary<string, object?> Detail(PeerMovie m)
    {
        var item = Line(m);
        item["OriginalTitle"] = m.Name;
        item["SortName"] = m.Name;
        item["Overview"] = m.Overview;
        item["Taglines"] = new[] { "A tagline" };
        item["Genres"] = m.Genres;
        item["Studios"] = new[] { new Dictionary<string, object?> { ["Name"] = "Studio One", ["Id"] = Guid.NewGuid().ToString("N") } };
        item["People"] = m.Actors.Select(a => new Dictionary<string, object?> { ["Name"] = a, ["Role"] = "Role", ["Type"] = "Actor", ["Id"] = Guid.NewGuid().ToString("N") }).ToArray();
        item["OfficialRating"] = m.Rating;
        item["CommunityRating"] = 7.5;
        if (m.Year is { } year)
        {
            item["PremiereDate"] = $"{year}-03-30T00:00:00.0000000Z";
        }

        item["RunTimeTicks"] = 72_000_000_000L;
        item["MediaSources"] = m.Sources is { } listed
            ? listed.Select(s => new Dictionary<string, object?>
            {
                ["Id"] = s.Id.ToString("N"),
                ["Size"] = s.Size,
                ["Bitrate"] = s.Bitrate,
                ["Container"] = s.Container,
                ["RunTimeTicks"] = 72_000_000_000L,
                ["MediaStreams"] = new[] { new Dictionary<string, object?> { ["Type"] = "Video", ["Codec"] = s.Codec, ["Width"] = s.Width, ["Height"] = s.Height } },
            }).ToArray()
            : new[]
            {
                new Dictionary<string, object?>
                {
                    ["Id"] = m.Id.ToString("N"),
                    ["Size"] = m.Size,
                    ["Bitrate"] = 8_000_000,
                    ["Container"] = m.Container,
                    ["RunTimeTicks"] = 72_000_000_000L,
                    ["MediaStreams"] = new[] { new Dictionary<string, object?> { ["Type"] = "Video", ["Codec"] = "h264", ["Width"] = 1920, ["Height"] = 1080 } },
                },
            };
        if (m.SourceExtra is { } sourceExtra && item["MediaSources"] is Dictionary<string, object?>[] written)
        {
            foreach (var source in written)
            {
                foreach (var (key, value) in sourceExtra)
                {
                    source[key] = value;
                }
            }
        }

        if (m.ExtraDetail is { } extra)
        {
            foreach (var (key, value) in extra)
            {
                item[key] = value;
            }
        }

        item["ImageTags"] = m.PosterTag is null ? new Dictionary<string, string>() : new Dictionary<string, string> { ["Primary"] = m.PosterTag };
        item["BackdropImageTags"] = m.BackdropTag is null ? Array.Empty<string>() : new[] { m.BackdropTag };
        return item;
    }

    private static Task WriteJsonAsync(HttpContext ctx, object body)
    {
        ctx.Response.ContentType = "application/json";
        return ctx.Response.WriteAsync(JsonSerializer.Serialize(body));
    }

    private IEnumerable<PeerMovie> Live() => Movies.Where(static m => !m.Deleted);

    private async Task HandleAsync(HttpContext ctx)
    {
        var path = ctx.Request.Path.Value ?? string.Empty;
        Requests.Enqueue(path + ctx.Request.QueryString.Value);
        if (Down)
        {
            ctx.Abort(); // the server is offline: the connection drops
            return;
        }

        if (DelayMs > 0)
        {
            await Task.Delay(DelayMs);
        }

        if (path == "/System/Info/Public")
        {
            await WriteJsonAsync(ctx, new { Id = ServerId.ToString("N"), ServerName = "Hops", Version = "12.1.0" });
        }
        else if (path == "/Users/Me")
        {
            if (MeOverride?.Invoke() is { } me)
            {
                ctx.Response.StatusCode = me.Status;
                ctx.Response.ContentType = "application/json";
                await ctx.Response.WriteAsync(me.Body);
                return;
            }

            // The shape of the share user's own /Users/Me (Pilot 21 G1): ids in N form, EnableAllFolders false.
            var enabled = EnabledFolders ?? [LibraryId.ToString("N"), .. Tv is null ? Array.Empty<string>() : [Tv.LibraryId.ToString("N")]];
            await WriteJsonAsync(ctx, new
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = "shoal-home",
                Policy = new { IsAdministrator = false, EnableAllFolders, EnabledFolders = enabled, EnableAllDevices = false, MaxActiveSessions = 1 },
            });
        }
        else if (path == "/UserViews")
        {
            var views = new List<object> { new { Id = LibraryId.ToString("N"), Name = LibraryName, CollectionType = CollectionType } };
            if (Tv is not null)
            {
                views.Add(new { Id = Tv.LibraryId.ToString("N"), Name = Tv.LibraryName, CollectionType = "tvshows" });
            }

            if (ListPlaylistsView)
            {
                views.Add(new { Id = PlaylistsViewId.ToString("N"), Name = "Playlists", CollectionType = "playlists" });
            }

            await WriteJsonAsync(ctx, new { Items = views, TotalRecordCount = views.Count });
        }
        else if (path == "/Items")
        {
            await HandleItemsAsync(ctx);
        }
        else if (path.StartsWith("/Items/", StringComparison.Ordinal) && path.Contains("/Images/", StringComparison.Ordinal))
        {
            await HandleImageAsync(ctx, path);
        }
        else
        {
            ctx.Response.StatusCode = 404;
        }
    }

    private async Task HandleItemsAsync(HttpContext ctx)
    {
        var query = ctx.Request.Query;
        if (Tv is not null
            && Tv.TryServe(
                query.TryGetValue("IncludeItemTypes", out var types) ? types.ToString() : string.Empty,
                query.TryGetValue("Ids", out var tvIds) ? tvIds.ToString() : string.Empty,
                query.TryGetValue("StartIndex", out var tvStart) ? int.Parse(tvStart.ToString(), System.Globalization.CultureInfo.InvariantCulture) : 0,
                query.TryGetValue("Limit", out var tvLimit) ? int.Parse(tvLimit.ToString(), System.Globalization.CultureInfo.InvariantCulture) : 200,
                out var tvBody))
        {
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsync(tvBody);
            return;
        }

        if (query.TryGetValue("Ids", out var idsValue))
        {
            var ids = idsValue.ToString().Split(',', StringSplitOptions.RemoveEmptyEntries).Select(s => Guid.ParseExact(s, "N")).ToHashSet();
            var found = Live().Where(m => ids.Contains(m.Id)).Select(Detail).ToArray();
            await WriteJsonAsync(ctx, new { Items = found, TotalRecordCount = found.Length, StartIndex = 0 });
            return;
        }

        if (query.TryGetValue("IncludeItemTypes", out var kinds) && (kinds == "Series" || kinds == "Episode"))
        {
            await WriteJsonAsync(ctx, new { Items = Array.Empty<object>(), TotalRecordCount = kinds == "Series" ? SeriesCount : EpisodeCount, StartIndex = 0 });
            return;
        }

        var start = int.Parse(query["StartIndex"].ToString(), System.Globalization.CultureInfo.InvariantCulture);
        var limit = int.Parse(query["Limit"].ToString(), System.Globalization.CultureInfo.InvariantCulture);
        if (ListingOverride?.Invoke(start) is { } custom)
        {
            ctx.Response.StatusCode = custom.Status;
            ctx.Response.ContentType = "application/json";
            await ctx.Response.Body.WriteAsync(custom.Body);
            return;
        }

        var listed = Live().Where(static m => !m.HideFromListing).ToList();
        var total = listed.Count + GeneratedCount;
        var items = new List<Dictionary<string, object?>>();
        for (var i = start; i < Math.Min(start + limit, total); i++)
        {
            items.Add(i < listed.Count ? Line(listed[i]) : GeneratedLine(i));
        }

        await WriteJsonAsync(ctx, new { Items = items, TotalRecordCount = TotalOverride ?? total, StartIndex = start });
    }

    private static Dictionary<string, object?> GeneratedLine(int index)
    {
        var bytes = new byte[16];
        BitConverter.GetBytes(index).CopyTo(bytes, 0);
        bytes[15] = 0x77;
        return new Dictionary<string, object?>
        {
            ["Id"] = new Guid(bytes).ToString("N"),
            ["Name"] = "Generated " + index.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Etag"] = "g1",
            ["ProviderIds"] = new Dictionary<string, string> { ["Tmdb"] = (1_000_000 + index).ToString(System.Globalization.CultureInfo.InvariantCulture) },
            ["ProductionYear"] = 2001,
            ["Type"] = "Movie",
        };
    }

    private async Task HandleImageAsync(HttpContext ctx, string path)
    {
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var movie = Movies.FirstOrDefault(m => m.Id.ToString("N") == parts[1]);
        var type = parts[3];
        if (movie is null && Tv is not null && Guid.TryParseExact(parts[1], "N", out var tvId))
        {
            var (tvStatus, tvType, tvBody) = TvImageOverride?.Invoke(tvId, type) ?? (200, "image/jpeg", ImageBuilder.Jpeg(480, 270, 3000));
            if (tvStatus < 0)
            {
                ctx.Abort(); // the connection drops: the peer is not answering
                return;
            }

            ctx.Response.StatusCode = tvStatus;
            ctx.Response.ContentType = tvType;
            ctx.Response.ContentLength = tvBody.Length;
            await ctx.Response.Body.WriteAsync(tvBody);
            return;
        }

        if (movie is null)
        {
            ctx.Response.StatusCode = 404;
            return;
        }

        var (status, contentType, body) = ImageOverride?.Invoke(movie, type) ?? (200, "image/jpeg", ImageBuilder.Jpeg(600, 900, 3000));
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = contentType;
        ctx.Response.ContentLength = body.Length;
        await ctx.Response.Body.WriteAsync(body);
    }
}

internal sealed class FakeLocalMovies : ILocalMovies
{
    public List<LocalMovie> Movies { get; } = [];

    public bool Throw { get; set; }

    public IReadOnlyList<LocalMovie> List(string stubRoot) => Throw ? throw new InvalidOperationException("boom") : Movies.ToList();
}

internal sealed class FakeRefresh : ILibraryRefresh
{
    public bool IsScanRunning { get; set; }

    public List<string> Reported { get; } = [];

    public bool Throw { get; set; }

    public void ReportChanged(string path)
    {
        if (Throw)
        {
            throw new InvalidOperationException("the refresher failed");
        }

        Reported.Add(path);
    }

    public int Scans { get; private set; }

    public void QueueScan() => Scans++;

    public List<string> LibraryScans { get; } = [];

    public void ScanLibrariesAt(IReadOnlyCollection<string> folders) => LibraryScans.AddRange(folders);
}

/// <summary>
/// A Home with one peer wired like the plugin wires it.
/// </summary>
internal sealed class SyncRig : IAsyncDisposable
{
    public const string PeerKey = "4b0c1d2e3f405162738495a6b7c8d9e0";
    public const string MappingKey = "movies-1";
    public const string TvMappingKey = "tv-1";

    private bool _realTv;

    private SyncRig(FakeLibraryPeer peer)
    {
        Peer = peer;
    }

    public FakeLibraryPeer Peer { get; }

    public TempDirectory Dir { get; } = new();

    public StateStore State { get; private set; } = null!;

    public PeerClient Client { get; private set; } = null!;

    public PeerRegistry Registry { get; private set; } = null!;

    public CatalogStore Catalogs { get; private set; } = null!;

    public FakeLocalMovies Local { get; } = new();

    public FakeRefresh Refresh { get; } = new();

    public ManualTimeProvider Time { get; } = new();

    public CatalogSyncService Sync { get; private set; } = null!;

    /// <summary>Peers the sync counts as unreachable (offline or paused): their titles hold nothing against another peer.</summary>
    public HashSet<string> DownPeers { get; } = new(StringComparer.Ordinal);

    public PeerCatalogReader Reader { get; private set; } = null!;

    public SyncSettings Settings { get; set; } = SyncSettings.Default;

    public CapturingLogger Log { get; } = new();

    /// <summary>Gets or sets a value indicating whether TV libraries are synced (the <c>TvShowsEnabled</c> switch).</summary>
    public bool TvEnabled { get; set; } = true;

    /// <summary>Gets Home's own series, for series-level dedup.</summary>
    public FakeLocalSeries LocalSeries { get; } = new();

    /// <summary>Gets the TV sync engine with the test stand-ins of K35's classes.</summary>
    public TvSyncEngine Tv { get; private set; } = null!;

    /// <summary>Gets the library folder of the TV mapping.</summary>
    public string TvFolder => Path.Combine(StubRoot, PeerKey, "tv");

    public string StubRoot => Dir.Combine("stubs");

    public string LibraryFolder => Path.Combine(StubRoot, PeerKey, "movies");

    public PeerEndpoint Endpoint { get; set; } = null!;

    public bool HasCredentials { get; set; } = true;

    public ShoalPaths Paths { get; private set; } = null!;

    public static async Task<SyncRig> CreateAsync(Action<FakeLibraryPeer>? seed = null) => await CreateAsync(seed, realTv: false);

    /// <summary>Creates the rig; with <paramref name="realTv"/> the TV engine runs with the real sanitizer, layout, NFO writer and image stage (what the plugin registers) instead of the stand-ins.</summary>
    public static async Task<SyncRig> CreateAsync(Action<FakeLibraryPeer>? seed, bool realTv)
    {
        var peer = new FakeLibraryPeer();
        seed?.Invoke(peer);
        await peer.StartAsync();
        var rig = new SyncRig(peer) { _realTv = realTv };
        await rig.InitializeAsync();
        return rig;
    }

    public async Task AddPeerAsync(string peerKey, string name, int priority, DateTime paired)
    {
        await State.UpdateAsync(s => s.Home.Peers.Add(new PeerState
        {
            PeerKey = peerKey,
            DisplayName = name,
            BaseUrl = "http://other.tail1234.ts.net:8096",
            Priority = priority,
            Pairing = new PairingInfo { Method = PairingMethod.Qc, PairedUtc = paired },
            Libraries = { new LibraryMapping { MappingKey = "movies-1", RemoteName = "Movies", LocalLibraryName = name + " Movies", Subfolder = "movies" } },
        }));
    }

    /// <summary>Maps the TV library of the fake peer (set <see cref="FakeLibraryPeer.Tv"/> first).</summary>
    public async Task AddTvMappingAsync()
    {
        await State.UpdateAsync(s => s.Home.Peers[0].Libraries.Add(new LibraryMapping { MappingKey = TvMappingKey, RemoteName = Peer.Tv!.LibraryName, CollectionType = "tvshows", LocalLibraryName = "Hops TV shows", Subfolder = "tv" }));
        RememberLibrary(TvMappingKey, Peer.Tv!.LibraryId);
    }

    /// <summary>
    /// Stores the peer library id of a mapping, as Add does since 2.9.2 K1 (the rig's legacy keys "movies-1" and "tv-1" resolve by stored id only).
    /// </summary>
    public void RememberLibrary(string mappingKey, Guid peerLibraryId)
    {
        var document = Catalog();
        document.Libraries.RemoveAll(l => l.MappingKey == mappingKey);
        document.Libraries.Add(new CatalogLibrary { MappingKey = mappingKey, PeerLibraryId = peerLibraryId.ToString("N") });
        Catalogs.Save(document);
    }

    public CatalogDocument Catalog() => Catalogs.Load(PeerKey).Document;

    public IEnumerable<CatalogEntry> Entries => Catalog().Entries;

    public Task<SyncReport> SyncAsync(SyncTrigger trigger = SyncTrigger.Scheduled, CancellationToken ct = default) => Sync.SyncAsync(PeerKey, trigger, ct);

    public IEnumerable<string> AllStubFiles() => Directory.Exists(StubRoot) ? Directory.EnumerateFiles(StubRoot, "*", SearchOption.AllDirectories) : [];

    public async ValueTask DisposeAsync()
    {
        Sync.Dispose();
        Client.Dispose();
        State.Dispose();
        await Peer.DisposeAsync();
        Dir.Dispose();
    }

    private async Task InitializeAsync()
    {
        Paths = new ShoalPaths(Dir.Path);
        State = new StateStore(Paths, NullLogger.Instance, Time, null);
        await State.UpdateAsync(s =>
        {
            s.StubRoot = StubRoot;
            s.Home.Peers.Add(new PeerState
            {
                PeerKey = PeerKey,
                DisplayName = "Hops",
                BaseUrl = Peer.Url,
                PinnedServerId = Peer.ServerId.ToString("N"),
                Priority = 100,
                Pairing = new PairingInfo { Method = PairingMethod.Qc, PairedUtc = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc) },
                Libraries = { new LibraryMapping { MappingKey = MappingKey, RemoteName = "Movies", LocalLibraryName = "Hops Movies", Subfolder = "movies" } },
            });
        });
        Registry = new PeerRegistry(Time);
        Client = new PeerClient(() => "Home", () => "0.1.0.0", Time, Registry) { EnforceAddressPolicy = false };
        var trust = new PeerTrust(Client, Registry, Time);
        Endpoint = new PeerEndpoint(PeerKey, "Hops", new Uri(Peer.Url), "shoal-home-device", "token-for-tests-0123456789abcdef").Configured(PeerNetwork.Tailnet, null, false, Peer.ServerId.ToString("N"));
        var sanitizer = new MetadataSanitizer(new KnownRatings("R", "PG-13", "PG", "G"), new AlternateVersionSourceSanitizer(() => Settings.AlternateVersions), new TrickplaySanitizer());
        var reader = new PeerCatalogReader(Client, trust, Registry, sanitizer, Log);
        Reader = reader;
        Catalogs = new CatalogStore(Paths, Log);
        RememberLibrary(MappingKey, Peer.LibraryId);
        var fetcher = new ImageFetcher(Client, NullLogger<ImageFetcher>.Instance);
        var writer = new StubWriter(fetcher, Log);
        ITvLayout layout = _realTv ? new TvLayout() : new TestTvLayout();
        ITvSanitizer tvSanitizer = _realTv ? new TvSanitizer(new KnownRatings("R", "PG-13", "PG", "G"), new TrickplaySanitizer()) : new TestTvSanitizer();
        ITvNfoWriter tvNfo = _realTv ? new TvNfoWriter() : new TestTvNfo();
        Tv = new TvSyncEngine(tvSanitizer, layout, tvNfo, new PeerTvReader(reader, Log), new TvPlanner(layout), new TvStubWriter(Log), LocalSeries, Log) { RefreshDeferral = TimeSpan.Zero };
        if (_realTv)
        {
            Tv.ImageWork = new TvImageWork();
            Tv.Images = new TvImageRunner(fetcher, Time);
        }

        Sync = new CatalogSyncService(State, key => HasCredentials && key == PeerKey ? Endpoint : null, trust, reader, Catalogs, writer, Local, Refresh, () => Settings, Time, Log, Tv, () => TvEnabled, key => !DownPeers.Contains(key));
    }

    private sealed class KnownRatings(params string[] known) : IRatingTable
    {
        public bool IsRecognized(string rating) => known.Contains(rating, StringComparer.Ordinal);
    }
}
