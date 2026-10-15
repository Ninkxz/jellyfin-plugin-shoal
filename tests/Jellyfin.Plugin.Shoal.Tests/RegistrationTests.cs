using System;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Jellyfin.Plugin.Shoal.Tests;

/// <summary>
/// Warden 07 (Info): a service or hosted service registered twice runs twice (two sync schedulers, two health monitors). No registration may repeat.
/// </summary>
public sealed class RegistrationTests
{
    [Fact]
    public void NoServiceIsRegisteredTwice_AndNoHostedServiceRunsTwice()
    {
        var services = new ServiceCollection();

        new PluginServiceRegistrator().RegisterServices(services, null!);

        var repeated = services
            .Where(static d => d.ImplementationType is not null || d.ImplementationFactory is not null)
            .GroupBy(static d => (d.ServiceType, Impl: d.ImplementationType, Factory: d.ImplementationFactory is not null && d.ImplementationType is null ? "factory" : string.Empty))
            .Where(static g => g.Count() > 1 && g.Key.Factory.Length == 0)
            .Select(static g => g.Key.ServiceType.Name + " -> " + g.Key.Impl?.Name)
            .ToList();
        var hosted = services
            .Where(static d => d.ServiceType == typeof(Microsoft.Extensions.Hosting.IHostedService))
            .Select(static d => d.ImplementationType?.Name ?? "factory")
            .GroupBy(static n => n)
            .Where(static g => g.Key != "factory" && g.Count() > 1)
            .Select(static g => g.Key)
            .ToList();

        Assert.True(repeated.Count == 0, string.Join(", ", repeated));
        Assert.True(hosted.Count == 0, string.Join(", ", hosted));
    }
}
