using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;

namespace Jellyfin.Plugin.Shoal.Tests;

/// <summary>One series of a fake peer's TV library (DESIGN v2.7, K34).</summary>
internal sealed class FakeTvSeries
{
    public Guid Id { get; set; }

    public string Name { get; set; } = "Show";

    public int? Year { get; set; } = 2010;

    public string Etag { get; set; } = "s1";

    public Dictionary<string, string> ProviderIds { get; set; } = [];

    public string Overview { get; set; } = "A show.";

    public string? Status { get; set; } = "Ended";

    /// <summary>Extra or replaced members of the details answer (hostile values go here).</summary>
    public Dictionary<string, object?> Extra { get; set; } = [];
}

/// <summary>One season of a fake peer's TV library.</summary>
internal sealed class FakeTvSeason
{
    public Guid Id { get; set; }

    public Guid SeriesId { get; set; }

    public int? Number { get; set; }

    public string Name { get; set; } = string.Empty;

    public Dictionary<string, object?> Extra { get; set; } = [];
}

/// <summary>One episode of a fake peer's TV library.</summary>
internal sealed class FakeTvEpisode
{
    public Guid Id { get; set; }

    public Guid SeriesId { get; set; }

    public Guid SeasonId { get; set; }

    public int? Season { get; set; }

    public int? Number { get; set; }

    public int? NumberEnd { get; set; }

    public string Name { get; set; } = "Episode";

    public string Etag { get; set; } = "e1";

    public string LocationType { get; set; } = "FileSystem";

    public Dictionary<string, object?> Extra { get; set; } = [];
}

/// <summary>
/// The TV library of a fake peer: series, seasons and episodes served the way stock Jellyfin serves <c>/Items?IncludeItemTypes=Series|Season|Episode</c>
/// (paged cheap lines, and details by <c>Ids</c>). <see cref="Generate"/> makes N series by M seasons by E episodes with deterministic ids, so a test can
/// ask for 5,000 episodes without a fixture file; <see cref="FakeTvFixtures"/> adds the hostile cases F-TV-01 to F-TV-12.
/// </summary>
internal sealed class FakeTvTree
{
    public Guid LibraryId { get; set; } = Guid.Parse("bbbbbbbbccccddddeeeeffffffffffff");

    public string LibraryName { get; set; } = "TV shows";

    public List<FakeTvSeries> Series { get; } = [];

    public List<FakeTvSeason> Seasons { get; } = [];

    public List<FakeTvEpisode> Episodes { get; } = [];

    /// <summary>Gets or sets what the total of a listing claims (for the partial-listing rule); null tells the truth.</summary>
    public Func<string, int>? TotalOverride { get; set; }

    public static Guid IdOf(char kind, int a, int b = 0, int c = 0)
    {
        var bytes = new byte[16];
        BitConverter.GetBytes(a).CopyTo(bytes, 0);
        BitConverter.GetBytes(b).CopyTo(bytes, 4);
        BitConverter.GetBytes(c).CopyTo(bytes, 8);
        bytes[15] = (byte)kind;
        return new Guid(bytes);
    }

    public static FakeTvTree Generate(int series, int seasonsPerSeries, int episodesPerSeason)
    {
        var tree = new FakeTvTree();
        for (var s = 0; s < series; s++)
        {
            var show = new FakeTvSeries
            {
                Id = IdOf('S', s),
                Name = "Show " + s.ToString(CultureInfo.InvariantCulture),
                ProviderIds = new Dictionary<string, string> { ["Tvdb"] = (100_000 + s).ToString(CultureInfo.InvariantCulture) },
            };
            tree.Series.Add(show);
            for (var n = 1; n <= seasonsPerSeries; n++)
            {
                var season = new FakeTvSeason { Id = IdOf('N', s, n), SeriesId = show.Id, Number = n };
                tree.Seasons.Add(season);
                for (var e = 1; e <= episodesPerSeason; e++)
                {
                    tree.Episodes.Add(new FakeTvEpisode
                    {
                        Id = IdOf('E', s, n, e),
                        SeriesId = show.Id,
                        SeasonId = season.Id,
                        Season = n,
                        Number = e,
                        Name = "Episode " + e.ToString(CultureInfo.InvariantCulture),
                    });
                }
            }
        }

        return tree;
    }

    /// <summary>The cheap line of an item, as <c>Fields=ProviderIds,Etag</c> returns it.</summary>
    public Dictionary<string, object?> Line(FakeTvSeries s) => new()
    {
        ["Id"] = s.Id.ToString("N"),
        ["Name"] = s.Name,
        ["Etag"] = s.Etag,
        ["ProviderIds"] = s.ProviderIds,
        ["ProductionYear"] = s.Year,
        ["Type"] = "Series",
    };

    public Dictionary<string, object?> Line(FakeTvSeason s) => new()
    {
        ["Id"] = s.Id.ToString("N"),
        ["Name"] = s.Name.Length > 0 ? s.Name : "Season " + s.Number,
        ["SeriesId"] = s.SeriesId.ToString("N"),
        ["IndexNumber"] = s.Number,
        ["ImageTags"] = new Dictionary<string, string> { ["Primary"] = "sp1" }, // what a real peer lists with EnableImages=true
        ["Type"] = "Season",
    };

    public Dictionary<string, object?> Line(FakeTvEpisode e)
    {
        var line = new Dictionary<string, object?>
        {
            ["Id"] = e.Id.ToString("N"),
            ["Name"] = e.Name,
            ["Etag"] = e.Etag,
            ["SeriesId"] = e.SeriesId.ToString("N"),
            ["SeasonId"] = e.SeasonId.ToString("N"),
            ["ParentIndexNumber"] = e.Season,
            ["IndexNumber"] = e.Number,
            ["IndexNumberEnd"] = e.NumberEnd,
            ["LocationType"] = e.LocationType,
            ["Type"] = "Episode",
        };
        foreach (var (key, value) in e.Extra)
        {
            line[key] = value;
        }

        return line;
    }

    /// <summary>The details of an item, as <c>Fields=...Overview,Genres,Studios,People,MediaSources...</c> returns them.</summary>
    public Dictionary<string, object?> Detail(Guid id)
    {
        if (Series.FirstOrDefault(s => s.Id == id) is { } series)
        {
            var item = Line(series);
            item["OriginalTitle"] = series.Name;
            item["SortName"] = series.Name;
            item["Overview"] = series.Overview;
            item["Genres"] = new[] { "Drama" };
            item["Studios"] = new[] { new Dictionary<string, object?> { ["Name"] = "Network One", ["Id"] = Guid.NewGuid().ToString("N") } };
            item["People"] = new[] { new Dictionary<string, object?> { ["Name"] = "Actor One", ["Role"] = "Role", ["Type"] = "Actor", ["Id"] = Guid.NewGuid().ToString("N") } };
            item["OfficialRating"] = "TV-14";
            item["CommunityRating"] = 8.1;
            item["Status"] = series.Status;
            item["PremiereDate"] = (series.Year ?? 2010).ToString("0000", CultureInfo.InvariantCulture) + "-01-01T00:00:00.0000000Z";
            item["ImageTags"] = new Dictionary<string, string> { ["Primary"] = "p1", ["Banner"] = "bn1", ["Thumb"] = "t1" };
            item["BackdropImageTags"] = new[] { "b1" };
            foreach (var (key, value) in series.Extra)
            {
                item[key] = value;
            }

            return item;
        }

        if (Seasons.FirstOrDefault(s => s.Id == id) is { } season)
        {
            var item = Line(season);
            item["Overview"] = "A season.";
            item["ImageTags"] = new Dictionary<string, string> { ["Primary"] = "sp1" };
            foreach (var (key, value) in season.Extra)
            {
                item[key] = value;
            }

            return item;
        }

        var episode = Episodes.First(e => e.Id == id);
        var detail = Line(episode);
        detail["Overview"] = "An episode.";
        detail["PremiereDate"] = "2011-03-30T00:00:00.0000000Z";
        detail["CommunityRating"] = 7.9;
        detail["RunTimeTicks"] = 27_000_000_000L;
        detail["ImageTags"] = new Dictionary<string, string> { ["Primary"] = "ep1" };
        detail["MediaSources"] = new[]
        {
            new Dictionary<string, object?>
            {
                ["Id"] = episode.Id.ToString("N"),
                ["Size"] = 600_000_000L,
                ["Bitrate"] = 4_000_000,
                ["Container"] = "mkv",
                ["RunTimeTicks"] = 27_000_000_000L,
                ["MediaStreams"] = new[] { new Dictionary<string, object?> { ["Type"] = "Video", ["Codec"] = "h264", ["Width"] = 1920, ["Height"] = 1080 } },
            },
        };
        return detail;
    }

    /// <summary>
    /// Serves a <c>/Items</c> request when it is about TV: <c>IncludeItemTypes</c> names Series, Season or Episode, or <c>Ids</c> names tree items.
    /// </summary>
    /// <param name="includeItemTypes">The <c>IncludeItemTypes</c> value, or empty.</param>
    /// <param name="ids">The <c>Ids</c> value (comma separated), or empty.</param>
    /// <param name="start">StartIndex.</param>
    /// <param name="limit">Limit.</param>
    /// <param name="body">The JSON body.</param>
    /// <returns>True when the request was about TV.</returns>
    public bool TryServe(string includeItemTypes, string ids, int start, int limit, out string body)
    {
        body = string.Empty;
        if (ids.Length > 0)
        {
            var wanted = ids.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(static s => Guid.ParseExact(s, "N")).ToHashSet();
            var known = Series.Select(static s => s.Id).Concat(Seasons.Select(static s => s.Id)).Concat(Episodes.Select(static e => e.Id)).ToHashSet();
            if (!wanted.Overlaps(known))
            {
                return false;
            }

            var found = wanted.Where(known.Contains).Select(Detail).ToArray();
            body = JsonSerializer.Serialize(new { Items = found, TotalRecordCount = found.Length, StartIndex = 0 });
            return true;
        }

        IReadOnlyList<Dictionary<string, object?>>? lines = null;
        string kind;
        if (includeItemTypes.Contains("Episode", StringComparison.Ordinal))
        {
            kind = "Episode";
            lines = [.. Episodes.Select(Line)];
        }
        else if (includeItemTypes.Contains("Season", StringComparison.Ordinal))
        {
            kind = "Season";
            lines = [.. Seasons.Select(Line)];
        }
        else if (includeItemTypes.Contains("Series", StringComparison.Ordinal))
        {
            kind = "Series";
            lines = [.. Series.Select(Line)];
        }
        else
        {
            return false;
        }

        var page = lines.Skip(start).Take(limit).ToArray();
        body = JsonSerializer.Serialize(new { Items = page, TotalRecordCount = TotalOverride?.Invoke(kind) ?? lines.Count, StartIndex = start });
        return true;
    }
}

/// <summary>
/// The hostile cases F-TV-01 to F-TV-12 of DESIGN v2.7.4, as trees a test can serve (K34 publishes them; K35 and K36 assert the specified outcome of each).
/// </summary>
internal static class FakeTvFixtures
{
    /// <summary>The names of the fixtures in order.</summary>
    public static readonly string[] Names =
    [
        "F-TV-01 episode titled S09E09", "F-TV-02 series named 24", "F-TV-03 series named ..", "F-TV-04 NUL and bidi controls", "F-TV-05 300-character title", "F-TV-06 tt injection in provider ids",
        "F-TV-07 episode numbers -1 and 99999", "F-TV-08 end less than start", "F-TV-09 250 seasons", "F-TV-10 3,000 episodes in one season", "F-TV-11 wrong SeriesId and a virtual episode",
        "F-TV-12 equal numbers, overlapping ranges, script in overview",
    ];

    /// <summary>Builds a fixture on top of a tree that has one plain series (<c>Show 0</c>, one season, two episodes).</summary>
    /// <param name="number">1 to 12.</param>
    /// <returns>The tree.</returns>
    public static FakeTvTree Build(int number)
    {
        var tree = FakeTvTree.Generate(1, 1, 2);
        var show = tree.Series[0];
        var season = tree.Seasons[0];
        FakeTvEpisode Add(int n, string name, int? seasonNumber = 1, int? end = null) =>
            Push(tree, new FakeTvEpisode { Id = FakeTvTree.IdOf('X', number, n), SeriesId = show.Id, SeasonId = season.Id, Season = seasonNumber, Number = n, NumberEnd = end, Name = name });

        switch (number)
        {
            case 1:
                Add(3, "S09E09");
                break;
            case 2:
                show.Name = "24";
                break;
            case 3:
                show.Name = "..";
                break;
            case 4:
                show.Name = "Evil\0Show‮fdp.exe";
                Add(3, "Ep\0‮⁦bidi");
                break;
            case 5:
                show.Name = new string('A', 300);
                Add(3, new string('B', 300));
                break;
            case 6:
                show.ProviderIds = new Dictionary<string, string> { ["Tvdb"] = "81189; rm -rf", ["Tmdb"] = "-5", ["Imdb"] = "tt1234567\"><script>" };
                break;
            case 7:
                Add(-1, "Minus one");
                Add(99999, "Huge");
                break;
            case 8:
                Add(5, "Backwards", end: 3);
                Add(7, "Too long", end: 7 + 500);
                break;
            case 9:
                for (var n = 2; n <= 250; n++)
                {
                    var extra = new FakeTvSeason { Id = FakeTvTree.IdOf('N', 0, n), SeriesId = show.Id, Number = n };
                    tree.Seasons.Add(extra);
                    tree.Episodes.Add(new FakeTvEpisode { Id = FakeTvTree.IdOf('E', 0, n, 1), SeriesId = show.Id, SeasonId = extra.Id, Season = n, Number = 1, Name = "Only" });
                }

                break;
            case 10:
                for (var n = 3; n <= 3_000; n++)
                {
                    tree.Episodes.Add(new FakeTvEpisode { Id = FakeTvTree.IdOf('X', 10, n), SeriesId = show.Id, SeasonId = season.Id, Season = 1, Number = n, Name = "Bulk" });
                }

                break;
            case 11:
                tree.Episodes.Add(new FakeTvEpisode { Id = FakeTvTree.IdOf('X', 11, 1), SeriesId = FakeTvTree.IdOf('S', 999), SeasonId = season.Id, Season = 1, Number = 3, Name = "Wrong series" });
                tree.Episodes.Add(new FakeTvEpisode { Id = FakeTvTree.IdOf('X', 11, 2), SeriesId = show.Id, SeasonId = season.Id, Season = 1, Number = 4, Name = "Missing", LocationType = "Virtual" });
                break;
            case 12:
                Add(3, "Twin A");
                tree.Episodes.Add(new FakeTvEpisode { Id = FakeTvTree.IdOf('X', 12, 90), SeriesId = show.Id, SeasonId = season.Id, Season = 1, Number = 3, Name = "Twin B" });
                Add(10, "Range", end: 12);
                Add(11, "Inside the range");
                show.Overview = "<script>alert(1)</script> & <b>bold</b>\nsecond paragraph";
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(number));
        }

        return tree;
    }

    private static FakeTvEpisode Push(FakeTvTree tree, FakeTvEpisode episode)
    {
        tree.Episodes.Add(episode);
        return episode;
    }
}
