using System;
using Jellyfin.Plugin.Shoal.Sync;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Jellyfin.Plugin.Shoal.Registrations;

/// <summary>
/// The TV services (DESIGN v2.7, K34). The call stands in <see cref="PluginServiceRegistrator"/>. TV is always on since v2.9.1 (the old TvShowsEnabled switch is ignored). K35 (sanitizer, layout, NFO writer), K36 (reader, planner) and K37 (image work) each Replace their default in
/// their own registration file or here, and nobody else edits this file.
/// </summary>
public static class Tv
{
    /// <summary>
    /// Registers the do-nothing defaults of every TV interface.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same collection.</returns>
    public static IServiceCollection AddShoalTv(this IServiceCollection services)
    {
        services.TryAddSingleton<ITvSanitizer>(NoTv.Instance);
        services.TryAddSingleton<ITvLayout>(NoTv.Instance);
        services.TryAddSingleton<ITvNfoWriter>(NoTv.Instance);
        services.TryAddSingleton<IPeerTvReader>(NoTv.Instance);
        services.TryAddSingleton<ITvPlanner>(NoTv.Instance);
        services.TryAddSingleton<ITvImageWork>(NoTv.Instance);

        // K35: the pure libraries replace their defaults (sanitizer, layout, NFO writer). (TV always on since v2.9.1.)
        services.Replace(ServiceDescriptor.Singleton<ITvSanitizer>(sp => new TvSanitizer(sp.GetRequiredService<IRatingTable>(), sp.GetService<ITrickplaySanitizer>())));
        services.TryAddSingleton<ILocalSeries, JellyfinLocalSeries>();
        services.Replace(ServiceDescriptor.Singleton<ITvImageWork, TvImageWork>()); // K37
        services.TryAddSingleton(sp => new TvImageRunner(sp.GetRequiredService<ImageFetcher>(), sp.GetService<TimeProvider>() ?? TimeProvider.System));
        services.Replace(ServiceDescriptor.Singleton<ITvLayout, TvLayout>());
        services.Replace(ServiceDescriptor.Singleton<ITvNfoWriter, TvNfoWriter>());
        return services;
    }
}
