using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Shoal.Sync;

/// <summary>
/// Runs the catalog sync on its interval (DESIGN 5.9): the first run 10 minutes after startup, then every <c>SyncIntervalHours</c>; when a
/// library scan is running the sync waits 10 minutes. There is no other startup trigger.
/// </summary>
public sealed class SyncScheduler : BackgroundService
{
    private static readonly TimeSpan FirstDelay = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan ScanRetry = TimeSpan.FromMinutes(10);

    private readonly CatalogSyncService _sync;
    private readonly ILogger<SyncScheduler> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="SyncScheduler"/> class.
    /// </summary>
    /// <param name="sync">The sync service.</param>
    /// <param name="logger">The logger.</param>
    public SyncScheduler(CatalogSyncService sync, ILogger<SyncScheduler> logger)
    {
        _sync = sync;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(FirstDelay, stoppingToken).ConfigureAwait(false);
            var followUps = 0;
            while (!stoppingToken.IsCancellationRequested)
            {
                var delay = TimeSpan.FromHours(Math.Clamp(Plugin.Instance?.Configuration.SyncIntervalHours ?? 6, 1, 168));
                try
                {
                    var reports = await _sync.SyncAllAsync(SyncTrigger.Scheduled, stoppingToken).ConfigureAwait(false);
                    if (reports.Any(static r => r.Error == "scanRunning"))
                    {
                        delay = ScanRetry;
                    }
                    else
                    {
                        // Images of a TV library that did not fit in the run's image budget: look again in 10 minutes, at most 12 times in a row (DESIGN v2.7.3).
                        (delay, followUps) = SyncFollowUp.Next(reports, delay, followUps);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning("The scheduled sync failed ({Error})", ex.GetType().Name);
                }

                await Task.Delay(delay, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
    }
}
