using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.Peers;
using Jellyfin.Plugin.Shoal.Playback;
using Jellyfin.Plugin.Shoal.Security;
using Jellyfin.Plugin.Shoal.State;
using Jellyfin.Plugin.Shoal.Sync;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Shoal.Diagnostics;

/// <summary>The sizes and timings of a speed test (DESIGN 7.1). The defaults are the design's; tests shrink them.</summary>
public sealed class SpeedTestOptions
{
    /// <summary>Gets or sets the pings after the first (the first one measures the connection).</summary>
    public int Pings { get; set; } = 10;

    /// <summary>Gets or sets the pause between pings.</summary>
    public TimeSpan PingGap { get; set; } = TimeSpan.FromMilliseconds(200);

    /// <summary>Gets or sets the size of the discarded warm-up run.</summary>
    public long WarmupBytes { get; set; } = 1_000_000;

    /// <summary>Gets or sets the size of the main run.</summary>
    public long MainBytes { get; set; } = 8_000_000;

    /// <summary>Gets or sets the size of the second run, made when the main one took less than <see cref="FastRun"/>.</summary>
    public long BigBytes { get; set; } = 32_000_000;

    /// <summary>Gets or sets the size of each of the two parallel runs.</summary>
    public long ParallelBytes { get; set; } = 16_000_000;

    /// <summary>Gets or sets how many seconds a run should take at the measured rate (the sizes are fitted to the link; a call may not run longer than 30 s).</summary>
    public double RunSeconds { get; set; } = 8;

    /// <summary>Gets or sets how short a main run must be to be followed by the big one.</summary>
    public TimeSpan FastRun { get; set; } = TimeSpan.FromSeconds(1.5);
}

/// <summary>
/// Measures the link to one peer (DESIGN 7.1): ping round trips, jitter and loss on a warm connection, then throughput with
/// <c>GET /Playback/BitrateTest</c> (a discarded warm-up, 8 MB, and 32 MB when the 8 MB came fast), and for a manual or deep test two
/// 16 MB runs at once. Every result, a failure too, is appended to the peer's <see cref="SpeedHistoryStore"/>. One test per peer at a time.
/// The calls never touch the circuit breaker: a slow link is a measurement, not a failure.
/// </summary>
public sealed class SpeedTestService
{
    private const double MinParallelMbps = 4;
    private const long SizeSlack = 64 * 1024;
    private static readonly PeerCallProfile PingProfile = new("ping", TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2), 4096);

    private readonly IStateStore _state;
    private readonly IPeerEndpoints _peers;
    private readonly PeerClient _client;
    private readonly SpeedHistoryStore _history;
    private readonly StreamSlots? _slots;
    private readonly ITailscaleStatus _tailscale;
    private readonly CatalogStore? _catalogs;
    private readonly TranscodeProbe? _probe;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.Ordinal);

    /// <summary>
    /// Initializes a new instance of the <see cref="SpeedTestService"/> class.
    /// </summary>
    /// <param name="state">The state store.</param>
    /// <param name="peers">The peers with their credentials.</param>
    /// <param name="client">The peer client.</param>
    /// <param name="history">The speed history.</param>
    /// <param name="slots">The stream slots (a scheduled test waits while the peer is streaming).</param>
    /// <param name="tailscale">Tailscale's status (direct or relayed), when it can be asked.</param>
    /// <param name="catalogs">The catalogs (the deep test picks the smallest title).</param>
    /// <param name="probe">The transcode probe of the deep test.</param>
    /// <param name="logger">The logger.</param>
    public SpeedTestService(IStateStore state, IPeerEndpoints peers, PeerClient client, SpeedHistoryStore history, StreamSlots slots, ITailscaleStatus tailscale, CatalogStore catalogs, TranscodeProbe probe, ILogger<SpeedTestService> logger)
        : this(state, peers, client, history, slots, TimeProvider.System, logger, tailscale, catalogs, probe)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="SpeedTestService"/> class (tests: any clock, logger and no slots).
    /// </summary>
    /// <param name="state">The state store.</param>
    /// <param name="peers">The peers.</param>
    /// <param name="client">The peer client.</param>
    /// <param name="history">The speed history.</param>
    /// <param name="slots">The stream slots, or null.</param>
    /// <param name="time">The clock.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="tailscale">Tailscale's status, or null to never ask.</param>
    /// <param name="catalogs">The catalogs, or null (no transcode probe).</param>
    /// <param name="probe">The transcode probe, or null.</param>
    internal SpeedTestService(IStateStore state, IPeerEndpoints peers, PeerClient client, SpeedHistoryStore history, StreamSlots? slots, TimeProvider time, ILogger logger, ITailscaleStatus? tailscale = null, CatalogStore? catalogs = null, TranscodeProbe? probe = null)
    {
        _catalogs = catalogs;
        _probe = probe;
        _tailscale = tailscale ?? new NoTailscaleStatus();
        _state = state;
        _peers = peers;
        _client = client;
        _history = history;
        _slots = slots;
        _time = time;
        _logger = logger;
    }

    /// <summary>Gets the sizes and timings.</summary>
    public SpeedTestOptions Options { get; } = new();

    /// <summary>Says whether a test is running for a peer.</summary>
    /// <param name="peerKey">The peer.</param>
    /// <returns>True when one is.</returns>
    public bool IsRunning(string peerKey) => _gates.TryGetValue(peerKey, out var gate) && gate.CurrentCount == 0;

    /// <summary>Says whether any of Home's streams run through the peer.</summary>
    /// <param name="peerKey">The peer.</param>
    /// <returns>True when one does.</returns>
    public bool IsStreaming(string peerKey) => _slots?.ActiveSlotsOnPeer(peerKey) > 0;

    /// <summary>
    /// Runs one test and saves the result.
    /// </summary>
    /// <param name="peerKey">The peer.</param>
    /// <param name="kind">light, manual or deep (<see cref="SpeedKinds"/>).</param>
    /// <param name="cancellationToken">Cancellation (shutdown).</param>
    /// <returns>The sample, or null when the peer is unknown or disabled, or a test is already running for it.</returns>
    public async Task<SpeedSample?> RunAsync(string peerKey, string kind, CancellationToken cancellationToken)
    {
        if (_state.Status != StateStatus.Ready || _state.Current.Home.Peers.FirstOrDefault(p => p.PeerKey == peerKey) is not { Enabled: true } peer || _peers.Find(peerKey) is not { } endpoint)
        {
            return null;
        }

        var gate = _gates.GetOrAdd(peerKey, static _ => new SemaphoreSlim(1, 1));
        if (!await gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        try
        {
            var sample = await MeasureAsync(endpoint, peer, kind, cancellationToken).ConfigureAwait(false);
            _history.Append(peerKey, sample);
            _logger.LogInformation("A {Kind} speed test of peer {Peer} finished ({Result})", kind, Redact.Id(peerKey), sample.Error ?? "ok");
            return sample;
        }
        finally
        {
            gate.Release();
        }
    }

    private static string ErrorWord(PeerCallException ex) => ex.Kind switch
    {
        PeerFailureKind.Timeout => "timeout",
        PeerFailureKind.Unreachable => "unreachable",
        PeerFailureKind.TooLarge or PeerFailureKind.BadResponse => "badResponse",
        _ => "refused"
    };

    private static double Median(List<double> sorted) =>
        sorted.Count % 2 == 1 ? sorted[sorted.Count / 2] : (sorted[(sorted.Count / 2) - 1] + sorted[sorted.Count / 2]) / 2;

    private static double Percentile90(List<double> sorted) => sorted[Math.Min(sorted.Count - 1, (int)Math.Ceiling(sorted.Count * 0.9) - 1)];

    private static double Jitter(List<double> inOrder) =>
        inOrder.Count < 2 ? 0 : Enumerable.Range(1, inOrder.Count - 1).Average(i => Math.Abs(inOrder[i] - inOrder[i - 1]));

    private static long NextPowerOfTwo(long value)
    {
        var result = 1L;
        while (result < value && result < (1L << 40))
        {
            result <<= 1;
        }

        return result;
    }

    private static double Round(double value) => Math.Round(value, 1);

    // The size of a run that takes about eight seconds at the measured rate, at most the wanted size and at least a quarter of a megabyte.
    private long Fit(long wanted, double mbps) => Math.Clamp((long)(mbps * 1e6 / 8 * Options.RunSeconds), 250_000, wanted);

    private async Task<SpeedSample> MeasureAsync(PeerEndpoint endpoint, PeerState peer, string kind, CancellationToken cancellationToken)
    {
        var start = _time.GetUtcNow();
        var path = string.IsNullOrEmpty(peer.ProxyUrl) ? "unknown" : "proxy";
        var sample = new SpeedSample { T = start, Kind = kind, Path = path };
        var streamingAtStart = IsStreaming(peer.PeerKey);
        try
        {
            // Pings: the first one on whatever connection there is (its time includes the setup when the connection is new), then the rest warm.
            var connect = await PingAsync(endpoint, cancellationToken).ConfigureAwait(false);
            sample = sample with { ConnectMs = Round(connect) };
            var rtts = new List<double>();
            var lost = 0;
            for (var i = 0; i < Options.Pings; i++)
            {
                await Task.Delay(Options.PingGap, _time, cancellationToken).ConfigureAwait(false);
                try
                {
                    rtts.Add(await PingAsync(endpoint, cancellationToken).ConfigureAwait(false));
                }
                catch (PeerCallException)
                {
                    lost++;
                }
            }

            if (rtts.Count > 0)
            {
                var sorted = rtts.Order().ToList();
                sample = sample with { RttMedianMs = Round(Median(sorted)), RttP90Ms = Round(Percentile90(sorted)), JitterMs = Round(Jitter(rtts)) };
            }

            sample = sample with { LossPct = Options.Pings == 0 ? 0 : Round(100.0 * lost / Options.Pings), RttDropPct = Drop(rtts) };

            // Throughput: a warm-up that is thrown away, the main run, and a bigger one when the main run was quick; the best rate counts.
            // A run is sized so that it ends within the call's 30 second limit whatever the link: the warm-up says how fast it is.
            var warmup = await RunBitrateAsync(endpoint, Options.WarmupBytes, cancellationToken).ConfigureAwait(false);
            var best = await RunBitrateAsync(endpoint, Fit(Options.MainBytes, warmup.Mbps), cancellationToken).ConfigureAwait(false);
            if (best.Elapsed < Options.FastRun)
            {
                try
                {
                    var big = await RunBitrateAsync(endpoint, Options.BigBytes, cancellationToken).ConfigureAwait(false);
                    best = big.Mbps > best.Mbps ? big : best;
                }
                catch (PeerCallException)
                {
                    // The bigger run is a refinement; what was measured stays.
                }
            }

            sample = sample with { Mbps = Round(best.Mbps), Bytes = best.Bytes, DurationMs = Round(best.Elapsed.TotalMilliseconds) };
            sample = await WithPathAsync(endpoint, peer, sample, cancellationToken).ConfigureAwait(false);

            // The extras below must never cost the numbers above: a failure in them leaves the sample as it is (Warden 09 M1).
            if (kind is SpeedKinds.Manual or SpeedKinds.Deep && best.Mbps >= MinParallelMbps)
            {
                try
                {
                    sample = sample with { MbpsParallel = await ParallelAsync(endpoint, best.Mbps, cancellationToken).ConfigureAwait(false) };
                }
                catch (PeerCallException)
                {
                    // no parallel figure this time
                }
            }

            if (kind == SpeedKinds.Deep)
            {
                sample = await WithProbeAsync(endpoint, peer, sample, cancellationToken).ConfigureAwait(false);
            }

            return streamingAtStart || IsStreaming(peer.PeerKey) ? sample with { SharedWithStreams = true } : sample;
        }
        catch (PeerCallException ex)
        {
            return sample with { Error = ErrorWord(ex), Mbps = null, MbpsParallel = null, Bytes = null, DurationMs = null };
        }
    }

    // The peer's transcode speed, measured only when none of Home's streams shares the peer's CPU (the result would be wrong and the viewers would stutter).
    private async Task<SpeedSample> WithProbeAsync(PeerEndpoint endpoint, PeerState peer, SpeedSample sample, CancellationToken cancellationToken)
    {
        var peerKey = peer.PeerKey;
        if (_probe is null || _catalogs is null || IsStreaming(peerKey))
        {
            return sample;
        }

        CatalogEntry? entry;
        try
        {
            entry = TranscodeProbe.Pick(_catalogs.Load(peerKey).Document.Entries);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return sample;
        }

        if (entry is null)
        {
            return sample;
        }

        // The probe is a conversion on the peer like any other: it holds a real slot while it runs, so a stream that starts meanwhile is counted
        // against the peer's limits with it (Warden 09 L5), and no probe starts when the slots are taken.
        var slotId = "speed-probe-" + Guid.NewGuid().ToString("N")[..9];
        if (_slots is not null && !_slots.TryTakeHls(ProbeGrant(slotId, peerKey), peer.MaxConcurrentStreams, peer.MaxConcurrentTranscodes, out _))
        {
            return sample;
        }

        try
        {
            var result = await _probe.RunAsync(endpoint, entry, cancellationToken).ConfigureAwait(false);
            return sample with { TranscodeSpeed = result.Speed, SegTtfbMs = result.MeanTtfbMs };
        }
        finally
        {
            _slots?.ReleaseHls(slotId);
        }
    }

    private static Grant ProbeGrant(string id, string peerKey) => new()
    {
        GrantId = id,
        LocalUserId = Guid.Empty,
        LocalItemId = Guid.Empty,
        CallerDeviceId = "speed-probe",
        LocalPlaySessionId = id,
        PeerKey = peerKey,
        PeerItemId = Guid.Empty,
        PeerMediaSourceId = Guid.Empty
    };

    // How much lower the last pings were than the first ones (a path that upgraded from a relay to a direct one while it was measured).
    private static double? Drop(List<double> rtts)
    {
        if (rtts.Count < 6)
        {
            return null;
        }

        var half = rtts.Count / 2;
        var first = Median(rtts.Take(half).Order().ToList());
        var second = Median(rtts.Skip(rtts.Count - half).Order().ToList());
        return first <= 0 ? null : Round(Math.Max(0, (first - second) / first * 100));
    }

    // Tailscale's own answer when it can be asked, else the heuristic (a hint, never a fact). A proxy hides the path.
    private async Task<SpeedSample> WithPathAsync(PeerEndpoint endpoint, PeerState peer, SpeedSample sample, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrEmpty(peer.ProxyUrl) || peer.Network != PeerNetwork.Tailnet)
        {
            return sample;
        }

        if (await _tailscale.FindAsync(endpoint.BaseUri.Host, cancellationToken).ConfigureAwait(false) is { } known)
        {
            return sample with { Path = known.Relayed ? "derp" : "direct", DerpRegion = known.Relayed ? known.Region : null };
        }

        return LinkAnalyzer.PossiblyRelayed(_history.Read(peer.PeerKey), sample) ? sample with { Path = "derp?" } : sample;
    }

    private async Task<double?> ParallelAsync(PeerEndpoint endpoint, double singleMbps, CancellationToken cancellationToken)
    {
        var size = Fit(Options.ParallelBytes, singleMbps / 2); // two streams share the link: each needs about half of it
        var both = await Task.WhenAll(RunBitrateAsync(endpoint, size, cancellationToken), RunBitrateAsync(endpoint, size, cancellationToken)).ConfigureAwait(false);
        var span = both.Max(r => r.EndTicks) - both.Min(r => r.StartTicks);
        var seconds = _time.GetElapsedTime(0, span).TotalSeconds;
        return seconds > 0 ? Round(both.Sum(r => r.Bytes) * 8 / 1e6 / seconds) : null;
    }

    private async Task<double> PingAsync(PeerEndpoint endpoint, CancellationToken cancellationToken)
    {
        var started = _time.GetTimestamp();
        using var response = await _client.SendAsync(endpoint, PeerRequest.GetAnonymous("/System/Ping").WithoutBreaker(), PingProfile, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccess)
        {
            throw new PeerCallException(PeerFailureKind.BadResponse);
        }

        return _time.GetElapsedTime(started).TotalMilliseconds;
    }

    private async Task<BitrateRun> RunBitrateAsync(PeerEndpoint endpoint, long size, CancellationToken cancellationToken)
    {
        using var response = await _client.SendAsync(endpoint, PeerRequest.Get("/Playback/BitrateTest?Size=" + size.ToString(System.Globalization.CultureInfo.InvariantCulture)).WithoutBreaker(), PeerCallProfile.BitrateTest, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccess || !response.HasBody)
        {
            throw new PeerCallException(response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden ? PeerFailureKind.TrustLapsed : PeerFailureKind.BadResponse);
        }

        var body = response.OpenBody();
        await using var bodyScope = body.ConfigureAwait(false);

        // The peer was asked for `size` bytes. Jellyfin 12.1 rounds the size up to the next power of two, so that much (and a little slack) is
        // expected; anything more is cut off whatever the call's own cap is (Warden 09 L2).
        var allowed = NextPowerOfTwo(size) + SizeSlack;
        if (response.ContentLength > allowed)
        {
            throw new PeerCallException(PeerFailureKind.TooLarge);
        }

        var buffer = new byte[64 * 1024];
        long total = 0;
        long first = 0;
        long startTicks = 0;
        int read;
        while ((read = await body.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (total == 0)
            {
                // The clock starts when the first bytes arrive, so the wait for the first byte (latency) is not part of the rate.
                startTicks = _time.GetTimestamp();
                first = read;
            }

            total += read;
            if (total > allowed)
            {
                throw new PeerCallException(PeerFailureKind.TooLarge);
            }
        }

        var endTicks = _time.GetTimestamp();
        var elapsed = _time.GetElapsedTime(startTicks, endTicks);
        var counted = total - first;
        if (total == 0 || counted <= 0 || elapsed <= TimeSpan.Zero)
        {
            throw new PeerCallException(PeerFailureKind.BadResponse);
        }

        return new BitrateRun(counted * 8 / 1e6 / elapsed.TotalSeconds, total, elapsed, startTicks, endTicks);
    }

    private sealed record BitrateRun(double Mbps, long Bytes, TimeSpan Elapsed, long StartTicks, long EndTicks);
}
