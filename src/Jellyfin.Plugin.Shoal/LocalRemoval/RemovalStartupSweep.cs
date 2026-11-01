using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Shoal.LocalRemoval;

/// <summary>
/// 2.10.3 (W20-5): once at plugin start, classifies the permanent deletes a previous process left unfinished (a crash, a kill, a power cut), so Restore
/// and new removals are not blocked until someone edits files by hand. See <see cref="RemovalCoordinator.SweepInterruptedAsync"/>; it never deletes.
/// </summary>
public sealed class RemovalStartupSweep : BackgroundService
{
    private readonly RemovalCoordinator _coordinator;
    private readonly ILogger<RemovalStartupSweep> _logger;

    /// <summary>Initializes a new instance of the <see cref="RemovalStartupSweep"/> class.</summary>
    /// <param name="coordinator">The single removal coordinator.</param>
    /// <param name="logger">The logger (counts only, never paths).</param>
    public RemovalStartupSweep(RemovalCoordinator coordinator, ILogger<RemovalStartupSweep> logger)
    {
        _coordinator = coordinator;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            var classified = await _coordinator.SweepInterruptedAsync(stoppingToken).ConfigureAwait(false);
            if (classified > 0)
            {
                _logger.LogInformation("Shoal checked {Count} permanent deletions an earlier start left unfinished", classified);
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or OperationCanceledException)
        {
            _logger.LogWarning("Shoal could not check unfinished permanent deletions at start ({Error}); a Restore tries again", ex.GetType().Name);
        }
    }
}
