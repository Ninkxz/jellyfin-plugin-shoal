using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.Shoal.Playback;
using Xunit;

namespace Jellyfin.Plugin.Shoal.Tests;

/// <summary>
/// K18 acceptance (DESIGN 6.6a steps 9 and 9a, W4-8, W4-9): the cue-text rule and its fuzz property, the VTT, SRT and JSON rebuilds with the hostile
/// fixtures (extra members, depth 9, 20,001 events, script and img tags, bidi overrides, NUL, ticks out of range, STYLE with url()).
/// </summary>
public sealed class SubtitleRebuilderTests
{
    private const long Runtime = 20_000_000_000L;

    private static readonly char Rlo = (char)0x202E;
    private static readonly string Emoji = char.ConvertFromUtf32(0x1F600);

    private static readonly Regex Invariant = new("^(?:[^<>]|</?[ibu]>)*$", RegexOptions.CultureInvariant);

    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    private static string Text(SubtitleResult result) => Encoding.UTF8.GetString(result.Body);

    private static string Vtt(params string[] lines) => string.Join('\n', lines) + "\n";

    // --- the cue-text rule

    [Theory]
    [InlineData("plain text", "plain text")]
    [InlineData("<i>a</i> <b>b</b> <u>c</u>", "<i>a</i> <b>b</b> <u>c</u>")]
    [InlineData("<script>alert(1)</script>x", "alert(1)x")]
    [InlineData("<img src=x onerror=alert(1)>hi", "hi")]
    [InlineData("<font color=\"red\">red</font>", "red")]
    [InlineData("<I>upper</I>", "upper")]
    [InlineData("<i >spaced</i>", "spaced</i>")]
    [InlineData("<i/>x", "x")]
    [InlineData("a < b > c", "a  c")]
    [InlineData("1 > 0", "1  0")]
    [InlineData("a < 3", "a  3")]
    [InlineData("<<i>>", "")]
    [InlineData("{\\an8}top", "{\\an8}top")]
    [InlineData("tab\there", "tabhere")]
    [InlineData("nul\0x", "nulx")]
    [InlineData("keep\nnewline", "keep\nnewline")]
    public void CueText_FollowsTheRule(string input, string expected) => Assert.Equal(expected, CueText.Clean(input));

    [Fact]
    public void CueText_DropsBidiControlsAndSeparators()
    {
        var bad = string.Concat(new[] { 0x202A, 0x202B, 0x202C, 0x202D, 0x202E, 0x2066, 0x2067, 0x2068, 0x2069, 0x7F, 0x85, 0x2028, 0x2029 }.Select(static c => ((char)c).ToString()));

        Assert.Equal("ab", CueText.Clean("a" + bad + "b"));
    }

    [Fact]
    public void CueText_CutsAt2000Characters_OnARuneBoundary_AndNeverLeavesHalfATag()
    {
        var emoji = string.Concat(Enumerable.Repeat("\U0001F600", 1500)); // 3000 UTF-16 units

        var cut = CueText.Clean(emoji);
        var halfTag = CueText.Clean(new string('x', 1998) + "<i>y</i>");

        Assert.True(cut.Length <= CueText.MaxLength);
        Assert.False(char.IsHighSurrogate(cut[^1]));
        Assert.Matches(Invariant, halfTag);
        Assert.StartsWith(new string('x', 1998), halfTag, StringComparison.Ordinal);
    }

    [Fact]
    public void CueText_Fuzz_TheOutputAlwaysMatchesTheInvariant()
    {
        var alphabet = new[] { "<", ">", "</", "<i>", "</i>", "<b>", "<u>", "</u>", "<script>", "&", "a", "b", " ", "\n", "\t", "\0", Rlo.ToString(), Emoji, "{\\an8}", "/", "\"", "'", "=", "-->" };
        var random = new Random(20261015);
        for (var round = 0; round < 3000; round++)
        {
            var builder = new StringBuilder();
            for (var i = random.Next(0, 80); i > 0; i--)
            {
                builder.Append(alphabet[random.Next(alphabet.Length)]);
            }

            var output = CueText.Clean(builder.ToString());

            Assert.Matches(Invariant, output);
            Assert.True(output.Length <= CueText.MaxLength);
            Assert.DoesNotContain("\0", output, StringComparison.Ordinal);
            Assert.DoesNotContain(Rlo.ToString(), output, StringComparison.Ordinal);
            Assert.Equal(output, CueText.Clean(output)); // idempotent
        }
    }

    // --- WebVTT

    [Fact]
    public void Vtt_IsRebuiltLineByLine()
    {
        var input = Vtt("WEBVTT - a title", "Kind: captions", "Language: en", string.Empty, "intro", "00:01.000 --> 00:02.500 align:center line:90% position:50%,center size:80% vertical:rl evil:1 region:r1", "<i>Hello</i> <script>x</script>", "second line", string.Empty, "00:00:03.000 --> 00:00:04.000", "Bye");

        var result = SubtitleRebuilder.Vtt(Bytes(input), Runtime);

        Assert.True(result.Ok);
        Assert.Equal(
            "WEBVTT\n\n00:00:01.000 --> 00:00:02.500 align:center line:90% position:50%,center size:80% vertical:rl\n<i>Hello</i> x\nsecond line\n\n00:00:03.000 --> 00:00:04.000\nBye\n\n",
            Text(result));
        Assert.Equal(2, result.Cues);
    }

    [Theory]
    [InlineData("WEBVTT")]
    [InlineData("WEBVTT title")]
    [InlineData("WEBVTT\ttitle")]
    public void Vtt_TheHeaderMayCarryATitle(string header) =>
        Assert.True(SubtitleRebuilder.Vtt(Bytes(header + "\n\n00:01.000 --> 00:02.000\nx\n"), Runtime).Ok);

    [Theory]
    [InlineData("")]
    [InlineData("WEBVTTX\n\n00:01.000 --> 00:02.000\nx\n")]
    [InlineData("webvtt\n\n00:01.000 --> 00:02.000\nx\n")]
    [InlineData("\nWEBVTT\n\n")]
    [InlineData("1\n00:01,000 --> 00:02,000\nx\n")]
    public void Vtt_ABodyWithoutTheHeader_IsRefused(string input) =>
        Assert.Equal(SubtitleFault.Header, SubtitleRebuilder.Vtt(Bytes(input), Runtime).Fault);

    [Fact]
    public void Vtt_ABomIsSkipped_NulAndBadUtf8AreRefused()
    {
        var body = Bytes("WEBVTT\n\n00:01.000 --> 00:02.000\nx\n");

        Assert.True(SubtitleRebuilder.Vtt([0xEF, 0xBB, 0xBF, .. body], Runtime).Ok);
        Assert.Equal(SubtitleFault.Header, SubtitleRebuilder.Vtt(Bytes("WEBVTT\n\n00:01.000 --> 00:02.000\nx\0y\n"), Runtime).Fault);
        Assert.Equal(SubtitleFault.Encoding, SubtitleRebuilder.Vtt([.. body, 0xFF, 0xFE], Runtime).Fault);
    }

    [Fact]
    public void Vtt_TimestampMap_IsKeptOnlyInTheExactForm_AndReserialized()
    {
        var ok = SubtitleRebuilder.Vtt(Bytes(Vtt("WEBVTT", "X-TIMESTAMP-MAP=LOCAL:00:00:00.000,MPEGTS:900000", string.Empty, "00:01.000 --> 00:02.000", "x")), Runtime);
        var evil = SubtitleRebuilder.Vtt(Bytes(Vtt("WEBVTT", "X-TIMESTAMP-MAP=MPEGTS:900000,LOCAL:http://evil/", string.Empty, "00:01.000 --> 00:02.000", "x")), Runtime);
        var half = SubtitleRebuilder.Vtt(Bytes(Vtt("WEBVTT", "X-TIMESTAMP-MAP=MPEGTS:1234567890123", string.Empty, "00:01.000 --> 00:02.000", "x")), Runtime);

        Assert.StartsWith("WEBVTT\nX-TIMESTAMP-MAP=MPEGTS:900000,LOCAL:00:00:00.000\n\n", Text(ok), StringComparison.Ordinal);
        Assert.StartsWith("WEBVTT\n\n", Text(evil), StringComparison.Ordinal);
        Assert.StartsWith("WEBVTT\n\n", Text(half), StringComparison.Ordinal);
        Assert.DoesNotContain("evil", Text(evil), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("NOTE a comment\nmore", "x")]
    [InlineData("STYLE\n::cue { background-image: url(http://evil.example/p.png) }", "x")]
    [InlineData("REGION\nid:r1\nwidth:40%", "x")]
    [InlineData("NOTEBOOK", "x")]
    public void Vtt_NoteStyleAndRegionBlocks_AreDroppedWhole(string block, string text)
    {
        var input = "WEBVTT\n\n" + block + "\n\n00:01.000 --> 00:02.000\n" + text + "\n";

        var result = SubtitleRebuilder.Vtt(Bytes(input), Runtime);

        Assert.True(result.Ok);
        Assert.Equal("WEBVTT\n\n00:00:01.000 --> 00:00:02.000\nx\n\n", Text(result));
        Assert.DoesNotContain("evil", Text(result), StringComparison.Ordinal);
        Assert.DoesNotContain("url(", Text(result), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("00:01.000 --> 00:02.000x")]
    [InlineData("0:01.000 --> 00:02.000")]
    [InlineData("00:61.000 --> 00:62.000")]
    [InlineData("00:02.000 --> 00:01.000")]
    [InlineData("00:01.00 --> 00:02.000")]
    [InlineData("00:01.000-->00:02.000")]
    [InlineData("00:01,000 --> 00:02,000")]
    [InlineData("0000:01.000 --> 0000:02.000")]
    [InlineData("99:00:00.000 --> 99:00:01.000")]
    [InlineData("not a timing line")]
    public void Vtt_ABadTimingLineOrTimesOutOfRange_DropTheCue(string timing)
    {
        var input = "WEBVTT\n\n" + timing + "\nbad\n\n00:05.000 --> 00:06.000\ngood\n";

        var result = SubtitleRebuilder.Vtt(Bytes(input), Runtime);

        Assert.True(result.Ok);
        Assert.Equal("WEBVTT\n\n00:05.000 --> 00:06.000\ngood\n\n".Replace("00:05.000", "00:00:05.000").Replace("00:06.000", "00:00:06.000"), Text(result));
        Assert.Equal(1, result.Dropped);
    }

    [Fact]
    public void Vtt_CueSettings_AreKeptTokenByToken_FromAClosedList()
    {
        var input = "WEBVTT\n\n00:01.000 --> 00:02.000 align:start align:evil line:-1 line:5,start line:abc position:10% position:10 size:100% size:1e9 vertical:lr foo:bar url(http://evil/)\nx\n";

        var result = SubtitleRebuilder.Vtt(Bytes(input), Runtime);

        Assert.Equal("WEBVTT\n\n00:00:01.000 --> 00:00:02.000 align:start line:-1 line:5,start position:10% size:100% vertical:lr\nx\n\n", Text(result));
    }

    [Fact]
    public void Vtt_CueTextIsCleaned_EveryLine()
    {
        var input = "WEBVTT\n\n00:01.000 --> 00:02.000\n<font face='x'>a</font>\n<c.yellow>b</c>\n" + Rlo + "cba\n<img src=x onerror=alert(1)>\n";

        var result = SubtitleRebuilder.Vtt(Bytes(input), Runtime);

        Assert.Equal("WEBVTT\n\n00:00:01.000 --> 00:00:02.000\na\nb\ncba\n\n", Text(result));
    }

    [Fact]
    public void Vtt_ACueEndingBeyondRuntimePlusAnHour_IsDropped()
    {
        var input = "WEBVTT\n\n00:01.000 --> 00:02.000\nin\n\n10:00:00.000 --> 10:00:01.000\nout\n";

        var result = SubtitleRebuilder.Vtt(Bytes(input), Runtime); // runtime 2000 s + 1 h = 5600 s

        Assert.Equal(1, result.Cues);
        Assert.Equal(1, result.Dropped);
    }

    [Fact]
    public void Vtt_CueNumberTwentyThousandAndOne_Is502()
    {
        string Cues(int n) => "WEBVTT\n\n" + string.Concat(Enumerable.Range(0, n).Select(static i => $"00:01.000 --> 00:02.000\nc{i}\n\n"));

        Assert.True(SubtitleRebuilder.Vtt(Bytes(Cues(20_000)), Runtime).Ok);
        Assert.Equal(SubtitleFault.TooMany, SubtitleRebuilder.Vtt(Bytes(Cues(20_001)), Runtime).Fault);
    }

    [Fact]
    public void Vtt_LinesMaySplitOnCrLfOrLoneCr()
    {
        var input = "WEBVTT\r\n\r\n00:01.000 --> 00:02.000\r\nx\r\r00:03.000 --> 00:04.000\ry\r\n";

        var result = SubtitleRebuilder.Vtt(Bytes(input), Runtime);

        Assert.Equal(2, result.Cues);
        Assert.DoesNotContain('\r', Text(result));
    }

    // --- SRT

    [Fact]
    public void Srt_IsRebuilt_WithHomesCounterAndCommas()
    {
        var input = "7\n00:00:01,000 --> 00:00:02,500 X1:10 X2:20 Y1:30\n<font color=\"#ff0000\">Hello</font>\n{\\an8}top\n\n9\n00:00:03.000 --> 00:00:04.000\nBye\n";

        var result = SubtitleRebuilder.Srt(Bytes(input), Runtime);

        Assert.True(result.Ok);
        Assert.Equal("1\n00:00:01,000 --> 00:00:02,500\nHello\n{\\an8}top\n\n2\n00:00:03,000 --> 00:00:04,000\nBye\n\n", Text(result));
    }

    [Theory]
    [InlineData("00:00:01,000 -> 00:00:02,000")]
    [InlineData("00:00:01,000 --> 00:00:02")]
    [InlineData("1:00:01,000 --> 00:00:02,000")]
    [InlineData("00:00:03,000 --> 00:00:02,000")]
    [InlineData("00:70:01,000 --> 00:70:02,000")]
    [InlineData("00:00:01,000  --> 00:00:02,000")]
    public void Srt_ABadTiming_DropsTheCue(string timing)
    {
        var result = SubtitleRebuilder.Srt(Bytes("1\n" + timing + "\nbad\n\n2\n00:00:05,000 --> 00:00:06,000\ngood\n"), Runtime);

        Assert.Equal("1\n00:00:05,000 --> 00:00:06,000\ngood\n\n", Text(result));
    }

    [Fact]
    public void Srt_NulBadUtf8AndTooManyCues_AreRefused()
    {
        string Cues(int n) => string.Concat(Enumerable.Range(0, n).Select(static i => $"{i}\n00:00:01,000 --> 00:00:02,000\nc\n\n"));

        Assert.Equal(SubtitleFault.Header, SubtitleRebuilder.Srt(Bytes("1\n00:00:01,000 --> 00:00:02,000\na\0b\n"), Runtime).Fault);
        Assert.Equal(SubtitleFault.Encoding, SubtitleRebuilder.Srt([0xC0, 0xAF], Runtime).Fault);
        Assert.True(SubtitleRebuilder.Srt(Bytes(Cues(20_000)), Runtime).Ok);
        Assert.Equal(SubtitleFault.TooMany, SubtitleRebuilder.Srt(Bytes(Cues(20_001)), Runtime).Fault);
    }

    // --- JSON (core's JsonWriter shape)

    private static string Event(string id = "a", string text = "Hello", long start = 10_000_000, long end = 20_000_000) =>
        $"{{\"Id\":\"{id}\",\"Text\":{JsonSerializer.Serialize(text)},\"StartPositionTicks\":{start},\"EndPositionTicks\":{end}}}";

    private static string Doc(params string[] events) => "{\"TrackEvents\":[" + string.Join(',', events) + "]}";

    private static JsonElement Parse(SubtitleResult result) => JsonDocument.Parse(result.Body).RootElement;

    [Fact]
    public void Json_IsRebuilt_WithRenumberedIds_AndCleanedText()
    {
        var result = SubtitleRebuilder.Json(Bytes(Doc(Event("zz", "<i>a</i><script>x</script>"), Event("yy", "b", 30_000_000, 40_000_000))), Runtime);

        Assert.True(result.Ok);
        var events = Parse(result).GetProperty("TrackEvents").EnumerateArray().ToList();
        Assert.Equal(2, events.Count);
        Assert.Equal("1", events[0].GetProperty("Id").GetString());
        Assert.Equal("2", events[1].GetProperty("Id").GetString());
        Assert.Equal("<i>a</i>x", events[0].GetProperty("Text").GetString());
        Assert.Equal(10_000_000, events[0].GetProperty("StartPositionTicks").GetInt64());
        Assert.DoesNotContain("<script>", Text(result), StringComparison.Ordinal);
        Assert.Contains("\\u003Ci\\u003E", Text(result), StringComparison.Ordinal); // the default encoder escapes angle brackets
    }

    [Fact]
    public void Json_UnknownMembers_AreSkippedAtBothLevels_NeverStored()
    {
        var input = "{\"Extra\":{\"deep\":[1,2,{\"x\":1}]},\"TrackEvents\":[{\"Id\":\"a\",\"Speaker\":\"Bob\",\"Text\":\"Hi\",\"StartPositionTicks\":1,\"EndPositionTicks\":2,\"Style\":{\"a\":1}}],\"Version\":7}";

        var result = SubtitleRebuilder.Json(Bytes(input), Runtime);

        Assert.True(result.Ok);
        Assert.DoesNotContain("Bob", Text(result), StringComparison.Ordinal);
        Assert.DoesNotContain("Extra", Text(result), StringComparison.Ordinal);
        Assert.DoesNotContain("Style", Text(result), StringComparison.Ordinal);
        Assert.Equal(1, result.Cues);
    }

    [Theory]
    [InlineData("{\"TrackEvents\":[{\"Id\":\"a\",\"Text\":\"x\",\"StartPositionTicks\":1}]}")] // a member missing
    [InlineData("{\"TrackEvents\":[{\"Id\":\"a\",\"Id\":\"b\",\"Text\":\"x\",\"StartPositionTicks\":1,\"EndPositionTicks\":2}]}")] // a duplicate known member
    [InlineData("{\"TrackEvents\":[{\"Id\":1,\"Text\":\"x\",\"StartPositionTicks\":1,\"EndPositionTicks\":2}]}")] // Id must be a string
    [InlineData("{\"TrackEvents\":[{\"Id\":\"a\",\"Text\":5,\"StartPositionTicks\":1,\"EndPositionTicks\":2}]}")]
    [InlineData("{\"TrackEvents\":[{\"Id\":\"a\",\"Text\":\"x\",\"StartPositionTicks\":\"1\",\"EndPositionTicks\":2}]}")]
    [InlineData("{\"TrackEvents\":[{\"Id\":\"a\",\"Text\":\"x\",\"StartPositionTicks\":1.5,\"EndPositionTicks\":2}]}")]
    [InlineData("{\"TrackEvents\":[{\"Id\":\"a\",\"Text\":\"x\",\"StartPositionTicks\":1e3,\"EndPositionTicks\":2}]}")]
    [InlineData("{\"TrackEvents\":[{\"Id\":\"a\",\"Text\":\"x\",\"StartPositionTicks\":99999999999999999999,\"EndPositionTicks\":2}]}")]
    [InlineData("{\"TrackEvents\":[{\"Id\":\"a\",\"Text\":null,\"StartPositionTicks\":1,\"EndPositionTicks\":2}]}")]
    [InlineData("{\"TrackEvents\":[1]}")]
    [InlineData("{\"TrackEvents\":{}}")]
    [InlineData("{\"TrackEvents\":[],\"TrackEvents\":[]}")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{\"TrackEvents\":[]} trailing")]
    [InlineData("{\"TrackEvents\":[]}{}")]
    [InlineData("{\"TrackEvents\":[],}")]
    [InlineData("{/*c*/\"TrackEvents\":[]}")]
    [InlineData("{\"TrackEvents\":[")]
    [InlineData("")]
    public void Json_AnyOtherShape_Is502(string input) =>
        Assert.Equal(SubtitleFault.Shape, SubtitleRebuilder.Json(Bytes(input), Runtime).Fault);

    [Fact]
    public void Json_DepthNine_Is502_DepthEightIsFine()
    {
        string Nest(int depth) => "{\"TrackEvents\":[],\"X\":" + string.Concat(Enumerable.Repeat("[", depth - 1)) + string.Concat(Enumerable.Repeat("]", depth - 1)) + "}";

        Assert.True(SubtitleRebuilder.Json(Bytes(Nest(7)), Runtime).Ok);
        Assert.Equal(SubtitleFault.Shape, SubtitleRebuilder.Json(Bytes(Nest(9)), Runtime).Fault);
    }

    [Fact]
    public void Json_EventTwentyThousandAndOne_Is502_BeforeAnyTreeIsBuilt()
    {
        var one = Event();
        var ok = "{\"TrackEvents\":[" + string.Join(',', Enumerable.Repeat(one, 20_000)) + "]}";
        var over = "{\"TrackEvents\":[" + string.Join(',', Enumerable.Repeat(one, 20_001)) + "]}";

        Assert.True(SubtitleRebuilder.Json(Bytes(ok), Runtime).Ok);
        Assert.Equal(SubtitleFault.TooMany, SubtitleRebuilder.Json(Bytes(over), Runtime).Fault);
    }

    [Fact]
    public void Json_TicksOutOfRange_AndInvertedEvents_AreDropped()
    {
        var result = SubtitleRebuilder.Json(Bytes(Doc(
            Event(start: -1),
            Event(start: 30, end: 20),
            Event(start: 10, end: Runtime + 36_000_000_001L),
            Event(text: "kept", start: 10, end: Runtime + 36_000_000_000L))), Runtime);

        Assert.True(result.Ok);
        Assert.Equal(1, result.Cues);
        Assert.Equal(3, result.Dropped);
    }

    [Fact]
    public void Json_Fuzz_TextNeverCarriesMarkupOrControls()
    {
        var alphabet = new[] { "<", ">", "<script>alert(1)</script>", "<img src=x onerror=alert(1)>", Rlo.ToString(), "\0", "\u0001", "\u007f", "</i>", "<i>", "a", " ", "\\", "\"", "&" };
        var random = new Random(7);
        for (var round = 0; round < 300; round++)
        {
            var text = string.Concat(Enumerable.Range(0, random.Next(1, 40)).Select(_ => alphabet[random.Next(alphabet.Length)]));

            var result = SubtitleRebuilder.Json(Bytes(Doc(Event(text: text))), Runtime);

            Assert.True(result.Ok);
            var output = Parse(result).GetProperty("TrackEvents")[0].GetProperty("Text").GetString()!;
            Assert.Matches(Invariant, output);
            Assert.DoesNotContain("\0", output, StringComparison.Ordinal);
            Assert.DoesNotContain(Rlo.ToString(), output, StringComparison.Ordinal);
        }
    }

    // --- I7 for this file

    [Fact]
    public void ThePatterns_UseExplicitAsciiClasses_AndWholeLineAnchors()
    {
        var patterns = typeof(SubtitleRebuilder).GetProperty("AllPatterns", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null) as IReadOnlyList<Regex>;

        Assert.NotNull(patterns);
        foreach (var regex in patterns)
        {
            var text = regex.ToString();
            Assert.Equal(RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, regex.Options);
            Assert.StartsWith("^", text, StringComparison.Ordinal);
            Assert.EndsWith("\\z", text, StringComparison.Ordinal);
            foreach (var banned in new[] { "\\d", "\\D", "\\w", "\\W", "\\s", "\\S", "(?i" })
            {
                Assert.DoesNotContain(banned, text, StringComparison.Ordinal);
            }
        }
    }
}
