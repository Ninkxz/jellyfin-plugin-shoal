using Jellyfin.Plugin.Shoal.Peers;
using Jellyfin.Plugin.Shoal.Sharing;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.Shoal.Registrations;

/// <summary>
/// The services of token rotation (DESIGN v2.6.1): the Peer side (K25) and Home's side (K26). The call already stands in
/// <see cref="PluginServiceRegistrator"/> (DESIGN v2.6.9).
/// </summary>
public static class Rotation
{
    /// <summary>
    /// Registers the services of this feature.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same collection.</returns>
    public static IServiceCollection AddShoalRotation(this IServiceCollection services)
    {
        services.AddSingleton<ShareLocks>();
        services.AddSingleton<ShareRotation>();
        services.AddSingleton<IActiveTokenSource>(sp => sp.GetRequiredService<PeerDirectory>());
        services.AddSingleton<TokenRotation>();
        services.AddSingleton<RotationScheduler>();
        services.AddHostedService(sp => sp.GetRequiredService<RotationScheduler>());
        return services;
    }
}
