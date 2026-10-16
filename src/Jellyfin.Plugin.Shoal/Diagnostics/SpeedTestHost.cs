using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.Api.Admin;
using Jellyfin.Plugin.Shoal.Peers;
using Jellyfin.Plugin.Shoal.Playback;
using Jellyfin.Plugin.Shoal.Security;
using Jellyfin.Plugin.Shoal.State;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Shoal.Diagnostics;

/// <summary>
/// Decides which peers are due for a light speed test (DESIGN 1.4, 7.1): every <c>SpeedTestIntervalHours</c> hours, when a peer comes back
/// online, and right after pairing (a peer that was never measured). A peer that is streaming from Home is left alone. Pure: the host only
/// asks it and runs the tests.
/// </summary>
public sealed class SpeedSchedule
{
    /// <summary>The shortest time between two light tests of one peer, whatever the trigger.</summary>
    public static readonly TimeSpan MinGap = TimeSpan.FromMinutes(10);

    private readonly Dictionary<string, PeerStatus> _lastStatus = new(StringComparer.Ordinal);

    /// <summary>Says whether a peer is due now.</summary>
    /// <param name="peerKey">The peer.</param>
    /// <param name="status">The peer's status now.</param>
    /// <param name="lastTestUtc">When it was last tested (a failed test counts), or null.</param>
    /// <param name="lastSampleUtc">When the newest sample of any kind was taken, or null.</param>
    /// <param name="now">The time.</param>
    /// <param name="interval">The test interval.</param>
    /// <param name="streaming">True while a stream from Home runs through the peer.</param>
    /// <returns>True when a light test should run.</returns>
    public bool IsDue(string peerKey, PeerStatus status, DateTimeOffset? lastTestUtc, DateTimeOffset? lastSampleUtc, DateTimeOffset now, TimeSpan interval, bool streaming)
    {
        _lastStatus.TryGetValue(peerKey, out var before);
        var cameBack = _lastStatus.ContainsKey(peerKey) && before != PeerStatus.Online && status == PeerStatus.Online;
        _lastStatus[peerKey] = status;
        if (status != PeerStatus.Online || streaming)
        {
            return false;
        }

        // A peer that flaps (offline, online, offline...) or a pairing that is retried must not turn into a test every minute (Warden 09 M3).
        if (lastSampleUtc is { } any && now - any < MinGap)
        {
            return false;
        }

        return cameBack || lastTestUtc is null || now - lastTestUtc >= interval;
    }
}

/// <summary>Runs the light speed tests every minute that are due (see <see cref="SpeedSchedule"/>), one peer at a time.</summary>
public sealed class SpeedTestHost : BackgroundService
{
    private static readonly TimeSpan Tick = TimeSpan.FromMinutes(1);

    private readonly IStateStore _state;
    private readonly PeerRegistry _registry;
    private readonly SpeedTestService _tests;
    private readonly SpeedHistoryStore _history;
    private readonly ISettingsAccessor _settings;
    private readonly TimeProvider _time;
    private readonly ILogger<SpeedTestHost> _logger;
    private readonly PassiveTelemetry _passive;
    private readonly SpeedSchedule _schedule = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="SpeedTestHost"/> class.
    /// </summary>
    /// <param name="state">The state store.</param>
    /// <param name="registry">The runtime peer table (online or not).</param>
    /// <param name="tests">The speed tests.</param>
    /// <param name="history">The speed history.</param>
    /// <param name="settings">The plugin toggles (the interval).</param>
    /// <param name="passive">The passive playback telemetry (finished plays are saved here).</param>
    /// <param name="time">The clock.</param>
    /// <param name="logger">The logger.</param>
    public SpeedTestHost(IStateStore state, PeerRegistry registry, SpeedTestService tests, SpeedHistoryStore history, ISettingsAccessor settings, PassiveTelemetry passive, TimeProvider time, ILogger<SpeedTestHost> logger)
    {
        _passive = passive;
        _state = state;
        _registry = registry;
        _tests = tests;
        _history = history;
        _settings = settings;
        _time = time;
        _logger = logger;
    }

    /// <summary>Runs one pass: the light test for every peer that is due.</summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>A task that completes when the pass is done.</returns>
    public async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        _passive.FlushIdle();
        if (_state.Status != StateStatus.Ready)
        {
            return;
        }

        var interval = TimeSpan.FromHours(Math.Clamp(_settings.Current.SpeedTestIntervalHours, 1, 168));
        var now = _time.GetUtcNow();
        foreach (var peer in _state.Current.Home.Peers.Where(static p => p.Enabled).ToList())
        {
            var samples = _history.Read(peer.PeerKey);
            var last = samples.Where(static s => s.Kind != SpeedKinds.Playback).Select(static s => (DateTimeOffset?)s.T).Max();
            var lastAny = samples.Select(static s => (DateTimeOffset?)s.T).Max();
            var due = _schedule.IsDue(peer.PeerKey, _registry.StatusOf(peer.PeerKey), last, lastAny, now, interval, _tests.IsStreaming(peer.PeerKey));

            // The segments of a stream showed the link too slow: measure again as soon as nothing streams through the peer (at most every two minutes).
            if (!due && !_tests.IsStreaming(peer.PeerKey) && _registry.StatusOf(peer.PeerKey) == PeerStatus.Online && _passive.TakeRemeasure(peer.PeerKey) && (last is null || now - last >= TimeSpan.FromMinutes(2)))
            {
                due = true;
            }

            if (due)
            {
                await _tests.RunAsync(peer.PeerKey, SpeedKinds.Light, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Tick, _time);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    await RunOnceAsync(stoppingToken).ConfigureAwait(false);
                }
#pragma warning disable CA1031 // A failed pass must not stop the next one.
                catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
                {
                    _logger.LogWarning("The speed test pass failed: {Error}", ex.GetType().Name);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }
}

/// <summary>
/// Adds the link budget to the bitrate limits (DESIGN 6.2.1, 7.2): with auto-fit on, a stream from a peer is capped at what the link has measured
/// divided by the headroom, so the peer transcodes down instead of the stream buffering. A link that has not been measured lately has no budget.
/// </summary>
public sealed class LinkBudgetLimits : ISystemLimits
{
    /// <summary>How old the newest test may be for the budget to apply.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromHours(48);

    // The budget only ever lowers a cap, but a link that looked slow once must not pin streams to a crawl: never below the floor, and never a floor above what the link
    // can carry at all (the floor is the smaller of MinUsableMbps and T / 1.2, so a 1 Mbps link is not asked for 1.5).
    private const double FloorHeadroom = 1.2;

    private readonly ISystemLimits _inner;
    private readonly SpeedHistoryStore _history;
    private readonly ISettingsAccessor _settings;
    private readonly TimeProvider _time;
    private readonly PassiveTelemetry? _passive;

    /// <summary>
    /// Initializes a new instance of the <see cref="LinkBudgetLimits"/> class.
    /// </summary>
    /// <param name="inner">The server's own limits.</param>
    /// <param name="history">The speed history.</param>
    /// <param name="settings">The plugin toggles (auto-fit).</param>
    /// <param name="time">The clock.</param>
    /// <param name="passive">What the segments of a running stream show about the link, or null.</param>
    public LinkBudgetLimits(ISystemLimits inner, SpeedHistoryStore history, ISettingsAccessor settings, TimeProvider time, PassiveTelemetry? passive = null)
    {
        _passive = passive;
        _inner = inner;
        _history = history;
        _settings = settings;
        _time = time;
    }

    /// <summary>Computes the budget of a peer.</summary>
    /// <param name="history">Its history.</param>
    /// <param name="autoFit">Whether auto-fit is on.</param>
    /// <param name="now">The time.</param>
    /// <returns>The budget in bits per second, or null.</returns>
    public static int? BudgetOf(IReadOnlyList<SpeedSample> history, bool autoFit, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(history);
        if (!autoFit || VerdictEngine.BudgetThroughput(history) is not { } t)
        {
            return null;
        }

        var newest = history.Where(static s => s.HasSpeed && s.Kind != SpeedKinds.Playback && s.SharedWithStreams != true).Max(static s => (DateTimeOffset?)s.T);
        if (newest is null || now - newest > MaxAge)
        {
            return null;
        }

        return FromThroughput(t);
    }

    /// <summary>The budget a measured throughput allows: T / 1.5, never below the floor (the smaller of 1.5 Mbps and T / 1.2).</summary>
    /// <param name="throughputMbps">T.</param>
    /// <returns>Bits per second.</returns>
    public static int FromThroughput(double throughputMbps)
    {
        var bps = VerdictEngine.AutoFitMbps(throughputMbps) * 1_000_000;
        var floor = Math.Min(VerdictEngine.MinUsableMbps, throughputMbps / FloorHeadroom) * 1_000_000;
        return (int)Math.Min(Math.Max(bps, floor), int.MaxValue);
    }

    /// <inheritdoc />
    public BitrateLimits Current() => _inner.Current();

    /// <inheritdoc />
    public BitrateLimits ForPeer(string peerKey)
    {
        var autoFit = _settings.Current.AutoFitQualityToLink;
        var budget = BudgetOf(_history.Read(peerKey), autoFit, _time.GetUtcNow());

        // A link that dropped since the last test: what the segments of the last 30 seconds show wins at once if it is lower, without waiting for the next test, and it stays
        // (LinkLimitMbps) until fresh tests show the link recovered.
        if (autoFit && _passive?.LinkLimitMbps(peerKey) is { } live)
        {
            // Advice only: it can lower the budget, never below the lowest rate worth a conversion, and never raise it.
            var fromLive = Math.Max(FromThroughput(live), (int)(VerdictEngine.MinUsableMbps * 1_000_000));
            budget = budget is { } known ? Math.Min(known, fromLive) : fromLive;
        }

        return _inner.Current() with { LinkBudget = budget };
    }
}

/// <summary>Registers the speed diagnostics (one call from the plugin's registrator).</summary>
public static class DiagnosticsRegistration
{
    /// <summary>Adds the speed history, tests, schedule, cooldowns and summary.</summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same collection.</returns>
    public static IServiceCollection AddShoalDiagnostics(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<ITailscaleStatus, TailscaleLocalApi>();
        services.AddSingleton<SpeedHistoryStore>();
        services.AddSingleton<TranscodeProbe>();
        services.AddSingleton<SpeedTestService>();
        services.AddSingleton<SpeedSummaryProvider>();
        services.AddSingleton<SpeedCooldowns>();
        services.AddSingleton<PassiveTelemetry>();
        services.AddSingleton<ISegmentTelemetry>(sp => sp.GetRequiredService<PassiveTelemetry>());
        services.AddHostedService<SpeedTestHost>();
        return services;
    }

    /// <summary>Registers the bitrate limits with the link budget added (replaces the plain server limits).</summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same collection.</returns>
    public static IServiceCollection AddLinkBudget(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<JellyfinSystemLimits>();
        services.AddSingleton<ISystemLimits>(sp => new LinkBudgetLimits(sp.GetRequiredService<JellyfinSystemLimits>(), sp.GetRequiredService<SpeedHistoryStore>(), sp.GetRequiredService<ISettingsAccessor>(), sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<PassiveTelemetry>()));
        return services;
    }
}
