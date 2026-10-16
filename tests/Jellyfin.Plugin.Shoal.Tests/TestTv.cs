using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using Jellyfin.Plugin.Shoal.Sync;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.Shoal.Tests;

/// <summary>
/// Test stand-ins for K35's pure TV classes (the sanitizer, the layout and the NFO writer), so the sync engine of K36 is tested on its own. They follow DESIGN v2.7.1
/// and v2.7.4 closely enough for the engine: names from integers, overlaps by the specified rule, move matching by provider ids.
/// </summary>
internal sealed class TestTvSanitizer : ITvSanitizer
{
    private static string Hash(params object?[] parts) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\u001f', parts.Select(static p => Convert.ToString(p, CultureInfo.InvariantCulture))))))[..24];

    private static string Etag(BaseItemDto dto) => dto.Etag is { Length: > 0 and <= 64 } e && e.All(static c => char.IsAsciiLetterOrDigit(c) || c == '-') ? e : string.Empty;

    public SanitizedSeries? Series(BaseItemDto dto)
    {
        if (dto.Id == Guid.Empty)
        {
            return null;
        }

        var ids = MetadataSanitizer.CanonicalProviderIds(dto.ProviderIds);
        var title = Text(dto.Name, 300);
        var overview = Text(dto.Overview, 10_000);
        return new SanitizedSeries
        {
            PeerItemId = dto.Id,
            Title = title,
            Overview = overview,
            Year = dto.ProductionYear is >= 1870 and <= 2100 ? dto.ProductionYear : null,
            ProviderIds = ids,
            Etag = Etag(dto),
            ContentHash = Hash("series", title, overview, dto.ProductionYear, ids.Tvdb, ids.Tmdb, ids.Imdb),
        };
    }

    public SanitizedSeason? Season(BaseItemDto dto)
    {
        if (dto.Id == Guid.Empty || (dto.SeriesId ?? Guid.Empty) == Guid.Empty || dto.IndexNumber is not (>= 0 and <= 2100))
        {
            return null;
        }

        return new SanitizedSeason { PeerItemId = dto.Id, PeerSeriesId = dto.SeriesId!.Value, Number = dto.IndexNumber.Value, ContentHash = Hash("season", dto.IndexNumber) };
    }

    public TvEpisodeResult Episode(BaseItemDto dto)
    {
        if (dto.Id == Guid.Empty || (dto.SeriesId ?? Guid.Empty) == Guid.Empty)
        {
            return new TvEpisodeResult(null, TvSkip.BadId);
        }

        if (dto.LocationType == LocationType.Virtual)
        {
            return new TvEpisodeResult(null, TvSkip.Virtual);
        }

        if (dto.ParentIndexNumber is not (>= 0 and <= 2100) || dto.IndexNumber is not (>= 0 and <= 9999))
        {
            return new TvEpisodeResult(null, TvSkip.NoNumber);
        }

        var number = dto.IndexNumber.Value;
        int? end = dto.IndexNumberEnd is { } e && e >= number && e <= number + 99 && e != number ? e : null;
        var source = dto.MediaSources?.FirstOrDefault();
        SanitizedSource? primary = null;
        if (source is not null && MetadataSanitizer.TryParseId(source.Id, out var sourceId))
        {
            var video = source.MediaStreams?.FirstOrDefault(static s => s.Type == MediaStreamType.Video);
            primary = new SanitizedSource(sourceId, source.Size, source.Bitrate, source.Container, video?.Codec, video?.Width, video?.Height, source.RunTimeTicks);
        }

        var title = Text(dto.Name, 300);
        var overview = Text(dto.Overview, 10_000);
        return new TvEpisodeResult(
            new SanitizedEpisode
            {
                PeerItemId = dto.Id,
                PeerSeriesId = dto.SeriesId!.Value,
                Season = dto.ParentIndexNumber.Value,
                Number = number,
                NumberEnd = end,
                Title = title,
                Overview = overview,
                RunTimeTicks = dto.RunTimeTicks,
                Source = primary,
                Etag = Etag(dto),
                ContentHash = Hash("episode", title, overview, dto.ParentIndexNumber, number, end, dto.RunTimeTicks),
            },
            TvSkip.None);
    }

    private static string Text(string? value, int max)
    {
        var clean = new string((value ?? string.Empty).Where(static c => !char.IsControl(c) || c == '\n').ToArray());
        return clean.Length > max ? clean[..max] : clean;
    }
}

internal sealed class TestTvLayout : ITvLayout
{
    public string SeriesFolder(SanitizedSeries series, string stubKey, ISet<string> takenNormalized) =>
        PathSanitizer.Unique(PathSanitizer.FolderName(series.Title, series.Year, series.ProviderIds.Tvdb is null ? new CanonicalIds(series.ProviderIds.Tmdb, null, series.ProviderIds.Imdb) : new CanonicalIds(series.ProviderIds.Tvdb, null, null), stubKey), stubKey, new HashSet<string>(takenNormalized, StringComparer.Ordinal));

    public string SeasonFolder(int season) => "Season " + season.ToString("00", CultureInfo.InvariantCulture);

    public string EpisodeStem(int season, int episode, int? episodeEnd) =>
        "S" + season.ToString("00", CultureInfo.InvariantCulture) + "E" + episode.ToString("00", CultureInfo.InvariantCulture) + (episodeEnd is { } end ? "-E" + end.ToString("00", CultureInfo.InvariantCulture) : string.Empty);

    public TvEpisodeSet Resolve(IReadOnlyList<SanitizedEpisode> episodes)
    {
        var accepted = new List<SanitizedEpisode>();
        var duplicates = 0;
        foreach (var e in episodes.OrderBy(static e => e.Season).ThenBy(static e => e.Number).ThenBy(static e => e.NumberEnd ?? e.Number).ThenBy(static e => e.PeerItemId))
        {
            var last = e.NumberEnd ?? e.Number;
            if (accepted.Any(a => a.Season == e.Season && e.Number <= (a.NumberEnd ?? a.Number) && a.Number <= last))
            {
                duplicates++;
            }
            else
            {
                accepted.Add(e);
            }
        }

        return new TvEpisodeSet(accepted, duplicates);
    }

    public IReadOnlyList<TvMove> MatchSeriesMoves(IReadOnlyList<CatalogEntry> goneSeries, IReadOnlyList<SanitizedSeries> newSeries)
    {
        var moves = new List<TvMove>();
        foreach (var gone in goneSeries)
        {
            var keys = MetadataSanitizer.CanonicalProviderIds(gone.ProviderIds).DedupKeys();
            var match = newSeries.FirstOrDefault(n => n.ProviderIds.DedupKeys().Intersect(keys, StringComparer.Ordinal).Any() && moves.All(m => m.NewPeerItemId != n.PeerItemId));
            if (match is not null)
            {
                moves.Add(new TvMove(gone.StubKey, match.PeerItemId));
            }
        }

        return moves;
    }

    public IReadOnlyList<TvMove> MatchEpisodeMoves(IReadOnlyList<CatalogEntry> goneEpisodes, IReadOnlyList<SanitizedEpisode> newEpisodes)
    {
        var moves = new List<TvMove>();
        foreach (var gone in goneEpisodes)
        {
            var match = newEpisodes.FirstOrDefault(n => n.Season == gone.Season && n.Number == gone.Episode && n.NumberEnd == gone.EpisodeEnd && moves.All(m => m.NewPeerItemId != n.PeerItemId));
            if (match is not null)
            {
                moves.Add(new TvMove(gone.StubKey, match.PeerItemId));
            }
        }

        return moves;
    }
}

internal sealed class TestTvNfo : ITvNfoWriter
{
    public string Series(SanitizedSeries series, IReadOnlyList<string> tags) => Write("tvshow", w =>
    {
        w.WriteElementString("title", series.Title);
        w.WriteElementString("plot", series.Overview);
        w.WriteElementString("year", series.Year?.ToString(CultureInfo.InvariantCulture) ?? string.Empty);
        Tags(w, tags);
    });

    public string Season(SanitizedSeason season, IReadOnlyList<string> tags) => Write("season", w =>
    {
        w.WriteElementString("seasonnumber", season.Number.ToString(CultureInfo.InvariantCulture));
        Tags(w, tags);
    });

    public string Episode(SanitizedEpisode episode, IReadOnlyList<string> tags) => Write("episodedetails", w =>
    {
        w.WriteElementString("title", episode.Title);
        w.WriteElementString("season", episode.Season.ToString(CultureInfo.InvariantCulture));
        w.WriteElementString("episode", episode.Number.ToString(CultureInfo.InvariantCulture));
        Tags(w, tags);
    });

    private static void Tags(XmlWriter w, IReadOnlyList<string> tags)
    {
        foreach (var tag in tags)
        {
            w.WriteElementString("tag", tag);
        }
    }

    private static string Write(string root, Action<XmlWriter> body)
    {
        var builder = new StringBuilder();
        using (var w = XmlWriter.Create(builder, new XmlWriterSettings { CheckCharacters = true, OmitXmlDeclaration = false, Indent = true }))
        {
            w.WriteStartElement(root);
            body(w);
            w.WriteEndElement();
        }

        return builder.ToString();
    }
}

internal sealed class FakeLocalSeries : ILocalSeries
{
    public List<LocalSeries> Series { get; } = [];

    public IReadOnlyList<LocalSeries> List(string stubRoot) => Series.ToList();
}
