using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.Shoal.Sync;

/// <summary>
/// Turns the peer's hostile series, season and episode items into the sanitized records of DESIGN v2.7.4. Text follows the 5.5 rules, every number is parsed as an
/// integer and checked against its range, ids are GUIDs, provider ids are re-emitted from their integers, and an image tag is kept only if it is 1 to 64 letters
/// and digits. Each record carries a <c>ContentHash</c> over its sanitized fields, so a peer-side Etag storm that changes nothing rewrites nothing.
/// </summary>
public sealed class TvSanitizer : ITvSanitizer
{
    private const int TitleMax = 300;
    private const int OverviewMax = 10_000;
    private const int SeasonNameMax = 100;
    private const int SeasonOverviewMax = 2_000;
    private const int MaxGenres = 32;
    private const int MaxStudios = 16;
    private const int NameMax = 64;
    private const int StudioMax = 128;
    private const int MinYear = 1870;
    private const int MaxYear = 2100;
    private const int RatingMax = 32;

    private readonly IRatingTable _ratings;
    private readonly ITrickplaySanitizer _trickplay;

    /// <summary>
    /// Initializes a new instance of the <see cref="TvSanitizer"/> class.
    /// </summary>
    /// <param name="ratings">Jellyfin's rating table: an episode or series rating is kept only if it recognizes it.</param>
    /// <param name="trickplay">Reads the trickplay levels of an episode's source, or null for none.</param>
    public TvSanitizer(IRatingTable ratings, ITrickplaySanitizer? trickplay = null)
    {
        _ratings = ratings;
        _trickplay = trickplay ?? NoTrickplay.Instance;
    }

    /// <inheritdoc />
    public SanitizedSeries? Series(BaseItemDto dto)
    {
        ArgumentNullException.ThrowIfNull(dto);
        if (dto.Id == Guid.Empty)
        {
            return null;
        }

        var rating = PeerText.Clean(dto.OfficialRating, RatingMax);
        var series = new SanitizedSeries
        {
            PeerItemId = dto.Id,
            Title = PeerText.Clean(dto.Name, TitleMax),
            OriginalTitle = PeerText.Clean(dto.OriginalTitle, TitleMax),
            SortTitle = PeerText.Clean(dto.SortName, TitleMax),
            Overview = PeerText.Clean(dto.Overview, OverviewMax, keepNewlines: true),
            Genres = MetadataSanitizer.Distinct(dto.Genres, MaxGenres, NameMax),
            Studios = MetadataSanitizer.Distinct(dto.Studios?.Where(static s => s is not null).Select(static s => s.Name), MaxStudios, StudioMax),
            Actors = MetadataSanitizer.Actors(dto.People).Take(TvLimits.MaxActorsPerSeries).ToArray(),
            OfficialRating = rating.Length > 0 && _ratings.IsRecognized(rating) ? rating : null,
            CommunityRating = Rating(dto.CommunityRating),
            Year = dto.ProductionYear is { } year and >= MinYear and <= MaxYear ? year : null,
            Premiered = Date(dto.PremiereDate),
            EndDate = Date(dto.EndDate),
            Status = dto.Status is "Continuing" or "Ended" ? dto.Status : null,
            ProviderIds = MetadataSanitizer.CanonicalProviderIds(dto.ProviderIds),
            PosterTag = Tag(dto, ImageType.Primary),
            BackdropTag = dto.BackdropImageTags?.Select(MetadataSanitizer.ImageTag).FirstOrDefault(static t => t is not null),
            BannerTag = Tag(dto, ImageType.Banner),
            LandscapeTag = Tag(dto, ImageType.Thumb),
            Etag = Etag(dto.Etag),
        };
        return series with
        {
            ContentHash = Hash([
                series.Title, series.OriginalTitle, series.SortTitle, series.Overview, string.Join('\u0002', series.Genres), string.Join('\u0002', series.Studios),
                string.Join('\u0002', series.Actors.Select(static a => a.Name + "\u0003" + a.Role)), series.OfficialRating, Number(series.CommunityRating), Number(series.Year),
                Number(series.Premiered), Number(series.EndDate), series.Status, series.ProviderIds.ToString(), series.PosterTag, series.BackdropTag, series.BannerTag, series.LandscapeTag]),
        };
    }

    /// <inheritdoc />
    public SanitizedSeason? Season(BaseItemDto dto)
    {
        ArgumentNullException.ThrowIfNull(dto);
        if (dto.Id == Guid.Empty || dto.SeriesId is not { } seriesId || seriesId == Guid.Empty || dto.IndexNumber is not (>= 0 and <= TvLimits.MaxSeasonNumber))
        {
            return null;
        }

        var number = dto.IndexNumber.Value;
        var name = PeerText.Clean(dto.Name, SeasonNameMax);
        if (IsDefaultSeasonName(name, number))
        {
            name = string.Empty;
        }

        var season = new SanitizedSeason
        {
            PeerItemId = dto.Id,
            PeerSeriesId = seriesId,
            Number = number,
            Name = name,
            Overview = PeerText.Clean(dto.Overview, SeasonOverviewMax, keepNewlines: true),
            PosterTag = Tag(dto, ImageType.Primary),
        };
        return season with { ContentHash = Hash([Number(season.Number), season.Name, season.Overview, season.PosterTag]) };
    }

    /// <inheritdoc />
    public TvEpisodeResult Episode(BaseItemDto dto)
    {
        ArgumentNullException.ThrowIfNull(dto);
        if (dto.LocationType == LocationType.Virtual)
        {
            return new TvEpisodeResult(null, TvSkip.Virtual);
        }

        if (dto.Id == Guid.Empty || dto.SeriesId is not { } seriesId || seriesId == Guid.Empty)
        {
            return new TvEpisodeResult(null, TvSkip.BadId);
        }

        if (dto.ParentIndexNumber is not (>= 0 and <= TvLimits.MaxSeasonNumber) || dto.IndexNumber is not (>= 0 and <= TvLimits.MaxEpisodeNumber))
        {
            return new TvEpisodeResult(null, TvSkip.NoNumber);
        }

        var start = dto.IndexNumber.Value;

        // An end before the start, or more than 99 past it, is not believed: the file is one episode.
        int? end = dto.IndexNumberEnd is { } e && e > start && e <= start + TvLimits.MaxEpisodeSpan ? e : null;
        var source = MetadataSanitizer.Sources(dto, 1).FirstOrDefault();
        if (source is not null)
        {
            IReadOnlyList<SanitizedTrickplay> levels;
            try
            {
                levels = _trickplay.Read(dto, source.Id);
            }
            catch (Exception ex) when (ex is not (OperationCanceledException or OutOfMemoryException))
            {
                levels = []; // optional data never costs an episode
            }

            if (levels.Count > 0)
            {
                source = source with { Trickplay = levels };
            }
        }

        var episode = new SanitizedEpisode
        {
            PeerItemId = dto.Id,
            PeerSeriesId = seriesId,
            Season = dto.ParentIndexNumber.Value,
            Number = start,
            NumberEnd = end,
            Title = PeerText.Clean(dto.Name, TitleMax),
            Overview = PeerText.Clean(dto.Overview, OverviewMax, keepNewlines: true),
            Aired = Date(dto.PremiereDate),
            CommunityRating = Rating(dto.CommunityRating),
            RunTimeTicks = MetadataSanitizer.Ticks(dto.RunTimeTicks),
            Source = source,
            ThumbTag = Tag(dto, ImageType.Primary),
            Etag = Etag(dto.Etag),
        };
        var hashed = episode with
        {
            ContentHash = Hash([
                Number(episode.Season), Number(episode.Number), Number(episode.NumberEnd), episode.Title, episode.Overview, Number(episode.Aired), Number(episode.CommunityRating),
                Number(episode.RunTimeTicks), episode.ThumbTag, source?.Id.ToString("N"), Number(source?.Size), Number(source?.Bitrate), source?.Container, source?.VideoCodec,
                Number(source?.Width), Number(source?.Height), string.Join('\u0002', (source?.Trickplay ?? []).Select(static t => string.Join(',', t.Width, t.Height, t.TileWidth, t.TileHeight, t.ThumbnailCount, t.IntervalMs)))]),
        };
        return new TvEpisodeResult(hashed, TvSkip.None);
    }

    private static bool IsDefaultSeasonName(string name, int number) =>
        name.Length == 0 || string.Equals(name, "Season " + number.ToString(CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
        || string.Equals(name, "Season " + number.ToString("00", CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
        || (number == 0 && string.Equals(name, "Specials", StringComparison.OrdinalIgnoreCase));

    private static string? Tag(BaseItemDto dto, ImageType type) =>
        dto.ImageTags is { } tags && tags.TryGetValue(type, out var tag) ? MetadataSanitizer.ImageTag(tag) : null;

    private static string Etag(string? etag) =>
        etag is { Length: > 0 and <= 64 } && etag.All(static c => c is (>= '0' and <= '9') or (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or '-') ? etag : string.Empty;

    private static double? Rating(float? score) => score is { } s && float.IsFinite(s) && s is >= 0 and <= 10 ? Math.Round((double)s, 2) : null;

    private static DateOnly? Date(DateTime? value) => value is { } date && date.Year is >= MinYear and <= MaxYear ? DateOnly.FromDateTime(date) : null;

    private static string Number(IFormattable? value) => value?.ToString(null, CultureInfo.InvariantCulture) ?? string.Empty;

    private static string Hash(string?[] fields)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\u0001', fields.Select(static f => f ?? string.Empty))));
        return Convert.ToHexStringLower(bytes)[..32];
    }
}
