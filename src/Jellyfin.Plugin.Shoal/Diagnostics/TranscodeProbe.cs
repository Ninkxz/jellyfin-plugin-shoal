using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.Peers;
using Jellyfin.Plugin.Shoal.Playback;
using Jellyfin.Plugin.Shoal.Security;
using Jellyfin.Plugin.Shoal.Sync;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Shoal.Diagnostics;

/// <summary>What the transcode probe measured.</summary>
/// <param name="Speed">Segment length divided by the time to its first byte, as a multiple of real time, or null.</param>
/// <param name="MeanTtfbMs">The mean time to the first byte of the measured segments, or null.</param>
/// <param name="Error">Why there is no result (noTitle, noTranscode, badPlaylist, failed), or null.</param>
public sealed record TranscodeProbeResult(double? Speed, double? MeanTtfbMs, string? Error);

/// <summary>
/// The deep test's transcode probe (DESIGN 7.1): starts a Peer HLS transcode of the smallest title at 1080p and 8 Mbps with a profile that forces a
/// transcode, fetches the segments one after another as a player does, and times the first byte of segments 3 to 8. Speed is the video it got
/// per second of waiting. It ends with the scoped <c>ActiveEncodings</c> delete, whatever happened. The caller makes sure no stream from Home runs
/// through the peer.
/// </summary>
public sealed class TranscodeProbe
{
    /// <summary>The first segment whose time to first byte counts (the first ones carry the start of the job).</summary>
    public const int FirstMeasured = 3;

    /// <summary>The last segment that is fetched.</summary>
    public const int LastMeasured = 8;

    private const int MaxUri = 2048;
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(60);

    private readonly PeerClient _client;
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="TranscodeProbe"/> class.
    /// </summary>
    /// <param name="client">The peer client.</param>
    /// <param name="logger">The logger.</param>
    public TranscodeProbe(PeerClient client, ILogger<TranscodeProbe> logger)
        : this(client, (ILogger)logger)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="TranscodeProbe"/> class (tests: any logger).
    /// </summary>
    /// <param name="client">The peer client.</param>
    /// <param name="logger">The logger.</param>
    internal TranscodeProbe(PeerClient client, ILogger logger)
    {
        _client = client;
        _logger = logger;
    }

    /// <summary>The smallest title worth probing: long enough for nine segments, else the smallest of all.</summary>
    /// <param name="entries">The catalog entries.</param>
    /// <returns>The entry, or null when there is none.</returns>
    public static CatalogEntry? Pick(IEnumerable<CatalogEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var usable = entries.Where(static e => e.State == EntryState.Active && Guid.TryParseExact(e.PeerItemId, "N", out _) && e.SizeBytes > 0).ToList();
        var long_ = usable.Where(static e => e.RuntimeTicks >= 10 * TimeSpan.TicksPerMinute).OrderBy(static e => e.SizeBytes).FirstOrDefault();
        return long_ ?? usable.OrderBy(static e => e.SizeBytes).FirstOrDefault();
    }

    /// <summary>
    /// The suggested "Conversions at once" for a transcode speed: floor(speed / 1.5), between 1 and 4. Never applied automatically.
    /// </summary>
    /// <param name="speed">The measured speed as a multiple of real time.</param>
    /// <returns>The suggestion.</returns>
    public static int SuggestedConversions(double speed) => Math.Clamp((int)Math.Floor(speed / 1.5), 1, 4);

    /// <summary>Runs the probe.</summary>
    /// <param name="peer">The peer.</param>
    /// <param name="entry">The title to transcode.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The result; a failure is a result, not an exception.</returns>
    public async Task<TranscodeProbeResult> RunAsync(PeerEndpoint peer, CatalogEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(peer);
        ArgumentNullException.ThrowIfNull(entry);
        if (!Guid.TryParseExact(entry.PeerItemId, "N", out var itemId))
        {
            return new TranscodeProbeResult(null, null, "noTitle");
        }

        var sourceId = Guid.TryParseExact(entry.PeerMediaSourceId, "N", out var s) ? s : itemId;
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(Deadline);
        string? playSession = null;
        try
        {
            var started = await StartAsync(peer, itemId, sourceId, limit.Token).ConfigureAwait(false);
            if (started is null)
            {
                return new TranscodeProbeResult(null, null, "noTranscode");
            }

            playSession = started.Value.PlaySession;
            return await TimeSegmentsAsync(peer, itemId, started.Value.MasterQuery, limit.Token).ConfigureAwait(false);
        }
        catch (PeerCallException ex)
        {
            _logger.LogInformation("The transcode probe of peer {Peer} failed ({Kind})", Redact.Id(peer.PeerKey), ex.Kind);
            return new TranscodeProbeResult(null, null, "failed");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new TranscodeProbeResult(null, null, "failed"); // the probe's own time limit
        }
        finally
        {
            await StopAsync(peer, playSession).ConfigureAwait(false);
        }
    }

    /// <summary>Says whether a URI the peer wrote in a playlist may be requested: relative, no scheme, no authority, no way up.</summary>
    /// <param name="uri">The URI.</param>
    /// <returns>True when it is safe.</returns>
    internal static bool SafeUri(string uri) =>
        uri.Length is > 0 and <= MaxUri
        && uri.All(static c => c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '.' or '_' or '~' or '%' or '=' or '&' or ',' or '+' or '-' or '/' or '?')
        && !uri.StartsWith('/')
        && !uri.Contains("..", StringComparison.Ordinal)
        && !uri.Contains("//", StringComparison.Ordinal);

    /// <summary>Takes the peer's own credential out of a URI's query (it already travels in the Authorization header).</summary>
    /// <param name="uri">The URI.</param>
    /// <returns>The URI without <c>ApiKey</c> and <c>api_key</c>.</returns>
    internal static string WithoutKeys(string uri)
    {
        var question = uri.IndexOf('?', StringComparison.Ordinal);
        if (question < 0)
        {
            return uri;
        }

        var kept = uri[(question + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries).Where(static p => !p.StartsWith("ApiKey=", StringComparison.OrdinalIgnoreCase) && !p.StartsWith("api_key=", StringComparison.OrdinalIgnoreCase));
        return uri[..question] + (kept.Any() ? "?" + string.Join('&', kept) : string.Empty);
    }

    /// <summary>Reads the segments of a media playlist in order.</summary>
    /// <param name="playlist">The playlist text.</param>
    /// <returns>The length in seconds and the URI of each segment.</returns>
    internal static List<(double Seconds, string Uri)> Segments(string playlist)
    {
        var result = new List<(double, string)>();
        double? pending = null;
        foreach (var raw in playlist.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("#EXTINF:", StringComparison.Ordinal))
            {
                var value = line["#EXTINF:".Length..].Split(',')[0];
                pending = double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) && seconds > 0 && seconds < 600 ? seconds : null;
            }
            else if (line.Length > 0 && !line.StartsWith('#') && pending is { } length)
            {
                result.Add((length, line));
                pending = null;
            }
        }

        return result;
    }

    private static byte[] Body(Guid sourceId)
    {
        var profile = new JsonObject
        {
            ["Name"] = "Shoal speed probe",
            ["MaxStreamingBitrate"] = 8_000_000,
            ["DirectPlayProfiles"] = new JsonArray(),
            ["TranscodingProfiles"] = new JsonArray(new JsonObject
            {
                ["Container"] = "ts",
                ["Type"] = "Video",
                ["VideoCodec"] = "h264",
                ["AudioCodec"] = "aac",
                ["Protocol"] = "hls",
                ["Context"] = "Streaming",
                ["MaxAudioChannels"] = "2",
                ["MinSegments"] = 1,
                ["BreakOnNonKeyFrames"] = true
            }),
            ["CodecProfiles"] = new JsonArray(new JsonObject
            {
                ["Type"] = "Video",
                ["Codec"] = "h264",
                ["Conditions"] = new JsonArray(new JsonObject { ["Condition"] = "LessThanEqual", ["Property"] = "Height", ["Value"] = "1080", ["IsRequired"] = false })
            })
        };
        var body = new JsonObject
        {
            ["MediaSourceId"] = sourceId.ToString("N"),
            ["AutoOpenLiveStream"] = false,
            ["DeviceProfile"] = profile,
            ["MaxStreamingBitrate"] = 8_000_000,
            ["EnableDirectPlay"] = false,
            ["EnableDirectStream"] = false,
            ["EnableTranscoding"] = true,
            ["AllowVideoStreamCopy"] = false,
            ["AllowAudioStreamCopy"] = false
        };
        return Encoding.UTF8.GetBytes(body.ToJsonString());
    }

    private async Task<(string PlaySession, string MasterQuery)?> StartAsync(PeerEndpoint peer, Guid itemId, Guid sourceId, CancellationToken cancellationToken)
    {
        using var response = await _client.SendAsync(peer, PeerRequest.Post($"/Items/{itemId:N}/PlaybackInfo", Body(sourceId)).WithoutBreaker(), PeerCallProfile.PlaybackInfo, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccess)
        {
            return null;
        }

        var answer = PlaybackInfoBuilder.ParsePeerAnswer(await response.ReadBytesAsync(cancellationToken).ConfigureAwait(false));
        var url = answer?.MediaSources?.FirstOrDefault(static m => m is not null)?.TranscodingUrl;
        if (answer is null || string.IsNullOrEmpty(url))
        {
            return null;
        }

        var question = url.IndexOf('?', StringComparison.Ordinal);
        var path = question < 0 ? url : url[..question];
        var expected = new[] { "/videos/" + itemId.ToString("N") + "/master.m3u8", "/videos/" + itemId.ToString("D") + "/master.m3u8" };
        if (question < 0 || !expected.Contains(path, StringComparer.OrdinalIgnoreCase) || !SafeUri("x?" + url[(question + 1)..]))
        {
            return null;
        }

        return (PlaybackInfoBuilder.ValidPeerPlaySession(answer.PlaySessionId), WithoutKeys("x?" + url[(question + 1)..])[2..]);
    }

    private async Task<TranscodeProbeResult> TimeSegmentsAsync(PeerEndpoint peer, Guid itemId, string masterQuery, CancellationToken cancellationToken)
    {
        var root = $"/Videos/{itemId:N}/";
        var master = await TextAsync(peer, root + "master.m3u8?" + masterQuery, PeerCallProfile.HlsMaster, cancellationToken).ConfigureAwait(false);
        var variant = master.Split('\n').Select(static l => l.Trim()).FirstOrDefault(static l => l.Length > 0 && !l.StartsWith('#'));
        if (variant is null || !SafeUri(variant) || !variant.StartsWith("main.m3u8", StringComparison.Ordinal))
        {
            return new TranscodeProbeResult(null, null, "badPlaylist");
        }

        var main = await TextAsync(peer, root + WithoutKeys(variant), PeerCallProfile.HlsMain, cancellationToken).ConfigureAwait(false);
        var segments = Segments(main);
        if (segments.Count < 2 || segments.Any(static s => !SafeUri(s.Uri) || !s.Uri.StartsWith("hls1/main/", StringComparison.Ordinal)))
        {
            return new TranscodeProbeResult(null, null, "badPlaylist");
        }

        // Fetch one after the other, as a player filling its buffer does: the wait for segment k is what the peer needed to encode it.
        var first = Math.Min(FirstMeasured, segments.Count - 1);
        var waited = 0.0;
        var video = 0.0;
        var counted = 0;
        for (var k = 0; k < Math.Min(segments.Count, LastMeasured + 1); k++)
        {
            var clock = Stopwatch.StartNew();
            using var segment = await _client.SendAsync(peer, PeerRequest.GetMedia("/Videos/" + itemId.ToString("N") + "/" + WithoutKeys(segments[k].Uri)).WithoutBreaker(), PeerCallProfile.HlsSegment, cancellationToken).ConfigureAwait(false);
            var ttfb = clock.Elapsed.TotalMilliseconds;
            if (!segment.IsSuccess || !segment.HasBody)
            {
                return counted > 0 ? Result(waited, video, counted) : new TranscodeProbeResult(null, null, "failed");
            }

            var body = segment.OpenBody();
            await using var bodyScope = body.ConfigureAwait(false);
            var buffer = new byte[64 * 1024];
            while (await body.ReadAsync(buffer, cancellationToken).ConfigureAwait(false) > 0)
            {
            }

            if (k >= first)
            {
                waited += ttfb;
                video += segments[k].Seconds;
                counted++;
            }
        }

        return counted > 0 ? Result(waited, video, counted) : new TranscodeProbeResult(null, null, "failed");
    }

    private static TranscodeProbeResult Result(double waitedMs, double videoSeconds, int counted) =>
        new(waitedMs <= 0 ? null : Math.Round(videoSeconds / (waitedMs / 1000), 2), Math.Round(waitedMs / counted, 1), null);

    private async Task<string> TextAsync(PeerEndpoint peer, string pathAndQuery, PeerCallProfile profile, CancellationToken cancellationToken)
    {
        using var response = await _client.SendAsync(peer, PeerRequest.GetMedia(pathAndQuery).WithoutBreaker(), profile, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccess || !response.HasBody)
        {
            throw new PeerCallException(PeerFailureKind.BadResponse);
        }

        return Encoding.UTF8.GetString(await response.ReadBytesAsync(cancellationToken).ConfigureAwait(false));
    }

    private async Task StopAsync(PeerEndpoint peer, string? playSession)
    {
        if (string.IsNullOrEmpty(playSession))
        {
            return;
        }

        try
        {
            using var response = await _client.SendAsync(peer, PeerJobRequests.ScopedKill(peer.DeviceId, playSession), PeerCallProfile.SessionReport, CancellationToken.None).ConfigureAwait(false);
        }
        catch (PeerCallException ex)
        {
            _logger.LogInformation("The probe's transcode on peer {Peer} could not be stopped ({Kind})", Redact.Id(peer.PeerKey), ex.Kind);
        }
    }
}
