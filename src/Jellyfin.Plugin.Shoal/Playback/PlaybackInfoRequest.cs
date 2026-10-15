using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Extensions.Json;
using Jellyfin.Plugin.Shoal.Http;
using MediaBrowser.Model.Dlna;
using Microsoft.AspNetCore.Http;

namespace Jellyfin.Plugin.Shoal.Playback;

/// <summary>
/// A PlaybackInfo request after the query and the body were merged (DESIGN 6.2 step 2). It holds no <c>UserId</c>: the user is always the caller.
/// </summary>
/// <param name="MaxStreamingBitrate">The client's limit, or null.</param>
/// <param name="StartTimeTicks">The start position, or null.</param>
/// <param name="AudioStreamIndex">The audio stream the client wants, or null.</param>
/// <param name="SubtitleStreamIndex">The subtitle stream the client wants, or null.</param>
/// <param name="MaxAudioChannels">The channel limit, or null.</param>
/// <param name="MediaSourceId">The media source the client names, or null.</param>
/// <param name="EnableDirectPlay">Whether direct play is allowed.</param>
/// <param name="EnableDirectStream">Whether direct stream is allowed.</param>
/// <param name="EnableTranscoding">Whether transcoding is allowed.</param>
/// <param name="AllowVideoStreamCopy">Whether the video stream may be copied.</param>
/// <param name="AllowAudioStreamCopy">Whether the audio stream may be copied.</param>
/// <param name="AlwaysBurnInSubtitleWhenTranscoding">Whether subtitles are always burned in.</param>
/// <param name="DeviceProfile">The client's profile, or null.</param>
public sealed record PlaybackInfoRequest(
    long? MaxStreamingBitrate,
    long? StartTimeTicks,
    int? AudioStreamIndex,
    int? SubtitleStreamIndex,
    int? MaxAudioChannels,
    Guid? MediaSourceId,
    bool EnableDirectPlay,
    bool EnableDirectStream,
    bool EnableTranscoding,
    bool AllowVideoStreamCopy,
    bool AllowAudioStreamCopy,
    bool AlwaysBurnInSubtitleWhenTranscoding,
    DeviceProfile? DeviceProfile);

/// <summary>
/// The result of reading a PlaybackInfo request.
/// </summary>
/// <param name="Request">The request, when it could be read.</param>
/// <param name="FailureStatus">The status to answer when it could not (400 or 413), otherwise 0.</param>
public sealed record PlaybackInfoParse(PlaybackInfoRequest? Request, int FailureStatus)
{
    /// <summary>Gets a value indicating whether the request was read.</summary>
    public bool Ok => Request is not null;
}

/// <summary>
/// Reads a PlaybackInfo request the way core does (<c>MediaInfoController.cs:116-209</c>): the query value wins when present, otherwise the body
/// value, otherwise the default; <c>DeviceProfile</c> and <c>AlwaysBurnInSubtitleWhenTranscoding</c> are body only; <c>UserId</c> is ignored; a
/// query value that does not parse as its type is a 400. The body is at most 256 KB with JSON depth 32; unknown members are ignored.
/// </summary>
public static class PlaybackInfoParser
{
    /// <summary>The largest body that is read.</summary>
    public const int MaxBodyBytes = 256 * 1024;

    private static readonly TimeSpan BodyTimeout = TimeSpan.FromSeconds(10);

    private static readonly JsonSerializerOptions BodyOptions = new(JsonDefaults.Options)
    {
        MaxDepth = 32,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        AllowTrailingCommas = false,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Skip,
    };

    /// <summary>
    /// Reads the request.
    /// </summary>
    /// <param name="request">The HTTP request (a POST has a body; a GET only carries query values).</param>
    /// <param name="localItemId">The local item id, which a named media source must equal.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The merged request, or the status to answer.</returns>
    public static async Task<PlaybackInfoParse> ParseAsync(HttpRequest request, Guid localItemId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        Body? body = null;
        if (HttpMethods.IsPost(request.Method))
        {
            var bytes = await ReadBodyAsync(request, cancellationToken).ConfigureAwait(false);
            if (bytes is null)
            {
                return new PlaybackInfoParse(null, StatusCodes.Status413PayloadTooLarge);
            }

            if (bytes.Length > 0)
            {
                try
                {
                    body = JsonSerializer.Deserialize<Body>(bytes, BodyOptions);
                }
                catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException or FormatException)
                {
                    return new PlaybackInfoParse(null, StatusCodes.Status400BadRequest);
                }
            }
        }

        body ??= new Body();
        if (!TryLong(request, "MaxStreamingBitrate", body.MaxStreamingBitrate, out var maxBitrate)
            || !TryLong(request, "StartTimeTicks", body.StartTimeTicks, out var startTicks)
            || !TryInt(request, "AudioStreamIndex", body.AudioStreamIndex, out var audio)
            || !TryInt(request, "SubtitleStreamIndex", body.SubtitleStreamIndex, out var subtitle)
            || !TryInt(request, "MaxAudioChannels", body.MaxAudioChannels, out var channels)
            || !TryBool(request, "EnableDirectPlay", body.EnableDirectPlay, true, out var directPlay)
            || !TryBool(request, "EnableDirectStream", body.EnableDirectStream, true, out var directStream)
            || !TryBool(request, "EnableTranscoding", body.EnableTranscoding, true, out var transcoding)
            || !TryBool(request, "AllowVideoStreamCopy", body.AllowVideoStreamCopy, true, out var videoCopy)
            || !TryBool(request, "AllowAudioStreamCopy", body.AllowAudioStreamCopy, true, out var audioCopy)
            || !TryBool(request, "AutoOpenLiveStream", null, false, out _)
            || !TryText(request, "MediaSourceId", body.MediaSourceId, out var sourceText))
        {
            return new PlaybackInfoParse(null, StatusCodes.Status400BadRequest);
        }

        Guid? sourceId = null;
        if (!string.IsNullOrWhiteSpace(sourceText))
        {
            if (!ShoalRequest.TryParseGuid(sourceText, out var parsed))
            {
                return new PlaybackInfoParse(null, StatusCodes.Status400BadRequest); // garbage; whether the id belongs to the title is the handler's question (DESIGN v2.6.4)
            }

            sourceId = parsed;
        }

        return new PlaybackInfoParse(
            new PlaybackInfoRequest(maxBitrate, startTicks, audio, subtitle, channels, sourceId, directPlay, directStream, transcoding, videoCopy, audioCopy, body.AlwaysBurnInSubtitleWhenTranscoding ?? false, body.DeviceProfile),
            0);
    }

    private static async Task<byte[]?> ReadBodyAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        if (request.ContentLength > MaxBodyBytes)
        {
            return null;
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(BodyTimeout);
        using var collected = new MemoryStream();
        var buffer = new byte[16 * 1024];
        int read;
        while ((read = await request.Body.ReadAsync(buffer.AsMemory(), deadline.Token).ConfigureAwait(false)) > 0)
        {
            if (collected.Length + read > MaxBodyBytes)
            {
                return null;
            }

            await collected.WriteAsync(buffer.AsMemory(0, read), deadline.Token).ConfigureAwait(false);
        }

        return collected.ToArray();
    }

    private static bool TryText(HttpRequest request, string key, string? bodyValue, out string? value)
    {
        value = ShoalRequest.Values(request, key) is [var first, ..] ? first : bodyValue;
        return true;
    }

    private static bool TryLong(HttpRequest request, string key, long? bodyValue, out long? value)
    {
        value = bodyValue;
        if (ShoalRequest.Values(request, key) is [var first, ..])
        {
            if (!long.TryParse(first, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var parsed))
            {
                return false;
            }

            value = parsed;
        }

        return true;
    }

    private static bool TryInt(HttpRequest request, string key, int? bodyValue, out int? value)
    {
        value = bodyValue;
        if (ShoalRequest.Values(request, key) is [var first, ..])
        {
            if (!int.TryParse(first, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var parsed))
            {
                return false;
            }

            value = parsed;
        }

        return true;
    }

    private static bool TryBool(HttpRequest request, string key, bool? bodyValue, bool fallback, out bool value)
    {
        value = bodyValue ?? fallback;
        if (ShoalRequest.Values(request, key) is [var first, ..])
        {
            if (!bool.TryParse(first, out var parsed))
            {
                return false;
            }

            value = parsed;
        }

        return true;
    }

    /// <summary>The members of the body that Shoal reads; everything else (<c>UserId</c> included) is ignored.</summary>
    private sealed class Body
    {
        public long? MaxStreamingBitrate { get; set; }

        public long? StartTimeTicks { get; set; }

        public int? AudioStreamIndex { get; set; }

        public int? SubtitleStreamIndex { get; set; }

        public int? MaxAudioChannels { get; set; }

        public string? MediaSourceId { get; set; }

        public bool? EnableDirectPlay { get; set; }

        public bool? EnableDirectStream { get; set; }

        public bool? EnableTranscoding { get; set; }

        public bool? AllowVideoStreamCopy { get; set; }

        public bool? AllowAudioStreamCopy { get; set; }

        public bool? AlwaysBurnInSubtitleWhenTranscoding { get; set; }

        public DeviceProfile? DeviceProfile { get; set; }
    }
}
