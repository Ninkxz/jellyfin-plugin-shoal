using System.Linq;
using Jellyfin.Plugin.Shoal.Playback;
using Jellyfin.Plugin.Shoal.State;
using Jellyfin.Plugin.Shoal.Sync;
using Jellyfin.Plugin.Shoal.Visibility;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Shoal.Registrations;

/// <summary>
/// The TV sync engine (DESIGN v2.7.2, K36): the peer reader and the planner replace the do-nothing defaults of <see cref="Tv"/>, and the engine that
/// <see cref="CatalogSyncService"/> calls for TV libraries is built. TV is always on since v2.9.1. The sanitizer, layout and NFO writer stay
/// whatever K35 registers (the defaults until it lands).
/// </summary>
public static class TvSync
{
    /// <summary>
    /// Registers the TV sync services.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same collection.</returns>
    public static IServiceCollection AddShoalTvSync(this IServiceCollection services)
    {
        services.Replace(ServiceDescriptor.Singleton<IPeerTvReader, PeerTvReader>());
        services.Replace(ServiceDescriptor.Singleton<ITvPlanner>(sp => new TvPlanner(sp.GetRequiredService<ITvLayout>())));
        services.TryAddSingleton<ILocalSeries>(NoLocalSeries.Instance);
        services.TryAddSingleton<TvStubWriter>();
        services.TryAddSingleton(sp => new TvSyncEngine(
            sp.GetRequiredService<ITvSanitizer>(),
            sp.GetRequiredService<ITvLayout>(),
            sp.GetRequiredService<ITvNfoWriter>(),
            sp.GetRequiredService<IPeerTvReader>(),
            sp.GetRequiredService<ITvPlanner>(),
            sp.GetRequiredService<TvStubWriter>(),
            sp.GetRequiredService<ILocalSeries>(),
            sp.GetRequiredService<ILogger<TvSyncEngine>>())
        {
            ActiveStreams = key => sp.GetService<StreamSlots>()?.ActiveSlotsOnPeer(key) ?? 0,
            EpisodeThumbs = static () => Plugin.Instance?.Configuration.TvEpisodeThumbs ?? true,
            ImageWork = sp.GetRequiredService<ITvImageWork>(),
            Images = sp.GetRequiredService<TvImageRunner>(),
            SeriesGrouping = path => sp.GetService<ILibrarySource>()?.List().FirstOrDefault(l => l.Locations.Any(location => LibraryFolders.SamePath(location, path)))?.GroupsSeries,
        });
        return services;
    }
}
