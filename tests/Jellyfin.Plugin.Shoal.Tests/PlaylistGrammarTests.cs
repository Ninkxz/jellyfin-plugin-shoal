using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.Shoal.Playback;
using Xunit;

namespace Jellyfin.Plugin.Shoal.Tests;

/// <summary>
/// K16 acceptance (DESIGN 6.4): one test per rule (I1 to I8, the tag table, SI, MD, U-M, U-V, U-S, O1 to O4), F-PL-01 to F-PL-22 and F-PL-30, the
/// real 12.1 playlists from the spike, comma-decimal EXTINF, and a scan of the patterns for IgnoreCase, \d and \w.
/// </summary>
public sealed class PlaylistGrammarTests
{
    private const string Token = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string SourceN = "d6b690200fc1c631334425f6f7db7d2e";
    private const string PlaySession = "eb978ef02d8e411792e49ba28d5f975d";

    private static readonly Guid Source = Guid.Parse(SourceN);

    private static PlaylistContext Context(long runtimeTicks = 20_000_000_000L, Func<string, bool>? canary = null, string? token = null) => new(
        token ?? Token,
        Source,
        PlaySession,
        "kiro-capture",
        runtimeTicks,
        [.. Enumerable.Range(2, 10).Select(i => new GrantTrack(i, "Subtitle", "subrip", i == 2 ? "deu" : "eng", "Track \"" + i + "\"", true, false, i == 2, false)), new GrantTrack(12, "Subtitle", "pgssub", "eng", "Graphic", false, false, false, false)],
        canary);

    private static PlaylistResult Run(PlaylistKind kind, string text, PlaylistContext? context = null) =>
        PlaylistGrammar.Process(kind, Encoding.UTF8.GetBytes(text), context ?? Context());

    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    private static string V(params string[] lines) =>
        "#EXTM3U\n#EXT-X-VERSION:3\n#EXT-X-TARGETDURATION:3\n#EXT-X-MEDIA-SEQUENCE:0\n#EXT-X-PLAYLIST-TYPE:VOD\n" + string.Concat(lines.Select(static l => l + "\n")) + "#EXT-X-ENDLIST\n";

    private static string Seg(int n, string ext = "ts", string seconds = "3.000000", long runtime = 0, long length = 30_000_000) =>
        $"#EXTINF:{seconds}, nodesc\nhls1/main/{n}.{ext}?DeviceId=x&runtimeTicks={runtime}&actualSegmentLengthTicks={length}&ApiKey=zzz\n";

    private static string M(string variantUri = "main.m3u8?DeviceId=kiro-capture&MediaSourceId=" + SourceN + "&PlaySessionId=" + PlaySession + "&ApiKey=zzz&VideoCodec=h264") =>
        "#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=1372000,AVERAGE-BANDWIDTH=1372000,VIDEO-RANGE=SDR,CODECS=\"avc1.640028,mp4a.40.2\",RESOLUTION=1280x544,FRAME-RATE=24\n" + variantUri + "\n";

    private static string S(params string[] lines) =>
        "#EXTM3U\n#EXT-X-TARGETDURATION:30\n#EXT-X-VERSION:3\n#EXT-X-MEDIA-SEQUENCE:0\n#EXT-X-PLAYLIST-TYPE:VOD\n" + string.Concat(lines.Select(static l => l + "\n")) + "#EXT-X-ENDLIST\n";

    private static void AssertRejected(PlaylistResult result, PlaylistFault? fault = null)
    {
        Assert.Equal(PlaylistOutcome.Rejected, result.Outcome);
        Assert.Equal(string.Empty, result.Text);
        if (fault is { } expected)
        {
            Assert.Equal(expected, result.Fault);
        }
    }

    // --- the real 12.1 playlists from the spike, exact output

    [Fact]
    public void TheRealMasterPlaylist_IsRebuiltExactly()
    {
        var result = Run(PlaylistKind.Master, Fixture("jellyfin-master.m3u8"));

        Assert.Equal(PlaylistOutcome.Ok, result.Outcome);
        Assert.Equal(
            "#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=1372000,AVERAGE-BANDWIDTH=1372000,VIDEO-RANGE=SDR,CODECS=\"avc1.640028,mp4a.40.2\",RESOLUTION=1280x544,FRAME-RATE=24\nmain.m3u8?ApiKey=" + Token + "\n",
            result.Text);
        Assert.Contains(result.VariantQuery, static p => p.Key == "DeviceId" && p.Value == "kiro-capture");
        Assert.DoesNotContain(result.VariantQuery, static p => p.Key.Equals("ApiKey", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.VariantQuery, static p => p.Key == "TranscodeReasons" && p.Value == "AudioCodecNotSupported,ContainerBitrateExceedsLimit");
    }

    [Fact]
    public void TheRealMasterWithSubtitles_KeepsTheRenditionsOfTheGrant_AndDropsTheRest()
    {
        var result = Run(PlaylistKind.Master, Fixture("jellyfin-master-subs.m3u8"));

        Assert.Equal(PlaylistOutcome.Ok, result.Outcome);
        var lines = result.Text.Split('\n');
        Assert.Equal("#EXTM3U", lines[0]);
        Assert.Equal(
            "#EXT-X-MEDIA:TYPE=SUBTITLES,GROUP-ID=\"subs\",NAME=\"Track 2\",DEFAULT=YES,FORCED=NO,AUTOSELECT=YES,URI=\"subs/2/subtitles.m3u8?ApiKey=" + Token + "\",LANGUAGE=\"deu\"",
            lines[1]);
        Assert.Equal(10, lines.Count(static l => l.StartsWith("#EXT-X-MEDIA:", StringComparison.Ordinal)));
        Assert.Contains("SUBTITLES=\"subs\"", result.Text, StringComparison.Ordinal);
        Assert.Contains("main.m3u8?ApiKey=" + Token + "\n", result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(SourceN, result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("SegmentLength", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void TheRealMpegTsVariant_IsRebuilt_WithOnlyTheValidatedIntegers()
    {
        var result = Run(PlaylistKind.Variant, Fixture("jellyfin-main-ts.m3u8"));

        Assert.Equal(PlaylistOutcome.Ok, result.Outcome);
        var lines = result.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(["#EXTM3U", "#EXT-X-PLAYLIST-TYPE:VOD", "#EXT-X-VERSION:3", "#EXT-X-TARGETDURATION:3", "#EXT-X-MEDIA-SEQUENCE:0"], lines[..5]);
        Assert.Equal("#EXTINF:3.000,", lines[5]);
        Assert.Equal("hls1/main/0.ts?ApiKey=" + Token + "&runtimeTicks=0&actualSegmentLengthTicks=30000000", lines[6]);
        Assert.Equal("#EXT-X-ENDLIST", lines[^1]);
        Assert.Equal(lines.Count(static l => l.StartsWith("#EXTINF", StringComparison.Ordinal)), result.Segments);
        Assert.DoesNotContain("DeviceId", result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("Tag=", result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(SourceN, result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(PlaySession, result.Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("jellyfin-main-web-profile.m3u8")]
    [InlineData("jellyfin-main-ts.m3u8")]
    public void TheRealVariants_PassWithTheirOwnRuntime(string name) =>
        Assert.Equal(PlaylistOutcome.Ok, Run(PlaylistKind.Variant, Fixture(name)).Outcome);

    [Fact]
    public void TheRealFragmentedMp4Variant_GetsAFixedMapLine()
    {
        var result = Run(PlaylistKind.Variant, Fixture("jellyfin-main-fmp4.m3u8"));

        Assert.Equal(PlaylistOutcome.Ok, result.Outcome);
        Assert.Contains("#EXT-X-VERSION:7\n", result.Text, StringComparison.Ordinal);
        Assert.Contains("#EXT-X-MAP:URI=\"hls1/main/-1.mp4?ApiKey=" + Token + "&runtimeTicks=0&actualSegmentLengthTicks=0\"\n", result.Text, StringComparison.Ordinal);
        Assert.Contains("hls1/main/0.mp4?ApiKey=" + Token, result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void TheRealSubtitlePlaylist_IsRebuiltExactly()
    {
        var result = Run(PlaylistKind.Subtitle, Fixture("jellyfin-subtitles.m3u8"));

        Assert.Equal(PlaylistOutcome.Ok, result.Outcome);
        Assert.Equal(
            "#EXTM3U\n#EXT-X-TARGETDURATION:30\n#EXT-X-VERSION:3\n#EXT-X-MEDIA-SEQUENCE:0\n#EXT-X-PLAYLIST-TYPE:VOD\n"
            + "#EXTINF:30.000,\nstream.vtt?ApiKey=" + Token + "&StartPositionTicks=0&EndPositionTicks=300000000\n"
            + "#EXTINF:30.000,\nstream.vtt?ApiKey=" + Token + "&StartPositionTicks=300000000&EndPositionTicks=600000000\n#EXT-X-ENDLIST\n",
            result.Text);
    }

    // --- input rules

    [Fact]
    public void I1_InvalidUtf8_Is502_AndABomIsSkipped()
    {
        var bad = PlaylistGrammar.Process(PlaylistKind.Master, [.. Encoding.UTF8.GetBytes("#EXTM3U\n#EXT-X-X:"), 0xFF, 0xFE, .. Encoding.UTF8.GetBytes("\n")], Context());
        var overlong = PlaylistGrammar.Process(PlaylistKind.Master, [.. Encoding.UTF8.GetBytes("#EXTM3U\n#"), 0xC0, 0xAF], Context());
        var bom = PlaylistGrammar.Process(PlaylistKind.Master, [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(M())], Context());

        AssertRejected(bad, PlaylistFault.Encoding);
        AssertRejected(overlong, PlaylistFault.Encoding);
        Assert.Equal(PlaylistOutcome.Ok, bom.Outcome);
    }

    [Theory]
    [InlineData("\t")]
    [InlineData("\r")]
    [InlineData("\0")]
    [InlineData("\u001b")]
    [InlineData("\u007f")]
    public void I2_AControlCharacterAnywhere_Is502(string control)
    {
        AssertRejected(Run(PlaylistKind.Variant, V(Seg(0).Replace("hls1/main/0", "hls1/main/0" + control, StringComparison.Ordinal))), PlaylistFault.Control);
        AssertRejected(Run(PlaylistKind.Variant, V("# a comment with " + control + " in it", Seg(0))), PlaylistFault.Control);
    }

    [Theory]
    [InlineData(0x85)]
    [InlineData(0x2028)]
    [InlineData(0x2029)]
    public void I2_NelAndTheLineSeparators_Are502(int code)
    {
        var text = V("# note " + (char)code + " note", Seg(0));

        AssertRejected(Run(PlaylistKind.Variant, text), PlaylistFault.Control);
    }

    [Fact]
    public void I2_CrlfIsFine_ButALoneCrIsNot()
    {
        Assert.Equal(PlaylistOutcome.Ok, Run(PlaylistKind.Variant, V(Seg(0)).Replace("\n", "\r\n", StringComparison.Ordinal)).Outcome);
        AssertRejected(Run(PlaylistKind.Variant, V(Seg(0)).Replace("\n", "\r", StringComparison.Ordinal)));
        AssertRejected(Run(PlaylistKind.Variant, V(Seg(0)).TrimEnd('\n') + "\r"), PlaylistFault.Control); // a CR at the very end has no LF behind it
    }

    [Fact]
    public void I3_ALineOverEightKilobytes_Is502_EvenAComment()
    {
        AssertRejected(Run(PlaylistKind.Variant, V("#" + new string('a', 8 * 1024), Seg(0))), PlaylistFault.LineLength);
        Assert.Equal(PlaylistOutcome.Ok, Run(PlaylistKind.Variant, V("#" + new string('a', 8 * 1024 - 1), Seg(0))).Outcome);
    }

    [Theory]
    [InlineData("")]
    [InlineData("\n#EXTM3U")]
    [InlineData("#EXTM3U ")]
    [InlineData("#extm3u")]
    [InlineData("# #EXTM3U")]
    [InlineData("#EXT-X-VERSION:3")]
    public void I4_TheFirstLineMustBeExactlyExtM3u(string first) =>
        AssertRejected(Run(PlaylistKind.Master, first + "\n" + M().Split('\n', 2)[1]), PlaylistFault.Header);

    [Fact]
    public void I5_BlankLinesAreDropped() =>
        Assert.Equal(Run(PlaylistKind.Variant, V(Seg(0))).Text, Run(PlaylistKind.Variant, V(string.Empty, Seg(0).Replace("\n", "\n\n", StringComparison.Ordinal), string.Empty)).Text);

    [Fact]
    public void I6_TagNamesAreCaseSensitive_AndCommentsAreDropped()
    {
        var text = V("#ext-x-key:METHOD=AES-128,URI=\"https://evil/k\"", "#Ext-X-Daterange:ID=\"a\"", "# EXT-X-KEY:METHOD=AES-128", "#EXTRA", Seg(0));

        var result = Run(PlaylistKind.Variant, text);

        Assert.Equal(PlaylistOutcome.Ok, result.Outcome);
        Assert.DoesNotContain("evil", result.Text, StringComparison.Ordinal);
        Assert.Equal(4, result.Dropped);
    }

    [Fact]
    public void I7_ThePatterns_UseExplicitAsciiClasses_AndWholeLineAnchors()
    {
        var patterns = typeof(PlaylistGrammar).GetProperty("AllPatterns", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null) as IReadOnlyList<Regex>;

        Assert.NotNull(patterns);
        Assert.True(patterns.Count >= 20);
        foreach (var regex in patterns)
        {
            var text = regex.ToString();
            Assert.Equal(RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, regex.Options);
            Assert.False(regex.Options.HasFlag(RegexOptions.IgnoreCase), text);
            Assert.Equal(TimeSpan.FromMilliseconds(100), regex.MatchTimeout);
            Assert.StartsWith("^", text, StringComparison.Ordinal);
            Assert.EndsWith("\\z", text, StringComparison.Ordinal);
            foreach (var banned in new[] { "\\d", "\\D", "\\w", "\\W", "\\s", "\\S", "(?i", "$" })
            {
                Assert.DoesNotContain(banned, text.Replace("\\z", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void I7_TheSource_NeverNamesIgnoreCaseRegexOptionsOrShorthandClasses()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "src", "Jellyfin.Plugin.Shoal", "Playback", "PlaylistGrammar.cs")))
        {
            directory = directory.Parent;
        }

        var source = File.ReadAllText(Path.Combine(directory!.FullName, "src", "Jellyfin.Plugin.Shoal", "Playback", "PlaylistGrammar.cs"));
        Assert.DoesNotContain("RegexOptions.IgnoreCase", source, StringComparison.Ordinal);
        Assert.DoesNotContain("RegexOptions.Compiled", source, StringComparison.Ordinal);
        Assert.DoesNotContain("RegexOptions.ECMAScript", source, StringComparison.Ordinal);
    }

    [Fact]
    public void I7_AnArabicIndicDigit_IsNotADigit()
    {
        var arabic = ((char)0x0661).ToString(); // matches \d in .NET, never [0-9]
        AssertRejected(Run(PlaylistKind.Variant, V("#EXTINF:" + arabic + ".000,", "hls1/main/0.ts?runtimeTicks=0&actualSegmentLengthTicks=0")));
        Assert.Equal(PlaylistOutcome.Ok, Run(PlaylistKind.Variant, V("#EXT-X-VERSION:" + arabic, Seg(0))).Outcome); // dropped, never copied
        Assert.DoesNotContain(arabic, Run(PlaylistKind.Variant, V("#EXT-X-VERSION:" + arabic, Seg(0))).Text, StringComparison.Ordinal);
        AssertRejected(Run(PlaylistKind.Variant, V(Seg(0).Replace("runtimeTicks=0", "runtimeTicks=" + arabic, StringComparison.Ordinal))));
    }

    [Fact]
    public void I7_TheKelvinSign_IsNotAK()
    {
        var kelvin = ((char)0x212A).ToString();
        var result = Run(PlaylistKind.Variant, V("#EXT-X-" + kelvin + "EY:METHOD=AES-128,URI=\"x\"", Seg(0)));

        Assert.Equal(PlaylistOutcome.Ok, result.Outcome); // an unknown tag, dropped; not the EXT-X-KEY that would be 502
        Assert.DoesNotContain(kelvin, result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void I7_ATrailingNewlineBehindAUri_IsNotMatchedByDollar()
    {
        // lines are split first, so a line can never end in LF; a URI with an encoded one stays a 502
        AssertRejected(Run(PlaylistKind.Variant, V(Seg(0).Replace("&ApiKey=zzz", "%0A", StringComparison.Ordinal).TrimEnd('\n') + "\n#EXT-X-STRAY\nhls1/main/1.ts?x=1")));
    }

    // --- I8 structural caps

    [Fact]
    public void I8_TooManyLinesOrBytes_Are502()
    {
        var master = M() + string.Concat(Enumerable.Repeat("# c\n", 70));
        AssertRejected(Run(PlaylistKind.Master, master), PlaylistFault.LineCount);
        AssertRejected(PlaylistGrammar.Process(PlaylistKind.Master, new byte[256 * 1024 + 1], Context()), PlaylistFault.TooLarge);
        AssertRejected(PlaylistGrammar.Process(PlaylistKind.Subtitle, new byte[2 * 1024 * 1024 + 1], Context()), PlaylistFault.TooLarge);
        AssertRejected(PlaylistGrammar.Process(PlaylistKind.Variant, new byte[8 * 1024 * 1024 + 1], Context()), PlaylistFault.TooLarge);
    }

    [Fact]
    public void I8_MoreThanTheSegmentCap_Is502()
    {
        var many = V(string.Concat(Enumerable.Range(0, 20_001).Select(i => Seg(i % 1000, seconds: "0.001"))).TrimEnd('\n'));
        var ok = V(string.Concat(Enumerable.Range(0, 20_000).Select(i => Seg(i % 1000, seconds: "0.001"))).TrimEnd('\n'));

        AssertRejected(Run(PlaylistKind.Variant, many, Context(runtimeTicks: 1_000_000_000_000L)), PlaylistFault.Caps);
        Assert.Equal(PlaylistOutcome.Ok, Run(PlaylistKind.Variant, ok, Context(runtimeTicks: 1_000_000_000_000L)).Outcome);
        var subs = S(string.Concat(Enumerable.Range(0, 10_001).Select(static i => "#EXTINF:1,\nstream.vtt?StartPositionTicks=0&EndPositionTicks=0\n")).TrimEnd('\n'));
        AssertRejected(Run(PlaylistKind.Subtitle, subs), PlaylistFault.Caps);
    }

    [Fact]
    public void I8_ASingleExtInfOver600Seconds_AndASumOverTheRuntimeBudget_Are502()
    {
        AssertRejected(Run(PlaylistKind.Variant, V(Seg(0, seconds: "600.001"))), PlaylistFault.Value);
        Assert.Equal(PlaylistOutcome.Ok, Run(PlaylistKind.Variant, V(Seg(0, seconds: "600"))).Outcome);
        AssertRejected(Run(PlaylistKind.Variant, V(Seg(0, seconds: "86400"))));
        // runtime 100 s: budget 100 * 1.1 + 3600 = 3710 s
        var within = V(string.Concat(Enumerable.Range(0, 6).Select(i => Seg(i, seconds: "600"))).TrimEnd('\n'));
        var over = V(string.Concat(Enumerable.Range(0, 7).Select(i => Seg(i, seconds: "600"))).TrimEnd('\n'));
        Assert.Equal(PlaylistOutcome.Ok, Run(PlaylistKind.Variant, within, Context(runtimeTicks: 1_000_000_000L)).Outcome);
        AssertRejected(Run(PlaylistKind.Variant, over, Context(runtimeTicks: 1_000_000_000L)), PlaylistFault.Caps);
    }

    [Fact]
    public void I8_SingletonTags_AreEmittedOnce_TheFirstWins()
    {
        var text = "#EXTM3U\n#EXT-X-VERSION:3\n#EXT-X-VERSION:7\n#EXT-X-TARGETDURATION:3\n#EXT-X-TARGETDURATION:900\n#EXT-X-MEDIA-SEQUENCE:0\n#EXT-X-MEDIA-SEQUENCE:5\n"
            + "#EXT-X-PLAYLIST-TYPE:VOD\n#EXT-X-PLAYLIST-TYPE:VOD\n" + Seg(0) + "#EXT-X-ENDLIST\n";

        var result = Run(PlaylistKind.Variant, text);

        Assert.Equal(PlaylistOutcome.Ok, result.Outcome);
        Assert.Equal("#EXTM3U\n#EXT-X-VERSION:3\n#EXT-X-TARGETDURATION:3\n#EXT-X-MEDIA-SEQUENCE:0\n#EXT-X-PLAYLIST-TYPE:VOD\n#EXTINF:3.000,\nhls1/main/0.ts?ApiKey=" + Token + "&runtimeTicks=0&actualSegmentLengthTicks=30000000\n#EXT-X-ENDLIST\n", result.Text);
        Assert.Equal(4, result.Dropped);
    }

    // --- the tag table

    [Theory]
    [InlineData("#EXT-X-PLAYLIST-TYPE:EVENT")]
    [InlineData("#EXT-X-PLAYLIST-TYPE:vod")]
    [InlineData("#EXT-X-PLAYLIST-TYPE:")]
    [InlineData("#EXT-X-KEY:METHOD=AES-128,URI=\"https://h/k\"")]
    [InlineData("#EXT-X-SESSION-KEY:METHOD=AES-128")]
    public void TagTable_ForbiddenTags_Are502(string tag)
    {
        AssertRejected(Run(PlaylistKind.Variant, V(tag, Seg(0))));
        AssertRejected(Run(PlaylistKind.Subtitle, S(tag, "#EXTINF:30,\nstream.vtt?StartPositionTicks=0&EndPositionTicks=1")));
        if (tag.Contains("KEY", StringComparison.Ordinal))
        {
            AssertRejected(Run(PlaylistKind.Master, M().Replace("#EXTM3U\n", "#EXTM3U\n" + tag + "\n", StringComparison.Ordinal)), PlaylistFault.ForbiddenTag);
        }
    }

    [Theory]
    [InlineData("#EXT-X-VERSION:2")]
    [InlineData("#EXT-X-VERSION:8")]
    [InlineData("#EXT-X-VERSION:100")]
    [InlineData("#EXT-X-VERSION:3.0")]
    [InlineData("#EXT-X-VERSION: 3")]
    [InlineData("#EXT-X-TARGETDURATION:123456")]
    [InlineData("#EXT-X-MEDIA-SEQUENCE:1234567890")]
    [InlineData("#EXT-X-MEDIA-SEQUENCE:-1")]
    [InlineData("#EXT-X-DISCONTINUITY-SEQUENCE:x")]
    public void TagTable_ValuesOutsideTheirPattern_AreDroppedNotCopied(string tag)
    {
        var result = Run(PlaylistKind.Variant, V(tag, Seg(0)));

        Assert.Equal(PlaylistOutcome.Ok, result.Outcome);
        Assert.DoesNotContain(tag, result.Text, StringComparison.Ordinal);
        Assert.True(result.Dropped >= 1);
    }

    [Theory]
    [InlineData("#EXT-X-DATERANGE:ID=\"a\",X-ASSET-URI=\"https:/h/p.m3u8\"")]
    [InlineData("#EXT-X-DATERANGE:ID=\"b\",X-ASSET-LIST=\"https://h/l.json\"")]
    [InlineData("#EXT-X-CONTENT-STEERING:SERVER-URI=\"https://h/s\"")]
    [InlineData("#EXT-X-SESSION-DATA:DATA-ID=\"a\",URI=\"https:/h/x\"")]
    [InlineData("#EXT-X-DEFINE:NAME=\"a\",VALUE=\"b\"")]
    [InlineData("#EXT-X-I-FRAME-STREAM-INF:BANDWIDTH=1,URI=\"https://h/i\"")]
    [InlineData("#EXT-X-IMAGE-STREAM-INF:BANDWIDTH=1,URI=\"https://h/t\"")]
    [InlineData("#EXT-X-BYTERANGE:100@0")]
    [InlineData("#EXT-X-START:TIME-OFFSET=3")]
    [InlineData("#EXT-X-PROGRAM-DATE-TIME:2026-01-01T00:00:00Z")]
    [InlineData("#EXT-X-GAP")]
    [InlineData("#EXT-X-PART:DURATION=1,URI=\"https://h/p\"")]
    [InlineData("#EXT-X-PRELOAD-HINT:TYPE=PART,URI=\"https://h/p\"")]
    [InlineData("#EXT-X-SERVER-CONTROL:CAN-BLOCK-RELOAD=YES")]
    [InlineData("#EXT-X-SKIP:SKIPPED-SEGMENTS=3")]
    [InlineData("#EXT-X-RENDITION-REPORT:URI=\"https://h/r\"")]
    [InlineData("#EXT-X-UNKNOWN-FUTURE:URI=\"https://h/u\"")]
    public void TagTable_TheNamedDroppedTags_AreDropped_ForEveryKind(string tag)
    {
        foreach (var result in new[]
        {
            Run(PlaylistKind.Variant, V(tag, Seg(0))),
            Run(PlaylistKind.Subtitle, S(tag, "#EXTINF:30,\nstream.vtt?StartPositionTicks=0&EndPositionTicks=1")),
            Run(PlaylistKind.Master, M().Replace("#EXTM3U\n", "#EXTM3U\n" + tag + "\n", StringComparison.Ordinal)),
        })
        {
            Assert.Equal(PlaylistOutcome.Ok, result.Outcome);
            Assert.DoesNotContain("https", result.Text, StringComparison.Ordinal);
            Assert.DoesNotContain(tag, result.Text, StringComparison.Ordinal);
            Assert.Equal(1, result.Dropped);
        }
    }

    [Fact]
    public void TagTable_IndependentSegmentsAndDiscontinuity_AreKeptOnlyInTheExactForm()
    {
        var kept = Run(PlaylistKind.Variant, V("#EXT-X-INDEPENDENT-SEGMENTS", Seg(0), "#EXT-X-DISCONTINUITY", Seg(1), "#EXT-X-DISCONTINUITY-SEQUENCE:4"));
        var odd = Run(PlaylistKind.Variant, V("#EXT-X-INDEPENDENT-SEGMENTS:x", "#EXT-X-DISCONTINUITY:evil", Seg(0)));

        Assert.Contains("#EXT-X-INDEPENDENT-SEGMENTS\n", kept.Text, StringComparison.Ordinal);
        Assert.Contains("#EXT-X-DISCONTINUITY\n", kept.Text, StringComparison.Ordinal);
        Assert.Contains("#EXT-X-DISCONTINUITY-SEQUENCE:4\n", kept.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("evil", odd.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("INDEPENDENT", odd.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void TagTable_VariantOnlyTags_AreDroppedInASubtitlePlaylist()
    {
        var result = Run(PlaylistKind.Subtitle, S("#EXT-X-INDEPENDENT-SEGMENTS", "#EXT-X-DISCONTINUITY", "#EXT-X-MAP:URI=\"hls1/main/-1.mp4?a=b\"", "#EXTINF:30,", "stream.vtt?StartPositionTicks=0&EndPositionTicks=1"));

        Assert.Equal(PlaylistOutcome.Ok, result.Outcome);
        Assert.DoesNotContain("MAP", result.Text, StringComparison.Ordinal);
        Assert.Equal(3, result.Dropped);
    }

    [Fact]
    public void TagTable_EndListIsRequired_AndIsTheLastTag()
    {
        var withoutEnd = V(Seg(0)).Replace("#EXT-X-ENDLIST\n", string.Empty, StringComparison.Ordinal);
        AssertRejected(Run(PlaylistKind.Variant, withoutEnd), PlaylistFault.Structure);
        AssertRejected(Run(PlaylistKind.Subtitle, S("#EXTINF:30,", "stream.vtt?StartPositionTicks=0&EndPositionTicks=1").Replace("#EXT-X-ENDLIST\n", string.Empty, StringComparison.Ordinal)), PlaylistFault.Structure);
        AssertRejected(Run(PlaylistKind.Variant, V(Seg(0)) + Seg(1)), PlaylistFault.Structure);
        AssertRejected(Run(PlaylistKind.Variant, V(Seg(0)) + "#EXT-X-VERSION:3\n"), PlaylistFault.Structure);
        AssertRejected(Run(PlaylistKind.Variant, V(Seg(0)).Replace("#EXT-X-ENDLIST", "#EXT-X-ENDLIST:x", StringComparison.Ordinal)));
        Assert.Equal(PlaylistOutcome.Ok, Run(PlaylistKind.Variant, V(Seg(0)) + "# trailing comment\n\n#EXT-X-IMAGE-STREAM-INF:X=1\n").Outcome);
        AssertRejected(Run(PlaylistKind.Variant, V()), PlaylistFault.Structure); // no segment at all
    }

    [Fact]
    public void TagTable_ExtInfMustHaveExactlyOneUriLine()
    {
        AssertRejected(Run(PlaylistKind.Variant, V("#EXTINF:3.000, nodesc", "#EXTINF:3.000, nodesc", "hls1/main/0.ts?runtimeTicks=0&actualSegmentLengthTicks=0")));
        AssertRejected(Run(PlaylistKind.Variant, V("#EXTINF:3.000, nodesc")), PlaylistFault.Structure);
        AssertRejected(Run(PlaylistKind.Variant, V("hls1/main/0.ts?runtimeTicks=0&actualSegmentLengthTicks=0")), PlaylistFault.Uri);
        AssertRejected(Run(PlaylistKind.Variant, V(Seg(0) + "hls1/main/1.ts?runtimeTicks=0&actualSegmentLengthTicks=0")), PlaylistFault.Uri);
        AssertRejected(Run(PlaylistKind.Variant, V("#EXTINF:3.000, nodesc", "#EXT-X-DISCONTINUITY", "hls1/main/0.ts?runtimeTicks=0&actualSegmentLengthTicks=0")), PlaylistFault.Structure);
        Assert.Equal(PlaylistOutcome.Ok, Run(PlaylistKind.Variant, V("#EXTINF:3.000, nodesc", "#EXT-X-BYTERANGE:5", "# c", "hls1/main/0.ts?runtimeTicks=0&actualSegmentLengthTicks=0")).Outcome);
    }

    [Theory]
    [InlineData("#EXTINF:3.000000,", "3.000")]
    [InlineData("#EXTINF:3,", "3.000")]
    [InlineData("#EXTINF:3,000000,", "3.000")] // comma decimal: core writes S durations in the server's culture (SubtitleController.cs)
    [InlineData("#EXTINF:10,417000, nodesc", "10.417")]
    [InlineData("#EXTINF:1E-3,", "0.001")]
    [InlineData("#EXTINF:0.0005,", "0.0005")]
    [InlineData("#EXTINF:600.000000, nodesc", "600.000")]
    public void TagTable_ExtInfValues_AreReadInvariantly_AndReWritten(string line, string written)
    {
        var result = Run(PlaylistKind.Variant, V(line, "hls1/main/0.ts?runtimeTicks=0&actualSegmentLengthTicks=0"));

        Assert.Equal(PlaylistOutcome.Ok, result.Outcome);
        Assert.Contains("#EXTINF:" + written + ",\n", result.Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("#EXTINF:abc,")]
    [InlineData("#EXTINF:3.000")]
    [InlineData("#EXTINF:-3,")]
    [InlineData("#EXTINF:1234567,")]
    [InlineData("#EXTINF:3.000, title=\"x\"")]
    [InlineData("#EXTINF:3.0000000000000000000,")]
    [InlineData("#EXTINF:3.000,  nodesc")]
    [InlineData("#EXTINF:NaN,")]
    [InlineData("#EXTINF:1E-9999,")]
    public void TagTable_AMalformedExtInf_Is502(string line) =>
        AssertRejected(Run(PlaylistKind.Variant, V(line, "hls1/main/0.ts?runtimeTicks=0&actualSegmentLengthTicks=0")));

    [Fact]
    public void TagTable_Map_IsRebuiltWithFixedValues_AndRejectedInAnyOtherForm()
    {
        var map = "#EXT-X-MAP:URI=\"hls1/main/-1.mp4?DeviceId=x&ApiKey=zzz&runtimeTicks=0&actualSegmentLengthTicks=0\"";

        var ok = Run(PlaylistKind.Variant, V(map, Seg(0, "mp4")));

        Assert.Equal(PlaylistOutcome.Ok, ok.Outcome);
        Assert.Contains("#EXT-X-MAP:URI=\"hls1/main/-1.mp4?ApiKey=" + Token + "&runtimeTicks=0&actualSegmentLengthTicks=0\"\n", ok.Text, StringComparison.Ordinal);
        foreach (var bad in new[]
        {
            "#EXT-X-MAP:URI=hls1/main/-1.mp4?a=b",
            "#EXT-X-MAP:URI=\"https://evil/init.mp4\"",
            "#EXT-X-MAP:URI=\"hls1/main/-1.mp4?a=b\",BYTERANGE=\"5@0\"",
            "#EXT-X-MAP:URI=\"hls1/main/-1.mp4?a=b\",URI=\"https://evil/\"",
            "#EXT-X-MAP:URI=\"hls1/main/-1.mp4?a=http://evil/\"",
            "#EXT-X-MAP:URI=\"hls1/main/0.mp4?a=b\"",
            "#EXT-X-MAP:URI=\"/hls1/main/-1.mp4?a=b\"",
            "#EXT-X-MAP",
        })
        {
            AssertRejected(Run(PlaylistKind.Variant, V(bad, Seg(0, "mp4"))), PlaylistFault.Syntax);
        }
    }

    // --- rule SI

    [Fact]
    public void SI_KeepsOnlyTheListedAttributes_InFixedOrder()
    {
        var m = "#EXTM3U\n#EXT-X-STREAM-INF:FRAME-RATE=23.976,RESOLUTION=1920x1080,CODECS=\"avc1.640028,mp4a.40.2\",PROGRAM-ID=1,HDCP-LEVEL=TYPE-1,SUPPLEMENTAL-CODECS=\"dvh1.05.06/db1p\",AVERAGE-BANDWIDTH=900,BANDWIDTH=1000,VIDEO-RANGE=PQ,NAME=\"evil\"\nmain.m3u8?a=b\n";

        var result = Run(PlaylistKind.Master, m);

        Assert.Equal(PlaylistOutcome.Ok, result.Outcome);
        Assert.Equal("#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=1000,AVERAGE-BANDWIDTH=900,VIDEO-RANGE=PQ,CODECS=\"avc1.640028,mp4a.40.2\",SUPPLEMENTAL-CODECS=\"dvh1.05.06/db1p\",RESOLUTION=1920x1080,FRAME-RATE=23.976\nmain.m3u8?ApiKey=" + Token + "\n", result.Text);
    }

    [Theory]
    [InlineData("BANDWIDTH=1,BANDWIDTH=2")]
    [InlineData("BANDWIDTH=1,")]
    [InlineData(",BANDWIDTH=1")]
    [InlineData("BANDWIDTH=1,,CODECS=\"a\"")]
    [InlineData("BANDWIDTH")]
    [InlineData("BANDWIDTH=")]
    [InlineData("bandwidth=1")]
    [InlineData("BANDWIDTH=1 ,CODECS=\"a\"")]
    [InlineData("BANDWIDTH=1,CODECS=\"a")]
    [InlineData("BANDWIDTH=\"1\"")]
    [InlineData("BANDWIDTH=12345678901")]
    [InlineData("BANDWIDTH=1x")]
    [InlineData("CODECS=\"avc1\"")]
    [InlineData("")]
    public void SI_ASyntaxErrorADuplicateOrAMissingBandwidth_Is502(string attributes) =>
        AssertRejected(Run(PlaylistKind.Master, "#EXTM3U\n#EXT-X-STREAM-INF:" + attributes + "\nmain.m3u8?a=b\n"));

    [Fact]
    public void SI_AKeptAttributeWithAnOddValue_IsDroppedNotCopied()
    {
        var m = "#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=1000,VIDEO-RANGE=EVIL,CODECS=\"<script>\",RESOLUTION=9x,FRAME-RATE=1.2345,SUBTITLES=\"other\",AVERAGE-BANDWIDTH=x\nmain.m3u8?a=b\n";

        var result = Run(PlaylistKind.Master, m);

        Assert.Equal(PlaylistOutcome.Ok, result.Outcome);
        Assert.Equal("#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=1000\nmain.m3u8?ApiKey=" + Token + "\n", result.Text);
    }

    [Fact]
    public void SI_OnlyTheFirstVariantIsKept_TheOthersAreDroppedWithTheirUriLine()
    {
        var m = M() + "#EXT-X-STREAM-INF:BANDWIDTH=999\nmain.m3u8?x=y\n";
        var hostile = M() + "#EXT-X-STREAM-INF:BANDWIDTH=999\nhttp://evil/\n";

        Assert.Equal(M().Length > 0 ? Run(PlaylistKind.Master, M()).Text : string.Empty, Run(PlaylistKind.Master, m).Text);
        AssertRejected(Run(PlaylistKind.Master, hostile), PlaylistFault.Uri); // the dropped variant's URI must still match U-M
        AssertRejected(Run(PlaylistKind.Master, "#EXTM3U\n"), PlaylistFault.Structure);
    }

    [Fact]
    public void SI_SubtitlesIsKeptOnlyWhenARenditionWasEmitted()
    {
        var withVariantAttr = "#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=1000,SUBTITLES=\"subs\"\nmain.m3u8?a=b\n";

        Assert.DoesNotContain("SUBTITLES", Run(PlaylistKind.Master, withVariantAttr).Text, StringComparison.Ordinal);
        var withMedia = "#EXTM3U" + (char)10 + MediaLine(2) + (char)10 + withVariantAttr[8..];
        Assert.Contains("SUBTITLES=\"subs\"", Run(PlaylistKind.Master, withMedia).Text, StringComparison.Ordinal);
    }

    // --- rule MD

    private static string MediaLine(int index, string name = "German - SUBRIP", string? source = null, string? query = null, string language = "deu", string def = "NO") =>
        "#EXT-X-MEDIA:TYPE=SUBTITLES,GROUP-ID=\"subs\",NAME=\"" + name + "\",DEFAULT=" + def + ",FORCED=NO,AUTOSELECT=YES,URI=\"" + (source ?? SourceN) + "/Subtitles/" + index + "/subtitles.m3u8?" + (query ?? "SegmentLength=30&ApiKey=zzz") + "\",LANGUAGE=\"" + language + "\"";

    private static string MasterWith(params string[] media) => "#EXTM3U\n" + string.Concat(media.Select(static m => m + "\n")) + M().Split('\n', 2)[1];

    [Fact]
    public void MD_IsRebuiltFromGrantData_NotFromThePeersLabels()
    {
        var result = Run(PlaylistKind.Master, MasterWith(MediaLine(3, name: "Peer <b>label</b>", language: "xx;evil", def: "YES")));

        Assert.Equal(PlaylistOutcome.Ok, result.Outcome);
        Assert.Contains("#EXT-X-MEDIA:TYPE=SUBTITLES,GROUP-ID=\"subs\",NAME=\"Track 3\",DEFAULT=YES,FORCED=NO,AUTOSELECT=YES,URI=\"subs/3/subtitles.m3u8?ApiKey=" + Token + "\",LANGUAGE=\"eng\"\n", result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("evil", result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("<b>", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void MD_ALineThatFailsTheRule_IsDroppedAndTheRestSurvive()
    {
        var other = Guid.NewGuid().ToString("N");
        var result = Run(PlaylistKind.Master, MasterWith(
            MediaLine(3),
            MediaLine(4, source: other), // another media source
            MediaLine(99), // an index the grant does not have
            MediaLine(12), // a graphic subtitle: not a text stream
            MediaLine(3), // a repeat of an index
            MediaLine(5, query: "a=http://evil/"), // outside Q
            MediaLine(6).Replace("AUTOSELECT=YES", "AUTOSELECT=NO", StringComparison.Ordinal),
            MediaLine(7).Replace("URI=\"", "URI=", StringComparison.Ordinal), // unquoted URI
            MediaLine(8).Replace("TYPE=SUBTITLES", "TYPE=AUDIO", StringComparison.Ordinal),
            MediaLine(9).Replace("GROUP-ID=\"subs\"", "GROUP-ID=\"x\"", StringComparison.Ordinal),
            MediaLine(10) + ",EXTRA=1"));

        Assert.Equal(PlaylistOutcome.Ok, result.Outcome);
        Assert.Equal(1, result.Text.Split('\n').Count(static l => l.StartsWith("#EXT-X-MEDIA:", StringComparison.Ordinal)));
        Assert.Equal(10, result.Dropped);
        Assert.DoesNotContain("evil", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void MD_TheSourceIdMayBeDashed_AndIsComparedAsAGuid()
    {
        var dashed = Source.ToString("D").ToUpperInvariant();

        var result = Run(PlaylistKind.Master, MasterWith(MediaLine(3, source: dashed)));

        Assert.Contains("subs/3/subtitles.m3u8", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void MD_ACraftedName_CanOnlyPickAnotherValidIndex_NeverInjectAnything()
    {
        // F-PL-30: NAME carries a fake URI attribute; whichever group the engine reports, the output comes from grant data
        var name = "x\",DEFAULT=NO,FORCED=NO,AUTOSELECT=YES,URI=\"" + SourceN + "/Subtitles/4/subtitles.m3u8?a=b\",LANGUAGE=\"evil";

        var result = Run(PlaylistKind.Master, MasterWith(MediaLine(3, name: name)));

        Assert.Equal(PlaylistOutcome.Ok, result.Outcome);
        var media = result.Text.Split('\n').Where(static l => l.StartsWith("#EXT-X-MEDIA:", StringComparison.Ordinal)).ToList();
        Assert.True(media.Count <= 1);
        foreach (var line in media)
        {
            Assert.Matches("^#EXT-X-MEDIA:TYPE=SUBTITLES,GROUP-ID=\"subs\",NAME=\"Track [0-9]+\",DEFAULT=(YES|NO),FORCED=(YES|NO),AUTOSELECT=YES,URI=\"subs/[0-9]+/subtitles\\.m3u8\\?ApiKey=" + Token + "\",LANGUAGE=\"(deu|eng)\"$", line);
        }

        Assert.DoesNotContain("evil", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void MD_DuplicateAndUnquotedUriAttributes_AreDroppedOrBuiltFromGrantOnly()
    {
        // F-PL-15 / F-PL-16 for EXT-X-MEDIA
        var unquoted = MediaLine(3).Replace("URI=\"" + SourceN, "URI=" + SourceN, StringComparison.Ordinal).Replace("ApiKey=zzz\"", "ApiKey=zzz", StringComparison.Ordinal);
        var duplicate = MediaLine(3) + ",URI=\"https://evil/\"";

        var a = Run(PlaylistKind.Master, MasterWith(unquoted));
        var b = Run(PlaylistKind.Master, MasterWith(duplicate));

        Assert.DoesNotContain("MEDIA", a.Text, StringComparison.Ordinal);
        Assert.Contains("URI=\"subs/3/subtitles.m3u8?ApiKey=" + Token + "\"", b.Text, StringComparison.Ordinal); // the loose LANGUAGE group swallows the extra attribute; the output is still grant data
        Assert.DoesNotContain("evil", b.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void MD_ATitleWithQuotesOrLength_IsCleanedFromGrantData()
    {
        var context = Context() with { Streams = [new GrantTrack(3, "Subtitle", "srt", "ENG", "Say \"hi\" " + new string('x', 100), true, false, false, false)] };

        var result = Run(PlaylistKind.Master, MasterWith(MediaLine(3)), context);

        var media = result.Text.Split('\n').Single(static l => l.StartsWith("#EXT-X-MEDIA:", StringComparison.Ordinal));
        Assert.Contains("NAME=\"Say hi xxx", media, StringComparison.Ordinal);
        Assert.DoesNotContain("\"hi\"", media, StringComparison.Ordinal);
        Assert.Contains("LANGUAGE=\"und\"", media, StringComparison.Ordinal); // "ENG" is not a lowercase ISO code
        Assert.True(media.Split("NAME=\"")[1].Split('"')[0].Length <= 64);
    }

    // --- URI-line rules

    [Fact]
    public void UM_StoresTheVariantQuery_WithoutCredentials_AndChecksTheGrantsIds()
    {
        var uri = "main.m3u8?MediaSourceId=" + SourceN + "&PlaySessionId=" + PlaySession + "&DeviceId=kiro-capture&ApiKey=a&api_key=b&TranscodeReasons=A%2CB&Tag=t";

        var result = Run(PlaylistKind.Master, M(uri));

        Assert.Equal(PlaylistOutcome.Ok, result.Outcome);
        Assert.Equal(["MediaSourceId", "PlaySessionId", "DeviceId", "TranscodeReasons", "Tag"], result.VariantQuery.Select(static p => p.Key));
        Assert.Equal("A,B", result.VariantQuery.Single(static p => p.Key == "TranscodeReasons").Value);
        Assert.DoesNotContain(result.Text, "ApiKey=a", StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("main.m3u8?MediaSourceId=11111111111111111111111111111111")]
    [InlineData("main.m3u8?MediaSourceId=notaguid")]
    [InlineData("main.m3u8?PlaySessionId=someoneelse")]
    [InlineData("main.m3u8?DeviceId=another-device")]
    [InlineData("main.m3u8?mediasourceid=11111111111111111111111111111111")]
    [InlineData("main.m3u8?MediaSourceId=" + SourceN + "&MediaSourceId=11111111111111111111111111111111")]
    public void UM_AnIdThatIsNotTheGrants_Is502(string uri) =>
        AssertRejected(Run(PlaylistKind.Master, M(uri)), PlaylistFault.Value);

    [Theory]
    [InlineData("https:/collect.evil.example/s.ts")] // F-PL-01
    [InlineData("https:collect.evil.example/s.ts")] // F-PL-02
    [InlineData("HTTPS:/h/x")] // F-PL-03
    [InlineData(" data:text/plain,x")] // F-PL-04
    [InlineData("javascript:alert(1)")] // F-PL-05
    [InlineData("//h/x")] // F-PL-06
    [InlineData("/\\h/x")] // F-PL-07
    [InlineData("%2e%2e/%2e%2e/Items/x/Download")] // F-PL-08
    [InlineData("hls1/main/1.ts?next=http://evil/")] // F-PL-09
    [InlineData("http://evil/seg.ts")] // F-PL-20
    [InlineData("../x")] // F-PL-21
    [InlineData("/abs")] // F-PL-22
    [InlineData("hls1/main/1.ts?runtimeTicks=0&actualSegmentLengthTicks=0#frag")]
    [InlineData("hls1/main/1.ts?runtimeTicks=0&actualSegmentLengthTicks=0 ")]
    [InlineData("hls1/main/1.ts?runtimeTicks=0&actualSegmentLengthTicks=0\"")]
    [InlineData("hls1/main/1.ts?runtimeTicks=0&actualSegmentLengthTicks=0@evil")]
    [InlineData("hls1/main/-1.ts?runtimeTicks=0&actualSegmentLengthTicks=0")]
    [InlineData("hls1/main/01.ts?runtimeTicks=0&actualSegmentLengthTicks=0")]
    [InlineData("hls1/main/1000000.ts?runtimeTicks=0&actualSegmentLengthTicks=0")]
    [InlineData("hls1/main/1.TS?runtimeTicks=0&actualSegmentLengthTicks=0")]
    [InlineData("hls1/main/1.m4s?runtimeTicks=0&actualSegmentLengthTicks=0")]
    [InlineData("hls1/other/1.ts?runtimeTicks=0&actualSegmentLengthTicks=0")]
    [InlineData("HLS1/main/1.ts?runtimeTicks=0&actualSegmentLengthTicks=0")]
    [InlineData("hls1/main/1.ts")]
    public void F_PL_UriLines_ThatNoRuleAccepts_Are502_ForVariantAndSubtitlePlaylists(string uri)
    {
        AssertRejected(Run(PlaylistKind.Variant, V("#EXTINF:3.000, nodesc", uri)));
        AssertRejected(Run(PlaylistKind.Subtitle, S("#EXTINF:30,", uri)));
        AssertRejected(Run(PlaylistKind.Master, M(uri)));
    }

    [Fact]
    public void F_PL_17_ATabLoneCrOrLineSeparatorInsideAUri_Is502()
    {
        foreach (var bad in new[] { "\t", "\r", ((char)0x2028).ToString() })
        {
            AssertRejected(Run(PlaylistKind.Variant, V("#EXTINF:3.000, nodesc", "hls1/main/1.ts?a=b" + bad + "c")), PlaylistFault.Control);
        }
    }

    [Fact]
    public void F_PL_18_And_19_AMegabyteLine_AndThreeHundredThousandLines_Are502()
    {
        AssertRejected(Run(PlaylistKind.Variant, V("#" + new string('a', 1024 * 1024))));
        AssertRejected(Run(PlaylistKind.Variant, "#EXTM3U\n" + string.Concat(Enumerable.Repeat("#c\n", 300_000))));
        AssertRejected(Run(PlaylistKind.Subtitle, "#EXTM3U\n" + string.Concat(Enumerable.Repeat("#c\n", 300_000))));
    }

    [Fact]
    public void UV_ChecksTheTwoTickValues()
    {
        string Variant(string query) => V("#EXTINF:3.000, nodesc", "hls1/main/5.ts?" + query);
        var runtime = 20_000_000_000L;

        var ok = Run(PlaylistKind.Variant, Variant("runtimeTicks=" + (runtime + 36_000_000_000L) + "&actualSegmentLengthTicks=6000000000&Tag=t"), Context(runtime));
        Assert.Equal(PlaylistOutcome.Ok, ok.Outcome);
        Assert.Contains("hls1/main/5.ts?ApiKey=" + Token + "&runtimeTicks=" + (runtime + 36_000_000_000L) + "&actualSegmentLengthTicks=6000000000\n", ok.Text, StringComparison.Ordinal);
        foreach (var bad in new[]
        {
            "runtimeTicks=" + (runtime + 36_000_000_001L) + "&actualSegmentLengthTicks=0",
            "runtimeTicks=0&actualSegmentLengthTicks=6000000001",
            "runtimeTicks=0",
            "actualSegmentLengthTicks=0",
            "runtimeTicks=0&runtimeTicks=1&actualSegmentLengthTicks=0",
            "runtimeTicks=0&actualSegmentLengthTicks=0&ACTUALSEGMENTLENGTHTICKS=0",
            "runtimeTicks=-1&actualSegmentLengthTicks=0",
            "runtimeTicks=&actualSegmentLengthTicks=0",
            "runtimeTicks=1.5&actualSegmentLengthTicks=0",
            "runtimeTicks=99999999999999999999&actualSegmentLengthTicks=0",
            "runtimeTicks=0x10&actualSegmentLengthTicks=0",
        })
        {
            AssertRejected(Run(PlaylistKind.Variant, Variant(bad), Context(runtime)), PlaylistFault.Value);
        }
    }

    [Fact]
    public void UV_KeysAreComparedIgnoringCase_ButValuesAreStrictDigits()
    {
        var result = Run(PlaylistKind.Variant, V("#EXTINF:3.000, nodesc", "hls1/main/0.ts?RUNTIMETICKS=5&actualsegmentlengthticks=7"));

        Assert.Equal(PlaylistOutcome.Ok, result.Outcome);
        Assert.Contains("&runtimeTicks=5&actualSegmentLengthTicks=7\n", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void US_ChecksStartAndEnd()
    {
        string Sub(string query) => S("#EXTINF:30,", "stream.vtt?" + query);
        var runtime = 20_000_000_000L;

        Assert.Equal(PlaylistOutcome.Ok, Run(PlaylistKind.Subtitle, Sub("StartPositionTicks=5&EndPositionTicks=5&CopyTimestamps=true"), Context(runtime)).Outcome);
        Assert.Equal(PlaylistOutcome.Ok, Run(PlaylistKind.Subtitle, Sub("StartPositionTicks=0&EndPositionTicks=" + (runtime + 36_000_000_000L)), Context(runtime)).Outcome);
        foreach (var bad in new[]
        {
            "StartPositionTicks=6&EndPositionTicks=5",
            "StartPositionTicks=0&EndPositionTicks=" + (runtime + 36_000_000_001L),
            "StartPositionTicks=0",
            "EndPositionTicks=0",
            "StartPositionTicks=0&StartPositionTicks=0&EndPositionTicks=0",
            "StartPositionTicks=a&EndPositionTicks=0",
            "StartPositionTicks=0&EndPositionTicks=99999999999999999999",
        })
        {
            AssertRejected(Run(PlaylistKind.Subtitle, Sub(bad), Context(runtime)), PlaylistFault.Value);
        }

        AssertRejected(Run(PlaylistKind.Subtitle, S("#EXTINF:30,", "subs.vtt?StartPositionTicks=0&EndPositionTicks=0")), PlaylistFault.Uri);
        AssertRejected(Run(PlaylistKind.Subtitle, S("#EXTINF:30,", "hls1/main/0.ts?runtimeTicks=0&actualSegmentLengthTicks=0")), PlaylistFault.Uri);
        AssertRejected(Run(PlaylistKind.Variant, V("#EXTINF:30,", "stream.vtt?StartPositionTicks=0&EndPositionTicks=0")), PlaylistFault.Uri);
    }

    [Fact]
    public void AMasterUriInAMediaPlaylist_AndTheReverse_AreRefused()
    {
        AssertRejected(Run(PlaylistKind.Variant, V("#EXTINF:3.000, nodesc", "main.m3u8?a=b")), PlaylistFault.Uri);
        AssertRejected(Run(PlaylistKind.Master, "#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=1\nhls1/main/0.ts?runtimeTicks=0&actualSegmentLengthTicks=0\n"), PlaylistFault.Uri);
        AssertRejected(Run(PlaylistKind.Master, "#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=1\nmain.m3u8?a=b\nmain.m3u8?a=b\n"), PlaylistFault.Uri); // a second URI line
        AssertRejected(Run(PlaylistKind.Master, "#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=1\n#EXT-X-STREAM-INF:BANDWIDTH=2\nmain.m3u8?a=b\n"), PlaylistFault.Structure);
    }

    [Fact]
    public void F_PL_10_To_16_HostileUriAttributesInDroppedTags_NeverReachTheOutput()
    {
        var result = Run(PlaylistKind.Variant, V(
            "#EXT-X-DATERANGE:ID=\"a\",X-ASSET-URI=\"https:/h/p.m3u8\"",
            "#EXT-X-DATERANGE:ID=\"b\",X-ASSET-LIST=\"https://h/l.json\"",
            "#EXT-X-CONTENT-STEERING:SERVER-URI=\"https://h/s\"",
            "#EXT-X-SESSION-DATA:DATA-ID=\"a\",URI=\"https:/h/x\"",
            Seg(0)));

        Assert.Equal(PlaylistOutcome.Ok, result.Outcome);
        Assert.DoesNotContain("h/", result.Text.Replace("hls1/", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.Equal(4, result.Dropped);
    }

    // --- output rules

    [Fact]
    public void O1_TheOutputUsesLfOnly_AndIsAsciiExceptForGrantTitles()
    {
        var result = Run(PlaylistKind.Variant, V(Seg(0)).Replace("\n", "\r\n", StringComparison.Ordinal));

        Assert.DoesNotContain('\r', result.Text);
        Assert.EndsWith("\n", result.Text, StringComparison.Ordinal);
        Assert.All(result.Text, static c => Assert.True(c < 0x80));
        var context = Context() with { Streams = [new GrantTrack(3, "Subtitle", "srt", "eng", "Français", true, false, false, false)] };
        Assert.Contains("NAME=\"Fran" + (char)0xE7 + "ais\"", Run(PlaylistKind.Master, MasterWith(MediaLine(3)), context).Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("0123456789ABCDEF0123456789ABCDEF")]
    [InlineData("0123456789abcdef0123456789abcde")]
    [InlineData("0123456789abcdef0123456789abcdef0")]
    [InlineData("0123456789abcdef0123456789abcdeg")]
    [InlineData("0123456789abcdef0123456789abcde\n")]
    public void O2_ATokenThatIsNotAJellyfinAccessToken_Is403_ForEveryKind(string token)
    {
        foreach (var (kind, text) in new[] { (PlaylistKind.Master, M()), (PlaylistKind.Variant, V(Seg(0))), (PlaylistKind.Subtitle, S("#EXTINF:30,", "stream.vtt?StartPositionTicks=0&EndPositionTicks=0")) })
        {
            var result = Run(kind, text, Context(token: token));
            Assert.Equal(PlaylistOutcome.Forbidden, result.Outcome);
            Assert.Equal(PlaylistFault.Token, result.Fault);
            Assert.Equal(string.Empty, result.Text);
        }
    }

    [Fact]
    public void O2_TheTokenIsOnlyEverEchoedAsTheCallersOwn()
    {
        var result = Run(PlaylistKind.Master, Fixture("jellyfin-master-subs.m3u8"));

        Assert.Equal(11, Regex.Matches(result.Text, "ApiKey=" + Token).Count); // ten renditions and the variant
        Assert.DoesNotContain("0123456789abcdef0123456789abcdef", result.Text, StringComparison.Ordinal); // the Peer's own key from the fixture
    }

    [Fact]
    public void O3_EveryEmittedUriIsRelative()
    {
        foreach (var text in new[]
        {
            Run(PlaylistKind.Master, Fixture("jellyfin-master-subs.m3u8")).Text,
            Run(PlaylistKind.Variant, Fixture("jellyfin-main-fmp4.m3u8")).Text,
            Run(PlaylistKind.Subtitle, Fixture("jellyfin-subtitles.m3u8")).Text,
        })
        {
            foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var uris = line.StartsWith("#", StringComparison.Ordinal) ? Regex.Matches(line, "URI=\"([^\"]*)\"").Select(static m => m.Groups[1].Value) : [line];
                foreach (var uri in uris)
                {
                    Assert.Matches("^(main\\.m3u8|hls1/main/(-1|[0-9]+)\\.(ts|mp4)|subs/[0-9]+/subtitles\\.m3u8|stream\\.vtt)\\?ApiKey=", uri);
                }
            }
        }
    }

    [Fact]
    public void O4_ACanaryHit_Is502_AndNothingIsReturned()
    {
        var hit = Run(PlaylistKind.Master, MasterWith(MediaLine(3)), Context(canary: static text => text.Contains("Track 3", StringComparison.Ordinal)));
        var miss = Run(PlaylistKind.Master, MasterWith(MediaLine(3)), Context(canary: static _ => false));

        AssertRejected(hit, PlaylistFault.Canary);
        Assert.Equal(PlaylistOutcome.Ok, miss.Outcome);
    }

    [Fact]
    public void NoInputText_ReachesTheOutput_ForAHostileMixedPlaylist()
    {
        var hostile = V(
            "#EXT-X-DATERANGE:ID=\"a\",X-ASSET-URI=\"https://collect.evil.example/p\"",
            "# comment http://evil.example/c",
            "#EXT-X-UNKNOWN:http://evil.example/u",
            "#EXT-X-VERSION:3",
            Seg(0).Replace("DeviceId=x", "DeviceId=evil.example", StringComparison.Ordinal));

        var result = Run(PlaylistKind.Variant, hostile);

        Assert.Equal(PlaylistOutcome.Ok, result.Outcome);
        Assert.DoesNotContain("evil", result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("DeviceId", result.Text, StringComparison.Ordinal);
    }
}
