using System;

namespace Jellyfin.Plugin.Shoal.Peers;

/// <summary>
/// The per-peer circuit breaker (DESIGN 6.10): three consecutive connect, timeout or 5xx failures open it. While open, nothing is
/// sent except one half-open probe (a <c>GET /System/Ping</c>) at 30 s, then 60, 120 and so on, capped at 300 s; a successful probe
/// closes it. 401 and 403 are not failures here (the peer goes to <c>AuthFailed</c> instead).
/// </summary>
public sealed class CircuitBreaker
{
    /// <summary>Consecutive failures that open the breaker.</summary>
    public const int OpenAfterFailures = 3;

    private static readonly TimeSpan FirstProbeDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MaxProbeDelay = TimeSpan.FromSeconds(300);

    private readonly TimeProvider _time;
    private readonly object _lock = new();
    private int _failures;
    private bool _open;
    private bool _probeInFlight;
    private int _probeStep;
    private DateTimeOffset _nextProbe;

    /// <summary>
    /// Initializes a new instance of the <see cref="CircuitBreaker"/> class.
    /// </summary>
    /// <param name="time">The clock.</param>
    public CircuitBreaker(TimeProvider time) => _time = time;

    /// <summary>Gets a value indicating whether the breaker is open.</summary>
    public bool IsOpen
    {
        get
        {
            lock (_lock)
            {
                return _open;
            }
        }
    }

    /// <summary>Gets the number of failures in a row.</summary>
    public int ConsecutiveFailures
    {
        get
        {
            lock (_lock)
            {
                return _failures;
            }
        }
    }

    /// <summary>Gets when the next probe is due while the breaker is open, otherwise null.</summary>
    public DateTimeOffset? NextProbeUtc
    {
        get
        {
            lock (_lock)
            {
                return _open ? _nextProbe : null;
            }
        }
    }

    /// <summary>
    /// Asks whether a request may be sent now.
    /// </summary>
    /// <param name="isProbe">True for the health check's <c>GET /System/Ping</c>, the only request that may go while open.</param>
    /// <returns>True when the request may be sent. A true answer for a probe must be followed by <see cref="RecordSuccess"/>, <see cref="RecordFailure"/> or <see cref="ReleaseProbe"/>.</returns>
    public bool TryAcquire(bool isProbe)
    {
        lock (_lock)
        {
            if (!_open)
            {
                return true;
            }

            if (isProbe && !_probeInFlight && _time.GetUtcNow() >= _nextProbe)
            {
                _probeInFlight = true;
                return true;
            }

            return false;
        }
    }

    /// <summary>Records an answer that is not a failure (any status below 500).</summary>
    public void RecordSuccess()
    {
        lock (_lock)
        {
            _failures = 0;
            _open = false;
            _probeInFlight = false;
            _probeStep = 0;
        }
    }

    /// <summary>Records a connect failure, a timeout or a 5xx answer.</summary>
    public void RecordFailure()
    {
        lock (_lock)
        {
            _failures++;
            if (_open)
            {
                _probeInFlight = false;
                _probeStep++;
                ScheduleNextProbe();
            }
            else if (_failures >= OpenAfterFailures)
            {
                _open = true;
                _probeStep = 0;
                ScheduleNextProbe();
            }
        }
    }

    /// <summary>Gives back a probe slot without counting it (the probe was cancelled by the caller).</summary>
    public void ReleaseProbe()
    {
        lock (_lock)
        {
            _probeInFlight = false;
        }
    }

    private void ScheduleNextProbe()
    {
        var seconds = Math.Min(MaxProbeDelay.TotalSeconds, FirstProbeDelay.TotalSeconds * Math.Pow(2, Math.Min(_probeStep, 10)));
        _nextProbe = _time.GetUtcNow() + TimeSpan.FromSeconds(seconds);
    }
}
