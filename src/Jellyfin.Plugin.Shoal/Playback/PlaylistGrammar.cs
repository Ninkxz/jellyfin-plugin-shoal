#pragma warning disable SA1600 // internal plumbing of a public static grammar
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.Shoal.Playback;

/// <summary>
/// The three kinds of playlist the grammar reads (DESIGN 6.4).
/// </summary>
public enum PlaylistKind
{
    /// <summary>The master playlist (<c>master.m3u8</c>).</summary>
    Master,

    /// <summary>The media playlist of the video (<c>main.m3u8</c>).</summary>
    Variant,

    /// <summary>A subtitle media playlist (<c>subtitles.m3u8</c>).</summary>
    Subtitle,
}

/// <summary>
/// How the grammar ended.
/// </summary>
public enum PlaylistOutcome
{
    /// <summary>The playlist was rebuilt.</summary>
    Ok,

    /// <summary>The playlist broke a rule: answer 502.</summary>
    Rejected,

    /// <summary>The caller's token is not a Jellyfin access token: answer 403 (rule O2).</summary>
    Forbidden,
}

/// <summary>
/// Which rule rejected a playlist. Fixed values only; nothing of the input is carried.
/// </summary>
public enum PlaylistFault
{
    /// <summary>No fault.</summary>
    None,

    /// <summary>O2: the token is not 32 lowercase hex characters.</summary>
    Token,

    /// <summary>I8: the playlist is larger than its byte cap.</summary>
    TooLarge,

    /// <summary>I1: invalid UTF-8.</summary>
    Encoding,

    /// <summary>I2: a control, DEL, NEL, LS or PS character.</summary>
    Control,

    /// <summary>I3: a line longer than 8 KB.</summary>
    LineLength,

    /// <summary>I8: more lines than the cap.</summary>
    LineCount,

    /// <summary>I4: the first line is not <c>#EXTM3U</c>.</summary>
    Header,

    /// <summary>A URI line that no rule accepts at its position.</summary>
    Uri,

    /// <summary>A tag that must never appear (<c>EXT-X-KEY</c>, <c>EXT-X-SESSION-KEY</c>) or a PLAYLIST-TYPE other than VOD.</summary>
    ForbiddenTag,

    /// <summary>A structure error: a tag where a URI line must be, a missing <c>EXT-X-ENDLIST</c>, no variant, no segment, a tag after the end.</summary>
    Structure,

    /// <summary>A tag a rule recognizes by name but cannot read (MAP, EXTINF, STREAM-INF attribute list).</summary>
    Syntax,

    /// <summary>A value out of range or not equal to the grant's (ticks, durations, ids).</summary>
    Value,

    /// <summary>I8: more segments, or more total duration, than the caps.</summary>
    Caps,

    /// <summary>O4: the canary found something that belongs to the Peer in the output.</summary>
    Canary,
}

/// <summary>
/// What the grammar needs to know about the grant behind a playlist.
/// </summary>
/// <param name="Token">The caller's own access token (rule O2).</param>
/// <param name="PeerMediaSourceId">The grant's media source id at the Peer.</param>
/// <param name="PeerPlaySessionId">The grant's play session id at the Peer, or empty.</param>
/// <param name="PeerDeviceId">Home's device id at the Peer.</param>
/// <param name="RuntimeTicks">The grant's runtime in ticks (0 when unknown).</param>
/// <param name="Streams">The grant's sanitized streams (subtitle renditions are rebuilt from these).</param>
/// <param name="Canary">Rule O4: returns true when the output holds something that belongs to the Peer; null to skip.</param>
public sealed record PlaylistContext(
    string Token,
    Guid PeerMediaSourceId,
    string PeerPlaySessionId,
    string PeerDeviceId,
    long RuntimeTicks,
    IReadOnlyList<GrantTrack> Streams,
    Func<string, bool>? Canary = null);

/// <summary>
/// The result of reading one playlist.
/// </summary>
/// <param name="Outcome">How it ended.</param>
/// <param name="Fault">The rule that rejected it, or none.</param>
/// <param name="Text">The rebuilt playlist (UTF-8, <c>\n</c> line endings); empty unless <paramref name="Outcome"/> is <see cref="PlaylistOutcome.Ok"/>.</param>
/// <param name="VariantQuery">For a master: the Peer's query for <c>main.m3u8</c>, decoded, without credentials.</param>
/// <param name="Dropped">How many lines were dropped.</param>
/// <param name="Segments">How many segments the playlist lists.</param>
/// <param name="Seconds">The summed segment duration.</param>
public sealed record PlaylistResult(
    PlaylistOutcome Outcome,
    PlaylistFault Fault,
    string Text,
    IReadOnlyList<KeyValuePair<string, string>> VariantQuery,
    int Dropped,
    int Segments,
    double Seconds)
{
    internal static PlaylistResult Reject(PlaylistFault fault, int dropped = 0) =>
        new(PlaylistOutcome.Rejected, fault, string.Empty, [], dropped, 0, 0);
}

/// <summary>
/// The closed HLS grammar (DESIGN 6.4). Every line is either recognized by exactly one rule and rebuilt from parsed values, or dropped, or the whole
/// playlist is rejected. No input text reaches the output except values that matched a capture pattern and were converted, and Home's own strings.
/// </summary>
public static class PlaylistGrammar
{
    private const int MaxLineBytes = 8 * 1024;
    private const long TicksPerHour = 36_000_000_000L;
    private const long MaxSegmentTicks = 6_000_000_000L;
    private const double MaxSegmentSeconds = 600;

    // Q: no '/', ':', '?', '#', '@', '\', quotes or spaces, so no scheme, authority or path can hide in a query (rule 6.4).
    private const string Q = "[A-Za-z0-9._~%=&,+-]*";
    private const int MaxQuery = 4096; // {0,4096} makes NonBacktracking build an automaton over its 10,000 node limit, so the length is checked on the capture

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    // Every pattern below: explicit ASCII classes, '^' and '\z', CultureInvariant, NonBacktracking, no IgnoreCase, 100 ms (rule I7).
    private static readonly Regex Version = Rx(@"^#EXT-X-VERSION:([0-9]{1,2})\z");
    private static readonly Regex TargetDuration = Rx(@"^#EXT-X-TARGETDURATION:([0-9]{1,5})\z");
    private static readonly Regex MediaSequence = Rx(@"^#EXT-X-MEDIA-SEQUENCE:([0-9]{1,9})\z");
    private static readonly Regex PlaylistType = Rx(@"^#EXT-X-PLAYLIST-TYPE:VOD\z");
    private static readonly Regex DiscontinuitySequence = Rx(@"^#EXT-X-DISCONTINUITY-SEQUENCE:([0-9]{1,9})\z");
    private static readonly Regex ExtInf = Rx(@"^#EXTINF:([0-9]{1,6}(?:[.,][0-9]{1,17})?(?:E-[0-9]{1,3})?),(?: nodesc)?\z");
    private static readonly Regex Map = Rx("^#EXT-X-MAP:URI=\"hls1/main/-1\\.mp4\\?(" + Q + ")\"\\z");
    private static readonly Regex Media = Rx("^#EXT-X-MEDIA:TYPE=SUBTITLES,GROUP-ID=\"subs\",NAME=\".*\",DEFAULT=(YES|NO),FORCED=(YES|NO),AUTOSELECT=YES,URI=\"([0-9a-fA-F]{32}|[0-9a-fA-F-]{36})/Subtitles/([0-9]{1,3})/subtitles\\.m3u8\\?(" + Q + ")\",LANGUAGE=\".*\"\\z");
    private static readonly Regex UriMaster = Rx(@"^main\.m3u8\?(" + Q + @")\z");
    private static readonly Regex UriVariant = Rx(@"^hls1/main/(0|[1-9][0-9]{0,5})\.(ts|mp4)\?(" + Q + @")\z");
    private static readonly Regex UriSubtitle = Rx(@"^stream\.vtt\?(" + Q + @")\z");
    private static readonly Regex Ticks = Rx(@"^[0-9]{1,19}\z");
    private static readonly Regex Bandwidth = Rx(@"^[0-9]{1,10}\z");
    private static readonly Regex VideoRange = Rx(@"^(SDR|PQ|HLG)\z");
    private static readonly Regex Codecs = Rx(@"^[A-Za-z0-9.,+-]{1,256}\z");
    private static readonly Regex SupplementalCodecs = Rx(@"^[A-Za-z0-9.,/+-]{1,256}\z");
    private static readonly Regex Resolution = Rx(@"^[0-9]{1,5}x[0-9]{1,5}\z");
    private static readonly Regex FrameRate = Rx(@"^[0-9]{1,3}(?:\.[0-9]{1,3})?\z");
    private static readonly Regex Language = Rx(@"^[a-z]{2,3}\z");
    private static readonly Regex TokenShape = Rx(@"^[0-9a-f]{32}\z");

    private enum Pending
    {
        None,
        Extinf,
        FirstVariant,
        LaterVariant,
    }

    /// <summary>
    /// Gets every pattern the grammar uses, for the test that scans them for <c>IgnoreCase</c>, <c>\d</c>, <c>\w</c> and unanchored ends.
    /// </summary>
    internal static IReadOnlyList<Regex> AllPatterns { get; } =
    [
        Version, TargetDuration, MediaSequence, PlaylistType, DiscontinuitySequence, ExtInf, Map, Media, UriMaster, UriVariant, UriSubtitle,
        Ticks, Bandwidth, VideoRange, Codecs, SupplementalCodecs, Resolution, FrameRate, Language, TokenShape,
    ];

    /// <summary>
    /// Reads one playlist and rebuilds it.
    /// </summary>
    /// <param name="kind">Which playlist it is.</param>
    /// <param name="body">The Peer's bytes (already bounded by the read stream).</param>
    /// <param name="context">The grant behind it.</param>
    /// <returns>The result; <see cref="PlaylistResult.Text"/> is the playlist to send.</returns>
    public static PlaylistResult Process(PlaylistKind kind, ReadOnlySpan<byte> body, PlaylistContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!IsMatch(TokenShape, context.Token))
        {
            return new PlaylistResult(PlaylistOutcome.Forbidden, PlaylistFault.Token, string.Empty, [], 0, 0, 0);
        }

        var caps = CapsOf(kind);
        if (body.Length > caps.Bytes)
        {
            return PlaylistResult.Reject(PlaylistFault.TooLarge);
        }

        if (body.Length >= 3 && body[0] == 0xEF && body[1] == 0xBB && body[2] == 0xBF)
        {
            body = body[3..];
        }

        string text;
        try
        {
            text = StrictUtf8.GetString(body);
        }
        catch (ArgumentException)
        {
            return PlaylistResult.Reject(PlaylistFault.Encoding);
        }

        try
        {
            var run = new Reader(kind, caps, context);
            return run.Read(text);
        }
        catch (RegexMatchTimeoutException)
        {
            return PlaylistResult.Reject(PlaylistFault.Syntax);
        }
    }

    private static Regex Rx(string pattern) => new(pattern, RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(100));

    private static bool IsMatch(Regex regex, string value) => regex.IsMatch(value);

    private static Caps CapsOf(PlaylistKind kind) => kind switch
    {
        PlaylistKind.Master => new Caps(256 * 1024, 64, 0),
        PlaylistKind.Variant => new Caps(8 * 1024 * 1024, 100_000, 20_000),
        _ => new Caps(2 * 1024 * 1024, 20_000, 10_000),
    };

    private static bool IsForbiddenChar(char c) => c < 0x20 || c == 0x7F || c == 0x85 || c == 0x2028 || c == 0x2029;

    private static List<KeyValuePair<string, string>> ParseQuery(string query)
    {
        var pairs = new List<KeyValuePair<string, string>>();
        foreach (var part in query.Split('&'))
        {
            if (part.Length == 0)
            {
                continue;
            }

            var eq = part.IndexOf('=', StringComparison.Ordinal);
            var key = eq < 0 ? part : part[..eq];
            var value = eq < 0 ? string.Empty : part[(eq + 1)..];
            pairs.Add(new KeyValuePair<string, string>(Decode(key), Decode(value)));
        }

        return pairs;
    }

    private static string Decode(string value)
    {
        if (!value.Contains('%', StringComparison.Ordinal))
        {
            return value;
        }

        var bytes = new List<byte>(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '%' && i + 2 < value.Length + 0 && IsHex(value[i + 1]) && IsHex(value[i + 2]))
            {
                bytes.Add(byte.Parse(value.AsSpan(i + 1, 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture));
                i += 2;
            }
            else
            {
                bytes.AddRange(Encoding.UTF8.GetBytes(value[i].ToString()));
            }
        }

        return Encoding.UTF8.GetString([.. bytes]);
    }

    private static bool IsHex(char c) => c is (>= '0' and <= '9') or (>= 'a' and <= 'f') or (>= 'A' and <= 'F');

    private static bool TryTicks(string value, out long ticks)
    {
        ticks = 0;
        return IsMatch(Ticks, value) && long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out ticks);
    }

    private static bool TryGuid(string value, out Guid id)
    {
        id = Guid.Empty;
        return (value.Length == 32 && Guid.TryParseExact(value, "N", out id)) || (value.Length == 36 && Guid.TryParseExact(value, "D", out id));
    }

    private static string ToInvariant(long value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Finds the single value of a key (compared ordinal-ignore-case), or reports that it is missing or repeated.
    /// </summary>
    private static bool TryOne(List<KeyValuePair<string, string>> pairs, string key, out string value)
    {
        value = string.Empty;
        var found = pairs.Where(p => string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase)).ToList();
        if (found.Count != 1)
        {
            return false;
        }

        value = found[0].Value;
        return true;
    }

    private readonly record struct Caps(int Bytes, int Lines, int Segments);

    private sealed class Reader
    {
        private readonly PlaylistKind _kind;
        private readonly Caps _caps;
        private readonly PlaylistContext _ctx;
        private readonly StringBuilder _out = new();
        private readonly HashSet<string> _singletons = new(StringComparer.Ordinal);
        private readonly List<string> _mediaLines = [];
        private readonly HashSet<int> _mediaIndexes = [];
        private Pending _pending;
        private double _extinf;
        private int _dropped;
        private int _segments;
        private double _seconds;
        private bool _ended;
        private bool _mapSeen;
        private string? _variantAttributes;
        private string? _variantUri;
        private List<KeyValuePair<string, string>> _variantQuery = [];
        private bool _variantWantsSubtitles;

        public Reader(PlaylistKind kind, Caps caps, PlaylistContext ctx)
        {
            _kind = kind;
            _caps = caps;
            _ctx = ctx;
        }

        public PlaylistResult Read(string text)
        {
            var lineNumber = 0;
            var start = 0;
            while (start < text.Length)
            {
                var end = text.IndexOf('\n', start);
                var terminated = end >= 0;
                var line = terminated ? text[start..end] : text[start..];
                start = terminated ? end + 1 : text.Length;
                lineNumber++;
                if (lineNumber > _caps.Lines)
                {
                    return PlaylistResult.Reject(PlaylistFault.LineCount, _dropped);
                }

                if (terminated && line.Length > 0 && line[^1] == '\r')
                {
                    line = line[..^1];
                }

                if (line.Length > MaxLineBytes || Encoding.UTF8.GetByteCount(line) > MaxLineBytes)
                {
                    return PlaylistResult.Reject(PlaylistFault.LineLength, _dropped);
                }

                if (line.Any(IsForbiddenChar))
                {
                    return PlaylistResult.Reject(PlaylistFault.Control, _dropped);
                }

                if (lineNumber == 1)
                {
                    if (!string.Equals(line, "#EXTM3U", StringComparison.Ordinal))
                    {
                        return PlaylistResult.Reject(PlaylistFault.Header, _dropped);
                    }

                    continue;
                }

                if (line.Length == 0)
                {
                    continue;
                }

                var fault = line[0] == '#' ? Tag(line) : UriLine(line);
                if (fault != PlaylistFault.None)
                {
                    return PlaylistResult.Reject(fault, _dropped);
                }
            }

            return Finish();
        }

        private PlaylistResult Finish()
        {
            if (_pending != Pending.None)
            {
                return PlaylistResult.Reject(PlaylistFault.Structure, _dropped);
            }

            var body = new StringBuilder("#EXTM3U\n");
            if (_kind == PlaylistKind.Master)
            {
                if (_variantAttributes is null || _variantUri is null)
                {
                    return PlaylistResult.Reject(PlaylistFault.Structure, _dropped);
                }

                foreach (var media in _mediaLines)
                {
                    body.Append(media).Append('\n');
                }

                var attributes = _variantAttributes;
                if (_variantWantsSubtitles && _mediaLines.Count > 0)
                {
                    attributes += ",SUBTITLES=\"subs\"";
                }

                body.Append("#EXT-X-STREAM-INF:").Append(attributes).Append('\n').Append(_variantUri).Append('\n');
            }
            else
            {
                if (!_ended || _segments == 0)
                {
                    return PlaylistResult.Reject(PlaylistFault.Structure, _dropped);
                }

                body.Append(_out);
            }

            var result = body.ToString();
            if (_ctx.Canary is { } canary && canary(result))
            {
                return PlaylistResult.Reject(PlaylistFault.Canary, _dropped);
            }

            return new PlaylistResult(PlaylistOutcome.Ok, PlaylistFault.None, result, _variantQuery, _dropped, _segments, _seconds);
        }

        private PlaylistFault Tag(string line)
        {
            if (!line.StartsWith("#EXT", StringComparison.Ordinal))
            {
                _dropped++; // a comment
                return PlaylistFault.None;
            }

            var colon = line.IndexOf(':', StringComparison.Ordinal);
            var name = colon < 0 ? line : line[..colon];
            if (_ended && name != "#EXT-X-ENDLIST" && _kind != PlaylistKind.Master)
            {
                return IsDroppedName(name) ? Drop() : PlaylistFault.Structure; // the end tag is the last tag
            }

            switch (name)
            {
                case "#EXTM3U":
                    return Drop(); // line 1 only
                case "#EXT-X-KEY":
                case "#EXT-X-SESSION-KEY":
                    return PlaylistFault.ForbiddenTag;
                case "#EXT-X-VERSION":
                    return Singleton(name, Version, line, static (m, o) => o.Append("#EXT-X-VERSION:").Append(m.Groups[1].Value), versionRange: true);
                case "#EXT-X-TARGETDURATION":
                    return Singleton(name, TargetDuration, line, static (m, o) => o.Append("#EXT-X-TARGETDURATION:").Append(long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture)));
                case "#EXT-X-MEDIA-SEQUENCE":
                    return Singleton(name, MediaSequence, line, static (m, o) => o.Append("#EXT-X-MEDIA-SEQUENCE:").Append(long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture)));
                case "#EXT-X-PLAYLIST-TYPE":
                    return PlaylistTypeTag(line);
                case "#EXT-X-DISCONTINUITY-SEQUENCE":
                    return _kind == PlaylistKind.Variant
                        ? Singleton(name, DiscontinuitySequence, line, static (m, o) => o.Append("#EXT-X-DISCONTINUITY-SEQUENCE:").Append(long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture)))
                        : Drop();
                case "#EXT-X-INDEPENDENT-SEGMENTS":
                case "#EXT-X-DISCONTINUITY":
                    return _kind == PlaylistKind.Variant && string.Equals(line, name, StringComparison.Ordinal) ? Emit(line) : Drop();
                case "#EXTINF":
                    return ExtInfTag(line);
                case "#EXT-X-MAP":
                    return _kind == PlaylistKind.Variant ? MapTag(line) : Drop();
                case "#EXT-X-ENDLIST":
                    return EndList(line);
                case "#EXT-X-STREAM-INF":
                    return _kind == PlaylistKind.Master ? StreamInf(line) : Drop();
                case "#EXT-X-MEDIA":
                    return _kind == PlaylistKind.Master ? MediaTag(line) : Drop();
                default:
                    return Drop(); // every other tag, named or not
            }
        }

        private static bool IsDroppedName(string name) => name is not ("#EXTINF" or "#EXT-X-ENDLIST" or "#EXT-X-STREAM-INF" or "#EXT-X-MEDIA" or "#EXT-X-MAP" or "#EXT-X-VERSION"
            or "#EXT-X-TARGETDURATION" or "#EXT-X-MEDIA-SEQUENCE" or "#EXT-X-PLAYLIST-TYPE" or "#EXT-X-DISCONTINUITY" or "#EXT-X-DISCONTINUITY-SEQUENCE"
            or "#EXT-X-INDEPENDENT-SEGMENTS" or "#EXT-X-KEY" or "#EXT-X-SESSION-KEY");

        private PlaylistFault Drop()
        {
            _dropped++;
            return PlaylistFault.None;
        }

        private PlaylistFault Emit(string line)
        {
            if (_pending != Pending.None)
            {
                return PlaylistFault.Structure;
            }

            _out.Append(line).Append('\n');
            return PlaylistFault.None;
        }

        private PlaylistFault Singleton(string name, Regex pattern, string line, Action<Match, StringBuilder> write, bool versionRange = false)
        {
            if (_kind == PlaylistKind.Master)
            {
                return Drop();
            }

            var match = pattern.Match(line);
            if (!match.Success)
            {
                return Drop(); // not the form the rule reads: dropped, never copied
            }

            if (versionRange && !(int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var version) && version is >= 3 and <= 7))
            {
                return Drop();
            }

            if (_pending != Pending.None)
            {
                return PlaylistFault.Structure;
            }

            if (!_singletons.Add(name))
            {
                return Drop();
            }

            write(match, _out);
            _out.Append('\n');
            return PlaylistFault.None;
        }

        private PlaylistFault PlaylistTypeTag(string line)
        {
            if (_kind == PlaylistKind.Master)
            {
                return Drop();
            }

            if (!PlaylistType.IsMatch(line))
            {
                return PlaylistFault.ForbiddenTag; // anything but VOD (live and event playlists are not supported)
            }

            if (_pending != Pending.None)
            {
                return PlaylistFault.Structure;
            }

            if (!_singletons.Add("#EXT-X-PLAYLIST-TYPE"))
            {
                return Drop();
            }

            _out.Append("#EXT-X-PLAYLIST-TYPE:VOD\n");
            return PlaylistFault.None;
        }

        private PlaylistFault ExtInfTag(string line)
        {
            if (_kind == PlaylistKind.Master)
            {
                return Drop();
            }

            if (_pending != Pending.None)
            {
                return PlaylistFault.Structure; // an EXTINF without its URI line
            }

            var match = ExtInf.Match(line);
            if (!match.Success)
            {
                return PlaylistFault.Syntax;
            }

            var raw = match.Groups[1].Value.Replace(',', '.');
            if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) || !double.IsFinite(seconds) || seconds < 0 || seconds > MaxSegmentSeconds)
            {
                return PlaylistFault.Value;
            }

            if (_segments + 1 > _caps.Segments)
            {
                return PlaylistFault.Caps;
            }

            _extinf = seconds;
            _pending = Pending.Extinf;
            return PlaylistFault.None;
        }

        private PlaylistFault MapTag(string line)
        {
            if (_pending != Pending.None)
            {
                return PlaylistFault.Structure;
            }

            var mapMatch = Map.Match(line);
            if (!mapMatch.Success || mapMatch.Groups[1].Value.Length > MaxQuery)
            {
                return PlaylistFault.Syntax; // unquoted, duplicated or foreign URI attribute (F-PL-15, F-PL-16)
            }

            if (_mapSeen)
            {
                return Drop();
            }

            _mapSeen = true;
            _out.Append("#EXT-X-MAP:URI=\"hls1/main/-1.mp4?ApiKey=").Append(_ctx.Token).Append("&runtimeTicks=0&actualSegmentLengthTicks=0\"\n");
            return PlaylistFault.None;
        }

        private PlaylistFault EndList(string line)
        {
            if (_kind == PlaylistKind.Master)
            {
                return Drop();
            }

            if (_pending != Pending.None || !string.Equals(line, "#EXT-X-ENDLIST", StringComparison.Ordinal))
            {
                return PlaylistFault.Structure;
            }

            _ended = true;
            _out.Append("#EXT-X-ENDLIST\n");
            return PlaylistFault.None;
        }

        // Rule SI. The attribute list is parsed strictly; only the kept attributes are rebuilt, in a fixed order.
        private PlaylistFault StreamInf(string line)
        {
            if (_pending != Pending.None)
            {
                return PlaylistFault.Structure;
            }

            const string Prefix = "#EXT-X-STREAM-INF:";
            if (!line.StartsWith(Prefix, StringComparison.Ordinal) || !TryAttributes(line[Prefix.Length..], out var attributes))
            {
                return PlaylistFault.Syntax;
            }

            if (_variantAttributes is not null)
            {
                _pending = Pending.LaterVariant; // dropped with its URI line
                _dropped++;
                return PlaylistFault.None;
            }

            if (!attributes.TryGetValue("BANDWIDTH", out var bandwidth) || bandwidth.Quoted || !Bandwidth.IsMatch(bandwidth.Value))
            {
                return PlaylistFault.Syntax;
            }

            var built = new StringBuilder("BANDWIDTH=").Append(long.Parse(bandwidth.Value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture));
            if (attributes.TryGetValue("AVERAGE-BANDWIDTH", out var average) && !average.Quoted && Bandwidth.IsMatch(average.Value))
            {
                built.Append(",AVERAGE-BANDWIDTH=").Append(long.Parse(average.Value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture));
            }

            if (attributes.TryGetValue("VIDEO-RANGE", out var range) && !range.Quoted && VideoRange.IsMatch(range.Value))
            {
                built.Append(",VIDEO-RANGE=").Append(range.Value);
            }

            if (attributes.TryGetValue("CODECS", out var codecs) && codecs.Quoted && Codecs.IsMatch(codecs.Value))
            {
                built.Append(",CODECS=\"").Append(codecs.Value).Append('"');
            }

            if (attributes.TryGetValue("SUPPLEMENTAL-CODECS", out var supplemental) && supplemental.Quoted && SupplementalCodecs.IsMatch(supplemental.Value))
            {
                built.Append(",SUPPLEMENTAL-CODECS=\"").Append(supplemental.Value).Append('"');
            }

            if (attributes.TryGetValue("RESOLUTION", out var resolution) && !resolution.Quoted && Resolution.IsMatch(resolution.Value))
            {
                built.Append(",RESOLUTION=").Append(resolution.Value);
            }

            if (attributes.TryGetValue("FRAME-RATE", out var frameRate) && !frameRate.Quoted && FrameRate.IsMatch(frameRate.Value))
            {
                built.Append(",FRAME-RATE=").Append(frameRate.Value);
            }

            _variantWantsSubtitles = attributes.TryGetValue("SUBTITLES", out var subtitles) && subtitles.Quoted && string.Equals(subtitles.Value, "subs", StringComparison.Ordinal);
            _variantAttributes = built.ToString();
            _pending = Pending.FirstVariant;
            return PlaylistFault.None;
        }

        private static bool TryAttributes(string list, out Dictionary<string, Attribute> attributes)
        {
            attributes = new Dictionary<string, Attribute>(StringComparer.Ordinal);
            var i = 0;
            while (true)
            {
                var nameStart = i;
                while (i < list.Length && (list[i] is (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '-'))
                {
                    i++;
                }

                if (i == nameStart || i >= list.Length || list[i] != '=')
                {
                    return false;
                }

                var name = list[nameStart..i];
                i++;
                string value;
                bool quoted;
                if (i < list.Length && list[i] == '"')
                {
                    var close = list.IndexOf('"', i + 1);
                    if (close < 0)
                    {
                        return false;
                    }

                    value = list[(i + 1)..close];
                    quoted = true;
                    i = close + 1;
                }
                else
                {
                    var valueStart = i;
                    while (i < list.Length && (list[i] is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '.' or '-'))
                    {
                        i++;
                    }

                    if (i == valueStart)
                    {
                        return false;
                    }

                    value = list[valueStart..i];
                    quoted = false;
                }

                if (!attributes.TryAdd(name, new Attribute(value, quoted)))
                {
                    return false; // a duplicate name
                }

                if (i == list.Length)
                {
                    return true;
                }

                if (list[i] != ',')
                {
                    return false;
                }

                i++;
                if (i == list.Length)
                {
                    return false; // a trailing comma
                }
            }
        }

        // Rule MD: only the exact form core writes; rebuilt from grant data; a line that fails is dropped.
        private PlaylistFault MediaTag(string line)
        {
            if (_pending != Pending.None)
            {
                return PlaylistFault.Structure;
            }

            var match = Media.Match(line);
            if (!match.Success
                || !TryGuid(match.Groups[3].Value, out var sourceId)
                || sourceId != _ctx.PeerMediaSourceId
                || !int.TryParse(match.Groups[4].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var index)
                || match.Groups[5].Value.Length > MaxQuery)
            {
                return Drop();
            }

            var track = _ctx.Streams.FirstOrDefault(s => s.Index == index && string.Equals(s.Type, "Subtitle", StringComparison.Ordinal) && s.IsText);
            if (track is null || !_mediaIndexes.Add(index))
            {
                return Drop();
            }

            var title = (track.Title ?? string.Empty).Replace("\"", string.Empty, StringComparison.Ordinal);
            title = title.Length > 64 ? title[..64] : title;
            if (title.Length == 0)
            {
                title = "Subtitle " + index.ToString(CultureInfo.InvariantCulture);
            }

            var language = track.Language is { } l && Language.IsMatch(l) ? l : "und";
            _mediaLines.Add(
                "#EXT-X-MEDIA:TYPE=SUBTITLES,GROUP-ID=\"subs\",NAME=\"" + title + "\",DEFAULT=" + match.Groups[1].Value + ",FORCED=" + match.Groups[2].Value
                + ",AUTOSELECT=YES,URI=\"subs/" + index.ToString(CultureInfo.InvariantCulture) + "/subtitles.m3u8?ApiKey=" + _ctx.Token + "\",LANGUAGE=\"" + language + "\"");
            return PlaylistFault.None;
        }

        private PlaylistFault UriLine(string line)
        {
            var pending = _pending;
            _pending = Pending.None;
            switch (pending)
            {
                case Pending.FirstVariant:
                case Pending.LaterVariant:
                    return MasterUri(line, store: pending == Pending.FirstVariant);
                case Pending.Extinf when _kind == PlaylistKind.Variant:
                    return VariantUri(line);
                case Pending.Extinf when _kind == PlaylistKind.Subtitle:
                    return SubtitleUri(line);
                default:
                    return PlaylistFault.Uri;
            }
        }

        // U-M
        private PlaylistFault MasterUri(string line, bool store)
        {
            var match = UriMaster.Match(line);
            if (!match.Success)
            {
                return PlaylistFault.Uri;
            }

            if (match.Groups[1].Value.Length > MaxQuery)
            {
                return PlaylistFault.Uri;
            }

            var pairs = ParseQuery(match.Groups[1].Value);
            foreach (var (key, value) in pairs)
            {
                if (string.Equals(key, "MediaSourceId", StringComparison.OrdinalIgnoreCase) && !(TryGuid(value, out var id) && id == _ctx.PeerMediaSourceId))
                {
                    return PlaylistFault.Value;
                }

                if (string.Equals(key, "PlaySessionId", StringComparison.OrdinalIgnoreCase) && !string.Equals(value, _ctx.PeerPlaySessionId, StringComparison.Ordinal))
                {
                    return PlaylistFault.Value;
                }

                if (string.Equals(key, "DeviceId", StringComparison.OrdinalIgnoreCase) && !string.Equals(value, _ctx.PeerDeviceId, StringComparison.Ordinal))
                {
                    return PlaylistFault.Value;
                }
            }

            if (store)
            {
                _variantQuery = [.. pairs.Where(static p => !string.Equals(p.Key, "ApiKey", StringComparison.OrdinalIgnoreCase) && !string.Equals(p.Key, "api_key", StringComparison.OrdinalIgnoreCase))];
                _variantUri = "main.m3u8?ApiKey=" + _ctx.Token;
            }
            else
            {
                _dropped++;
            }

            return PlaylistFault.None;
        }

        // U-V
        private PlaylistFault VariantUri(string line)
        {
            var match = UriVariant.Match(line);
            if (!match.Success)
            {
                return PlaylistFault.Uri;
            }

            if (match.Groups[3].Value.Length > MaxQuery)
            {
                return PlaylistFault.Uri;
            }

            var pairs = ParseQuery(match.Groups[3].Value);
            if (!TryOne(pairs, "runtimeTicks", out var runtimeText) || !TryOne(pairs, "actualSegmentLengthTicks", out var lengthText)
                || !TryTicks(runtimeText, out var runtime) || !TryTicks(lengthText, out var length))
            {
                return PlaylistFault.Value;
            }

            if (runtime > _ctx.RuntimeTicks + TicksPerHour || length > MaxSegmentTicks)
            {
                return PlaylistFault.Value;
            }

            if (!AddSegment())
            {
                return PlaylistFault.Caps;
            }

            _out.Append("#EXTINF:").Append(_extinf.ToString("0.000###", CultureInfo.InvariantCulture)).Append(",\n");
            _out.Append("hls1/main/").Append(match.Groups[1].Value).Append('.').Append(match.Groups[2].Value)
                .Append("?ApiKey=").Append(_ctx.Token).Append("&runtimeTicks=").Append(ToInvariant(runtime)).Append("&actualSegmentLengthTicks=").Append(ToInvariant(length)).Append('\n');
            return PlaylistFault.None;
        }

        // U-S
        private PlaylistFault SubtitleUri(string line)
        {
            var match = UriSubtitle.Match(line);
            if (!match.Success)
            {
                return PlaylistFault.Uri;
            }

            if (match.Groups[1].Value.Length > MaxQuery)
            {
                return PlaylistFault.Uri;
            }

            var pairs = ParseQuery(match.Groups[1].Value);
            if (!TryOne(pairs, "StartPositionTicks", out var startText) || !TryOne(pairs, "EndPositionTicks", out var endText)
                || !TryTicks(startText, out var startTicks) || !TryTicks(endText, out var endTicks))
            {
                return PlaylistFault.Value;
            }

            if (startTicks > endTicks || endTicks > _ctx.RuntimeTicks + TicksPerHour)
            {
                return PlaylistFault.Value;
            }

            if (!AddSegment())
            {
                return PlaylistFault.Caps;
            }

            _out.Append("#EXTINF:").Append(_extinf.ToString("0.000###", CultureInfo.InvariantCulture)).Append(",\n");
            _out.Append("stream.vtt?ApiKey=").Append(_ctx.Token).Append("&StartPositionTicks=").Append(ToInvariant(startTicks)).Append("&EndPositionTicks=").Append(ToInvariant(endTicks)).Append('\n');
            return PlaylistFault.None;
        }

        private bool AddSegment()
        {
            _segments++;
            _seconds += _extinf;
            var limit = (_ctx.RuntimeTicks / 10_000_000d * 1.1) + 3600d;
            return _segments <= _caps.Segments && _seconds <= limit;
        }

        private readonly record struct Attribute(string Value, bool Quoted);
    }
}
