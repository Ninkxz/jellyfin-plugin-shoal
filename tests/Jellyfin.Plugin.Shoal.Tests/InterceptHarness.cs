using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.Authorization;
using Jellyfin.Plugin.Shoal.Http;
using Jellyfin.Plugin.Shoal.Peers;
using Jellyfin.Plugin.Shoal.Playback;
using Jellyfin.Plugin.Shoal.State;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Shoal.Tests;

/// <summary>
/// Core's media routes with the templates 6.1 lists, so the real filter runs on real routing. Every action answers 200 with <c>X-Core</c>; a test
/// that sees that header on a federated item has caught core's action running, which is the bug the filter exists to prevent. Authorization
/// attributes follow core: the routes it leaves anonymous are <c>[AllowAnonymous]</c> here.
/// </summary>
public sealed class FakeCoreController : ControllerBase
{
    public const string Header = "X-Core";

    [Authorize]
    [HttpGet("Items/{itemId}/PlaybackInfo")]
    [HttpPost("Items/{itemId}/PlaybackInfo")]
    public IActionResult PlaybackInfo() => Core("PlaybackInfo");

    [AllowAnonymous]
    [HttpGet("Videos/{itemId}/stream")]
    [HttpHead("Videos/{itemId}/stream")]
    public IActionResult Stream() => Core("Stream");

    [AllowAnonymous]
    [HttpGet("Videos/{itemId}/stream.{container}")]
    [HttpHead("Videos/{itemId}/stream.{container}")]
    public IActionResult StreamWithContainer() => Core("StreamWithContainer");

    [AllowAnonymous]
    [HttpGet("Videos/{routeItemId}/{routeMediaSourceId}/Subtitles/{routeIndex}/Stream.{routeFormat}")]
    [HttpGet("Videos/{routeItemId}/{routeMediaSourceId}/Subtitles/{routeIndex}/{routeStartPositionTicks}/Stream.{routeFormat}")]
    public IActionResult Subtitle() => Core("Subtitle");

    [Authorize]
    [HttpGet("Items/{itemId}/Download")]
    public IActionResult Download() => Core("Download");

    [Authorize]
    [HttpGet("Items/{itemId}/File")]
    public IActionResult File() => Core("File");

    [Authorize]
    [HttpPost("LiveStreams/Open")]
    public IActionResult LiveStreamsOpen() => Core("LiveStreamsOpen");

    [AllowAnonymous]
    [HttpGet("Videos/{itemId}/master.m3u8")]
    [HttpGet("Videos/{itemId}/main.m3u8")]
    [HttpGet("Videos/{itemId}/live.m3u8")]
    [HttpGet("Videos/{itemId}/hls1/{playlistId}/{segmentId}.{container}")]
    [HttpGet("Videos/{itemId}/hls/{playlistId}/{segmentId}.{segmentContainer}")]
    [HttpGet("Videos/{itemId}/{mediaSourceId}/Subtitles/{index}/subtitles.m3u8")]
    [HttpGet("Videos/{itemId}/Trickplay/{width}/tiles.m3u8")]
    [HttpGet("Videos/{itemId}/Trickplay/{width}/{index}.jpg")]
    [HttpGet("Videos/{videoId}/{mediaSourceId}/Attachments/{index}")]
    public IActionResult CoreHls() => Core("CoreHls");

    /// <summary>Core's playback report routes: while the offline card is playing, the filter answers them itself and this action must never run.</summary>
    [Authorize]
    [HttpPost("Sessions/Playing")]
    [HttpPost("Sessions/Playing/Progress")]
    [HttpPost("Sessions/Playing/Ping")]
    [HttpPost("Sessions/Playing/Stopped")]
    [HttpPost("PlayingItems/{itemId}")]
    [HttpPost("PlayingItems/{itemId}/Progress")]
    [HttpDelete("PlayingItems/{itemId}")]
    public IActionResult PlaybackReport() => Core("PlaybackReport");

    [AllowAnonymous]
    [HttpGet("Audio/{itemId}/stream")]
    [HttpHead("Audio/{itemId}/stream")]
    [HttpGet("Audio/{itemId}/stream.{container}")]
    [HttpHead("Audio/{itemId}/stream.{container}")]
    [HttpGet("Audio/{itemId}/universal")]
    [HttpHead("Audio/{itemId}/universal")]
    [HttpGet("Audio/{itemId}/master.m3u8")]
    [HttpGet("Audio/{itemId}/main.m3u8")]
    [HttpGet("Audio/{itemId}/hls1/{playlistId}/{segmentId}.{container}")]
    [HttpGet("Audio/{itemId}/hls/{segmentId}/stream.mp3")]
    [HttpGet("Audio/{itemId}/hls/{segmentId}/stream.aac")]
    public IActionResult Audio() => Core("Audio");

    [Authorize]
    [HttpDelete("Videos/ActiveEncodings")]
    public IActionResult ActiveEncodings() => Core("ActiveEncodings");

    /// <summary>A route that is not in 6.1; the filter must leave it alone.</summary>
    [AllowAnonymous]
    [HttpGet("Items/{itemId}/Images/{imageType}")]
    public IActionResult Image() => Core("Image");

    private IActionResult Core(string name)
    {
        Response.Headers[Header] = name;
        return Content("CORE:" + name);
    }
}

/// <summary>
/// Handlers that stand in for K14, K15 and K18: they answer 200 after exactly one request to the fake peer, and can be told to fail.
/// </summary>
public sealed class RecordingHandlers : IPlaybackInfoHandler, IStaticStreamHandler, ISubtitleHandler, IActiveEncodingsHandler
{
    private readonly PeerClient _client;
    private readonly PeerEndpoint _peer;

    public RecordingHandlers(PeerClient client, PeerEndpoint peer)
    {
        _client = client;
        _peer = peer;
    }

    public ConcurrentQueue<string> Calls { get; } = new();

    public Exception? Throw { get; set; }

    public string? OwnedPlaySession { get; set; }

    public Task<IActionResult> HandleAsync(FederatedRequest request, CancellationToken cancellationToken) => ServeAsync(request.Template, request.Caller.UserId, cancellationToken);

    async Task<IActionResult?> IActiveEncodingsHandler.HandleAsync(HttpContext http, Guid? userId, string? deviceId, string? playSessionId, CancellationToken cancellationToken)
    {
        if (OwnedPlaySession is null || playSessionId != OwnedPlaySession)
        {
            return null;
        }

        Calls.Enqueue("ActiveEncodings");
        await Task.CompletedTask;
        return new ShoalResult(204);
    }

    Task<IActionResult> IPlaybackInfoHandler.HandleAsync(FederatedRequest request, CancellationToken cancellationToken) => HandleAsync(request, cancellationToken);

    Task<IActionResult> IStaticStreamHandler.HandleAsync(FederatedRequest request, CancellationToken cancellationToken) => HandleAsync(request, cancellationToken);

    Task<IActionResult> ISubtitleHandler.HandleAsync(FederatedRequest request, CancellationToken cancellationToken) => HandleAsync(request, cancellationToken);

    private async Task<IActionResult> ServeAsync(string template, Guid userId, CancellationToken cancellationToken)
    {
        Calls.Enqueue(template);
        if (Throw is not null)
        {
            throw Throw;
        }

        using var response = await _client.SendAsync(_peer, PeerRequest.GetAnonymous("/System/Info/Public"), PeerCallProfile.Info, cancellationToken);
        return new ShoalResult((int)response.StatusCode, new { Handled = template, User = userId.ToString("N") });
    }
}

/// <summary>
/// The federated items of the matrix world as <see cref="IItemPaths"/>: each item has a stub path below its mapping's library folder.
/// </summary>
public sealed class WorldItemPaths : IItemPaths
{
    private readonly MatrixWorld _world;
    private readonly IStateStore _state;

    public WorldItemPaths(MatrixWorld world, IStateStore state)
    {
        _world = world;
        _state = state;
    }

#pragma warning disable CS0067
    public event EventHandler<ItemPathEventArgs>? Added;

    public event EventHandler<ItemPathEventArgs>? Removed;
#pragma warning restore CS0067

    public string? PathOf(Guid itemId)
    {
        if (!_world.Federated.TryGetValue(itemId, out var item))
        {
            return null;
        }

        if (_world.TvPaths.TryGetValue(itemId, out var tvPath))
        {
            var tvSubfolder = _state.Current.Home.Peers.SelectMany(static p => p.Libraries).FirstOrDefault(l => l.MappingKey == item.MappingKey)?.Subfolder ?? "unmapped";
            return System.IO.Path.Combine(_state.Current.StubRoot, item.PeerKey, tvSubfolder, tvPath.Replace('/', System.IO.Path.DirectorySeparatorChar));
        }

        var folder = FolderOf(itemId);
        if (item.LibKey.Length > 0)
        {
            // K44: a title folder in a shared library folder <StubRoot>/libs/<libKey>; its owner comes from WorldOwners.
            return System.IO.Path.Combine(_state.Current.StubRoot, ShoalLibrary.LibsFolder, item.LibKey, folder, folder + ".strm");
        }

        var subfolder = _state.Current.Home.Peers.SelectMany(static p => p.Libraries).FirstOrDefault(l => l.MappingKey == item.MappingKey)?.Subfolder ?? "unmapped";
        return System.IO.Path.Combine(_state.Current.StubRoot, item.PeerKey, subfolder, folder, folder + ".strm");
    }

    /// <summary>The title folder the world gives an item.</summary>
    /// <param name="itemId">The item.</param>
    /// <returns>The folder name.</returns>
    public static string FolderOf(Guid itemId) => "Title " + itemId.ToString("N")[^4..];

    public IReadOnlyList<ItemPathEventArgs> ItemsBelow(string stubRoot) =>
        [.. _world.Federated.Keys.Select(id => new ItemPathEventArgs(id, PathOf(id)))];
}

/// <summary>
/// The owners of the world's title folders in shared library folders, with the same rule as the catalog lookup: the item's connection must feed the library for
/// the mapping's type; an item with no connection has no owner.
/// </summary>
public sealed class WorldOwners : ILibraryTreeOwners
{
    private readonly MatrixWorld _world;

    public WorldOwners(MatrixWorld world) => _world = world;

    public TreeOwner? OwnerOf(ShoalState state, ShoalLibrary library, string topFolder)
    {
        foreach (var (id, item) in _world.Federated)
        {
            if (item.PeerKey.Length == 0 || item.LibKey != library.LibKey || WorldItemPaths.FolderOf(id) != topFolder)
            {
                continue;
            }

            var mapping = state.Home.Peers.FirstOrDefault(p => p.PeerKey == item.PeerKey)?.Libraries.FirstOrDefault(m => m.MappingKey == item.MappingKey);
            if (mapping is not null && LibraryFolders.FedBy(state, item.PeerKey, mapping.CollectionType)?.LibKey == library.LibKey)
            {
                return new TreeOwner(item.PeerKey, item.MappingKey, null, item.IsCopy);
            }
        }

        return null;
    }
}

/// <summary>
/// Hands the capturing logger to the logging system, for every category.
/// </summary>
internal sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly CapturingLogger _logger;

    public CapturingLoggerProvider(CapturingLogger logger) => _logger = logger;

    public ILogger CreateLogger(string categoryName) =>
        categoryName.StartsWith("Jellyfin.Plugin.Shoal", StringComparison.Ordinal) ? _logger : Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance; // the framework logs full URLs by itself (DESIGN 6.1: out of scope)

    public void Dispose()
    {
    }
}

/// <summary>
/// What the matrix harness adds when it hosts the real filter: the index, the counters, the recording handlers and the log.
/// </summary>
internal sealed class FilterParts
{
    private FilterParts(ShoalIndex index, InterceptCounters counters, RecordingHandlers handlers, CapturingLogger log, PlaybackParts? playback)
    {
        Index = index;
        Counters = counters;
        Handlers = handlers;
        Log = log;
        Playback = playback;
    }

    public ShoalIndex Index { get; }

    public InterceptCounters Counters { get; }

    public RecordingHandlers Handlers { get; }

    public CapturingLogger Log { get; }

    public PlaybackParts? Playback { get; }

    internal static FilterParts Register(WebApplicationBuilder builder, MatrixWorld world, StateStore state, PeerClient client, FakeJellyfinPeer peer, int skippedStep, TempDirectory? playbackDir = null)
    {
        var log = new CapturingLogger();
        builder.Logging.AddProvider(new CapturingLoggerProvider(log));
        builder.Logging.SetMinimumLevel(LogLevel.Trace);
        var counters = new InterceptCounters();
        var index = new ShoalIndex(new WorldItemPaths(world, state), state, log, new WorldOwners(world));
        var endpoint = new PeerEndpoint(world.PeerKey, "Hops", new Uri(peer.BaseUrl), "shoal-a1b2c3d4e5f6-4b0c1d2e", FakeJellyfinPeer.DefaultToken)
            .Configured(PeerNetwork.Tailnet, null, false, peer.ServerId);
        var handlers = new RecordingHandlers(client, endpoint);
        builder.Services.AddSingleton(index);
        builder.Services.AddSingleton(counters);
        PlaybackParts? playback = null;
        if (playbackDir is null)
        {
            builder.Services.AddSingleton<IPlaybackInfoHandler>(handlers);
            builder.Services.AddSingleton<IStaticStreamHandler>(handlers);
            builder.Services.AddSingleton<IActiveEncodingsHandler>(handlers);
            builder.Services.AddSingleton<ISubtitleHandler>(handlers);
        }
        else
        {
            playback = PlaybackParts.Build(builder, state, client, endpoint, playbackDir, log);
        }

        builder.Services.AddScoped(sp => new CallerContext(
            sp.GetRequiredService<IAuthorizationService>(),
            sp.GetRequiredService<IAuthorizationPolicyProvider>(),
            new MatrixHarness.FakeUsers(world),
            new MatrixHarness.FakeVisible(world),
            index,
            state,
            _ => false,
            log,
            skippedStep));
        builder.Services.AddControllers(options => options.Filters.Add<ShoalInterceptFilter>()).AddApplicationPart(typeof(FakeCoreController).Assembly);
        return new FilterParts(index, counters, handlers, log, playback);
    }
}
