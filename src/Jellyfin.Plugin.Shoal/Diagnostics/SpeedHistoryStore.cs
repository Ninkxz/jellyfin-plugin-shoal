using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Jellyfin.Plugin.Shoal.Storage;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Shoal.Diagnostics;

/// <summary>The kinds of speed sample (DESIGN 3.6).</summary>
public static class SpeedKinds
{
    /// <summary>The scheduled light test (pings and one bitrate run).</summary>
    public const string Light = "light";

    /// <summary>"Test now".</summary>
    public const string Manual = "manual";

    /// <summary>The deep test.</summary>
    public const string Deep = "deep";

    /// <summary>One playback, measured passively (U9).</summary>
    public const string Playback = "playback";
}

/// <summary>One line of <c>speed.jsonl</c> (DESIGN 3.6). A failed test is a sample too, with <see cref="Error"/> set and no numbers.</summary>
public sealed record SpeedSample
{
    /// <summary>Gets when the sample was taken (UTC).</summary>
    [JsonPropertyName("t")]
    public DateTimeOffset T { get; init; }

    /// <summary>Gets the kind (<see cref="SpeedKinds"/>).</summary>
    [JsonPropertyName("kind")]
    public string Kind { get; init; } = SpeedKinds.Light;

    /// <summary>Gets the median round trip in ms.</summary>
    [JsonPropertyName("rttMedianMs")]
    public double? RttMedianMs { get; init; }

    /// <summary>Gets the 90th percentile round trip in ms.</summary>
    [JsonPropertyName("rttP90Ms")]
    public double? RttP90Ms { get; init; }

    /// <summary>Gets the jitter (mean absolute difference of consecutive round trips) in ms.</summary>
    [JsonPropertyName("jitterMs")]
    public double? JitterMs { get; init; }

    /// <summary>Gets the share of pings that got no answer, in percent.</summary>
    [JsonPropertyName("lossPct")]
    public double? LossPct { get; init; }

    /// <summary>Gets the time the first ping took (connection setup included when the connection was new) in ms.</summary>
    [JsonPropertyName("connectMs")]
    public double? ConnectMs { get; init; }

    /// <summary>Gets the throughput of the largest bitrate run in Mbps.</summary>
    [JsonPropertyName("mbps")]
    public double? Mbps { get; init; }

    /// <summary>Gets the aggregate throughput of the two parallel runs in Mbps (manual and deep tests).</summary>
    [JsonPropertyName("mbpsParallel")]
    public double? MbpsParallel { get; init; }

    /// <summary>Gets the size of the run that produced <see cref="Mbps"/>.</summary>
    [JsonPropertyName("bytes")]
    public long? Bytes { get; init; }

    /// <summary>Gets the duration of that run in ms.</summary>
    [JsonPropertyName("durationMs")]
    public double? DurationMs { get; init; }

    /// <summary>Gets the path: direct, derp, derp?, proxy or unknown.</summary>
    [JsonPropertyName("path")]
    public string Path { get; init; } = "unknown";

    /// <summary>Gets the DERP region, when known.</summary>
    [JsonPropertyName("derpRegion")]
    public string? DerpRegion { get; init; }

    /// <summary>Gets the peer transcode speed of the deep test (U9).</summary>
    [JsonPropertyName("transcodeSpeed")]
    public double? TranscodeSpeed { get; init; }

    /// <summary>Gets the segment time to first byte of a playback sample (U9).</summary>
    [JsonPropertyName("segTtfbMs")]
    public double? SegTtfbMs { get; init; }

    /// <summary>Gets how many conversions Home held on the peer at most while the playback ran (playback samples, DESIGN 7.1).</summary>
    [JsonPropertyName("conversionsActive")]
    public int? ConversionsActive { get; init; }

    /// <summary>Gets the bitrate of the stream that was played, in Mbps (playback samples).</summary>
    [JsonPropertyName("streamMbps")]
    public double? StreamMbps { get; init; }

    /// <summary>Gets the stalls seen in a playback (gaps between segment requests over 1.5 times the segment length).</summary>
    [JsonPropertyName("stalls")]
    public int? Stalls { get; init; }

    /// <summary>Gets how much lower the second half of the pings was than the first half, in percent (a link that upgraded from a relay to a direct path while it was measured).</summary>
    [JsonPropertyName("rttDropPct")]
    public double? RttDropPct { get; init; }

    /// <summary>Gets a value indicating whether a stream from Home ran through the peer while the test did (the number is then a share of the link, not the link).</summary>
    [JsonPropertyName("shared")]
    public bool? SharedWithStreams { get; init; }

    /// <summary>Gets the failure word (unreachable, timeout, authFailed, tooLarge, refused), or null.</summary>
    [JsonPropertyName("error")]
    public string? Error { get; init; }

    /// <summary>Gets a value indicating whether the sample carries a throughput.</summary>
    [JsonIgnore]
    public bool HasSpeed => Error is null && Mbps is not null;
}

/// <summary>
/// The speed history of each peer: <c>peers/&lt;key&gt;/speed.jsonl</c>, one JSON object per line, capped at 2,000 lines and compacted when it
/// passes 2,500 (DESIGN 3.6). Damaged lines are skipped; the file never holds a token, an address or a title.
/// </summary>
public sealed class SpeedHistoryStore
{
    /// <summary>The lines that are kept.</summary>
    public const int KeepLines = 2000;

    /// <summary>How many of the kept lines may be playback samples: a busy server must never push the tests out of the file (Warden 09 M2).</summary>
    public const int PlaybackKeep = 300;

    /// <summary>The line count at which the file is compacted.</summary>
    public const int CompactAt = 2500;

    private const long MaxFileBytes = 4 * 1024 * 1024;

    private static readonly JsonSerializerOptions Options = new() { MaxDepth = 4, DefaultIgnoreCondition = JsonIgnoreCondition.Never };

    private readonly ShoalPaths _paths;
    private readonly ILogger _logger;
    private readonly object _lock = new();
    private readonly Dictionary<string, int> _lines = new(StringComparer.Ordinal);

    /// <summary>
    /// Initializes a new instance of the <see cref="SpeedHistoryStore"/> class.
    /// </summary>
    /// <param name="paths">The data folder layout.</param>
    /// <param name="logger">The logger.</param>
    public SpeedHistoryStore(ShoalPaths paths, ILogger<SpeedHistoryStore> logger)
        : this(paths, (ILogger)logger)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="SpeedHistoryStore"/> class (tests: any logger).
    /// </summary>
    /// <param name="paths">The data folder layout.</param>
    /// <param name="logger">The logger.</param>
    internal SpeedHistoryStore(ShoalPaths paths, ILogger logger)
    {
        _paths = paths;
        _logger = logger;
    }

    /// <summary>Raised after a sample was saved, with the peer key.</summary>
    internal event Action<string>? Appended;

    /// <summary>Adds one sample.</summary>
    /// <param name="peerKey">The peer.</param>
    /// <param name="sample">The sample.</param>
    public void Append(string peerKey, SpeedSample sample)
    {
        ArgumentNullException.ThrowIfNull(sample);
        var file = FileOf(peerKey);
        var line = JsonSerializer.Serialize(sample, Options) + "\n";
        lock (_lock)
        {
            try
            {
                SecureDirectory.Ensure(Path.GetDirectoryName(file)!);
                var options = new FileStreamOptions { Mode = FileMode.Append, Access = FileAccess.Write, Share = FileShare.Read };
                if (!OperatingSystem.IsWindows())
                {
                    options.UnixCreateMode = AtomicFile.PrivateFileMode;
                }

                using (var stream = new FileStream(file, options))
                {
                    var bytes = Encoding.UTF8.GetBytes(line);
                    stream.Write(bytes, 0, bytes.Length);
                }

                var count = _lines.TryGetValue(peerKey, out var known) ? known + 1 : CountLines(file);
                _lines[peerKey] = count;
                if (count > CompactAt)
                {
                    Compact(peerKey, file);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning("A speed sample could not be saved: {Error}", ex.GetType().Name);
            }
        }

        Appended?.Invoke(peerKey);
    }

    /// <summary>Reads the samples, oldest first.</summary>
    /// <param name="peerKey">The peer.</param>
    /// <param name="since">Only samples at or after this time, or null for all.</param>
    /// <returns>The samples; empty when there is no file or it is unreadable.</returns>
    public IReadOnlyList<SpeedSample> Read(string peerKey, DateTimeOffset? since = null)
    {
        var file = FileOf(peerKey);
        lock (_lock)
        {
            return ReadAll(file).Where(s => since is null || s.T >= since).ToList();
        }
    }

    /// <summary>Removes a peer's history (the peer was removed).</summary>
    /// <param name="peerKey">The peer.</param>
    public void Delete(string peerKey)
    {
        var file = FileOf(peerKey);
        lock (_lock)
        {
            _lines.Remove(peerKey);
            try
            {
                File.Delete(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning("A speed history could not be deleted: {Error}", ex.GetType().Name);
            }
        }
    }

    private string FileOf(string peerKey) => Path.Combine(_paths.PeerDir(peerKey), "speed.jsonl");

    private int CountLines(string file) => ReadAll(file).Count;

    private List<SpeedSample> ReadAll(string file)
    {
        var result = new List<SpeedSample>();
        try
        {
            if (!File.Exists(file) || new FileInfo(file).Length > MaxFileBytes)
            {
                return result;
            }

            foreach (var line in File.ReadLines(file, Encoding.UTF8))
            {
                if (line.Length == 0 || line.Length > 2048)
                {
                    continue;
                }

                try
                {
                    if (JsonSerializer.Deserialize<SpeedSample>(line, Options) is { } sample)
                    {
                        result.Add(sample);
                    }
                }
                catch (JsonException)
                {
                    // A damaged line is skipped.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("A speed history could not be read: {Error}", ex.GetType().Name);
        }

        return result;
    }

    private void Compact(string peerKey, string file)
    {
        var all = ReadAll(file);
        var keep = new HashSet<int>();
        var playbacks = 0;
        var others = 0;
        var otherRoom = KeepLines - Math.Min(PlaybackKeep, all.Count(static s => s.Kind == SpeedKinds.Playback));
        for (var i = all.Count - 1; i >= 0; i--)
        {
            // From the newest line back: up to 300 playback samples, and everything else in the room that is left (1,700 or more).
            if (all[i].Kind == SpeedKinds.Playback ? playbacks++ < PlaybackKeep : others++ < otherRoom)
            {
                keep.Add(i);
            }
        }

        var kept = all.Where((_, i) => keep.Contains(i)).ToList();
        var text = new StringBuilder();
        foreach (var sample in kept)
        {
            text.Append(JsonSerializer.Serialize(sample, Options)).Append('\n');
        }

        AtomicFile.Write(file, Encoding.UTF8.GetBytes(text.ToString()));
        _lines[peerKey] = kept.Count;
    }
}
