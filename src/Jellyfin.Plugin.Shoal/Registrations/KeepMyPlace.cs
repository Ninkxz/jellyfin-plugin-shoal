using System;
using System.IO;
using Jellyfin.Plugin.Shoal.Api.Admin;
using Jellyfin.Plugin.Shoal.Progress;
using Jellyfin.Plugin.Shoal.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Jellyfin.Plugin.Shoal.Registrations;

/// <summary>Registers configurable same-user local progress retention, without a new endpoint.</summary>
public static class KeepMyPlace
{
    /// <summary>Registers capture, private storage, fresh core checks and the bounded event worker.</summary>
    /// <param name="services">Host services.</param>
    /// <returns>The same collection.</returns>
    public static IServiceCollection AddShoalKeepMyPlace(this IServiceCollection services)
    {
        services.TryAddSingleton(sp =>
        {
            var root = sp.GetRequiredService<ShoalPaths>().Root;
            SecureDirectory.Ensure(root);
            return new KeepMyPlaceStore(Path.Combine(root, "keep-my-place.json"), sp.GetService<TimeProvider>() ?? TimeProvider.System);
        });
        services.TryAddSingleton<JellyfinKeepMyPlaceLibrary>();
        services.TryAddSingleton<IKeepMyPlaceLibrary>(sp => sp.GetRequiredService<JellyfinKeepMyPlaceLibrary>());
        services.TryAddSingleton(sp => new KeepMyPlaceService(sp.GetRequiredService<IKeepMyPlaceLibrary>(), sp.GetRequiredService<KeepMyPlaceStore>(), () => sp.GetRequiredService<ISettingsAccessor>().Current.KeepMyPlaceEnabled));
        services.TryAddSingleton<IKeepMyPlaceRetention>(sp => sp.GetRequiredService<KeepMyPlaceService>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, KeepMyPlaceHost>());
        return services;
    }
}
