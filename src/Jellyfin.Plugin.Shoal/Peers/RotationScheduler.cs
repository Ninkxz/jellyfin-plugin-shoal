using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.State;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Shoal.Peers;

/// <summary>
/// The optional rotation schedule (DESIGN v2.6.1): once an hour, every invite-paired peer whose <c>rotationDays</c> is 30, 60 or 90 and is due gets
/// a rotation when its preconditions hold. A peer that stays blocked for 24 hours is reported as overdue. The default is off for every peer.
/// </summary>
public sealed class RotationScheduler : BackgroundService
{
    /// <summary>How often the schedule is looked at.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    private static readonly TimeSpan StartDelay = TimeSpan.FromMinutes(5);

    private readonly IStateStore _state;
    private readonly TokenRotation _rotation;
    private readonly ILogger<RotationScheduler> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="RotationScheduler"/> class.
    /// </summary>
    /// <param name="state">The state store.</param>
    /// <param name="rotation">The rotation service.</param>
    /// <param name="logger">The logger.</param>
    public RotationScheduler(IStateStore state, TokenRotation rotation, ILogger<RotationScheduler> logger)
    {
        _state = state;
        _rotation = rotation;
        _logger = logger;
    }

    /// <summary>
    /// Runs one pass over the peers (also used by the tests).
    /// </summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>A task that completes when every due peer was tried.</returns>
    public async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        if (_state.Status != StateStatus.Ready)
        {
            return;
        }

        foreach (var peer in _state.Current.Home.Peers.Where(_rotation.IsDue).ToList())
        {
            var result = await _rotation.RotateAsync(peer.PeerKey, RotationTrigger.Scheduled, cancellationToken).ConfigureAwait(false);
            _rotation.NoteScheduled(peer.PeerKey, result.Error is RotateError.NotOnline or RotateError.StreamsActive or RotateError.Busy);
        }
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(StartDelay, stoppingToken).ConfigureAwait(false);
            using var timer = new PeriodicTimer(Interval);
            do
            {
                try
                {
                    await RunOnceAsync(stoppingToken).ConfigureAwait(false);
                }
#pragma warning disable CA1031 // One bad pass must not stop the schedule.
                catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
                {
                    _logger.LogWarning("The rotation schedule failed this hour ({Error})", ex.GetType().Name);
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            // stopping
        }
    }
}
