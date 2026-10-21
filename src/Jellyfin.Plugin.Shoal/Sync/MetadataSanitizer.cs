using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Jellyfin.Data.Enums;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Globalization;

namespace Jellyfin.Plugin.Shoal.Sync;

/// <summary>
/// Tells whether Jellyfin's rating table knows a content rating (production: <c>ILocalizationManager.GetRatingScore</c>).
/// </summary>
public interface IRatingTable
{
    /// <summary>
    /// Whether the table recognizes the rating.
    /// </summary>
    /// <param name="rating">An already sanitized rating, for example <c>PG-13</c>.</param>
    /// <returns>True when it is a known rating.</returns>
    bool IsRecognized(string rating);
}

/// <summary>
/// The production rating table, backed by core's localization manager.
/// </summary>
public sealed class JellyfinRatingTable : IRatingTable
{
    private readonly ILocalizationManager _localization;

    /// <summary>
    /// Initializes a new instance of the <see cref="JellyfinRatingTable"/> class.
    /// </summary>
    /// <param name="localization">Core's localization manager.</param>
    public JellyfinRatingTable(ILocalizationManager localization) => _localization = localization;

    /// <inheritdoc />
    public bool IsRecognized(string rating) => _localization.GetRatingScore(rating, null) is not null;
}

/// <summary>
/// Turns peer JSON into values Home may store (DESIGN 5.5). Every peer string is hostile input; nothing else touches the raw DTO. A field
/// that fails its rule is dropped (null or empty), never repaired by guessing; only a missing or malformed item id rejects the item.
/// </summary>
public sealed partial class MetadataSanitizer
{
    /// <summary>The longest title, original title or sort title.</summary>
    public const int TitleMax = 300;

    /// <summary>The longest overview.</summary>
    public const int OverviewMax = 10_000;

    /// <summary>The longest tagline.</summary>
    public const int TaglineMax = 500;

    /// <summary>The longest rating text.</summary>
    public const int RatingMax = 32;

    /// <summary>The longest display name (server, library) and the longest genre.</summary>
    public const int NameMax = 64;

    /// <summary>The most actors kept (W§A-10).</summary>
    public const int MaxActors = 5;

    private const int MaxGenres = 32;
    private const int MaxStudios = 16;
    private const int StudioMax = 128;
    private const int ActorFieldMax = 128;
    private const int MinYear = 1870;
    private const int MaxYear = 2100;
    private const long MaxRuntimeTicks = 48L * 3600 * TimeSpan.TicksPerSecond;
    private const long MaxSize = 1L << 50;
    private const int MaxBitrate = 1_000_000_000;
    private const int MaxDimension = 16_384;
    private const int ContainerMax = 40;
    private static readonly char[] LinkPunctuation = ['(', ')', '[', ']', '{', '}', '<', '>', '"', (char)0x27, ',', ';', ':', '!', '?', '.', '*', '_', '~'];

    private readonly IRatingTable _ratings;
    private readonly ISourceSanitizer _sourceHook;
    private readonly ITrickplaySanitizer _trickplayHook;

    /// <summary>
    /// Initializes a new instance of the <see cref="MetadataSanitizer"/> class.
    /// </summary>
    /// <param name="ratings">Jellyfin's rating table.</param>
    /// <param name="sources">Chooses which validated sources are kept (default: all).</param>
    /// <param name="trickplay">Reads the trickplay levels of a source (default: none).</param>
    public MetadataSanitizer(IRatingTable ratings, ISourceSanitizer? sources = null, ITrickplaySanitizer? trickplay = null)
    {
        _ratings = ratings;
        _sourceHook = sources ?? KeepSources.Instance;
        _trickplayHook = trickplay ?? NoTrickplay.Instance;
    }

    /// <summary>
    /// Sanitizes a display name (server name, library name): the text rules, up to 64 characters, to be rendered as text only.
    /// </summary>
    /// <param name="value">The raw name.</param>
    /// <returns>The cleaned name, possibly empty.</returns>
    public static string DisplayName(string? value) => PeerText.Clean(value, NameMax);

    /// <summary>
    /// Sanitizes a friend's server name for text users read (plot label, studio, toasts, offline card, the admin's link label) (2.9.3 K6, Warden 17/18 L2):
    /// the <see cref="DisplayName"/> rules, then every token that looks like a link is dropped: one holding <c>://</c>, one starting with <c>www.</c>, or a
    /// domain (<c>label(.label)*.tld</c>, an alphabetic ASCII TLD of 2 to 24 letters, an optional port or path). Spaces left behind are collapsed. "Dr. Who"
    /// and "St. Louis" survive (a TLD needs a dot with no space before it); "Dr.Who" does not (a known false positive).
    /// </summary>
    /// <param name="value">The raw name.</param>
    /// <returns>The cleaned name; empty when nothing is left (the caller uses its "no name" form).</returns>
    public static string FriendName(string? value)
    {
        var clean = DisplayName(value);
        if (clean.Length == 0)
        {
            return clean;
        }

        return string.Join(' ', clean.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Where(static t => !IsLinkToken(t)));
    }

    /// <summary>
    /// Parses a peer id: 32 hex digits, or a GUID with hyphens, and not the empty GUID.
    /// </summary>
    /// <param name="value">The raw id.</param>
    /// <param name="id">The parsed id.</param>
    /// <returns>True when the id is well formed.</returns>
    public static bool TryParseId(string? value, out Guid id)
    {
        id = Guid.Empty;
        if (value is null)
        {
            return false;
        }

        var format = value.Length switch
        {
            32 => 'N',
            36 => 'D',
            _ => '\0',
        };
        if (format == '\0')
        {
            return false;
        }

        foreach (var c in value)
        {
            if (!(IsAsciiHex(c) || (format == 'D' && c == '-')))
            {
                return false;
            }
        }

        return Guid.TryParseExact(value, format.ToString(), out id) && id != Guid.Empty;
    }

    /// <summary>
    /// Re-emits a TMDb or TVDb id from its integer value.
    /// </summary>
    /// <param name="value">The raw id: 1 to 10 ASCII digits.</param>
    /// <returns>The integer, or null when the text is anything else (including zero).</returns>
    public static long? CanonicalNumericId(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 10 || !value.All(static c => c is >= '0' and <= '9'))
        {
            return null;
        }

        return long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number > 0 ? number : null;
    }

    /// <summary>
    /// Re-emits an IMDb id: <c>tt</c> and 5 to 10 ASCII digits.
    /// </summary>
    /// <param name="value">The raw id.</param>
    /// <returns><c>"tt" + digits</c>, or null.</returns>
    public static string? CanonicalImdbId(string? value)
    {
        if (value is null || value.Length is < 7 or > 12 || value[0] != 't' || value[1] != 't')
        {
            return null;
        }

        for (var i = 2; i < value.Length; i++)
        {
            if (value[i] is < '0' or > '9')
            {
                return null;
            }
        }

        return string.Concat("tt", value.AsSpan(2));
    }

    /// <summary>
    /// Reads the provider ids of an item; any provider other than TMDb, TVDb and IMDb is dropped.
    /// </summary>
    /// <param name="providerIds">The peer's provider id map, or null.</param>
    /// <returns>The canonical ids.</returns>
    public static CanonicalIds CanonicalProviderIds(IReadOnlyDictionary<string, string>? providerIds)
    {
        if (providerIds is null || providerIds.Count == 0)
        {
            return default;
        }

        long? tmdb = null;
        long? tvdb = null;
        string? imdb = null;
        foreach (var pair in providerIds.OrderBy(static p => p.Key, StringComparer.Ordinal))
        {
            if (string.Equals(pair.Key, "Tmdb", StringComparison.OrdinalIgnoreCase))
            {
                tmdb ??= CanonicalNumericId(pair.Value);
            }
            else if (string.Equals(pair.Key, "Tvdb", StringComparison.OrdinalIgnoreCase))
            {
                tvdb ??= CanonicalNumericId(pair.Value);
            }
            else if (string.Equals(pair.Key, "Imdb", StringComparison.OrdinalIgnoreCase))
            {
                imdb ??= CanonicalImdbId(pair.Value);
            }
        }

        return new CanonicalIds(tmdb, tvdb, imdb);
    }

    /// <summary>
    /// Validates an image tag.
    /// </summary>
    /// <param name="tag">The raw tag.</param>
    /// <returns>The tag when it is 1 to 64 ASCII letters and digits, else null (no image).</returns>
    public static string? ImageTag(string? tag)
    {
        if (string.IsNullOrEmpty(tag) || tag.Length > 64 || !tag.All(static c => c is (>= '0' and <= '9') or (>= 'a' and <= 'z') or (>= 'A' and <= 'Z')))
        {
            return null;
        }

        return tag;
    }

    /// <summary>
    /// Validates a container list: lower case, 1 to 8 tokens of 1 to 8 letters and digits, at most 40 characters in all.
    /// </summary>
    /// <param name="raw">The container text.</param>
    /// <returns>The lower-case list, or null.</returns>
    public static string? ValidContainer(string? raw)
    {
        if (raw is null || raw.Length is 0 or > ContainerMax)
        {
            return null;
        }

        var lower = raw.ToLowerInvariant();
        var parts = lower.Split(',');
        if (parts.Length > 8)
        {
            return null;
        }

        foreach (var part in parts)
        {
            if (part.Length is 0 or > 8 || !part.All(static c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9')))
            {
                return null;
            }
        }

        return lower;
    }

    /// <summary>
    /// Sanitizes one peer movie.
    /// </summary>
    /// <param name="dto">The parsed peer item.</param>
    /// <returns>The sanitized movie, or null when the item id is the empty GUID (the item is skipped and counted).</returns>
    public SanitizedMovie? Sanitize(BaseItemDto dto)
    {
        ArgumentNullException.ThrowIfNull(dto);
        if (dto.Id == Guid.Empty)
        {
            return null;
        }

        var rating = PeerText.Clean(dto.OfficialRating, RatingMax);
        var premiered = dto.PremiereDate is { } date && date.Year is >= MinYear and <= MaxYear ? DateOnly.FromDateTime(date) : (DateOnly?)null;
        var examined = WithTrickplay(dto, Sources(dto, Math.Max(1, _sourceHook.MaxExamined)));
        return new SanitizedMovie
        {
            PeerItemId = dto.Id,
            Title = PeerText.Clean(dto.Name, TitleMax),
            OriginalTitle = PeerText.Clean(dto.OriginalTitle, TitleMax),
            SortTitle = PeerText.Clean(dto.SortName, TitleMax),
            Overview = PeerText.Clean(dto.Overview, OverviewMax, keepNewlines: true),
            Tagline = PeerText.Clean(dto.Taglines?.FirstOrDefault(static t => !string.IsNullOrWhiteSpace(t)), TaglineMax),
            Genres = Distinct(dto.Genres, MaxGenres, NameMax),
            Studios = Distinct(dto.Studios?.Where(static s => s is not null).Select(static s => s.Name), MaxStudios, StudioMax),
            Actors = Actors(dto.People),
            OfficialRating = rating.Length > 0 && _ratings.IsRecognized(rating) ? rating : null,
            CommunityRating = dto.CommunityRating is { } score && float.IsFinite(score) && score is >= 0 and <= 10 ? Math.Round((double)score, 2) : null,
            Year = dto.ProductionYear is { } year and >= MinYear and <= MaxYear ? year : null,
            Premiered = premiered,
            RunTimeTicks = Ticks(dto.RunTimeTicks),
            ProviderIds = CanonicalProviderIds(dto.ProviderIds),
            Sources = RefineSources(dto, examined),
            Examined = examined,
            PrimaryImageTag = dto.ImageTags is { } tags && tags.TryGetValue(ImageType.Primary, out var primary) ? ImageTag(primary) : null,
            BackdropImageTag = dto.BackdropImageTags?.Select(ImageTag).FirstOrDefault(static t => t is not null),
        };
    }

    // The trickplay levels of every examined source, so the sources the selector keeps carry their own (DESIGN v2.6.5, v2.6.4).
    private SanitizedSource[] WithTrickplay(BaseItemDto dto, SanitizedSource[] sources)
    {
        var result = new SanitizedSource[sources.Length];
        for (var i = 0; i < result.Length; i++)
        {
            IReadOnlyList<SanitizedTrickplay> levels;
            try
            {
                levels = _trickplayHook.Read(dto, sources[i].Id);
            }
            catch (Exception ex) when (ex is not (OperationCanceledException or OutOfMemoryException))
            {
                levels = []; // trickplay is optional: a manifest that cannot be read costs the thumbnails, never the title
            }

            result[i] = levels.Count > 0 ? sources[i] with { Trickplay = levels } : sources[i];
        }

        return result;
    }

    private SanitizedSource[] RefineSources(BaseItemDto dto, SanitizedSource[] examined)
    {
        return [.. _sourceHook.Refine(dto, examined)];
    }

    private static bool IsAsciiHex(char c) => c is (>= '0' and <= '9') or (>= 'a' and <= 'f') or (>= 'A' and <= 'F');

#pragma warning disable SA1202, SA1611, SA1615 // Shared with TvSanitizer (DESIGN v2.7.4): the same rules for the same fields.
    /// <summary>Keeps a duration only when it is 0 to 48 hours.</summary>
    internal static long? Ticks(long? ticks) => ticks is >= 0 and <= MaxRuntimeTicks ? ticks : null;

    /// <summary>Cleans a list of names: distinct, non empty, at most a count and a length.</summary>
    internal static string[] Distinct(IEnumerable<string?>? values, int maxCount, int maxLength)
    {
        if (values is null)
        {
            return [];
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var raw in values)
        {
            var clean = PeerText.Clean(raw, maxLength);
            if (clean.Length > 0 && seen.Add(clean))
            {
                result.Add(clean);
                if (result.Count == maxCount)
                {
                    break;
                }
            }
        }

        return [.. result];
    }

    /// <summary>Keeps up to five actors with cleaned names and roles.</summary>
    internal static SanitizedActor[] Actors(IReadOnlyList<BaseItemPerson>? people)
    {
        if (people is null)
        {
            return [];
        }

        var result = new List<SanitizedActor>();
        foreach (var person in people)
        {
            if (person is null || person.Type != PersonKind.Actor)
            {
                continue;
            }

            var name = PeerText.Clean(person.Name, ActorFieldMax);
            if (name.Length == 0)
            {
                continue;
            }

            result.Add(new SanitizedActor(name, PeerText.Clean(person.Role, ActorFieldMax)));
            if (result.Count == MaxActors)
            {
                break;
            }
        }

        return [.. result];
    }

    private static string? VideoCodec(string? raw)
    {
        if (raw is null || raw.Length is 0 or > 32)
        {
            return null;
        }

        var lower = raw.ToLowerInvariant();
        return lower.All(static c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_' or '.' or '-') ? lower : null;
    }

    private static int? Dimension(int? value) => value is >= 0 and <= MaxDimension ? value : null;

    /// <summary>Validates the media sources of an item (at most <paramref name="max"/>).</summary>
    internal static SanitizedSource[] Sources(BaseItemDto dto, int max)
    {
        if (dto.MediaSources is null)
        {
            return [];
        }

        var result = new List<SanitizedSource>();
        foreach (var source in dto.MediaSources)
        {
            if (source is null || !TryParseId(source.Id, out var id))
            {
                continue;
            }

            var video = source.MediaStreams?.FirstOrDefault(static s => s is not null && s.Type == MediaStreamType.Video);
            result.Add(new SanitizedSource(
                id,
                source.Size is >= 0 and <= MaxSize ? source.Size : null,
                source.Bitrate is >= 0 and <= MaxBitrate ? source.Bitrate : null,
                ValidContainer(source.Container),
                VideoCodec(video?.Codec),
                Dimension(video?.Width),
                Dimension(video?.Height),
                Ticks(source.RunTimeTicks ?? dto.RunTimeTicks)));
            if (result.Count == max)
            {
                break;
            }
        }

        return [.. result];
    }

    private static bool IsLinkToken(string token)
    {
        if (token.Contains("://", StringComparison.Ordinal))
        {
            return true;
        }

        // Full-width and ideographic dots read as a dot; punctuation around a domain ("(evil.example)," or a final full stop) does not hide it.
        var core = token.Replace((char)0xFF0E, '.').Replace((char)0x3002, '.').Replace((char)0xFF61, '.').Trim(LinkPunctuation);
        return core.StartsWith("www.", StringComparison.OrdinalIgnoreCase) || DomainRegex().IsMatch(core);
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"^[\p{L}\p{N}-]+(?:\.[\p{L}\p{N}-]+)*\.[A-Za-z]{2,24}(?:[:/].*)?$", System.Text.RegularExpressions.RegexOptions.CultureInvariant, 1000)]
    private static partial System.Text.RegularExpressions.Regex DomainRegex();
}
