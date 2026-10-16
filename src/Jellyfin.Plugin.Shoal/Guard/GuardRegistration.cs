using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.Api.Admin;
using Jellyfin.Plugin.Shoal.Authorization;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Shoal.Guard;

/// <summary><see cref="IGuardItems"/> over Jellyfin's library manager.</summary>
public sealed class JellyfinGuardItems : IGuardItems
{
    private readonly ILibraryManager _library;
    private readonly IVisibleItems _visible;

    /// <summary>
    /// Initializes a new instance of the <see cref="JellyfinGuardItems"/> class.
    /// </summary>
    /// <param name="library">The library manager.</param>
    /// <param name="visible">The user's own view of the library (the only authorization question).</param>
    public JellyfinGuardItems(ILibraryManager library, IVisibleItems visible)
    {
        _library = library;
        _visible = visible;
    }

    /// <inheritdoc />
    public bool? IsShared(Guid itemId, IReadOnlySet<Guid> sharedLibraryIds)
    {
#pragma warning disable RS0030 // Allowed here only: classification of an item by its libraries, never authorization (DESIGN 8.4, 8.8).
        var item = _library.GetItemById(itemId);
#pragma warning restore RS0030
        if (item is null)
        {
            return null;
        }

        return _library.GetCollectionFolders(item).Any(f => sharedLibraryIds.Contains(f.Id)) || sharedLibraryIds.Contains(item.Id);
    }

    /// <inheritdoc />
    public bool UserCanSee(Guid itemId, Guid userId) => _visible.IsVisible(userId, itemId);
}

/// <summary><see cref="ITranscodeProbe"/> over Jellyfin's transcode manager.</summary>
public sealed class JellyfinTranscodeProbe : ITranscodeProbe
{
    private readonly ITranscodeManager _transcodes;

    /// <summary>
    /// Initializes a new instance of the <see cref="JellyfinTranscodeProbe"/> class.
    /// </summary>
    /// <param name="transcodes">The transcode manager.</param>
    public JellyfinTranscodeProbe(ITranscodeManager transcodes) => _transcodes = transcodes;

    /// <inheritdoc />
    public bool IsTranscoding(string playSessionId) => _transcodes.GetTranscodingJob(playSessionId) is not null;
}

/// <summary>Logs, once at startup, how the running server's anonymous item routes differ from the checked-in table (DESIGN 8.4 job a).</summary>
public sealed class GuardStartup : IHostedService
{
    private readonly Microsoft.AspNetCore.Mvc.Infrastructure.IActionDescriptorCollectionProvider _actions;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<GuardStartup> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="GuardStartup"/> class.
    /// </summary>
    /// <param name="actions">The MVC action descriptors (what the guard filter sees on each request).</param>
    /// <param name="lifetime">The host lifetime (the check runs once the server has started).</param>
    /// <param name="logger">The logger.</param>
    public GuardStartup(Microsoft.AspNetCore.Mvc.Infrastructure.IActionDescriptorCollectionProvider actions, IHostApplicationLifetime lifetime, ILogger<GuardStartup> logger)
    {
        _actions = actions;
        _lifetime = lifetime;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _lifetime.ApplicationStarted.Register(Check);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private void Check()
    {
        try
        {
            var diff = GuardRoutes.Diff(GuardRoutes.Compute(_actions.ActionDescriptors.Items), GuardRoutes.Expected);
            if (diff.Count > 0)
            {
                // An error with a fixed marker: the lab test greps for it, and a new anonymous route is a reason to review before Enforce is relied on.
                _logger.LogError("SHOAL-GUARD-ROUTE-DIFF: the anonymous item routes of this Jellyfin differ from the table Shoal was built against: {Differences}", string.Join(' ', diff));
            }
        }
#pragma warning disable CA1031 // The check is informational; it must never stop the server from starting.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _logger.LogWarning("The anonymous route check could not run: {Error}", ex.GetType().Name);
        }
    }
}

/// <summary>Registers the guard (one call from the plugin's registrator).</summary>
public static class GuardRegistration
{
    /// <summary>Adds the guard's services and the global filter.</summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same collection.</returns>
    public static IServiceCollection AddShoalGuard(this IServiceCollection services)
    {
        services.AddSingleton<GuardShares>();
        services.AddSingleton<GuardCounters>();
        services.AddSingleton<IGuardCounters>(sp => sp.GetRequiredService<GuardCounters>());
        services.AddSingleton<SharedItemCache>();
        services.AddSingleton<IGuardItems, JellyfinGuardItems>();
        services.AddSingleton<ITranscodeProbe, JellyfinTranscodeProbe>();
        services.AddHostedService<GuardStartup>();
        services.Configure<MvcOptions>(options => options.Filters.Add<PeerGuardFilter>());
        return services;
    }
}
