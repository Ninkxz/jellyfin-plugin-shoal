using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Jellyfin.Plugin.Shoal.Sync;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.Shoal.Playback;

/// <summary>
/// The streams of a rebuilt media source: the whitelisted <see cref="MediaStream"/> objects for the client and the lighter records the grant keeps.
/// </summary>
/// <param name="Streams">The sanitized streams, at most 128, with unique indexes.</param>
/// <param name="GrantStreams">The same streams as grant records.</param>
public sealed record SanitizedStreams(IReadOnlyList<MediaStream> Streams, IReadOnlyList<GrantTrack> GrantStreams);

/// <summary>
/// Rebuilds the <c>MediaStream</c> list of a Peer's PlaybackInfo answer, field by field (DESIGN 6.2.3). jellyfin-web puts <c>DisplayTitle</c> into
/// <c>innerHTML</c> without escaping and other clients are unknown, so every field has a rule and the Peer's <c>DisplayTitle</c> is never used: core
/// computes it on Home from the sanitized fields. A stream that cannot be made safe is dropped, never repaired.
/// </summary>
public static class StreamSanitizer
{
    /// <summary>The most streams kept.</summary>
    public const int MaxStreams = 128;

    private static readonly HashSet<string> ServedSubtitleFormats = new(StringComparer.Ordinal) { "srt", "subrip", "ass", "ssa", "vtt", "webvtt", "js", "json" };

    /// <summary>
    /// Rebuilds the streams.
    /// </summary>
    /// <param name="peerStreams">The Peer's streams.</param>
    /// <param name="localItemId">The local item id, which the subtitle <c>DeliveryUrl</c> names.</param>
    /// <param name="callerToken">The caller's own token, echoed into the <c>DeliveryUrl</c> as core does (clients' players send no headers).</param>
    /// <returns>The sanitized streams.</returns>
    public static SanitizedStreams Rebuild(IEnumerable<MediaStream>? peerStreams, Guid localItemId, string callerToken)
    {
        var streams = new List<MediaStream>();
        var records = new List<GrantTrack>();
        var seen = new HashSet<int>();
        foreach (var source in peerStreams ?? [])
        {
            if (streams.Count >= MaxStreams)
            {
                break;
            }

            if (source is null
                || source.Type is not (MediaStreamType.Video or MediaStreamType.Audio or MediaStreamType.Subtitle)
                || source.Index is < 0 or > 127
                || !seen.Add(source.Index)
                || IsAbsoluteUrl(source.DeliveryUrl)
                || IsAbsoluteUrl(source.Path)
                || source.IsExternalUrl == true)
            {
                continue; // an absolute URL would hand clients a Peer-chosen address (StreamInfo.cs:1263-1274)
            }

            var clean = Sanitize(source, localItemId, callerToken);
            streams.Add(clean);
            records.Add(new GrantTrack(
                clean.Index,
                clean.Type.ToString(),
                clean.Codec,
                clean.Language,
                clean.Title,
                source.Type == MediaStreamType.Subtitle && source.IsTextSubtitleStream,
                clean.IsExternal,
                clean.IsDefault,
                clean.IsForced));
        }

        return new SanitizedStreams(streams, records);
    }

    /// <summary>
    /// The title rule of a stream: the text rules of DESIGN 5.5 plus the removal of quotes, ampersands and backticks, at most 64 characters.
    /// </summary>
    /// <param name="title">The Peer's title.</param>
    /// <returns>The title, or null when nothing is left.</returns>
    public static string? Title(string? title)
    {
        var text = PeerText.Clean(title, 64);
        if (text.Length == 0)
        {
            return null;
        }

        var cleaned = new string([.. text.Where(static c => c is not ('"' or '\'' or '&' or '`'))]).Trim();
        return cleaned.Length == 0 ? null : cleaned;
    }

    private static MediaStream Sanitize(MediaStream s, Guid localItemId, string callerToken)
    {
        var clean = new MediaStream
        {
            Type = s.Type,
            Index = s.Index,
            Codec = Pattern(s.Codec?.ToLowerInvariant(), 1, 32, static c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_' or '.' or '-'),
            CodecTag = Pattern(s.CodecTag, 1, 8, static c => c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9')),
            Profile = Pattern(s.Profile, 1, 48, static c => c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or ' ' or '.' or ':' or '+' or '_' or '/' or '-'),
            Language = Language(s.Language),
            Title = Title(s.Title),
            ChannelLayout = Pattern(s.ChannelLayout, 1, 32, static c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '.' or '(' or ')' or ' ' or '_' or '-'),
            ColorRange = Token(s.ColorRange),
            ColorSpace = Token(s.ColorSpace),
            ColorTransfer = Token(s.ColorTransfer),
            ColorPrimaries = Token(s.ColorPrimaries),
            PixelFormat = Token(s.PixelFormat),
            AspectRatio = AspectRatio(s.AspectRatio),
            Channels = Range(s.Channels, 0, 32),
            BitRate = Range(s.BitRate, 0, 1_000_000_000),
            BitDepth = Range(s.BitDepth, 0, 64),
            RefFrames = Range(s.RefFrames, 0, 64),
            Height = Range(s.Height, 0, 16_384),
            Width = Range(s.Width, 0, 16_384),
            SampleRate = Range(s.SampleRate, 0, 768_000),
            Level = s.Level is { } level && double.IsFinite(level) && level is >= 0 and <= 1000 ? level : null,
            AverageFrameRate = Frames(s.AverageFrameRate),
            RealFrameRate = Frames(s.RealFrameRate),
            DvVersionMajor = Range(s.DvVersionMajor, 0, 255),
            DvVersionMinor = Range(s.DvVersionMinor, 0, 255),
            DvProfile = Range(s.DvProfile, 0, 255),
            DvLevel = Range(s.DvLevel, 0, 255),
            RpuPresentFlag = Range(s.RpuPresentFlag, 0, 1),
            ElPresentFlag = Range(s.ElPresentFlag, 0, 1),
            BlPresentFlag = Range(s.BlPresentFlag, 0, 1),
            DvBlSignalCompatibilityId = Range(s.DvBlSignalCompatibilityId, 0, 255),
            Rotation = Range(s.Rotation, -360, 360),
            IsDefault = s.IsDefault,
            IsForced = s.IsForced,
            IsHearingImpaired = s.IsHearingImpaired,
            IsOriginal = s.IsOriginal,
            IsInterlaced = s.IsInterlaced,
            IsAVC = s.IsAVC,
            IsAnamorphic = s.IsAnamorphic,
            Hdr10PlusPresentFlag = s.Hdr10PlusPresentFlag,
            SupportsExternalStream = s.SupportsExternalStream,
            IsExternal = s.IsExternal,
            DeliveryMethod = s.DeliveryMethod,
            Path = null,
        };

        if (s.Type == MediaStreamType.Subtitle && s.DeliveryMethod == SubtitleDeliveryMethod.External)
        {
            var format = FormatOf(s.DeliveryUrl);
            if (format is not null)
            {
                clean.DeliveryUrl = string.Create(
                    CultureInfo.InvariantCulture,
                    $"/Shoal/Subtitles/{localItemId:N}/{s.Index}/Stream.{format}?ApiKey={Uri.EscapeDataString(callerToken)}");
            }
            else
            {
                clean.DeliveryMethod = null; // a format nobody serves: no URL, and no claim of an external file
                clean.IsExternal = false;
            }
        }

        return clean;
    }

    private static bool IsAbsoluteUrl(string? value) =>
        !string.IsNullOrEmpty(value) && (value.Contains("://", StringComparison.Ordinal) || value.StartsWith("//", StringComparison.Ordinal));

    /// <summary>The subtitle format named by the last segment <c>Stream.&lt;fmt&gt;</c> of the Peer's delivery URL, if Shoal serves it.</summary>
    private static string? FormatOf(string? deliveryUrl)
    {
        if (string.IsNullOrEmpty(deliveryUrl))
        {
            return null;
        }

        var path = deliveryUrl.Split('?')[0];
        var last = path[(path.LastIndexOf('/') + 1)..];
        if (!last.StartsWith("Stream.", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var format = last["Stream.".Length..].ToLowerInvariant();
        return ServedSubtitleFormats.Contains(format) ? format : null;
    }

    private static string? Pattern(string? value, int min, int max, Func<char, bool> allowed) =>
        value is not null && value.Length >= min && value.Length <= max && value.All(allowed) ? value : null;

    private static string? Token(string? value) => Pattern(value, 1, 32, static c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_' or '-');

    private static string? Language(string? value)
    {
        if (value is null || value.Length is < 2 or > 12)
        {
            return null;
        }

        var parts = value.Split('-');
        var primary = parts[0];
        if (parts.Length > 2 || primary.Length is < 2 or > 3 || !primary.All(static c => c is >= 'a' and <= 'z'))
        {
            return null;
        }

        return parts.Length == 1 || (parts[1].Length is >= 2 and <= 8 && parts[1].All(static c => c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9'))) ? value : null;
    }

    private static string? AspectRatio(string? value)
    {
        if (value is null)
        {
            return null;
        }

        var parts = value.Split(':');
        return parts.Length == 2 && parts.All(static p => p.Length is >= 1 and <= 5 && p.All(static c => c is >= '0' and <= '9')) ? value : null;
    }

    private static int? Range(int? value, int min, int max) => value is { } v && v >= min && v <= max ? v : null;

    private static float? Frames(float? value) => value is { } v && float.IsFinite(v) && v is >= 0 and <= 1000 ? v : null;
}
