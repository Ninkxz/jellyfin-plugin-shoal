using System;
using System.Collections.Generic;
using System.Globalization;

namespace Jellyfin.Plugin.Shoal.Sync;

/// <summary>
/// The provider ids of an item in their canonical re-emitted form (DESIGN 5.5). Folder names, <c>&lt;uniqueid&gt;</c>, dedup keys and
/// anything that can reach a URL use these values and never the string a peer sent.
/// </summary>
/// <param name="Tmdb">The TMDb id, from an integer.</param>
/// <param name="Tvdb">The TVDb id, from an integer.</param>
/// <param name="Imdb">The IMDb id, <c>tt</c> plus 5 to 10 digits.</param>
public readonly record struct CanonicalIds(long? Tmdb, long? Tvdb, string? Imdb)
{
    /// <summary>Gets a value indicating whether at least one id is present.</summary>
    public bool HasAny => Tmdb is not null || Tvdb is not null || Imdb is not null;

    /// <summary>
    /// Gets the dedup keys of this item, one per provider id, in a fixed order (<c>tmdb:603</c>, <c>imdb:tt0133093</c>, <c>tvdb:290</c>).
    /// </summary>
    /// <returns>The keys; empty when the item has no ids.</returns>
    public IReadOnlyList<string> DedupKeys()
    {
        var keys = new List<string>(3);
        if (Tmdb is { } tmdb)
        {
            keys.Add("tmdb:" + tmdb.ToString(CultureInfo.InvariantCulture));
        }

        if (Imdb is { } imdb)
        {
            keys.Add("imdb:" + imdb);
        }

        if (Tvdb is { } tvdb)
        {
            keys.Add("tvdb:" + tvdb.ToString(CultureInfo.InvariantCulture));
        }

        return keys;
    }
}

/// <summary>
/// One media source of a peer item after validation.
/// </summary>
/// <param name="Id">The source id.</param>
/// <param name="Size">Bytes, 0..2^50, or null.</param>
/// <param name="Bitrate">Bits per second, 0..1 Gbps, or null.</param>
/// <param name="Container">The container list, lowercase, or null when it did not match the allowed shape.</param>
/// <param name="VideoCodec">The first video stream's codec, or null.</param>
/// <param name="Width">The first video stream's width, 0..16384, or null.</param>
/// <param name="Height">The first video stream's height, 0..16384, or null.</param>
/// <param name="RunTimeTicks">The duration, 0..48 h, or null.</param>
/// <param name="Trickplay">The trickplay levels the peer has for the source (DESIGN v2.6.5), or null when none were read.</param>
public sealed record SanitizedSource(Guid Id, long? Size, int? Bitrate, string? Container, string? VideoCodec, int? Width, int? Height, long? RunTimeTicks, IReadOnlyList<SanitizedTrickplay>? Trickplay = null);

/// <summary>
/// One trickplay width of a peer source after validation (DESIGN v2.6.5).
/// </summary>
/// <param name="Width">The thumbnail width, 1..1024.</param>
/// <param name="Height">The thumbnail height, 1..1024.</param>
/// <param name="TileWidth">The thumbnails per tile row, 1..32.</param>
/// <param name="TileHeight">The thumbnails per tile column, 1..32.</param>
/// <param name="ThumbnailCount">The number of thumbnails, 1..100,000.</param>
/// <param name="IntervalMs">The milliseconds between thumbnails, 1..3,600,000.</param>
/// <param name="Bandwidth">The bandwidth, 0..100,000,000.</param>
public sealed record SanitizedTrickplay(int Width, int Height, int TileWidth, int TileHeight, int ThumbnailCount, int IntervalMs, int Bandwidth);

/// <summary>
/// One billed actor (at most five per item).
/// </summary>
/// <param name="Name">The name, up to 128 characters.</param>
/// <param name="Role">The role, up to 128 characters, or empty.</param>
public sealed record SanitizedActor(string Name, string Role);

/// <summary>
/// A peer movie after <see cref="MetadataSanitizer"/>: the only form of peer metadata the rest of Shoal may store or write.
/// Only the sanitizer creates one.
/// </summary>
public sealed record SanitizedMovie
{
    /// <summary>Gets the peer's item id.</summary>
    public Guid PeerItemId { get; internal init; }

    /// <summary>Gets the title, up to 300 characters; empty when the peer sent nothing usable.</summary>
    public string Title { get; internal init; } = string.Empty;

    /// <summary>Gets the original title, or empty.</summary>
    public string OriginalTitle { get; internal init; } = string.Empty;

    /// <summary>Gets the sort title, or empty.</summary>
    public string SortTitle { get; internal init; } = string.Empty;

    /// <summary>Gets the overview (paragraph breaks kept), up to 10,000 characters, or empty.</summary>
    public string Overview { get; internal init; } = string.Empty;

    /// <summary>Gets the tagline, up to 500 characters, or empty.</summary>
    public string Tagline { get; internal init; } = string.Empty;

    /// <summary>Gets the genres (at most 32).</summary>
    public IReadOnlyList<string> Genres { get; internal init; } = [];

    /// <summary>Gets the studios (at most 16).</summary>
    public IReadOnlyList<string> Studios { get; internal init; } = [];

    /// <summary>Gets the actors (at most 5).</summary>
    public IReadOnlyList<SanitizedActor> Actors { get; internal init; } = [];

    /// <summary>Gets the content rating, only when Jellyfin's rating table recognizes it; otherwise null (unrated).</summary>
    public string? OfficialRating { get; internal init; }

    /// <summary>Gets the community rating, 0..10, or null.</summary>
    public double? CommunityRating { get; internal init; }

    /// <summary>Gets the production year, 1870..2100, or null.</summary>
    public int? Year { get; internal init; }

    /// <summary>Gets the premiere date, a year between 1870 and 2100, or null.</summary>
    public DateOnly? Premiered { get; internal init; }

    /// <summary>Gets the duration of the item, 0..48 h, or null.</summary>
    public long? RunTimeTicks { get; internal init; }

    /// <summary>Gets the canonical provider ids.</summary>
    public CanonicalIds ProviderIds { get; internal init; }

    /// <summary>Gets the media sources the source hook kept (all validated ones, at most 8, in v1).</summary>
    public IReadOnlyList<SanitizedSource> Sources { get; internal init; } = [];

    /// <summary>Gets every source that passed validation before the source hook chose (at most 8, or 64 with alternate versions); the sync re-selects from it against a title's current primary.</summary>
    public IReadOnlyList<SanitizedSource> Examined { get; internal init; } = [];

    /// <summary>Gets the primary image tag, or null when there is no valid one.</summary>
    public string? PrimaryImageTag { get; internal init; }

    /// <summary>Gets the first backdrop image tag, or null when there is no valid one.</summary>
    public string? BackdropImageTag { get; internal init; }

    /// <summary>
    /// Gets a value indicating whether the rating came from the peer and so cannot be trusted (F-MD-06): a peer can label an adult title
    /// <c>G</c>. This is why restricted profiles are excluded from "Everyone" by default (DESIGN 5.6). True whenever a rating is present.
    /// </summary>
    public bool RatingUntrusted => OfficialRating is not null;
}
