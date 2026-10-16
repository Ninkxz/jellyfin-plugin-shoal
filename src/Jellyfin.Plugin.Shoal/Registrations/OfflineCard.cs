using System;
using System.IO;
using Jellyfin.Plugin.Shoal.Playback;
using Jellyfin.Plugin.Shoal.Storage;
using Jellyfin.Plugin.Shoal.Sync;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Shoal.Registrations;

/// <summary>
/// The offline card (owner request 2026-10-16). The call stands in <see cref="PluginServiceRegistrator"/>.
/// </summary>
public static class OfflineCard
{
    /// <summary>
    /// Registers the card service with the host's ffmpeg as its encoder.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same collection.</returns>
    public static IServiceCollection AddShoalOfflineCards(this IServiceCollection services)
    {
        services.TryAddSingleton<IEncoderLocator>(sp => new MediaEncoderLocator(sp.GetRequiredService<IMediaEncoder>()));
        services.TryAddSingleton<IOfflineCardEncoder>(sp => new FfmpegCardEncoder(sp.GetRequiredService<IEncoderLocator>(), sp.GetRequiredService<ILogger<FfmpegCardEncoder>>()));
        services.TryAddSingleton(sp => new OfflineCards(
            static () =>
            {
                var configuration = Plugin.Instance?.Configuration;
                return new OfflineCardSettings(configuration?.OfflineCardEnabled ?? true, configuration?.RequestServiceName ?? "Seerr", configuration?.RequestServiceUrl ?? string.Empty);
            },
            sp.GetRequiredService<IOfflineCardEncoder>(),
            Path.Combine(sp.GetRequiredService<ShoalPaths>().Root, "offline-cards"),
            sp.GetService<TimeProvider>() ?? TimeProvider.System));
        return services;
    }
}
