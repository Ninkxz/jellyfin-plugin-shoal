using System;
using System.Collections.Generic;
using System.Linq;
using MediaBrowser.Model.Dlna;

namespace Jellyfin.Plugin.Shoal.Playback;

/// <summary>
/// Builds the DeviceProfile that goes to the Peer (DESIGN 6.2.2): a <b>new</b> object from the client's, with Shoal's own name, capped lists and
/// strings that match a closed alphabet. The client's name, model and manufacturer never reach the Peer, and an entry with any odd string is
/// dropped rather than repaired.
/// </summary>
public static class DeviceProfileRebuilder
{
    /// <summary>The name every forwarded profile has.</summary>
    public const string ProfileName = "Shoal";

    private const int MaxStringChars = 256;
    private const int MaxConditions = 32;
    private const int OneGigabit = 1_000_000_000;

    private static readonly HashSet<string> ServedSubtitleFormats = new(StringComparer.OrdinalIgnoreCase)
    {
        "srt", "subrip", "ass", "ssa", "vtt", "webvtt", "js", "json",
    };

    /// <summary>
    /// Rebuilds a profile.
    /// </summary>
    /// <param name="client">The client's profile.</param>
    /// <param name="maxStreamingBitrate">The computed streaming limit (DESIGN 6.2.1), or null.</param>
    /// <param name="hlsSubtitlesEnabled">The HLS-subtitle kill switch; when false, <c>Hls</c> subtitle entries are removed.</param>
    /// <param name="cap">The connection's quality limit (PLAN v2.9 K51), or null: a box adds a video codec profile with required Width and Height conditions.</param>
    /// <returns>The new profile.</returns>
    public static DeviceProfile Rebuild(DeviceProfile client, int? maxStreamingBitrate, bool hlsSubtitlesEnabled, QualityCap? cap = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        var codecs = (client.CodecProfiles ?? []).Select(Codec).OfType<CodecProfile>().Take(64).ToList();
        if (cap?.ProfileRule() is { } rule)
        {
            codecs.Add(rule); // after the cap of 64, so the client's entries cannot push it out
        }

        return new DeviceProfile
        {
            Name = ProfileName,
            Id = null,
            MaxStreamingBitrate = maxStreamingBitrate,
            MaxStaticBitrate = MinOf(client.MaxStaticBitrate, maxStreamingBitrate),
            MusicStreamingTranscodingBitrate = Clamp(client.MusicStreamingTranscodingBitrate),
            MaxStaticMusicBitrate = Clamp(client.MaxStaticMusicBitrate),
            DirectPlayProfiles = [.. (client.DirectPlayProfiles ?? []).Select(DirectPlay).OfType<DirectPlayProfile>().Take(64)],
            TranscodingProfiles = [.. (client.TranscodingProfiles ?? []).Select(Transcoding).OfType<TranscodingProfile>().Take(32)],
            ContainerProfiles = [.. (client.ContainerProfiles ?? []).Select(Container).OfType<ContainerProfile>().Take(32)],
            CodecProfiles = [.. codecs],
            SubtitleProfiles = [.. (client.SubtitleProfiles ?? []).Select(p => Subtitle(p, hlsSubtitlesEnabled)).OfType<SubtitleProfile>().Take(32)],
        };
    }

    /// <summary>
    /// Whether a string may appear in a forwarded profile: at most 256 characters of <c>A-Z a-z 0-9 space , . _ | : + / -</c>.
    /// </summary>
    /// <param name="value">The string; null is fine (an optional member).</param>
    /// <returns>True when it is acceptable.</returns>
    public static bool IsSafe(string? value)
    {
        if (value is null)
        {
            return true;
        }

        if (value.Length > MaxStringChars)
        {
            return false;
        }

        foreach (var c in value)
        {
            if (!(c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or ' ' or ',' or '.' or '_' or '|' or ':' or '+' or '/' or '-'))
            {
                return false;
            }
        }

        return true;
    }

    private static int? Clamp(int? value) => value is null ? null : Math.Clamp(value.Value, 0, OneGigabit);

    private static int? MinOf(int? client, int? limit)
    {
        var clamped = Clamp(client);
        return (clamped, limit) switch
        {
            (null, _) => limit,
            (_, null) => clamped,
            _ => Math.Min(clamped.Value, limit.Value),
        };
    }

    private static ProfileCondition[]? Conditions(ProfileCondition[]? source)
    {
        if (source is null)
        {
            return null;
        }

        var result = new List<ProfileCondition>();
        foreach (var condition in source.Where(static c => c is not null).Take(MaxConditions))
        {
            if (!IsSafe(condition.Value))
            {
                return null; // one odd condition value drops the entry that holds it
            }

            result.Add(new ProfileCondition(condition.Condition, condition.Property, condition.Value, condition.IsRequired));
        }

        return [.. result];
    }

    private static DirectPlayProfile? DirectPlay(DirectPlayProfile? p) =>
        p is null || !IsSafe(p.Container) || !IsSafe(p.AudioCodec) || !IsSafe(p.VideoCodec)
            ? null
            : new DirectPlayProfile { Container = p.Container ?? string.Empty, AudioCodec = p.AudioCodec, VideoCodec = p.VideoCodec, Type = p.Type };

    private static TranscodingProfile? Transcoding(TranscodingProfile? p)
    {
        if (p is null || !IsSafe(p.Container) || !IsSafe(p.VideoCodec) || !IsSafe(p.AudioCodec) || !IsSafe(p.MaxAudioChannels))
        {
            return null;
        }

        var conditions = Conditions(p.Conditions);
        if (p.Conditions is not null && conditions is null)
        {
            return null;
        }

        return new TranscodingProfile
        {
            Container = p.Container ?? string.Empty,
            Type = p.Type,
            VideoCodec = p.VideoCodec ?? string.Empty,
            AudioCodec = p.AudioCodec ?? string.Empty,
            Protocol = p.Protocol,
            EstimateContentLength = p.EstimateContentLength,
            EnableMpegtsM2TsMode = p.EnableMpegtsM2TsMode,
            TranscodeSeekInfo = p.TranscodeSeekInfo,
            CopyTimestamps = p.CopyTimestamps,
            Context = p.Context,
            EnableSubtitlesInManifest = p.EnableSubtitlesInManifest,
            MaxAudioChannels = p.MaxAudioChannels,
            MinSegments = Math.Clamp(p.MinSegments, 0, 100),
            SegmentLength = Math.Clamp(p.SegmentLength, 0, 600),
            Conditions = conditions ?? [],
            EnableAudioVbrEncoding = p.EnableAudioVbrEncoding,
        };
    }

    private static ContainerProfile? Container(ContainerProfile? p)
    {
        if (p is null || !IsSafe(p.Container) || !IsSafe(p.SubContainer))
        {
            return null;
        }

        var conditions = Conditions(p.Conditions);
        return p.Conditions is not null && conditions is null
            ? null
            : new ContainerProfile { Type = p.Type, Container = p.Container ?? string.Empty, SubContainer = p.SubContainer, Conditions = conditions ?? [] };
    }

    private static CodecProfile? Codec(CodecProfile? p)
    {
        if (p is null || !IsSafe(p.Codec) || !IsSafe(p.Container) || !IsSafe(p.SubContainer))
        {
            return null;
        }

        var conditions = Conditions(p.Conditions);
        var apply = Conditions(p.ApplyConditions);
        return (p.Conditions is not null && conditions is null) || (p.ApplyConditions is not null && apply is null)
            ? null
            : new CodecProfile
            {
                Type = p.Type,
                Conditions = conditions ?? [],
                ApplyConditions = apply ?? [],
                Codec = p.Codec ?? string.Empty,
                Container = p.Container ?? string.Empty,
                SubContainer = p.SubContainer,
            };
    }

    private static SubtitleProfile? Subtitle(SubtitleProfile? p, bool hlsSubtitlesEnabled)
    {
        if (p is null || !IsSafe(p.Format) || !IsSafe(p.DidlMode) || !IsSafe(p.Language) || !IsSafe(p.Container))
        {
            return null;
        }

        if (p.Method == SubtitleDeliveryMethod.Hls && !hlsSubtitlesEnabled)
        {
            return null; // the Peer then picks External or burn-in
        }

        if (p.Method == SubtitleDeliveryMethod.External && (p.Format is null || !ServedSubtitleFormats.Contains(p.Format)))
        {
            return null; // Shoal serves only these External formats: bitmap, smi, ttml, xsub and unknown ones never get a DeliveryUrl
        }

        return new SubtitleProfile { Format = p.Format, Method = p.Method, DidlMode = p.DidlMode, Language = p.Language, Container = p.Container };
    }
}
