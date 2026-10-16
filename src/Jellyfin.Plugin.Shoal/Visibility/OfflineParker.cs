using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.Peers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Shoal.Visibility;

/// <summary>
/// Told by the health monitor what it found about a peer, so the libraries of a peer that stays offline can be hidden (<c>ParkOfflinePeers</c>).
/// </summary>
public interface IOfflineParking
{
    /// <summary>
    /// Records one health observation.
    /// </summary>
    /// <param name="peerKey">The peer.</param>
    /// <param name="status">What the check found.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>A task that completes when any hiding or showing is done.</returns>
    Task ObserveAsync(string peerKey, PeerStatus status, CancellationToken cancellationToken);

    /// <summary>
    /// Shows again the libraries of every hidden peer that is no longer an enabled peer, and of all of them when the setting is off.
    /// </summary>
    /// <param name="enabledPeerKeys">The peers that are enabled right now.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>A task that completes when done.</returns>
    Task SweepAsync(IReadOnlyCollection<string> enabledPeerKeys, CancellationToken cancellationToken);
}

/// <summary>
/// The settings the parking works from.
/// </summary>
/// <param name="Enabled">Whether offline peers are hidden at all (<c>ParkOfflinePeers</c>, off by default).</param>
/// <param name="AfterHours">How long a peer must be offline without a break before it is hidden; fractions allowed.</param>
public sealed record ParkingSettings(bool Enabled, double AfterHours);

/// <summary>
/// Hides the libraries of a peer that has been offline for a sustained time and shows them again after it has been healthy for a while. A peer that flaps is neither
/// hidden (any healthy check restarts the offline clock) nor shown again too early (any offline check restarts the healthy clock), so it does not churn. The record of
/// what was hidden is in the state, so a restart in between loses nothing; the clocks themselves start over after a restart, which only delays things.
/// </summary>
public sealed class OfflineParker : IOfflineParking
{
    /// <summary>How long a peer must stay healthy before its libraries come back.</summary>
    public static readonly TimeSpan RestoreAfter = TimeSpan.FromMinutes(10);

    private readonly VisibilityService _visibility;
    private readonly Func<ParkingSettings> _settings;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, Clock> _clocks = new(StringComparer.Ordinal);

    /// <summary>
    /// Initializes a new instance of the <see cref="OfflineParker"/> class.
    /// </summary>
    /// <param name="visibility">Hides and shows the libraries.</param>
    /// <param name="time">The clock.</param>
    /// <param name="logger">The logger.</param>
    public OfflineParker(VisibilityService visibility, TimeProvider time, ILogger<OfflineParker> logger)
        : this(visibility, static () => new ParkingSettings(Plugin.Instance?.Configuration.ParkOfflinePeers ?? false, Plugin.Instance?.Configuration.ParkAfterHours ?? 0.5), time, logger)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="OfflineParker"/> class (tests: own settings and any logger).
    /// </summary>
    /// <param name="visibility">Hides and shows the libraries.</param>
    /// <param name="settings">The settings in force.</param>
    /// <param name="time">The clock.</param>
    /// <param name="logger">The logger.</param>
    internal OfflineParker(VisibilityService visibility, Func<ParkingSettings> settings, TimeProvider time, ILogger logger)
    {
        _visibility = visibility;
        _settings = settings;
        _time = time;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task ObserveAsync(string peerKey, PeerStatus status, CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow();
        var settings = _settings();
        var clock = _clocks.GetOrAdd(peerKey, static _ => new Clock());
        bool offlineLongEnough;
        bool healthyLongEnough;
        lock (clock)
        {
            if (status == PeerStatus.Offline)
            {
                clock.HealthySince = null;
                clock.OfflineSince ??= now;
            }
            else if (status == PeerStatus.Online)
            {
                clock.OfflineSince = null;
                clock.HealthySince ??= now;
            }

            // Anything else (degraded, a stop that needs the admin, pairing) says nothing about whether the server is reachable: both clocks stand.
            offlineLongEnough = clock.OfflineSince is { } off && now - off >= TimeSpan.FromHours(Math.Max(0.05, settings.AfterHours));
            healthyLongEnough = clock.HealthySince is { } ok && now - ok >= RestoreAfter;
        }

        var parked = _visibility.IsParked(peerKey);
        try
        {
            if (!parked && settings.Enabled && offlineLongEnough)
            {
                await _visibility.ParkPeerAsync(peerKey, cancellationToken).ConfigureAwait(false);
            }
            else if (parked && (!settings.Enabled || healthyLongEnough))
            {
                await _visibility.UnparkPeerAsync(peerKey, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning("Hiding or showing the libraries of a server failed ({Error})", ex.GetType().Name);
        }
    }

    /// <inheritdoc />
    public async Task SweepAsync(IReadOnlyCollection<string> enabledPeerKeys, CancellationToken cancellationToken)
    {
        var enabled = _settings().Enabled;
        foreach (var key in _visibility.ParkedPeerKeys())
        {
            if (enabled && enabledPeerKeys.Contains(key, StringComparer.Ordinal))
            {
                continue;
            }

            try
            {
                await _visibility.UnparkPeerAsync(key, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning("Showing the libraries of a server again failed ({Error})", ex.GetType().Name);
            }
        }
    }

    private sealed class Clock
    {
        public DateTimeOffset? OfflineSince { get; set; }

        public DateTimeOffset? HealthySince { get; set; }
    }
}
