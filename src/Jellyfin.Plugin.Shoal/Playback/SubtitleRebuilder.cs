#pragma warning disable SA1600 // internal plumbing; the public surface is documented
#pragma warning disable CA1819 // SubtitleResult.Body is the rebuilt payload, handed on once

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.Shoal.Playback;

/// <summary>
/// Why a subtitle body was refused. Fixed values only; nothing of the input is carried.
/// </summary>
public enum SubtitleFault
{
    /// <summary>No fault.</summary>
    None,

    /// <summary>The body is not valid UTF-8.</summary>
    Encoding,

    /// <summary>The body holds a NUL, or no recognizable header.</summary>
    Header,

    /// <summary>More cues or events than the cap.</summary>
    TooMany,

    /// <summary>The JSON is not exactly the shape core writes.</summary>
    Shape,
}

/// <summary>
/// The result of rebuilding one subtitle body.
/// </summary>
/// <param name="Fault">The rule that refused it, or none.</param>
/// <param name="Body">The rebuilt body (UTF-8), empty on a fault.</param>
/// <param name="Cues">How many cues or events were kept.</param>
/// <param name="Dropped">How many cues or events were dropped.</param>
public sealed record SubtitleResult(SubtitleFault Fault, byte[] Body, int Cues, int Dropped)
{
    /// <summary>Gets a value indicating whether the body was rebuilt.</summary>
    public bool Ok => Fault == SubtitleFault.None;

    internal static SubtitleResult Refuse(SubtitleFault fault) => new(fault, [], 0, 0);
}

/// <summary>
/// The cue-text rule of DESIGN 6.6a step 9: what survives of the text of a subtitle. The output always matches
/// <c>^(?:[^&lt;&gt;]|&lt;/?[ibu]&gt;)*$</c>.
/// </summary>
public static class CueText
{
    /// <summary>The most characters of one cue text.</summary>
    public const int MaxLength = 2000;

    private static readonly string[] Allowed = ["<i>", "</i>", "<b>", "</b>", "<u>", "</u>"];

    /// <summary>
    /// Cleans one piece of cue text.
    /// </summary>
    /// <param name="text">The text from the Peer.</param>
    /// <returns>The text that may be shown.</returns>
    public static string Clean(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var stripped = StripTags(DropControls(text));
        if (stripped.Length > MaxLength)
        {
            var cut = MaxLength;
            if (char.IsHighSurrogate(stripped[cut - 1]))
            {
                cut--;
            }

            stripped = StripTags(stripped[..cut]); // a cut can leave half a tag; the second pass removes the stray bracket
        }

        return stripped;
    }

    private static string DropControls(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if ((c < 0x20 && c != '\n') || c == 0x7F || c == 0x85 || c == 0x2028 || c == 0x2029 || (c >= 0x202A && c <= 0x202E) || (c >= 0x2066 && c <= 0x2069))
            {
                continue;
            }

            builder.Append(c);
        }

        return builder.ToString();
    }

    private static string StripTags(string text)
    {
        var builder = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '<')
            {
                var close = text.IndexOf('>', i + 1);
                if (close < 0)
                {
                    continue; // a stray bracket
                }

                var tag = text.Substring(i, close - i + 1);
                if (Allowed.Contains(tag, StringComparer.Ordinal))
                {
                    builder.Append(tag);
                }

                i = close;
                continue;
            }

            if (c == '>')
            {
                continue;
            }

            builder.Append(c);
        }

        return builder.ToString();
    }
}

/// <summary>
/// Rebuilds the subtitle bodies a Peer sends (DESIGN 6.6a steps 9 and 9a): nothing is copied except cue text after the cue-text rule.
/// </summary>
public static class SubtitleRebuilder
{
    /// <summary>The most cues or events one body may hold.</summary>
    public const int MaxCues = 20_000;

    private const long TicksPerMillisecond = 10_000;
    private const long OneHourTicks = 36_000_000_000L;
    private const string Time = "(?:[0-9]{2,3}:)?[0-5][0-9]:[0-5][0-9]\\.[0-9]{3}";

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private static readonly Regex VttTiming = Rx("^(" + Time + ") +--> +(" + Time + ")(?:[ \\t]+(.*))?\\z");
    private static readonly Regex VttTime = Rx("^(?:([0-9]{2,3}):)?([0-5][0-9]):([0-5][0-9])\\.([0-9]{3})\\z");
    private static readonly Regex SrtTiming = Rx("^([0-9]{2,3}:[0-5][0-9]:[0-5][0-9][,.][0-9]{3}) --> ([0-9]{2,3}:[0-5][0-9]:[0-5][0-9][,.][0-9]{3})(?:.*)\\z");
    private static readonly Regex SrtTime = Rx("^([0-9]{2,3}):([0-5][0-9]):([0-5][0-9])[,.]([0-9]{3})\\z");
    private static readonly Regex MapMpegts = Rx("^MPEGTS:([0-9]{1,12})\\z");
    private static readonly Regex MapLocal = Rx("^LOCAL:(" + Time + ")\\z");
    private static readonly Regex[] Settings =
    [
        Rx("^align:(?:start|center|end|left|right)\\z"),
        Rx("^line:-?[0-9]{1,3}(?:\\.[0-9]{1,3})?%?(?:,(?:start|center|end))?\\z"),
        Rx("^position:[0-9]{1,3}(?:\\.[0-9]{1,3})?%(?:,(?:line-left|center|line-right))?\\z"),
        Rx("^size:[0-9]{1,3}(?:\\.[0-9]{1,3})?%\\z"),
        Rx("^vertical:(?:rl|lr)\\z"),
    ];

    /// <summary>
    /// Gets every pattern the rebuilder uses, for the scan for <c>IgnoreCase</c>, <c>\d</c> and <c>\w</c>.
    /// </summary>
    internal static IReadOnlyList<Regex> AllPatterns { get; } = [VttTiming, VttTime, SrtTiming, SrtTime, MapMpegts, MapLocal, .. Settings];

    /// <summary>
    /// Rebuilds a WebVTT body (rule 9a). Every VTT Shoal emits goes through here.
    /// </summary>
    /// <param name="body">The Peer's bytes.</param>
    /// <param name="runtimeTicks">The title's runtime in ticks (cues beyond runtime plus one hour are dropped).</param>
    /// <returns>The rebuilt body.</returns>
    public static SubtitleResult Vtt(ReadOnlySpan<byte> body, long runtimeTicks)
    {
        if (!TryLines(body, out var lines, out var fault))
        {
            return SubtitleResult.Refuse(fault);
        }

        if (lines.Count == 0 || !IsVttHeader(lines[0]))
        {
            return SubtitleResult.Refuse(SubtitleFault.Header);
        }

        var limit = runtimeTicks + OneHourTicks;
        var output = new StringBuilder("WEBVTT\n");
        var blocks = Blocks(lines, 1, out var headerLines);
        string? mpegts = null;
        string? local = null;
        foreach (var line in headerLines)
        {
            if (line.StartsWith("X-TIMESTAMP-MAP=", StringComparison.Ordinal))
            {
                foreach (var part in line["X-TIMESTAMP-MAP=".Length..].Split(','))
                {
                    var m = MapMpegts.Match(part);
                    if (m.Success)
                    {
                        mpegts = m.Groups[1].Value;
                    }
                    else if (MapLocal.Match(part) is { Success: true } l && TryTime(VttTime, l.Groups[1].Value, out var ticks))
                    {
                        local = FormatTime(ticks, '.');
                    }
                }
            }
        }

        if (mpegts is not null && local is not null)
        {
            output.Append("X-TIMESTAMP-MAP=MPEGTS:").Append(mpegts).Append(",LOCAL:").Append(local).Append('\n');
        }

        output.Append('\n');
        var kept = 0;
        var dropped = 0;
        var seen = 0;
        foreach (var block in blocks)
        {
            if (block[0].StartsWith("NOTE", StringComparison.Ordinal) || block[0].StartsWith("STYLE", StringComparison.Ordinal) || block[0].StartsWith("REGION", StringComparison.Ordinal))
            {
                dropped++;
                continue;
            }

            seen++;
            if (seen > MaxCues)
            {
                return SubtitleResult.Refuse(SubtitleFault.TooMany);
            }

            var timingIndex = block[0].Contains("-->", StringComparison.Ordinal) ? 0 : 1;
            if (timingIndex >= block.Count || VttTiming.Match(block[timingIndex]) is not { Success: true } timing
                || !TryTime(VttTime, timing.Groups[1].Value, out var start) || !TryTime(VttTime, timing.Groups[2].Value, out var end)
                || start > end || end > limit)
            {
                dropped++;
                continue;
            }

            var text = CleanLines(block.Skip(timingIndex + 1));
            if (text.Length == 0)
            {
                dropped++;
                continue;
            }

            output.Append(FormatTime(start, '.')).Append(" --> ").Append(FormatTime(end, '.'));
            foreach (var token in (timing.Groups[3].Success ? timing.Groups[3].Value : string.Empty).Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries))
            {
                if (Settings.Any(s => s.IsMatch(token)))
                {
                    output.Append(' ').Append(token);
                }
            }

            output.Append('\n').Append(text).Append("\n\n");
            kept++;
        }

        return new SubtitleResult(SubtitleFault.None, Encoding.UTF8.GetBytes(output.ToString()), kept, dropped);
    }

    /// <summary>
    /// Rebuilds an SRT body (rule 9a).
    /// </summary>
    /// <param name="body">The Peer's bytes.</param>
    /// <param name="runtimeTicks">The title's runtime in ticks.</param>
    /// <returns>The rebuilt body.</returns>
    public static SubtitleResult Srt(ReadOnlySpan<byte> body, long runtimeTicks)
    {
        if (!TryLines(body, out var lines, out var fault))
        {
            return SubtitleResult.Refuse(fault);
        }

        var limit = runtimeTicks + OneHourTicks;
        var output = new StringBuilder();
        var kept = 0;
        var dropped = 0;
        var seen = 0;
        foreach (var block in Blocks(lines, 0, out _))
        {
            seen++;
            if (seen > MaxCues)
            {
                return SubtitleResult.Refuse(SubtitleFault.TooMany);
            }

            var timingIndex = block[0].Contains("-->", StringComparison.Ordinal) ? 0 : 1;
            if (timingIndex >= block.Count || SrtTiming.Match(block[timingIndex]) is not { Success: true } timing
                || !TryTime(SrtTime, timing.Groups[1].Value, out var start) || !TryTime(SrtTime, timing.Groups[2].Value, out var end)
                || start > end || end > limit)
            {
                dropped++;
                continue;
            }

            var text = CleanLines(block.Skip(timingIndex + 1));
            if (text.Length == 0)
            {
                dropped++;
                continue;
            }

            kept++;
            output.Append(kept.ToString(CultureInfo.InvariantCulture)).Append('\n')
                .Append(FormatTime(start, ',')).Append(" --> ").Append(FormatTime(end, ',')).Append('\n')
                .Append(text).Append("\n\n");
        }

        return new SubtitleResult(SubtitleFault.None, Encoding.UTF8.GetBytes(output.ToString()), kept, dropped);
    }

    /// <summary>
    /// Rebuilds a JSON body in the shape core writes (rule 9): <c>{"TrackEvents":[{"Id","Text","StartPositionTicks","EndPositionTicks"}]}</c>.
    /// </summary>
    /// <param name="body">The Peer's bytes.</param>
    /// <param name="runtimeTicks">The title's runtime in ticks.</param>
    /// <returns>The rebuilt body.</returns>
    public static SubtitleResult Json(ReadOnlySpan<byte> body, long runtimeTicks)
    {
        var limit = runtimeTicks + OneHourTicks;
        var events = new List<(string Text, long Start, long End)>();
        var dropped = 0;
        try
        {
            var reader = new Utf8JsonReader(body, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Disallow, AllowTrailingCommas = false, MaxDepth = 8 });
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                return SubtitleResult.Refuse(SubtitleFault.Shape);
            }

            var sawEvents = false;
            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                if (reader.TokenType != JsonTokenType.PropertyName)
                {
                    return SubtitleResult.Refuse(SubtitleFault.Shape);
                }

                if (!reader.ValueTextEquals("TrackEvents"u8))
                {
                    reader.Read();
                    reader.Skip(); // an unknown member is skipped and never stored
                    continue;
                }

                if (sawEvents)
                {
                    return SubtitleResult.Refuse(SubtitleFault.Shape);
                }

                sawEvents = true;
                if (!reader.Read() || reader.TokenType != JsonTokenType.StartArray)
                {
                    return SubtitleResult.Refuse(SubtitleFault.Shape);
                }

                var count = 0;
                while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                {
                    if (reader.TokenType != JsonTokenType.StartObject)
                    {
                        return SubtitleResult.Refuse(SubtitleFault.Shape);
                    }

                    count++;
                    if (count > MaxCues)
                    {
                        return SubtitleResult.Refuse(SubtitleFault.TooMany);
                    }

                    if (!TryEvent(ref reader, out var text, out var start, out var end))
                    {
                        return SubtitleResult.Refuse(SubtitleFault.Shape);
                    }

                    if (start < 0 || end < start || end > limit)
                    {
                        dropped++;
                        continue;
                    }

                    events.Add((text, start, end));
                }
            }

            if (!sawEvents || reader.TokenType != JsonTokenType.EndObject || reader.Read())
            {
                return SubtitleResult.Refuse(SubtitleFault.Shape); // a missing member, or something after the object
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException or FormatException)
        {
            return SubtitleResult.Refuse(SubtitleFault.Shape);
        }

        using var stream = new System.IO.MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Encoder = JavaScriptEncoder.Default }))
        {
            writer.WriteStartObject();
            writer.WriteStartArray("TrackEvents");
            var id = 0;
            foreach (var (text, start, end) in events)
            {
                writer.WriteStartObject();
                writer.WriteString("Id", (++id).ToString(CultureInfo.InvariantCulture));
                writer.WriteString("Text", CueText.Clean(text));
                writer.WriteNumber("StartPositionTicks", start);
                writer.WriteNumber("EndPositionTicks", end);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return new SubtitleResult(SubtitleFault.None, stream.ToArray(), events.Count, dropped);
    }

    private static Regex Rx(string pattern) => new(pattern, RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(100));

    private static bool TryEvent(ref Utf8JsonReader reader, out string text, out long start, out long end)
    {
        text = string.Empty;
        start = 0;
        end = 0;
        var seen = 0;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                return false;
            }

            var bit = reader.ValueTextEquals("Id"u8) ? 1 : reader.ValueTextEquals("Text"u8) ? 2 : reader.ValueTextEquals("StartPositionTicks"u8) ? 4 : reader.ValueTextEquals("EndPositionTicks"u8) ? 8 : 0;
            if (!reader.Read())
            {
                return false;
            }

            if (bit == 0)
            {
                reader.Skip();
                continue;
            }

            if ((seen & bit) != 0)
            {
                return false; // a duplicate known member
            }

            seen |= bit;
            switch (bit)
            {
                case 1 when reader.TokenType == JsonTokenType.String:
                    break;
                case 2 when reader.TokenType == JsonTokenType.String:
                    text = reader.GetString() ?? string.Empty;
                    break;
                case 4 when reader.TokenType == JsonTokenType.Number && reader.TryGetInt64(out var s):
                    start = s;
                    break;
                case 8 when reader.TokenType == JsonTokenType.Number && reader.TryGetInt64(out var e):
                    end = e;
                    break;
                default:
                    return false; // a wrong token type
            }
        }

        return seen == 15;
    }

    private static bool TryLines(ReadOnlySpan<byte> body, out List<string> lines, out SubtitleFault fault)
    {
        lines = [];
        fault = SubtitleFault.None;
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
            fault = SubtitleFault.Encoding;
            return false;
        }

        if (text.Contains('\0', StringComparison.Ordinal))
        {
            fault = SubtitleFault.Header;
            return false;
        }

        lines = [.. text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n')];
        return true;
    }

    private static bool IsVttHeader(string line) =>
        line.StartsWith("WEBVTT", StringComparison.Ordinal) && (line.Length == 6 || line[6] is ' ' or '\t');

    // Splits the lines from <paramref name="first"/> into blocks at blank lines; for a VTT the first block is the header.
    private static List<List<string>> Blocks(List<string> lines, int first, out List<string> header)
    {
        header = [];
        var blocks = new List<List<string>>();
        var current = new List<string>();
        var isHeader = first == 1;
        for (var i = first; i < lines.Count; i++)
        {
            if (lines[i].Length == 0)
            {
                if (current.Count > 0)
                {
                    if (isHeader)
                    {
                        header = current;
                    }
                    else
                    {
                        blocks.Add(current);
                    }

                    current = [];
                }

                isHeader = false;
                continue;
            }

            current.Add(lines[i]);
        }

        if (current.Count > 0)
        {
            if (isHeader)
            {
                header = current;
            }
            else
            {
                blocks.Add(current);
            }
        }

        return blocks;
    }

    private static string CleanLines(IEnumerable<string> lines)
    {
        var cleaned = lines.Select(static l => CueText.Clean(l)).Where(static l => l.Trim().Length > 0);
        return string.Join('\n', cleaned);
    }

    private static bool TryTime(Regex pattern, string text, out long ticks)
    {
        ticks = 0;
        var m = pattern.Match(text);
        if (!m.Success)
        {
            return false;
        }

        var hours = m.Groups[1].Success && m.Groups[1].Length > 0 ? long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
        var minutes = long.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        var seconds = long.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
        var millis = long.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture);
        var totalMs = (((((hours * 60) + minutes) * 60) + seconds) * 1000) + millis;
        ticks = totalMs * TicksPerMillisecond;
        return true;
    }

    private static string FormatTime(long ticks, char separator)
    {
        var totalMs = ticks / TicksPerMillisecond;
        var ms = totalMs % 1000;
        var totalSeconds = totalMs / 1000;
        return string.Create(CultureInfo.InvariantCulture, $"{totalSeconds / 3600:00}:{(totalSeconds / 60) % 60:00}:{totalSeconds % 60:00}{separator}{ms:000}");
    }
}
