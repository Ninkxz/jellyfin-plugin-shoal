using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.Authorization;
using Jellyfin.Plugin.Shoal.Peers;
using Jellyfin.Plugin.Shoal.Playback;
using Jellyfin.Plugin.Shoal.State;
using Jellyfin.Plugin.Shoal.Storage;
using Jellyfin.Plugin.Shoal.Sync;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Shoal.Registrations;

/// <summary>
/// The services of trickplay (DESIGN v2.6.5, K30). The call stands in <see cref="PluginServiceRegistrator"/> (DESIGN v2.6.9). The
/// <see cref="TrickplayInfoWriter"/> is a library post-scan task, which the server finds by itself.
/// </summary>
public static class Trickplay
{
    /// <summary>
    /// Registers the tile cache, the tile handler in place of the 404 default, the metadata sanitizer in place of the empty one, and the host that
    /// walks the cache once at start-up.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same collection.</returns>
    public static IServiceCollection AddShoalTrickplay(this IServiceCollection services)
    {
        services.TryAddSingleton(sp => new TrickplayCache(
            Path.Combine(sp.GetRequiredService<ShoalPaths>().Root, "trickplay"),
            () => (long)Math.Clamp(Plugin.Instance?.Configuration.TrickplayCacheGb ?? 1, 1, 20) * (1L << 30),
            sp.GetService<TimeProvider>() ?? TimeProvider.System,
            sp.GetRequiredService<ILogger<TrickplayCache>>()));
        services.Replace(ServiceDescriptor.Singleton<ITrickplayHandler>(sp => new TrickplayHandler(
            sp.GetRequiredService<ShoalIndex>(),
            sp.GetRequiredService<StubCatalog>(),
            sp.GetRequiredService<IVersionResolver>(),
            sp.GetRequiredService<IPeerEndpoints>(),
            sp.GetRequiredService<PeerClient>(),
            sp.GetRequiredService<PeerTrust>(),
            sp.GetRequiredService<PeerRegistry>(),
            sp.GetRequiredService<IStateStore>(),
            sp.GetRequiredService<TrickplayCache>(),
            sp.GetRequiredService<IImageSanitizer>(),
            sp.GetRequiredService<InterceptCounters>(),
            new RateLimiter(sp.GetService<TimeProvider>() ?? TimeProvider.System, perMinute: 2400, burst: 100),
            () => Plugin.Instance?.Configuration.TrickplayEnabled ?? true,
            sp.GetRequiredService<ILogger<TrickplayHandler>>())));
        services.Replace(ServiceDescriptor.Singleton<ITrickplaySanitizer, TrickplaySanitizer>());
        services.TryAddSingleton<ITrickplayManifestSource>(sp => new CatalogTrickplayManifest(
            sp.GetRequiredService<ShoalIndex>(),
            sp.GetRequiredService<StubCatalog>(),
            sp.GetRequiredService<IVersionResolver>(),
            static () => Plugin.Instance?.Configuration.TrickplayEnabled ?? true));
        services.Configure<Microsoft.AspNetCore.Mvc.MvcOptions>(static options => options.Filters.Add<TrickplayManifestFilter>());
        services.AddHostedService<TrickplayCacheHost>();
        return services;
    }
}

/// <summary>
/// Walks the tile cache once at start-up, in the background (DESIGN v2.6.5: capped at 200,000 files).
/// </summary>
public sealed class TrickplayCacheHost : IHostedService
{
    private readonly TrickplayCache _cache;

    /// <summary>
    /// Initializes a new instance of the <see cref="TrickplayCacheHost"/> class.
    /// </summary>
    /// <param name="cache">The tile cache.</param>
    public TrickplayCacheHost(TrickplayCache cache) => _cache = cache;

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ = Task.Run(_cache.Scan, CancellationToken.None);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
