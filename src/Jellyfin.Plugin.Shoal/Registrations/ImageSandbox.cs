using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.Storage;
using Jellyfin.Plugin.Shoal.Sync;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Shoal.Registrations;

/// <summary>
/// The services of the image child process (DESIGN v2.6.7, K32). The call stands in <see cref="PluginServiceRegistrator"/> (DESIGN v2.6.9).
/// </summary>
public static class ImageSandbox
{
    /// <summary>
    /// Registers <see cref="ChildImageSanitizer"/> in place of the pass-through default, with the host's ffmpeg, and the host that runs
    /// the self-test at startup and kills a running child at shutdown.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same collection.</returns>
    public static IServiceCollection AddShoalImageSandbox(this IServiceCollection services)
    {
        services.TryAddSingleton<IEncoderLocator>(sp => new MediaEncoderLocator(sp.GetRequiredService<IMediaEncoder>()));
        services.TryAddSingleton(sp => new ChildImageSanitizer(
            sp.GetRequiredService<IEncoderLocator>(),
            () => Plugin.Instance?.Configuration.ReencodeImages ?? true,
            Path.Combine(sp.GetRequiredService<ShoalPaths>().Root, "tmp"),
            sp.GetService<TimeProvider>() ?? TimeProvider.System,
            sp.GetRequiredService<ILogger<ChildImageSanitizer>>()));
        services.Replace(ServiceDescriptor.Singleton<IImageSanitizer>(sp => sp.GetRequiredService<ChildImageSanitizer>()));
        services.TryAddSingleton<IImageSandboxStatus>(sp => sp.GetRequiredService<ChildImageSanitizer>());
        services.AddHostedService<ImageSandboxHost>();
        return services;
    }
}

/// <summary>
/// Runs the sandbox self-test in the background at startup (when the switch is on) and kills a running child at shutdown (DESIGN v2.6.7).
/// </summary>
public sealed class ImageSandboxHost : IHostedService
{
    private readonly ChildImageSanitizer _sanitizer;

    /// <summary>
    /// Initializes a new instance of the <see cref="ImageSandboxHost"/> class.
    /// </summary>
    /// <param name="sanitizer">The sanitizer.</param>
    public ImageSandboxHost(ChildImageSanitizer sanitizer) => _sanitizer = sanitizer;

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_sanitizer.GetStatus().State != "off")
        {
            _sanitizer.RequestSelfTest();
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _sanitizer.KillAll();
        return Task.CompletedTask;
    }
}
