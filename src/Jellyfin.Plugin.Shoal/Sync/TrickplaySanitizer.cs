using System;
using System.Collections.Generic;
using System.Linq;
using MediaBrowser.Model.Dto;

namespace Jellyfin.Plugin.Shoal.Sync;

/// <summary>
/// Reads the trickplay levels of one peer media source from the item's <c>Trickplay</c> manifest (DESIGN v2.6.5, K30). The manifest is hostile input:
/// every number is range-checked by <see cref="TrickplayLevel.IsValid"/>, a level that fails is dropped, at most <see cref="TrickplayLevel.MaxPerSource"/>
/// levels are kept (the smallest widths first) and only a bounded part of the manifest is looked at.
/// </summary>
public sealed class TrickplaySanitizer : ITrickplaySanitizer
{
    private const int MaxSourcesLooked = 16;
    private const int MaxWidthsLooked = 32;

    /// <inheritdoc />
    public IReadOnlyList<SanitizedTrickplay> Read(BaseItemDto dto, Guid peerSourceId)
    {
        ArgumentNullException.ThrowIfNull(dto);
        if (dto.Trickplay is not { Count: > 0 } manifest || peerSourceId == Guid.Empty)
        {
            return [];
        }

        var found = new List<SanitizedTrickplay>();
        foreach (var (key, widths) in manifest.Take(MaxSourcesLooked))
        {
            if (widths is null || !Guid.TryParse(key, out var sourceId) || sourceId != peerSourceId)
            {
                continue;
            }

            foreach (var (width, info) in widths.Take(MaxWidthsLooked))
            {
                if (info is not null && info.Width == width && Validate(info.Width, info.Height, info.TileWidth, info.TileHeight, info.ThumbnailCount, info.Interval, info.Bandwidth) is { } level)
                {
                    found.Add(level);
                }
            }
        }

        return [.. found.DistinctBy(static l => l.Width).OrderBy(static l => l.Width).Take(TrickplayLevel.MaxPerSource)];
    }

    /// <summary>
    /// Checks one level against the ranges of DESIGN v2.6.5.
    /// </summary>
    /// <param name="width">The thumbnail width.</param>
    /// <param name="height">The thumbnail height.</param>
    /// <param name="tileWidth">The thumbnails per row.</param>
    /// <param name="tileHeight">The thumbnails per column.</param>
    /// <param name="thumbnailCount">The number of thumbnails.</param>
    /// <param name="intervalMs">The milliseconds between thumbnails.</param>
    /// <param name="bandwidth">The bandwidth.</param>
    /// <returns>The level, or null when any number is out of range or a tile image would be too large.</returns>
    public static SanitizedTrickplay? Validate(int width, int height, int tileWidth, int tileHeight, int thumbnailCount, int intervalMs, int bandwidth)
    {
        var candidate = new TrickplayLevel
        {
            Width = width,
            Height = height,
            TileWidth = tileWidth,
            TileHeight = tileHeight,
            ThumbnailCount = thumbnailCount,
            IntervalMs = intervalMs,
            Bandwidth = bandwidth,
        };
        return candidate.IsValid() ? new SanitizedTrickplay(width, height, tileWidth, tileHeight, thumbnailCount, intervalMs, bandwidth) : null;
    }
}
