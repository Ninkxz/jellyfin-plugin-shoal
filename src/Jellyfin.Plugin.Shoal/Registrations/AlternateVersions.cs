using Jellyfin.Plugin.Shoal.Playback;
using Jellyfin.Plugin.Shoal.Sync;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Jellyfin.Plugin.Shoal.Registrations;

/// <summary>
/// The services of alternate versions (DESIGN v2.6.4): the source selector as the sanitizer's source hook, and the resolver that maps a version's
/// local item to its stub and Peer ids. K29 adds the playback side to <see cref="AddShoalAlternateVersions"/>. The call already stands in
/// <see cref="PluginServiceRegistrator"/> (DESIGN v2.6.9).
/// </summary>
public static class AlternateVersions
{
    /// <summary>
    /// Registers the services of this feature.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same collection.</returns>
    public static IServiceCollection AddShoalAlternateVersions(this IServiceCollection services)
    {
        services.Replace(ServiceDescriptor.Singleton<ISourceSanitizer>(new AlternateVersionSourceSanitizer(static () => Plugin.Instance?.Configuration.AlternateVersionsEnabled ?? false)));
        services.Replace(ServiceDescriptor.Singleton<IVersionResolver, ShoalVersionResolver>());
        return services;
    }
}
