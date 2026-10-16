using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Jellyfin.Extensions.Json;
using Jellyfin.Plugin.Shoal.Authorization;
using Jellyfin.Plugin.Shoal.Sync;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.MediaInfo;

namespace Jellyfin.Plugin.Shoal.Playback;

/// <summary>
/// Why an answer could not be rebuilt.
/// </summary>
public enum BuildFailure
{
    /// <summary>Nothing is wrong.</summary>
    None,

    /// <summary>The answer carries no usable source, or a <c>TranscodingUrl</c> that is not the Peer's own master playlist: 502.</summary>
    BadAnswer,

    /// <summary>The Peer reports a source that neither plays directly nor transcodes: relayed as <c>NoCompatibleStream</c>.</summary>
    NoStream,
}

/// <summary>
/// The limits that cap the stream bitrate a Peer may be asked for (DESIGN 6.2.1).
/// </summary>
/// <param name="ServerRemoteLimit">The server-wide limit for remote clients (0 or less = none).</param>
/// <param name="LinkBudget">The link budget in bits per second (measured throughput / 1.5), or null (phase 4).</param>
public sealed record BitrateLimits(int ServerRemoteLimit, int? LinkBudget);

/// <summary>
/// What a successful rebuild of a Peer's answer produced.
/// </summary>
/// <param name="Response">The answer for the client.</param>
/// <param name="Grant">The grant to store.</param>
public sealed record BuiltPlaybackInfo(PlaybackInfoResponse Response, Grant Grant);

/// <summary>
/// The body that goes to the Peer and the answer that goes back (DESIGN 6.2.1, 6.2.3). Both are built field by field from validated values;
/// nothing is passed through.
/// </summary>
public static class PlaybackInfoBuilder
{
    private const long MaxSourceSize = 1L << 50;

    private static readonly JsonSerializerOptions Json = new(JsonDefaults.Options) { MaxDepth = 32 };

    /// <summary>
    /// Computes the streaming limit sent to the Peer: the smallest of the client's value, the user's and the server's remote limits when the caller is
    /// remote, and the link budget. This replaces core's exemption that lets remote sources ignore bitrate limits.
    /// </summary>
    /// <param name="client">The client's <c>MaxStreamingBitrate</c>, or null.</param>
    /// <param name="user">The caller (its remote limit applies when it is not on the local network).</param>
    /// <param name="limits">The server-wide limits.</param>
    /// <returns>The limit, or null when nothing limits.</returns>
    public static int? ComputeMaxStreamingBitrate(long? client, Caller user, BitrateLimits limits)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(limits);
        var candidates = new List<long>();
        if (client is > 0)
        {
            candidates.Add(client.Value);
        }

        if (!user.IsLocalNetwork)
        {
            if (user.User.RemoteClientBitrateLimit is > 0)
            {
                candidates.Add(user.User.RemoteClientBitrateLimit.Value);
            }

            if (limits.ServerRemoteLimit > 0)
            {
                candidates.Add(limits.ServerRemoteLimit);
            }
        }

        if (limits.LinkBudget is > 0)
        {
            candidates.Add(limits.LinkBudget.Value);
        }

        return candidates.Count == 0 ? null : (int)Math.Min(candidates.Min(), int.MaxValue);
    }

    /// <summary>
    /// Builds the JSON body of the Peer's PlaybackInfo call: exactly the fields of 6.2.1 and nothing else. No <c>UserId</c>, no
    /// <c>LiveStreamId</c>, no query string.
    /// </summary>
    /// <param name="request">The merged client request.</param>
    /// <param name="user">The caller, whose local policy is applied.</param>
    /// <param name="peerMediaSourceId">The Peer's media source id from the catalog.</param>
    /// <param name="runtimeTicks">The runtime from the catalog, for clamping the start position (0 = unknown).</param>
    /// <param name="profile">The rebuilt profile, or null when the client sent none and the device registered none.</param>
    /// <param name="maxStreamingBitrate">The computed limit, or null.</param>
    /// <returns>The UTF-8 JSON body.</returns>
    public static byte[] BuildPeerBody(PlaybackInfoRequest request, CallerUser user, Guid peerMediaSourceId, long runtimeTicks, DeviceProfile? profile, int? maxStreamingBitrate)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(user);
        var body = new JsonObject
        {
            ["MediaSourceId"] = peerMediaSourceId.ToString("N"),
            ["AutoOpenLiveStream"] = false,
        };
        if (profile is not null)
        {
            body["DeviceProfile"] = JsonSerializer.SerializeToNode(profile, Json);
        }

        if (maxStreamingBitrate is { } bitrate)
        {
            body["MaxStreamingBitrate"] = bitrate;
        }

        if (request.StartTimeTicks is { } start)
        {
            body["StartTimeTicks"] = Math.Clamp(start, 0, runtimeTicks > 0 ? runtimeTicks : long.MaxValue);
        }

        if (request.AudioStreamIndex is >= 0 and <= 127)
        {
            body["AudioStreamIndex"] = request.AudioStreamIndex;
        }

        if (request.SubtitleStreamIndex is >= -1 and <= 127)
        {
            body["SubtitleStreamIndex"] = request.SubtitleStreamIndex;
        }

        if (request.MaxAudioChannels is >= 1 and <= 32)
        {
            body["MaxAudioChannels"] = request.MaxAudioChannels;
        }

        body["EnableDirectPlay"] = request.EnableDirectPlay && !user.ForceRemoteSourceTranscoding;
        body["EnableDirectStream"] = request.EnableDirectStream && !user.ForceRemoteSourceTranscoding;
        body["EnableTranscoding"] = request.EnableTranscoding && user.CanTranscodeVideo;
        body["AllowVideoStreamCopy"] = request.AllowVideoStreamCopy && user.CanRemux && !user.ForceRemoteSourceTranscoding;
        body["AllowAudioStreamCopy"] = request.AllowAudioStreamCopy;
        body["AlwaysBurnInSubtitleWhenTranscoding"] = request.AlwaysBurnInSubtitleWhenTranscoding;
        return JsonSerializer.SerializeToUtf8Bytes(body);
    }

    /// <summary>
    /// Serializes an answer with Jellyfin's own naming (PascalCase), as clients parse it.
    /// </summary>
    /// <param name="response">The response.</param>
    /// <returns>The UTF-8 JSON.</returns>
    public static byte[] Serialize(PlaybackInfoResponse response) => JsonSerializer.SerializeToUtf8Bytes(response, Json);

    /// <summary>
    /// The small answer that carries only an error code.
    /// </summary>
    /// <param name="code">The code.</param>
    /// <returns>The UTF-8 JSON.</returns>
    public static byte[] ErrorBody(PlaybackErrorCode code) => Serialize(new PlaybackInfoResponse { MediaSources = [], ErrorCode = code });

    /// <summary>
    /// Parses a Peer's PlaybackInfo answer with depth 32.
    /// </summary>
    /// <param name="utf8Json">The body.</param>
    /// <returns>The answer, or null when it is not one.</returns>
    public static PlaybackInfoResponse? ParsePeerAnswer(ReadOnlySpan<byte> utf8Json)
    {
        try
        {
            return JsonSerializer.Deserialize<PlaybackInfoResponse>(utf8Json, Json);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// Rebuilds the Peer's answer from scratch.
    /// </summary>
    /// <param name="answer">The Peer's answer (no error code).</param>
    /// <param name="context">What the rebuild needs.</param>
    /// <param name="failure">Why it failed, when it did.</param>
    /// <returns>The answer and the grant, or null.</returns>
    public static BuiltPlaybackInfo? Rebuild(PlaybackInfoResponse answer, RebuildContext context, out BuildFailure failure)
    {
        ArgumentNullException.ThrowIfNull(answer);
        ArgumentNullException.ThrowIfNull(context);
        failure = BuildFailure.None;
        var peerSource = answer.MediaSources?.FirstOrDefault(s => s is not null && MetadataSanitizer.TryParseId(s.Id, out var id) && id == context.PeerMediaSourceId)
            ?? answer.MediaSources?.FirstOrDefault(static s => s is not null);
        if (peerSource is null)
        {
            failure = BuildFailure.BadAnswer;
            return null;
        }

        IReadOnlyList<KeyValuePair<string, string>> masterQuery = [];
        var transcoding = false;
        if (!string.IsNullOrEmpty(peerSource.TranscodingUrl))
        {
            var parsed = ParseMasterQuery(peerSource.TranscodingUrl, context.PeerItemId);
            if (parsed is null)
            {
                failure = BuildFailure.BadAnswer;
                return null;
            }

            masterQuery = parsed;
            transcoding = true;
        }

        var directPlay = peerSource.SupportsDirectPlay;
        if (!directPlay && !transcoding)
        {
            failure = BuildFailure.NoStream;
            return null;
        }

        var streams = StreamSanitizer.Rebuild(peerSource.MediaStreams, context.LocalItemId, context.CallerToken);
        var attachments = (context.Attachments ?? NoAttachments.Instance).Rebuild(peerSource, context.LocalItemId, context.VersionItemId ?? context.LocalItemId, context.CallerToken);
        var grant = new Grant
        {
            AttachmentIndexes = attachments.KeptIndexes,
            GrantId = context.GrantId,
            LocalUserId = context.UserId,
            LocalItemId = context.LocalItemId,
            VersionItemId = context.VersionItemId ?? Guid.Empty,
            LocalMediaSourceId = context.VersionItemId ?? Guid.Empty,
            SetId = context.SetId,
            CallerDeviceId = context.DeviceId,
            SeriesKey = context.Entry.Kind == EntryKind.Episode ? context.Entry.PeerSeriesId : string.Empty,
            LocalPlaySessionId = context.LocalPlaySessionId,
            PeerKey = context.PeerKey,
            PeerItemId = context.PeerItemId,
            PeerMediaSourceId = context.PeerMediaSourceId,
            PeerPlaySessionId = ValidPeerPlaySession(answer.PlaySessionId),
            PeerMasterQuery = masterQuery,
            Streams = streams.GrantStreams,
            AudioStreamIndex = Range(peerSource.DefaultAudioStreamIndex, 0, 127),
            SubtitleStreamIndex = Range(peerSource.DefaultSubtitleStreamIndex, -1, 127),
            RuntimeTicks = context.Entry.RuntimeTicks,
            Method = directPlay ? PlayMethod.DirectPlay : PlayMethod.Transcode,
            CreatedUtc = context.Now,
            LastAccessUtc = context.Now,
            LastAuthorizedUtc = context.Now,
        };

        var source = new MediaSourceInfo
        {
            Id = (context.VersionItemId ?? context.LocalItemId).ToString("N"),
            Protocol = MediaProtocol.File,
            IsRemote = false,
            Path = null,
            Container = MetadataSanitizer.ValidContainer(peerSource.Container) ?? context.Entry.Container,
            Size = peerSource.Size is >= 0 and <= MaxSourceSize ? peerSource.Size : context.Entry.SizeBytes > 0 ? context.Entry.SizeBytes : null,
            Bitrate = peerSource.Bitrate is >= 0 and <= 1_000_000_000 ? peerSource.Bitrate : context.Entry.Bitrate > 0 ? context.Entry.Bitrate : null,
            RunTimeTicks = peerSource.RunTimeTicks is >= 0 and <= 48L * 3600 * TimeSpan.TicksPerSecond ? peerSource.RunTimeTicks : context.Entry.RuntimeTicks > 0 ? context.Entry.RuntimeTicks : null,
            VideoType = peerSource.VideoType is { } vt && Enum.IsDefined(vt) ? vt : null,
            Video3DFormat = peerSource.Video3DFormat is { } v3 && Enum.IsDefined(v3) ? v3 : null,
            Timestamp = peerSource.Timestamp is { } ts && Enum.IsDefined(ts) ? ts : null,
            Formats = [.. (peerSource.Formats ?? []).Where(static f => f is { Length: >= 1 and <= 16 } && f.All(static c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9'))).Take(8)],
            TranscodeReasons = peerSource.TranscodeReasons,
            DefaultAudioStreamIndex = grant.AudioStreamIndex,
            DefaultSubtitleStreamIndex = grant.SubtitleStreamIndex,
            SupportsDirectPlay = directPlay,
            SupportsDirectStream = false, // always: jellyfin-web's DirectStream branch would request the raw file (DESIGN 6.2.3, G7)
            SupportsTranscoding = transcoding && peerSource.SupportsTranscoding,
            TranscodingSubProtocol = peerSource.TranscodingSubProtocol,
            TranscodingContainer = Token(peerSource.TranscodingContainer),
            AnalyzeDurationMs = Range(peerSource.AnalyzeDurationMs, 0, 600_000),
            IgnoreDts = peerSource.IgnoreDts,
            IgnoreIndex = peerSource.IgnoreIndex,
            GenPtsInput = peerSource.GenPtsInput,
            ReadAtNativeFramerate = peerSource.ReadAtNativeFramerate,
            Name = PeerText.Clean(peerSource.Name, 200),
            ETag = ETagOf(context.Entry.StubKey, context.Entry.PeerEtag),
            TranscodingUrl = transcoding ? $"/Shoal/Hls/{context.GrantId}/master.m3u8?ApiKey={Uri.EscapeDataString(context.CallerToken)}" : null,
            MediaStreams = streams.Streams,
            MediaAttachments = [.. attachments.Attachments],
            RequiredHttpHeaders = [],
            OpenToken = null,
            LiveStreamId = null,
            EncoderPath = null,
            RequiresOpening = false,
            RequiresClosing = false,
            IsInfiniteStream = false,
            SupportsProbing = false,
            HasSegments = false,
        };
        return new BuiltPlaybackInfo(new PlaybackInfoResponse { MediaSources = [source], PlaySessionId = context.LocalPlaySessionId }, grant);
    }

    /// <summary>
    /// Accepts a Peer PlaySessionId only in the shape Jellyfin writes (letters, digits and hyphens, 8 to 64): anything else is stored as empty, which makes
    /// the stream unstoppable by Home (the Peer's idle kill ends it) instead of a source of exceptions or of a kill request without a session (Warden 07 L-4).
    /// </summary>
    /// <param name="value">The Peer's value.</param>
    /// <returns>The value, or an empty string.</returns>
    public static string ValidPeerPlaySession(string? value) =>
        value is { Length: >= 8 and <= 64 } && value.All(static c => c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-') ? value : string.Empty;

    /// <summary>
    /// Whether serialized output contains anything that belongs to the Peer: its token, ids (N and D forms), play session id or host. A hit means a
    /// bug or a hostile answer slipped through the whitelist.
    /// </summary>
    /// <param name="json">The serialized answer.</param>
    /// <param name="peerToken">The Peer token.</param>
    /// <param name="peerIds">The Peer item, media source and library ids.</param>
    /// <param name="peerPlaySessionId">The Peer's play session id.</param>
    /// <param name="peerHost">The Peer's host name or address.</param>
    /// <returns>True on a hit.</returns>
    public static bool CanaryHit(string json, string peerToken, IEnumerable<Guid> peerIds, string? peerPlaySessionId, string peerHost)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (peerToken.Length >= 8 && json.Contains(peerToken, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        foreach (var id in peerIds)
        {
            if (json.Contains(id.ToString("N"), StringComparison.OrdinalIgnoreCase) || json.Contains(id.ToString("D"), StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        if (peerPlaySessionId is { Length: >= 8 } && json.Contains(peerPlaySessionId, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return ContainsHost(json, peerHost);
    }

    /// <summary>
    /// Finds a host name only on label boundaries and as written (case-sensitive: the host is the lower-case form Home sent, so a leak reproduces it exactly):
    /// the host must not be preceded or followed by a letter, digit, dot or hyphen, so a short host such as
    /// <c>tv</c> does not match "TV" or "tvs" in a title (W B-10).
    /// </summary>
    /// <param name="text">The text.</param>
    /// <param name="host">The host.</param>
    /// <returns>True when the host appears as a whole name.</returns>
    public static bool ContainsHost(string text, string host)
    {
        if (string.IsNullOrEmpty(host))
        {
            return false;
        }

        var index = 0;
        while ((index = text.IndexOf(host, index, StringComparison.Ordinal)) >= 0)
        {
            var before = index == 0 ? ' ' : text[index - 1];
            var afterIndex = index + host.Length;
            var after = afterIndex >= text.Length ? ' ' : text[afterIndex];
            if (!IsHostChar(before) && !IsHostChar(after))
            {
                return true;
            }

            index += host.Length;
        }

        return false;
    }

    /// <summary>The ETag clients see: a hash of the stub key and the Peer's etag, never the Peer's own value.</summary>
    /// <param name="stubKey">The stub key.</param>
    /// <param name="peerEtag">The Peer's etag.</param>
    /// <returns>32 lower-case hex characters.</returns>
    public static string ETagOf(string stubKey, string peerEtag) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(stubKey + peerEtag)))[..32];

    private static bool IsHostChar(char c) => c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '.' or '-';

    private static string? Token(string? value) =>
        value is { Length: >= 1 and <= 16 } && value.All(static c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9')) ? value : null;

    private static int? Range(int? value, int min, int max) => value is { } v && v >= min && v <= max ? v : null;

    /// <summary>
    /// Checks the Peer's <c>TranscodingUrl</c> and returns its query without credentials. The path must be the Peer's own master playlist for the
    /// item, and the query must stay inside the alphabet that cannot hide a scheme, authority or path.
    /// </summary>
    private static List<KeyValuePair<string, string>>? ParseMasterQuery(string url, Guid peerItemId)
    {
        var question = url.IndexOf('?', StringComparison.Ordinal);
        if (question < 0)
        {
            return null;
        }

        var path = url[..question];
        var query = url[(question + 1)..];
        var n = "/videos/" + peerItemId.ToString("N") + "/master.m3u8";
        var d = "/videos/" + peerItemId.ToString("D") + "/master.m3u8";
        if (!string.Equals(path, n, StringComparison.OrdinalIgnoreCase) && !string.Equals(path, d, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (query.Length > 4096 || !ValidEscapes(query) || !query.All(static c => c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '.' or '_' or '~' or '%' or '=' or '&' or ',' or '+' or '-'))
        {
            return null;
        }

        var pairs = new List<KeyValuePair<string, string>>();
        foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=', StringComparison.Ordinal);
            var key = Decode(eq < 0 ? part : part[..eq]);
            var value = eq < 0 ? string.Empty : Decode(part[(eq + 1)..]);
            if (key is null || value is null)
            {
                return null;
            }

            if (key.Equals("ApiKey", StringComparison.OrdinalIgnoreCase) || key.Equals("api_key", StringComparison.OrdinalIgnoreCase))
            {
                continue; // the Peer's own credential never leaves the grant
            }

            pairs.Add(new KeyValuePair<string, string>(key, value));
        }

        pairs.RemoveAll(static p => p.Key.Equals("EnableAdaptiveBitrateStreaming", StringComparison.OrdinalIgnoreCase));
        pairs.Add(new KeyValuePair<string, string>("EnableAdaptiveBitrateStreaming", "false"));
        return pairs;
    }

    private static bool ValidEscapes(string query)
    {
        for (var i = 0; i < query.Length; i++)
        {
            if (query[i] == '%' && (i + 2 >= query.Length || !Uri.IsHexDigit(query[i + 1]) || !Uri.IsHexDigit(query[i + 2])))
            {
                return false; // a percent sign that is not an escape
            }
        }

        return true;
    }

    private static string? Decode(string value)
    {
        try
        {
            return Uri.UnescapeDataString(value.Replace('+', ' '));
        }
        catch (UriFormatException)
        {
            return null;
        }
    }
}

/// <summary>
/// What <see cref="PlaybackInfoBuilder.Rebuild"/> needs besides the Peer's answer.
/// </summary>
/// <param name="GrantId">The new grant's id.</param>
/// <param name="LocalPlaySessionId">The new local play session id.</param>
/// <param name="UserId">The caller.</param>
/// <param name="DeviceId">The caller's capped device id.</param>
/// <param name="CallerToken">The caller's own token (echoed into URLs that go back to the caller).</param>
/// <param name="LocalItemId">The local item id.</param>
/// <param name="PeerKey">The peer.</param>
/// <param name="PeerItemId">The Peer's item id.</param>
/// <param name="PeerMediaSourceId">The Peer's media source id.</param>
/// <param name="Entry">The catalog entry.</param>
/// <param name="Now">The time.</param>
/// <param name="Attachments">Rebuilds the attachments (DESIGN v2.6.6); none means no attachments, as in v1.</param>
/// <param name="VersionItemId">The local item id of the version that is played (DESIGN v2.6.4), or null for a title with one version.</param>
/// <param name="SetId">The version set id, or empty.</param>
public sealed record RebuildContext(
    string GrantId,
    string LocalPlaySessionId,
    Guid UserId,
    string DeviceId,
    string CallerToken,
    Guid LocalItemId,
    string PeerKey,
    Guid PeerItemId,
    Guid PeerMediaSourceId,
    CatalogEntry Entry,
    DateTimeOffset Now,
    IAttachmentRebuilder? Attachments = null,
    Guid? VersionItemId = null,
    string SetId = "");
