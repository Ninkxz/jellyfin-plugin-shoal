using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Shoal.Sync;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using Xunit;

namespace Jellyfin.Plugin.Shoal.Tests;

/// <summary>
/// K8 acceptance for MetadataSanitizer (DESIGN 5.5): F-MD-02 and F-MD-03 stripped and truncated on rune boundaries, F-MD-04 and F-MD-05
/// canonical ids only, unknown ratings dropped, F-MD-06 flagged untrusted, and the numeric ranges.
/// </summary>
public sealed class MetadataSanitizerTests
{
    private static readonly Guid ItemId = Guid.Parse("0123456789abcdef0123456789abcdef");

    private sealed class FakeRatings(params string[] known) : IRatingTable
    {
        public bool IsRecognized(string rating) => known.Contains(rating, StringComparer.Ordinal);
    }

    private static MetadataSanitizer Sanitizer(params string[] ratings) => new(new FakeRatings(ratings));

    private static BaseItemDto Dto(Action<BaseItemDto>? configure = null)
    {
        var dto = new BaseItemDto { Id = ItemId, Name = "The Matrix" };
        configure?.Invoke(dto);
        return dto;
    }

    private static SanitizedMovie Clean(Action<BaseItemDto>? configure = null, params string[] ratings) =>
        Sanitizer(ratings).Sanitize(Dto(configure))!;

    // --- ids (F-MD-04, F-MD-05)

    [Fact]
    public void F_MD_04_AnImdbIdWithMarkup_IsDropped_AndNeverReachesAnyOutput()
    {
        var movie = Clean(d => d.ProviderIds = new Dictionary<string, string> { ["Imdb"] = "tt0133093\"><img src=x onerror=alert(1)>", ["Tmdb"] = "603" });

        Assert.Null(movie.ProviderIds.Imdb);
        Assert.Equal(603, movie.ProviderIds.Tmdb);
        var nfo = Encoding.UTF8.GetString(NfoWriter.Write(movie, "Hops"));
        Assert.DoesNotContain("onerror", nfo, StringComparison.Ordinal);
        Assert.DoesNotContain("<img", nfo, StringComparison.Ordinal);
        Assert.DoesNotContain("onerror", PathSanitizer.FolderName(movie.Title, movie.Year, movie.ProviderIds, "k3m9q2x7v1tz8c4n"), StringComparison.Ordinal);
    }

    [Fact]
    public void F_MD_05_ATmdbIdWithAnAttribute_IsDropped_AndNeverReachesAnyOutput()
    {
        var movie = Clean(d => d.ProviderIds = new Dictionary<string, string> { ["Tmdb"] = "603 \"onmouseover=alert(1)", ["Imdb"] = "tt0133093" });

        Assert.Null(movie.ProviderIds.Tmdb);
        Assert.Equal("tt0133093", movie.ProviderIds.Imdb);
        var nfo = Encoding.UTF8.GetString(NfoWriter.Write(movie, "Hops"));
        Assert.DoesNotContain("onmouseover", nfo, StringComparison.Ordinal);
        Assert.Contains("<uniqueid type=\"imdb\" default=\"true\">tt0133093</uniqueid>", nfo, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("603", 603L)]
    [InlineData("0603", 603L)]
    [InlineData("9999999999", 9999999999L)]
    [InlineData("1", 1L)]
    [InlineData("0", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    [InlineData("+603", null)]
    [InlineData("-603", null)]
    [InlineData("603\n", null)]
    [InlineData(" 603", null)]
    [InlineData("603 ", null)]
    [InlineData("10000000000", null)]
    [InlineData("６０３", null)] // full-width digits are not ASCII
    [InlineData("6٣", null)] // Arabic-Indic digit
    [InlineData("603.0", null)]
    [InlineData("0x25b", null)]
    [InlineData("603\"><x>", null)]
    public void NumericIds_AreAsciiDigitsOnly_AndReEmittedFromTheInteger(string? raw, long? expected)
    {
        Assert.Equal(expected, MetadataSanitizer.CanonicalNumericId(raw));
    }

    [Theory]
    [InlineData("tt0133093", "tt0133093")]
    [InlineData("tt12345", "tt12345")]
    [InlineData("tt1234567890", "tt1234567890")]
    [InlineData("tt1234", null)]
    [InlineData("tt12345678901", null)]
    [InlineData("TT0133093", null)]
    [InlineData("tt0133093\n", null)]
    [InlineData(" tt0133093", null)]
    [InlineData("tt013309x", null)]
    [InlineData("0133093", null)]
    [InlineData("tt０１３３０９３", null)]
    [InlineData("tt0133093\"><img src=x onerror=alert(1)>", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void ImdbIds_AreTtAndFiveToTenDigits(string? raw, string? expected)
    {
        Assert.Equal(expected, MetadataSanitizer.CanonicalImdbId(raw));
    }

    [Fact]
    public void OtherProviders_AreDropped_AndProviderKeysMatchInAnyCase()
    {
        var ids = MetadataSanitizer.CanonicalProviderIds(new Dictionary<string, string>
        {
            ["TMDB"] = "603",
            ["tvdb"] = "290",
            ["Zap2It"] = "EP0001",
            ["AniDB"] = "5",
            ["Imdb"] = "tt0133093",
        });

        Assert.Equal(new CanonicalIds(603, 290, "tt0133093"), ids);
        Assert.Equal(["tmdb:603", "imdb:tt0133093", "tvdb:290"], ids.DedupKeys());
        Assert.False(MetadataSanitizer.CanonicalProviderIds(null).HasAny);
        Assert.Empty(default(CanonicalIds).DedupKeys());
    }

    [Theory]
    [InlineData("0123456789abcdef0123456789abcdef", true)]
    [InlineData("0123456789ABCDEF0123456789ABCDEF", true)]
    [InlineData("01234567-89ab-cdef-0123-456789abcdef", true)]
    [InlineData("{01234567-89ab-cdef-0123-456789abcdef}", false)]
    [InlineData("(01234567-89ab-cdef-0123-456789abcdef)", false)]
    [InlineData("00000000000000000000000000000000", false)]
    [InlineData("0123456789abcdef0123456789abcdef\n", false)]
    [InlineData("0123456789abcdef0123456789abcde", false)]
    [InlineData("0123456789abcdef0123456789abcdeg", false)]
    [InlineData("0123456789abcdef-0123456789abcdef", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void PeerIds_AreThirtyTwoHexDigitsOrAHyphenatedGuid(string? raw, bool ok)
    {
        Assert.Equal(ok, MetadataSanitizer.TryParseId(raw, out var id));
        Assert.Equal(ok, id != Guid.Empty);
    }

    [Fact]
    public void AnItemWithTheEmptyGuid_IsRejected()
    {
        Assert.Null(Sanitizer().Sanitize(new BaseItemDto { Id = Guid.Empty, Name = "x" }));
    }

    // --- text rules (F-MD-02, F-MD-03)

    [Fact]
    public void F_MD_02_ControlsAndBidiCharacters_AreStripped()
    {
        var movie = Clean(d =>
        {
            d.Name = "A\u0000B\u202EC\u2066D\u200FE\u0085F\u001BG\u061CH\u2029I\u2028J\uFEFFK\u007FL";
            d.OriginalTitle = "x\u202Ay\u202Bz\u202C\u202D\u2067\u2068\u2069";
            d.Overview = "one\r\ntwo\rthree\tfour\u0000five";
        });

        Assert.Equal("ABCDEFGHIJKL", movie.Title);
        Assert.Equal("xyz", movie.OriginalTitle);
        Assert.Equal("one\ntwothree fourfive", movie.Overview);
    }

    [Fact]
    public void ANewlineInATitleBecomesASpace_AndInAnOverviewIsKept()
    {
        var movie = Clean(d =>
        {
            d.Name = "Two\nLines";
            d.Overview = "Para one.\n\nPara two.";
        });

        Assert.Equal("Two Lines", movie.Title);
        Assert.Equal("Para one.\n\nPara two.", movie.Overview);
    }

    [Fact]
    public void Markup_IsReducedToText_AfterNormalization()
    {
        var movie = Clean(d =>
        {
            d.Name = "</title><thumb>http://evil/x.jpg</thumb>";
            d.OriginalTitle = "＜script＞";
            d.Overview = "a < b > c";
        });

        Assert.DoesNotContain('<', movie.Title);
        Assert.DoesNotContain('>', movie.Title);
        Assert.Equal("/titlethumbhttp://evil/x.jpg/thumb", movie.Title);
        Assert.Equal("script", movie.OriginalTitle); // the full-width brackets normalize to ASCII and are then removed
        Assert.Equal("a  b  c", movie.Overview);
    }

    [Fact]
    public void Nfkc_FoldsCompatibilityCharacters()
    {
        var movie = Clean(d => d.Name = "ＡＢＣ ① ﬁlm ²");

        Assert.Equal("ABC 1 film 2", movie.Title);
    }

    [Fact]
    public void F_MD_03_AHugeField_IsCutAtItsCap()
    {
        var huge = new string('x', 100_000);

        var movie = Clean(d =>
        {
            d.Name = huge;
            d.OriginalTitle = huge;
            d.SortName = huge;
            d.Overview = huge;
            d.Taglines = [huge];
            d.OfficialRating = huge;
        }, "x");

        Assert.Equal(300, movie.Title.Length);
        Assert.Equal(300, movie.OriginalTitle.Length);
        Assert.Equal(300, movie.SortTitle.Length);
        Assert.Equal(10_000, movie.Overview.Length);
        Assert.Equal(500, movie.Tagline.Length);
        Assert.Null(movie.OfficialRating); // 32 characters of x is not a rating
    }

    [Fact]
    public void F_MD_03_TheCutNeverSplitsASurrogatePair()
    {
        var emoji = "\U0001F600";
        var movie = Clean(d => d.Name = new string('a', 299) + emoji + "tail");

        Assert.Equal(299, movie.Title.Length);
        Assert.Equal(new string('a', 299), movie.Title);

        var even = Clean(d => d.Name = new string('a', 298) + emoji + "tail");
        Assert.Equal(300, even.Title.Length);
        Assert.EndsWith(emoji, even.Title, StringComparison.Ordinal);
        Assert.True(even.Title.EnumerateRunes().All(static r => r != System.Text.Rune.ReplacementChar));
    }

    [Fact]
    public void UnpairedSurrogatesAndNoncharacters_AreDropped_SoTheFieldIsValidXml()
    {
        var movie = Clean(d => d.Name = "a\uD800b\uDC00c\uFFFEd\uFFFFe\uFDD0f\uFFFDg" + "\U0001FFFE" + "h");

        Assert.Equal("abcdefgh", movie.Title);
        Assert.NotEmpty(NfoWriter.Write(movie, "Hops"));
    }

    [Fact]
    public void ATrailingSpace_ExposedByTheCut_IsTrimmed()
    {
        var movie = Clean(d => d.Name = new string('a', 299) + "  b");

        Assert.Equal(new string('a', 299), movie.Title);
    }

    [Fact]
    public void AllHostileInputTypes_ProduceEmptyText_NotExceptions()
    {
        var movie = Clean(d =>
        {
            d.Name = "\u0000\u0000";
            d.OriginalTitle = null;
            d.Overview = new string('\u202E', 50_000);
        });

        Assert.Equal(string.Empty, movie.Title);
        Assert.Equal(string.Empty, movie.OriginalTitle);
        Assert.Equal(string.Empty, movie.Overview);
    }

    // --- lists

    [Fact]
    public void Genres_Studios_AndActors_AreCapped_DeduplicatedAndCleaned()
    {
        var movie = Clean(d =>
        {
            d.Genres = [.. Enumerable.Range(0, 100).Select(i => "Genre" + i), "genre1", string.Empty, "\u0000"];
            d.Studios = [.. Enumerable.Range(0, 40).Select(i => new NameGuidPair { Name = i + new string('s', 500) })];
            d.People =
            [
                new BaseItemPerson { Name = "Director", Type = PersonKind.Director, Role = "x" },
                new BaseItemPerson { Name = "Lead", Type = PersonKind.Actor, Role = new string('r', 500) },
                new BaseItemPerson { Name = "\u0000", Type = PersonKind.Actor },
                .. Enumerable.Range(0, 20).Select(i => new BaseItemPerson { Name = "Actor" + i, Type = PersonKind.Actor, Role = "Role" + i }),
            ];
        });

        Assert.Equal(32, movie.Genres.Count);
        Assert.Equal("Genre0", movie.Genres[0]);
        Assert.Equal(32, movie.Genres.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(16, movie.Studios.Count);
        Assert.All(movie.Studios, static s => Assert.Equal(128, s.Length));
        Assert.Equal(5, movie.Actors.Count);
        Assert.Equal("Lead", movie.Actors[0].Name);
        Assert.Equal(128, movie.Actors[0].Role.Length);
        Assert.Equal("Actor0", movie.Actors[1].Name);
        Assert.DoesNotContain(movie.Actors, static a => a.Name == "Director");
    }

    [Fact]
    public void LongGenreNames_AreCutAt64()
    {
        var movie = Clean(d => d.Genres = [new string('g', 200)]);

        Assert.Equal(64, movie.Genres.Single().Length);
    }

    // --- ratings (F-MD-06)

    [Fact]
    public void ARecognizedRating_IsKept_AndFlaggedUntrusted()
    {
        var movie = Clean(d => d.OfficialRating = " PG-13 ", "PG-13");

        Assert.Equal("PG-13", movie.OfficialRating);
        Assert.True(movie.RatingUntrusted);
    }

    [Fact]
    public void F_MD_06_AnAdultTitleLabelledG_IsKept_ButFlaggedUntrusted()
    {
        var movie = Clean(d => d.OfficialRating = "G", "G");

        Assert.Equal("G", movie.OfficialRating);
        Assert.True(movie.RatingUntrusted); // which is why restricted profiles are excluded from "Everyone" by default
    }

    [Theory]
    [InlineData("NOT-A-RATING")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("PG-13\"><script>")]
    public void AnUnknownRating_IsDropped_AndTheItemIsUnrated(string? rating)
    {
        var movie = Clean(d => d.OfficialRating = rating, "PG-13");

        Assert.Null(movie.OfficialRating);
        Assert.False(movie.RatingUntrusted);
        Assert.DoesNotContain("<mpaa>", Encoding.UTF8.GetString(NfoWriter.Write(movie, "Hops")), StringComparison.Ordinal);
    }

    [Fact]
    public void TheRatingIsLookedUpAfterCleaning_NotBefore()
    {
        var seen = new List<string>();
        var sanitizer = new MetadataSanitizer(new Recording(seen));

        sanitizer.Sanitize(Dto(d => d.OfficialRating = "ＰＧ\u202E-13"));

        Assert.Equal(["PG-13"], seen);
    }

    private sealed class Recording(List<string> seen) : IRatingTable
    {
        public bool IsRecognized(string rating)
        {
            seen.Add(rating);
            return false;
        }
    }

    // --- numbers and dates

    [Theory]
    [InlineData(0.0, 0.0)]
    [InlineData(8.2, 8.2)]
    [InlineData(10.0, 10.0)]
    [InlineData(10.01, null)]
    [InlineData(-0.1, null)]
    [InlineData(double.NaN, null)]
    [InlineData(double.PositiveInfinity, null)]
    public void TheCommunityRating_IsZeroToTen(double raw, double? expected)
    {
        Assert.Equal(expected, Clean(d => d.CommunityRating = (float)raw).CommunityRating is { } v ? Math.Round(v, 1) : (double?)null);
    }

    [Theory]
    [InlineData(1869, null)]
    [InlineData(1870, 1870)]
    [InlineData(1999, 1999)]
    [InlineData(2100, 2100)]
    [InlineData(2101, null)]
    [InlineData(-5, null)]
    public void TheProductionYear_IsBetween1870And2100(int raw, int? expected)
    {
        Assert.Equal(expected, Clean(d => d.ProductionYear = raw).Year);
    }

    [Fact]
    public void ThePremiereDate_IsBetween1870And2100_ElseDropped()
    {
        Assert.Equal(new DateOnly(1999, 3, 30), Clean(d => d.PremiereDate = new DateTime(1999, 3, 30, 0, 0, 0, DateTimeKind.Utc)).Premiered);
        Assert.Null(Clean(d => d.PremiereDate = new DateTime(1500, 1, 1, 0, 0, 0, DateTimeKind.Utc)).Premiered);
        Assert.Null(Clean(d => d.PremiereDate = new DateTime(2500, 1, 1, 0, 0, 0, DateTimeKind.Utc)).Premiered);
        Assert.Null(Clean().Premiered);
    }

    [Fact]
    public void TheRunTime_IsZeroTo48Hours()
    {
        var limit = TimeSpan.FromHours(48).Ticks;

        Assert.Equal(limit, Clean(d => d.RunTimeTicks = limit).RunTimeTicks);
        Assert.Equal(0, Clean(d => d.RunTimeTicks = 0).RunTimeTicks);
        Assert.Null(Clean(d => d.RunTimeTicks = limit + 1).RunTimeTicks);
        Assert.Null(Clean(d => d.RunTimeTicks = -1).RunTimeTicks);
    }

    // --- media sources

    private static MediaSourceInfo Source(string id = "fedcba9876543210fedcba9876543210", Action<MediaSourceInfo>? configure = null)
    {
        var source = new MediaSourceInfo
        {
            Id = id,
            Size = 1_000,
            Bitrate = 5_000_000,
            Container = "mkv",
            MediaStreams = [new MediaStream { Type = MediaStreamType.Video, Codec = "h264", Width = 1920, Height = 1080 }],
        };
        configure?.Invoke(source);
        return source;
    }

    [Fact]
    public void AGoodSource_IsKeptWithItsTechnicalFacts()
    {
        var movie = Clean(d => d.MediaSources = [Source()]);

        var source = Assert.Single(movie.Sources);
        Assert.Equal(Guid.Parse("fedcba9876543210fedcba9876543210"), source.Id);
        Assert.Equal(1_000, source.Size);
        Assert.Equal(5_000_000, source.Bitrate);
        Assert.Equal("mkv", source.Container);
        Assert.Equal("h264", source.VideoCodec);
        Assert.Equal(1920, source.Width);
        Assert.Equal(1080, source.Height);
    }

    [Theory]
    [InlineData("mkv", "mkv")]
    [InlineData("MKV", "mkv")]
    [InlineData("mov,mp4,m4a,3gp,3g2,mj2", "mov,mp4,m4a,3gp,3g2,mj2")]
    [InlineData("matroska,webm", "matroska,webm")]
    [InlineData("a,b,c,d,e,f,g,h", "a,b,c,d,e,f,g,h")]
    [InlineData("a,b,c,d,e,f,g,h,i", null)]
    [InlineData("mkv,", null)]
    [InlineData(",mkv", null)]
    [InlineData("mkv,,mp4", null)]
    [InlineData("mkv\"><script>", null)]
    [InlineData("abcdefghi", null)]
    [InlineData("mkv mp4", null)]
    [InlineData("mkv\n", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void TheContainer_IsLowercaseListsOfShortTokens(string? raw, string? expected)
    {
        Assert.Equal(expected, Clean(d => d.MediaSources = [Source(configure: s => s.Container = raw)]).Sources.Single().Container);
    }

    [Fact]
    public void TheContainer_IsAtMost40CharactersInTotal()
    {
        var at40 = "abcdefgh,abcdefgh,abcdefgh,abcdefgh,abcd"; // 40
        Assert.Equal(40, at40.Length);
        Assert.Equal(at40, Clean(d => d.MediaSources = [Source(configure: s => s.Container = at40)]).Sources.Single().Container);

        var at41 = at40 + "e";
        Assert.Null(Clean(d => d.MediaSources = [Source(configure: s => s.Container = at41)]).Sources.Single().Container);
    }

    [Theory]
    [InlineData("h264", "h264")]
    [InlineData("HEVC", "hevc")]
    [InlineData("vp9", "vp9")]
    [InlineData("mpeg2video", "mpeg2video")]
    [InlineData("h.264_x-1", "h.264_x-1")]
    [InlineData("h264\"><x>", null)]
    [InlineData("h 264", null)]
    [InlineData("", null)]
    public void TheVideoCodec_IsASimpleToken(string raw, string? expected)
    {
        var movie = Clean(d => d.MediaSources = [Source(configure: s => s.MediaStreams = [new MediaStream { Type = MediaStreamType.Video, Codec = raw }])]);

        Assert.Equal(expected, movie.Sources.Single().VideoCodec);
    }

    [Fact]
    public void TheNumbersOfASource_AreRangeChecked()
    {
        var movie = Clean(d => d.MediaSources =
        [
            Source(configure: s =>
            {
                s.Size = (1L << 50) + 1;
                s.Bitrate = 1_000_000_001;
                s.RunTimeTicks = TimeSpan.FromHours(49).Ticks;
                s.MediaStreams = [new MediaStream { Type = MediaStreamType.Video, Codec = "h264", Width = 16_385, Height = -1 }];
            }),
            Source("00112233445566778899aabbccddeeff", s =>
            {
                s.Size = 1L << 50;
                s.Bitrate = 1_000_000_000;
                s.MediaStreams = [new MediaStream { Type = MediaStreamType.Video, Codec = "h264", Width = 16_384, Height = 0 }];
            }),
        ]);

        var bad = movie.Sources[0];
        Assert.Null(bad.Size);
        Assert.Null(bad.Bitrate);
        Assert.Null(bad.RunTimeTicks);
        Assert.Null(bad.Width);
        Assert.Null(bad.Height);
        var edge = movie.Sources[1];
        Assert.Equal(1L << 50, edge.Size);
        Assert.Equal(1_000_000_000, edge.Bitrate);
        Assert.Equal(16_384, edge.Width);
        Assert.Equal(0, edge.Height);
    }

    [Fact]
    public void ASourceWithABadId_IsSkipped_AndAtMostEightAreKept()
    {
        var movie = Clean(d => d.MediaSources =
        [
            Source("not-an-id"),
            Source("0123456789abcdef0123456789abcdef\n"),
            .. Enumerable.Range(1, 12).Select(i => Source(i.ToString("x32", System.Globalization.CultureInfo.InvariantCulture))),
        ]);

        Assert.Equal(8, movie.Sources.Count);
        Assert.Equal(Guid.Parse("00000000000000000000000000000001"), movie.Sources[0].Id);
    }

    // --- image tags

    [Theory]
    [InlineData("a1b2C3", "a1b2C3")]
    [InlineData("0123456789abcdef0123456789abcdef", "0123456789abcdef0123456789abcdef")]
    [InlineData("tag-with-dash", null)]
    [InlineData("tag\"x", null)]
    [InlineData("tag\n", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void ImageTags_AreAlphanumeric(string? raw, string? expected)
    {
        Assert.Equal(expected, MetadataSanitizer.ImageTag(raw));
    }

    [Fact]
    public void ImageTagsOfAnItem_AreTakenFromPrimaryAndTheFirstValidBackdrop()
    {
        var movie = Clean(d =>
        {
            d.ImageTags = new Dictionary<ImageType, string> { [ImageType.Primary] = "abc123", [ImageType.Logo] = "zzz" };
            d.BackdropImageTags = ["bad tag", "def456"];
        });

        Assert.Equal("abc123", movie.PrimaryImageTag);
        Assert.Equal("def456", movie.BackdropImageTag);
        Assert.Null(Clean().PrimaryImageTag);
        Assert.Null(Clean(d => d.ImageTags = new Dictionary<ImageType, string> { [ImageType.Primary] = "<x>" }).PrimaryImageTag);
    }

    [Fact]
    public void ADisplayName_IsCleanedTo64Characters()
    {
        Assert.Equal("Hopsb", MetadataSanitizer.DisplayName(" H\u202Eops<b> "));
        Assert.Equal(64, MetadataSanitizer.DisplayName(new string('n', 500)).Length);
        Assert.Equal(string.Empty, MetadataSanitizer.DisplayName(null));
    }
}
