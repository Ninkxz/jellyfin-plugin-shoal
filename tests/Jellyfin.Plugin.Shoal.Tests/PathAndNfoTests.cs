using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Jellyfin.Plugin.Shoal.Sync;
using MediaBrowser.Model.Dto;
using Xunit;

namespace Jellyfin.Plugin.Shoal.Tests;

/// <summary>
/// K8 acceptance for PathSanitizer and NfoWriter (DESIGN 5.5, 3.4): T-SYNC-6 (10,000 hostile titles stay in the root and give valid XML),
/// reserved names, case- and normalization-insensitive collisions, link refusal, and the shape of the NFO.
/// </summary>
public sealed class PathAndNfoTests
{
    private const string StubKey = "k3m9q2x7v1tz8c4n";

    private sealed class NoRatings : IRatingTable
    {
        public bool IsRecognized(string rating) => rating == "R";
    }

    private static string Name(string? title, int? year = null, long? tmdb = null, string? imdb = null) =>
        PathSanitizer.FolderName(title, year, new CanonicalIds(tmdb, null, imdb), StubKey);

    // --- names

    [Fact]
    public void ANormalMovie_GetsTitleYearAndTheTmdbTag()
    {
        Assert.Equal("The Matrix (1999) [tmdbid-603]", Name("The Matrix", 1999, 603));
    }

    [Fact]
    public void TheIdTag_IsTmdb_ElseImdb_ElseTheStubKeyPrefix()
    {
        Assert.Equal("The Matrix (1999) [tmdbid-603]", PathSanitizer.FolderName("The Matrix", 1999, new CanonicalIds(603, 290, "tt0133093"), StubKey));
        Assert.Equal("The Matrix (1999) [imdbid-tt0133093]", Name("The Matrix", 1999, imdb: "tt0133093"));
        Assert.Equal("The Matrix (1999) [shoal-k3m9q2x7]", Name("The Matrix", 1999));
        Assert.Equal("The Matrix [shoal-k3m9q2x7]", Name("The Matrix", 1800));
        Assert.Equal("The Matrix [shoal-k3m9q2x7]", Name("The Matrix", null));
        Assert.Equal("The Matrix (2100) [shoal-k3m9q2x7]", Name("The Matrix", 2100));
    }

    [Fact]
    public void ATvdbId_IsNotUsedInAMovieName()
    {
        Assert.Equal("X [shoal-k3m9q2x7]", PathSanitizer.FolderName("X", null, new CanonicalIds(null, 290, null), StubKey));
    }

    [Theory]
    [InlineData("../../etc/x", "etc x")]
    [InlineData("..\\..\\Windows\\System32", "Windows System32")]
    [InlineData("C:\\boot.ini", "C boot.ini")]
    [InlineData("a/b\\c:d*e?f\"g<h>i|j", "a b c d e f g h i j")]
    [InlineData("  spaced   out  ", "spaced out")]
    [InlineData("...hidden...", "hidden")]
    [InlineData("trailing dot.", "trailing dot")]
    [InlineData("a..b", "ab")]
    [InlineData("a....b", "ab")]
    [InlineData("a...b", "a.b")]
    [InlineData("Dr. Strangelove", "Dr. Strangelove")]
    [InlineData("tab\tand\nnewline", "tab and newline")]
    [InlineData("nul\0byte", "nul byte")]
    [InlineData("rtl\u202Eevil", "rtlevil")]
    [InlineData("\uFF21\uFF22\uFF23", "ABC")]
    [InlineData("</title><thumb>http://evil</thumb>", "title thumb http evil thumb")]
    public void TheTitlePart_HasNoSeparatorsDotsOrControls(string title, string expectedTitlePart)
    {
        Assert.Equal(expectedTitlePart + " [shoal-k3m9q2x7]", Name(title));
    }

    [Theory]
    [InlineData("CON")]
    [InlineData("con")]
    [InlineData("Con.txt")]
    [InlineData("PRN")]
    [InlineData("AUX.tar.gz")]
    [InlineData("NUL")]
    [InlineData("COM1")]
    [InlineData("com9.log")]
    [InlineData("LPT1")]
    [InlineData("lpt9.x")]
    [InlineData("CON ")]
    [InlineData("CON.")]
    [InlineData("CONIN$")]
    [InlineData("CONOUT$")]
    [InlineData("\uFF23\uFF2F\uFF2E")] // full-width CON normalizes to CON
    [InlineData("COM\u00B9")] // superscript one normalizes to COM1
    [InlineData("")]
    [InlineData("...")]
    [InlineData(" . . ")]
    [InlineData("////")]
    [InlineData("\u0000\u0000")]
    [InlineData(null)]
    public void ReservedAndEmptyNames_BecomeUntitled(string? title)
    {
        Assert.Equal("Untitled [shoal-k3m9q2x7]", Name(title));
    }

    [Theory]
    [InlineData("CONSOLE")]
    [InlineData("Company")]
    [InlineData("COM10")]
    [InlineData("LPT0")]
    [InlineData("Conan")]
    [InlineData("AUX Man")]
    public void NamesThatOnlyStartLikeAReservedName_AreKept(string title)
    {
        Assert.Equal(title + " [shoal-k3m9q2x7]", Name(title));
    }

    [Fact]
    public void ALongTitle_IsCutAt120BytesOnARuneBoundary()
    {
        var ascii = Name(new string('a', 1_000_000));
        Assert.Equal(new string('a', 120) + " [shoal-k3m9q2x7]", ascii);

        var twoByte = Name(new string('\u00E9', 500)); // 2 bytes each
        Assert.Equal(new string('\u00E9', 60) + " [shoal-k3m9q2x7]", twoByte);

        var fourByte = Name(string.Concat(Enumerable.Repeat("\U0001F600", 100)));
        Assert.Equal(string.Concat(Enumerable.Repeat("\U0001F600", 30)) + " [shoal-k3m9q2x7]", fourByte);

        var threeByte = Name(new string('\u4E2D', 100)); // 3 bytes each
        Assert.Equal(new string('\u4E2D', 40) + " [shoal-k3m9q2x7]", threeByte);
    }

    [Fact]
    public void TheCut_NeverLeavesATrailingDotOrSpace_OrABrokenCharacter()
    {
        var name = Name(new string('a', 119) + ". b");

        Assert.Equal(new string('a', 119) + " [shoal-k3m9q2x7]", name);
        var utf8 = Encoding.UTF8.GetBytes(Name(new string('\u00E9', 61)));
        Assert.DoesNotContain("\uFFFD", Encoding.UTF8.GetString(utf8), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("short")]
    [InlineData("")]
    [InlineData("bad/key1234")]
    [InlineData("bad key 1234")]
    [InlineData("k3m9q2x7\n")]
    public void AStubKeyThatIsNotLettersAndDigits_IsAProgrammingError(string key)
    {
        Assert.Throws<ArgumentException>(() => PathSanitizer.FolderName("x", null, default, key));
    }

    // --- collisions

    [Fact]
    public void CollisionKeys_IgnoreCaseAndNormalization()
    {
        Assert.Equal(PathSanitizer.CollisionKey("Am\u00E9lie"), PathSanitizer.CollisionKey("AME\u0301LIE"));
        Assert.Equal(PathSanitizer.CollisionKey("ABC"), PathSanitizer.CollisionKey("\uFF41\uFF42\uFF43"));
        Assert.NotEqual(PathSanitizer.CollisionKey("abc"), PathSanitizer.CollisionKey("abd"));
    }

    [Fact]
    public void ACollision_GetsTheStubKeySuffix_AndIsDetectedAcrossCaseAndNormalization()
    {
        var used = new HashSet<string>(StringComparer.Ordinal);
        var ids = new CanonicalIds(1, null, null);

        var first = PathSanitizer.Unique(PathSanitizer.FolderName("Am\u00E9lie", 2001, ids, "aaaaaaaa11111111"), "aaaaaaaa11111111", used);
        var second = PathSanitizer.Unique(PathSanitizer.FolderName("AME\u0301LIE", 2001, ids, "bbbbbbbb22222222"), "bbbbbbbb22222222", used);
        var third = PathSanitizer.Unique(PathSanitizer.FolderName("amelie", 2001, ids, "cccccccc33333333"), "cccccccc33333333", used);
        var fourth = PathSanitizer.Unique(PathSanitizer.FolderName("\uFF21m\u00E9lie", 2001, ids, "dddddddd44444444"), "dddddddd44444444", used);

        Assert.Equal("Am\u00E9lie (2001) [tmdbid-1]", first);
        Assert.Equal("AM\u00C9LIE (2001) [tmdbid-1] [shoal-bbbbbbbb]", second);
        Assert.Equal("amelie (2001) [tmdbid-1]", third); // a different spelling, not a collision
        Assert.Equal("Am\u00E9lie (2001) [tmdbid-1] [shoal-dddddddd]", fourth);
        Assert.Equal(4, used.Count);
    }

    [Fact]
    public void WhenEvenTheShortSuffixCollides_TheWholeKeyIsUsed()
    {
        var used = new HashSet<string>(StringComparer.Ordinal);
        var ids = new CanonicalIds(1, null, null);
        var name = PathSanitizer.FolderName("Same", null, ids, "aaaaaaaa11111111");

        var a = PathSanitizer.Unique(name, "aaaaaaaa11111111", used);
        var b = PathSanitizer.Unique(name, "aaaaaaaa11111111", used);
        var c = PathSanitizer.Unique(name, "aaaaaaaa11111111", used);

        Assert.Equal("Same [tmdbid-1]", a);
        Assert.Equal("Same [tmdbid-1] [shoal-aaaaaaaa]", b);
        Assert.Equal("Same [tmdbid-1] [shoal-aaaaaaaa11111111]", c);
        Assert.Equal(3, new HashSet<string>(used).Count);
    }

    [Fact]
    public void ANameThatAlreadyEndsInTheStubTag_IsNotTaggedTwice()
    {
        var used = new HashSet<string>(StringComparer.Ordinal);
        var name = PathSanitizer.FolderName("Untagged", null, default, "aaaaaaaa11111111");

        Assert.Equal("Untagged [shoal-aaaaaaaa]", PathSanitizer.Unique(name, "aaaaaaaa11111111", used));
        Assert.Equal("Untagged [shoal-aaaaaaaa] [shoal-aaaaaaaa11111111]", PathSanitizer.Unique(name, "aaaaaaaa11111111", used));
    }

    // --- confinement

    [Fact]
    public void AnOrdinaryName_StaysInsideTheLibraryRoot()
    {
        using var temp = new TempDirectory();

        Assert.True(PathSanitizer.TryConfine(temp.Path, "Movie (1999) [tmdbid-1]", out var full));
        Assert.Equal(Path.Combine(Path.GetFullPath(temp.Path), "Movie (1999) [tmdbid-1]"), full);
        Assert.True(PathSanitizer.TryConfine(temp.Path + Path.DirectorySeparatorChar, "x", out _));
    }

    [Theory]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("../x")]
    [InlineData("..\\x")]
    [InlineData("/etc/passwd")]
    public void ANameWithASeparatorOrADotComponent_IsRefused(string name)
    {
        using var temp = new TempDirectory();

        Assert.False(PathSanitizer.TryConfine(temp.Path, name, out var full));
        Assert.Equal(string.Empty, full);
    }

    [Fact]
    public void AnExistingLink_BelowTheStubRoot_IsDetected()
    {
        using var temp = new TempDirectory();
        var root = temp.Combine("stubs");
        var real = Path.Combine(root, "peer", "lib");
        Directory.CreateDirectory(real);
        var elsewhere = temp.Combine("elsewhere");
        Directory.CreateDirectory(elsewhere);

        Assert.False(PathSanitizer.HasLinkBelow(root, Path.Combine(real, "Movie")));
        Assert.False(PathSanitizer.HasLinkBelow(root, real));

        var link = Path.Combine(root, "peer", "evil");
        try
        {
            Directory.CreateSymbolicLink(link, elsewhere);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            return; // symbolic links need a privilege on this machine; the Linux run covers this test
        }

        Assert.True(PathSanitizer.HasLinkBelow(root, link));
        Assert.True(PathSanitizer.HasLinkBelow(root, Path.Combine(link, "Movie")));
        Assert.False(PathSanitizer.HasLinkBelow(root, Path.Combine(real, "Movie")));
    }

    [Fact]
    public void APathOutsideTheStubRoot_IsTreatedAsUnsafe()
    {
        using var temp = new TempDirectory();

        Assert.True(PathSanitizer.HasLinkBelow(temp.Combine("stubs"), temp.Combine("other", "x")));
        Assert.True(PathSanitizer.HasLinkBelow(temp.Combine("stubs"), temp.Combine("stubs")));
    }

    // --- NFO

    private static SanitizedMovie FullMovie(Action<BaseItemDto>? configure = null)
    {
        var dto = new BaseItemDto
        {
            Id = Guid.Parse("0123456789abcdef0123456789abcdef"),
            Name = "The Matrix",
            OriginalTitle = "The Matrix",
            SortName = "Matrix",
            ProductionYear = 1999,
            PremiereDate = new DateTime(1999, 3, 30, 0, 0, 0, DateTimeKind.Utc),
            Overview = "A hacker learns the truth.\n\nSecond paragraph.",
            Taglines = ["Welcome to the Real World."],
            OfficialRating = "R",
            CommunityRating = 8.2f,
            Genres = ["Action", "Sci-Fi"],
            Studios = [new NameGuidPair { Name = "Warner Bros." }],
            ProviderIds = new Dictionary<string, string> { ["Tmdb"] = "603", ["Imdb"] = "tt0133093" },
            People = [new BaseItemPerson { Name = "Keanu Reeves", Role = "Neo", Type = Jellyfin.Data.Enums.PersonKind.Actor }],
        };
        configure?.Invoke(dto);
        return new MetadataSanitizer(new NoRatings()).Sanitize(dto)!;
    }

    [Fact]
    public void TheNfo_HasTheShapeOfDesign34()
    {
        var bytes = NfoWriter.Write(FullMovie(), "Hops");
        var text = Encoding.UTF8.GetString(bytes);

        Assert.StartsWith("<?xml version=\"1.0\" encoding=\"utf-8\" standalone=\"yes\"?>\n<movie>", text, StringComparison.Ordinal);
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF, "no byte order mark");
        var movie = XDocument.Parse(text).Root!;
        Assert.Equal("movie", movie.Name.LocalName);
        Assert.Equal("The Matrix", (string?)movie.Element("title"));
        Assert.Equal("The Matrix", (string?)movie.Element("originaltitle"));
        Assert.Equal("Matrix", (string?)movie.Element("sorttitle"));
        Assert.Equal("1999", (string?)movie.Element("year"));
        Assert.Equal("1999-03-30", (string?)movie.Element("premiered"));
        Assert.Equal("A hacker learns the truth.\n\nSecond paragraph.", (string?)movie.Element("plot"));
        Assert.Equal("Welcome to the Real World.", (string?)movie.Element("tagline"));
        Assert.Equal("R", (string?)movie.Element("mpaa"));
        Assert.Equal("8.2", (string?)movie.Element("rating"));
        Assert.Equal(["Action", "Sci-Fi"], movie.Elements("genre").Select(static e => e.Value));
        Assert.Equal(["Warner Bros."], movie.Elements("studio").Select(static e => e.Value));
        var ids = movie.Elements("uniqueid").ToList();
        Assert.Equal(["tmdb", "imdb"], ids.Select(static e => (string?)e.Attribute("type")));
        Assert.Equal(["603", "tt0133093"], ids.Select(static e => e.Value));
        Assert.Equal("true", (string?)ids[0].Attribute("default"));
        Assert.Null(ids[1].Attribute("default"));
        var actor = Assert.Single(movie.Elements("actor"));
        Assert.Equal("Keanu Reeves", (string?)actor.Element("name"));
        Assert.Equal("Neo", (string?)actor.Element("role"));
        Assert.Null(actor.Element("thumb"));
        Assert.Equal(["Shoal", "Shoal: Hops"], movie.Elements("tag").Select(static e => e.Value));
    }

    [Fact]
    public void TheNfo_NeverHasAnElementThatCarriesAUrlOrAnythingElseOnTheForbiddenList()
    {
        var movie = XDocument.Parse(Encoding.UTF8.GetString(NfoWriter.Write(FullMovie(), "Hops"))).Root!;
        var names = movie.DescendantsAndSelf().Select(static e => e.Name.LocalName).ToHashSet(StringComparer.Ordinal);

        foreach (var forbidden in new[] { "thumb", "fanart", "trailer", "fileinfo", "set", "lockdata", "website", "runtime" })
        {
            Assert.DoesNotContain(forbidden, names);
        }
    }

    [Fact]
    public void TheNfo_IsDeterministic_SoAnUnchangedItemWritesIdenticalBytes()
    {
        Assert.Equal(NfoWriter.Write(FullMovie(), "Hops"), NfoWriter.Write(FullMovie(), "Hops"));
    }

    [Fact]
    public void ASparseMovie_WritesOnlyWhatItHas_AndTheDefaultIdFallsToTheFirstPresent()
    {
        var sparse = new MetadataSanitizer(new NoRatings()).Sanitize(new BaseItemDto
        {
            Id = Guid.Parse("0123456789abcdef0123456789abcdef"),
            Name = "Only A Title",
            ProviderIds = new Dictionary<string, string> { ["Tvdb"] = "290" },
        })!;

        var movie = XDocument.Parse(Encoding.UTF8.GetString(NfoWriter.Write(sparse, string.Empty))).Root!;

        Assert.Equal(["title", "uniqueid", "tag"], movie.Elements().Select(static e => e.Name.LocalName));
        Assert.Equal("true", (string?)movie.Element("uniqueid")!.Attribute("default"));
        Assert.Equal("tvdb", (string?)movie.Element("uniqueid")!.Attribute("type"));
        Assert.Equal(["Shoal"], movie.Elements("tag").Select(static e => e.Value));
    }

    [Fact]
    public void ThePeerNameInTheTag_IsCleaned()
    {
        var movie = XDocument.Parse(Encoding.UTF8.GetString(NfoWriter.Write(FullMovie(), "<b>Ho\u202Eps</b>"))).Root!;

        Assert.Equal(["Shoal", "Shoal: bHops/b"], movie.Elements("tag").Select(static e => e.Value));
    }

    [Fact]
    public void TheNfo_ParsesWithDtdProcessingProhibited()
    {
        var bytes = NfoWriter.Write(FullMovie(), "Hops");
        using var reader = XmlReader.Create(new MemoryStream(bytes), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, CheckCharacters = true });

        while (reader.Read())
        {
        }
    }

    // --- T-SYNC-6

    private static readonly string[] Fragments =
    [
        "../../etc/x", "..\\..\\..\\Windows", "CON", "con.txt", "NUL", "COM1", "aux", "LPT9.x", "</title><thumb>http://evil</thumb>",
        "\u202Eevil", "\u2066", "\u0000", "\u0001\u001F", "\u0085", "\uD800", "\uDC00x", "\uFFFE", "\uFFFF", "\uFDD0", "..", "...", ". ", " .",
        "C:\\", "/", "\\", "*", "?", "\"", "<", ">", "|", "\uFF1Cscript\uFF1E", "\uFB01", "\u2460", "\uFDFA", "\U0001F600", "e\u0301",
        "\u0000\u0000\u0000", "\r\n", "\t", "~", "$", "%00", "%2e%2e%2f", "&amp;", "&#0;", "<![CDATA[x]]>", "]]>", "--", "<?xml ?>", "<!DOCTYPE x>",
        "tt0133093\"><img src=x onerror=alert(1)>", "603 \"onmouseover=alert(1)", "[tmdbid-1]", "(1999)", "a", "Z", "9", "Ünï", "日本語", "ﷺ",
    ];

    private static string HostileTitle(Random random)
    {
        var count = random.Next(1, 14);
        var builder = new StringBuilder();
        for (var i = 0; i < count; i++)
        {
            builder.Append(Fragments[random.Next(Fragments.Length)]);
        }

        return random.Next(40) == 0 ? builder.ToString() + new string('x', random.Next(200, 6000)) : builder.ToString();
    }

    [Fact]
    public void T_SYNC_6_TenThousandHostileTitles_StayInTheRootAndGiveValidXml()
    {
        using var temp = new TempDirectory();
        var root = Path.GetFullPath(temp.Combine("stubs", "peerkey", "Movies"));
        Directory.CreateDirectory(root);
        var sanitizer = new MetadataSanitizer(new NoRatings());
        var random = new Random(20261015);
        var used = new HashSet<string>(StringComparer.Ordinal);
        var created = 0;

        for (var i = 0; i < 10_000; i++)
        {
            var title = HostileTitle(random);
            var overview = HostileTitle(random);
            var key = "k" + i.ToString("x15", System.Globalization.CultureInfo.InvariantCulture);
            var movie = sanitizer.Sanitize(new BaseItemDto
            {
                Id = Guid.NewGuid(),
                Name = title,
                OriginalTitle = title,
                Overview = overview,
                Taglines = [title],
                Genres = [title],
                ProductionYear = random.Next(1800, 2200),
                OfficialRating = title,
                ProviderIds = new Dictionary<string, string> { ["Imdb"] = HostileTitle(random), ["Tmdb"] = HostileTitle(random) },
                People = [new BaseItemPerson { Name = title, Role = overview, Type = Jellyfin.Data.Enums.PersonKind.Actor }],
            })!;

            var name = PathSanitizer.Unique(PathSanitizer.FolderName(movie.Title, movie.Year, movie.ProviderIds, key), key, used);

            Assert.True(name.Length > 0);
            Assert.DoesNotContain(name, static c => c is '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|' || char.IsControl(c));
            Assert.DoesNotContain("..", name, StringComparison.Ordinal);
            Assert.False(name.StartsWith('.') || name.StartsWith(' '), name);
            Assert.True(PathSanitizer.TryConfine(root, name, out var full), name);
            Assert.Equal(root, Path.GetDirectoryName(full));
            Assert.True(Encoding.UTF8.GetByteCount(name) <= 120 + 80, name);

            var nfo = NfoWriter.Write(movie, title);
            using var reader = XmlReader.Create(new MemoryStream(nfo), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, CheckCharacters = true });
            var document = XDocument.Load(reader);
            Assert.Equal("movie", document.Root!.Name.LocalName);
            var elements = document.Root.Descendants().Select(static e => e.Name.LocalName).ToHashSet(StringComparer.Ordinal);
            Assert.False(elements.Overlaps(["thumb", "fanart", "trailer", "fileinfo", "set", "lockdata", "website", "runtime", "img", "script"]), name);
            Assert.All(document.Root.Descendants().Where(static e => !e.HasElements), static e => Assert.DoesNotContain('<', e.Value));

            if (i % 25 == 0)
            {
                Directory.CreateDirectory(full);
                File.WriteAllBytes(Path.Combine(full, name + ".nfo"), nfo);
                created++;
            }
        }

        Assert.Equal(10_000, used.Count);
        Assert.Equal(created, Directory.GetDirectories(root).Length);
        Assert.Single(Directory.GetFileSystemEntries(temp.Combine("stubs")));
        Assert.Single(Directory.GetFileSystemEntries(temp.Path));
    }
}
