using System;
using Jellyfin.Plugin.Shoal.Authorization;
using Jellyfin.Plugin.Shoal.Peers;
using Jellyfin.Plugin.Shoal.Playback;
using Jellyfin.Plugin.Shoal.State;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Shoal.Registrations;

/// <summary>
/// The services of attachments and fonts (DESIGN v2.6.6, K31). The call stands in <see cref="PluginServiceRegistrator"/> (DESIGN v2.6.9).
/// </summary>
public static class Attachments
{
    /// <summary>
    /// Registers the attachment rebuilder and the font handler in place of the do-nothing defaults. Both read the <c>AttachmentsEnabled</c> switch on
    /// every call, so it is a real kill switch.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same collection.</returns>
    public static IServiceCollection AddShoalAttachments(this IServiceCollection services)
    {
        services.Replace(ServiceDescriptor.Singleton<IAttachmentRebuilder>(_ => new AttachmentRebuilder(static () => Plugin.Instance?.Configuration.AttachmentsEnabled ?? true)));
        services.Replace(ServiceDescriptor.Singleton<IAttachmentHandler>(sp =>
        {
            var time = sp.GetService<TimeProvider>() ?? TimeProvider.System;
            return new AttachmentHandler(
                sp.GetRequiredService<GrantStore>(),
                sp.GetRequiredService<IVersionResolver>(),
                sp.GetRequiredService<IPeerEndpoints>(),
                sp.GetRequiredService<PeerClient>(),
                sp.GetRequiredService<PeerTrust>(),
                sp.GetRequiredService<PeerRegistry>(),
                sp.GetRequiredService<IStateStore>(),
                sp.GetRequiredService<PeerExtractionGate>(),
                sp.GetRequiredService<InterceptCounters>(),
                new RateLimiter(time, perMinute: 60, burst: 60),
                static () => Plugin.Instance?.Configuration.AttachmentsEnabled ?? true,
                time,
                sp.GetRequiredService<ILogger<AttachmentHandler>>());
        }));
        return services;
    }
}
