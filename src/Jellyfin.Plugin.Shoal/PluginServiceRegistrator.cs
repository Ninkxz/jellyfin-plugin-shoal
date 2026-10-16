using System;
using Jellyfin.Data.Events.Users;
using Jellyfin.Plugin.Shoal.Api.Admin;
using Jellyfin.Plugin.Shoal.Authorization;
using Jellyfin.Plugin.Shoal.Guard;
using Jellyfin.Plugin.Shoal.Pairing;
using Jellyfin.Plugin.Shoal.Peers;
using Jellyfin.Plugin.Shoal.Playback;
using Jellyfin.Plugin.Shoal.Registrations;
using Jellyfin.Plugin.Shoal.Security;
using Jellyfin.Plugin.Shoal.State;
using Jellyfin.Plugin.Shoal.Storage;
using Jellyfin.Plugin.Shoal.Sync;
using Jellyfin.Plugin.Shoal.Visibility;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Events;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Controller.Session;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Shoal;

/// <summary>
/// Registers the plugin's services and the global MVC filter that takes over PlaybackInfo for Shoal items.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        // Shoal's own stores (DESIGN 3.7, 8.2). Nothing in the plugin XML is critical any more.
        serviceCollection.AddSingleton(sp => new ShoalPaths(sp.GetRequiredService<IApplicationPaths>().DataPath));
        serviceCollection.AddSingleton<IStateStore, StateStore>();
        serviceCollection.AddSingleton<ISecretStore, SecretStore>();
        serviceCollection.AddSingleton<PeerDirectory>();
        serviceCollection.AddSingleton<IPeerEndpoints>(sp => sp.GetRequiredService<PeerDirectory>());
        serviceCollection.AddSingleton<PeerAddressService>();
        serviceCollection.AddSingleton<ReconciliationService>();
        serviceCollection.AddHostedService<StartupReport>();

        // The admin page's API (DESIGN 9.6). IPeerLiveStatus is the seam the health monitor and the sync service plug into (K11, K20);
        // until they exist, nothing has been measured and nothing is syncing.
        serviceCollection.TryAddSingleton(TimeProvider.System);
        serviceCollection.AddSingleton<AdminCooldowns>();
        serviceCollection.AddSingleton<IHomeNameSource, ServerConfigHomeName>();
        serviceCollection.TryAddSingleton<IPeerLiveStatus, StateBackedLiveStatus>();
        serviceCollection.AddSingleton<ISettingsAccessor, PluginSettingsAccessor>();
        serviceCollection.AddSingleton<IStubRootChecker, StubRootChecker>();
        serviceCollection.AddSingleton<StubRootInitializer>();
        serviceCollection.AddHostedService(sp => sp.GetRequiredService<StubRootInitializer>());
        serviceCollection.AddShoalGuard();
        Jellyfin.Plugin.Shoal.Diagnostics.DiagnosticsRegistration.AddShoalDiagnostics(serviceCollection);
        serviceCollection.AddSingleton<IFindingsSource, ReconciliationFindings>();

        serviceCollection.AddSingleton<PeerRegistry>();
        serviceCollection.AddSingleton<PeerClient>();
        serviceCollection.AddSingleton<ICallerUsers, JellyfinCallerUsers>();
        serviceCollection.AddSingleton<IVisibleItems, LibraryVisibleItems>();
        serviceCollection.AddSingleton<IItemPaths, JellyfinItemPaths>();
        serviceCollection.AddSingleton<ShoalIndex>();
        serviceCollection.AddSingleton<IFederatedItems>(sp => sp.GetRequiredService<ShoalIndex>());
        serviceCollection.AddHostedService<ShoalIndexHost>();
        serviceCollection.AddSingleton<StubCatalog>();
        serviceCollection.AddSingleton<IStubItems, JellyfinStubItems>();
        serviceCollection.AddSingleton<StubEnricher>();
        serviceCollection.AddScoped<CallerContext>();
        serviceCollection.AddSingleton<PeerTrust>();
        serviceCollection.AddSingleton<PeerHealthStore>();
        serviceCollection.AddSingleton<IHomeIdentity, ServerHostIdentity>();
        serviceCollection.AddSingleton<IAdminNotifier, SessionAdminNotifier>();
        serviceCollection.AddSingleton<PairingService>();
        serviceCollection.AddSingleton<IPairingGate>(sp => sp.GetRequiredService<PairingService>());
        serviceCollection.AddHostedService(sp => sp.GetRequiredService<PairingService>());
        serviceCollection.AddSingleton<IRatingTable, JellyfinRatingTable>();
        serviceCollection.AddSingleton<MetadataSanitizer>();
        serviceCollection.AddSingleton<ImageFetcher>();
        serviceCollection.AddSingleton<CatalogStore>();
        serviceCollection.AddSingleton<PeerCatalogReader>();
        serviceCollection.AddSingleton<StubWriter>();
        serviceCollection.AddSingleton<ILocalMovies, JellyfinLocalMovies>();
        serviceCollection.AddSingleton<ILibraryRefresh, JellyfinLibraryRefresh>();
        serviceCollection.AddSingleton<CatalogSyncService>();
        serviceCollection.AddShoalTv();
        serviceCollection.AddShoalTvSync();
        serviceCollection.AddSingleton<ISyncActivity>(sp => sp.GetRequiredService<CatalogSyncService>());
        serviceCollection.AddHostedService<SyncScheduler>();
        serviceCollection.AddSingleton<DedupSettler>();
        serviceCollection.AddHostedService(sp => sp.GetRequiredService<DedupSettler>());

        // Who may see a Shoal library (DESIGN 5.6).
        serviceCollection.TryAddSingleton(TimeProvider.System);
        serviceCollection.AddSingleton<ILibrarySource, JellyfinLibrarySource>();
        serviceCollection.AddSingleton<IAccountSource, JellyfinAccountSource>();
        serviceCollection.AddSingleton<VisibilityService>();
        serviceCollection.AddSingleton<AccessService>();
        serviceCollection.AddSingleton<ILibraryView, AdminLibraryView>();
        serviceCollection.AddSingleton<Jellyfin.Plugin.Shoal.Sharing.IShareUserSource, Jellyfin.Plugin.Shoal.Sharing.JellyfinShareUsers>();
        serviceCollection.AddSingleton<Jellyfin.Plugin.Shoal.Sharing.InviteBook>();
        serviceCollection.AddSingleton<Jellyfin.Plugin.Shoal.Sharing.IShareInvites>(sp => sp.GetRequiredService<Jellyfin.Plugin.Shoal.Sharing.InviteBook>());
        serviceCollection.AddSingleton<Jellyfin.Plugin.Shoal.Sharing.ShareService>();
        serviceCollection.AddSingleton<IShareService>(sp => sp.GetRequiredService<Jellyfin.Plugin.Shoal.Sharing.ShareService>());
        serviceCollection.AddSingleton<Jellyfin.Plugin.Shoal.Sharing.IShareLiveSource>(sp => sp.GetRequiredService<Jellyfin.Plugin.Shoal.Sharing.ShareService>());
        serviceCollection.AddHostedService<Jellyfin.Plugin.Shoal.Sharing.ShareSweeper>();
        serviceCollection.AddSingleton<ShoalLibraryService>();
        serviceCollection.AddSingleton<UserCreationWatch>();
        serviceCollection.AddSingleton<IEventConsumer<UserCreatedEventArgs>, UserCreatedConsumer>();
        serviceCollection.AddHostedService<VisibilityHost>();

        // The make-or-break mechanism of the spike: a plugin-registered MVC filter that sees
        // POST /Items/{id}/PlaybackInfo before the core MediaInfoController does.
        serviceCollection.AddSingleton<InterceptCounters>();
        serviceCollection.AddSingleton<NotYetImplementedHandlers>();
        serviceCollection.AddSingleton(sp => new StreamSlots(sp.GetRequiredService<TimeProvider>(), () => Plugin.Instance?.Configuration.MaxStreamsPerUser ?? 4));
        serviceCollection.AddSingleton<ISlotLedger>(sp => sp.GetRequiredService<StreamSlots>());
        serviceCollection.AddSingleton<GrantStore>();
        serviceCollection.AddSingleton(sp => new RateLimiter(sp.GetRequiredService<TimeProvider>(), perMinute: 30, burst: 15));
        serviceCollection.AddSingleton<IDeviceCapabilities, JellyfinDeviceCapabilities>();
        Jellyfin.Plugin.Shoal.Diagnostics.DiagnosticsRegistration.AddLinkBudget(serviceCollection);
        serviceCollection.AddSingleton<IOfflineNotice>(sp => new ClientOfflineNotice(sp.GetRequiredService<ISessionManager>(), () => Plugin.Instance?.Configuration.NotifyClientsWhenOffline ?? true, sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<ILogger<ClientOfflineNotice>>(), () => (Plugin.Instance?.Configuration.RequestServiceName ?? "Seerr", Plugin.Instance?.Configuration.RequestServiceUrl ?? string.Empty)));
        serviceCollection.AddShoalOfflineCards();
        serviceCollection.AddSingleton<OfflineParker>();
        serviceCollection.AddSingleton<Jellyfin.Plugin.Shoal.Visibility.IOfflineParking>(sp => sp.GetRequiredService<Jellyfin.Plugin.Shoal.Visibility.OfflineParker>());
        serviceCollection.AddSingleton<HealthMonitor>();
        serviceCollection.AddHostedService<HealthMonitorHost>();
        serviceCollection.AddSingleton<IPlaybackInfoHandler>(sp => new PlaybackInfoHandler(
            sp.GetRequiredService<GrantStore>(),
            sp.GetRequiredService<RateLimiter>(),
            sp.GetRequiredService<IPeerEndpoints>(),
            sp.GetRequiredService<PeerClient>(),
            sp.GetRequiredService<PeerTrust>(),
            sp.GetRequiredService<PeerRegistry>(),
            sp.GetRequiredService<StubCatalog>(),
            sp.GetRequiredService<IStateStore>(),
            sp.GetRequiredService<IDeviceCapabilities>(),
            sp.GetRequiredService<ISystemLimits>(),
            sp.GetRequiredService<IOfflineNotice>(),
            sp.GetRequiredService<IAdminNotifier>(),
            sp.GetRequiredService<ISlotLedger>(),
            () => Plugin.Instance?.Configuration.HlsSubtitlesEnabled ?? true,
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILogger<PlaybackInfoHandler>>(),
            sp.GetService<IAttachmentRebuilder>(),
            sp.GetService<IVersionResolver>(),
            sp.GetService<OfflineCards>()));
        serviceCollection.AddSingleton<IStaticStreamHandler>(sp => new StaticStreamHandler(
            sp.GetRequiredService<StubCatalog>(),
            sp.GetRequiredService<IPeerEndpoints>(),
            sp.GetRequiredService<PeerClient>(),
            sp.GetRequiredService<PeerTrust>(),
            sp.GetRequiredService<PeerRegistry>(),
            sp.GetRequiredService<StreamSlots>(),
            new RateLimiter(sp.GetRequiredService<TimeProvider>(), perMinute: 1200, burst: 100),
            sp.GetRequiredService<IStateStore>(),
            sp.GetRequiredService<IOfflineNotice>(),
            sp.GetRequiredService<ILogger<StaticStreamHandler>>(),
            sp.GetService<IVersionResolver>(),
            sp.GetService<OfflineCards>(),
            new ReadAheadBudget(64L * 1024 * 1024))); // direct play: 4 MB read ahead per stream, 64 MB for all of them
        serviceCollection.AddSingleton(sp => new PeerExtractionGate(sp.GetRequiredService<TimeProvider>()));
        serviceCollection.AddSingleton(sp => new SubtitleHandler(
            sp.GetRequiredService<GrantStore>(),
            sp.GetRequiredService<StubCatalog>(),
            sp.GetRequiredService<IPeerEndpoints>(),
            sp.GetRequiredService<PeerClient>(),
            sp.GetRequiredService<PeerTrust>(),
            sp.GetRequiredService<PeerRegistry>(),
            sp.GetRequiredService<IStateStore>(),
            sp.GetRequiredService<IOfflineNotice>(),
            sp.GetRequiredService<PeerExtractionGate>(),
            new RateLimiter(sp.GetRequiredService<TimeProvider>(), perMinute: 30, burst: 15),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILogger<SubtitleHandler>>(),
            sp.GetService<IVersionResolver>()));
        serviceCollection.AddSingleton<ISubtitleHandler>(sp => sp.GetRequiredService<SubtitleHandler>());
        serviceCollection.AddSingleton<HlsJobControl>();
        serviceCollection.AddSingleton<PeerSessionReporter>();
        serviceCollection.AddHostedService<SessionReporterHost>();
        serviceCollection.AddSingleton<IActiveEncodingsHandler>(sp => new ActiveEncodingsHandler(
            sp.GetRequiredService<GrantStore>(),
            sp.GetRequiredService<HlsJobControl>(),
            sp.GetRequiredService<StreamSlots>(),
            new RateLimiter(sp.GetRequiredService<TimeProvider>(), perMinute: 10, burst: 10)));
        serviceCollection.AddSingleton(sp =>
        {
            var grants = sp.GetRequiredService<GrantStore>();
            var slots = sp.GetRequiredService<StreamSlots>();
            var peers = sp.GetRequiredService<IPeerEndpoints>();
            var registry = sp.GetRequiredService<PeerRegistry>();
            var readAhead = new HlsReadAhead(
                () => Plugin.Instance?.Configuration.HlsBufferAhead ?? 6,
                id => grants.Get(id) is { } grant && slots.HoldsOwnSlot(grant) && peers.Find(grant.PeerKey) is not null && registry.StatusOf(grant.PeerKey) is not (PeerStatus.AuthFailed or PeerStatus.IdentityMismatch or PeerStatus.TlsKeyChanged or PeerStatus.Disabled or PeerStatus.Pairing or PeerStatus.PairingAborted),
                sp.GetRequiredService<TimeProvider>(),
                sp.GetRequiredService<ILogger<HlsReadAhead>>());
            slots.HlsReleased += readAhead.Release; // a stopped or freed stream keeps nothing
            registry.PeerStopped += readAhead.ReleasePeer; // nor does a server that was removed, stopped, or whose key is being replaced
            slots.HlsSuperseded += ids =>
            {
                foreach (var id in ids)
                {
                    readAhead.Release(id);
                }
            };
            return readAhead;
        });
        serviceCollection.AddSingleton(sp => new HlsProxyHandler(
            sp.GetRequiredService<GrantStore>(),
            sp.GetRequiredService<IPeerEndpoints>(),
            sp.GetRequiredService<PeerClient>(),
            sp.GetRequiredService<PeerTrust>(),
            sp.GetRequiredService<PeerRegistry>(),
            sp.GetRequiredService<StreamSlots>(),
            sp.GetRequiredService<IStateStore>(),
            sp.GetRequiredService<IOfflineNotice>(),
            sp.GetRequiredService<SubtitleHandler>(),
            new RateLimiter(sp.GetRequiredService<TimeProvider>(), perMinute: 600, burst: 40),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILogger<HlsProxyHandler>>(),
            sp.GetRequiredService<Jellyfin.Plugin.Shoal.Diagnostics.ISegmentTelemetry>(),
            sp.GetRequiredService<HlsReadAhead>()));

        // Phase 5 and 6 (DESIGN v2.6.9): the seams with their do-nothing defaults, then one call per feature. Each task fills in its own file under Registrations/.
        serviceCollection.AddShoalFoundation();
        serviceCollection.AddShoalRotation();
        serviceCollection.AddShoalAlternateVersions();
        serviceCollection.AddShoalTrickplay();
        serviceCollection.AddShoalAttachments();
        serviceCollection.AddShoalImageSandbox();
        serviceCollection.Configure<MvcOptions>(options => options.Filters.Add<ShoalInterceptFilter>());
    }
}
