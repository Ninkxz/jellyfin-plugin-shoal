using Jellyfin.Plugin.Shoal.Playback;
using Jellyfin.Plugin.Shoal.Sync;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Jellyfin.Plugin.Shoal.Registrations;

/// <summary>
/// The v2.6 seams and their do-nothing defaults (DESIGN v2.6.9, K24). Each phase 5 and 6 task replaces one default from its own
/// <c>Registrations/&lt;Feature&gt;.cs</c>, which runs after this one, so no task edits <see cref="PluginServiceRegistrator"/>.
/// </summary>
public static class Foundation
{
    /// <summary>
    /// Registers the defaults: no trickplay or attachments served (404), every item a set of one version, no attachments listed, images
    /// passed through, sources and trickplay levels kept as the sanitizer found them.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same collection.</returns>
    public static IServiceCollection AddShoalFoundation(this IServiceCollection services)
    {
        services.TryAddSingleton<NotServedHandlers>();
        services.TryAddSingleton<ITrickplayHandler>(sp => sp.GetRequiredService<NotServedHandlers>());
        services.TryAddSingleton<IAttachmentHandler>(sp => sp.GetRequiredService<NotServedHandlers>());
        services.TryAddSingleton<IVersionResolver, OwnSourceVersionResolver>();
        services.TryAddSingleton<IAttachmentRebuilder>(NoAttachments.Instance);
        services.TryAddSingleton<IImageSanitizer, PassThroughImageSanitizer>();
        services.TryAddSingleton<ISourceSanitizer>(KeepSources.Instance);
        services.TryAddSingleton<ITrickplaySanitizer>(NoTrickplay.Instance);
        return services;
    }
}
