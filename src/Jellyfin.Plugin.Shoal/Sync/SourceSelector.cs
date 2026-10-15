using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using MediaBrowser.Model.Dto;

namespace Jellyfin.Plugin.Shoal.Sync;

/// <summary>
/// Chooses which media sources of a peer item get a stub, and names them (DESIGN v2.6.4). Pure functions of validated numbers: no peer text
/// reaches a label or a file name.
/// </summary>
public static class SourceSelector
{
    /// <summary>The most sources one title keeps, the primary included.</summary>
    public const int MaxPerTitle = CatalogSource.MaxPerTitle;

    /// <summary>The most sources that are looked at.</summary>
    public const int MaxExamined = 64;

    private const int MaxLabelChars = 40;

    /// <summary>
    /// Selects up to <see cref="MaxPerTitle"/> sources: the primary first, then the best of the rest by height, bitrate and size. Two sources with the
    /// same width, height, codec and size collapse into one (the primary wins a collapse).
    /// </summary>
    /// <param name="examined">The validated sources, in the peer's order.</param>
    /// <param name="primary">The peer source id the title's primary stub is bound to, or null to choose the best.</param>
    /// <returns>The selected sources, the primary at index 0; empty when there are none.</returns>
    public static IReadOnlyList<SanitizedSource> Select(IReadOnlyList<SanitizedSource> examined, Guid? primary)
    {
        ArgumentNullException.ThrowIfNull(examined);
        var distinct = new List<SanitizedSource>();
        foreach (var source in examined.Take(MaxExamined))
        {
            var twin = distinct.FindIndex(d => SameFile(d, source));
            if (twin < 0)
            {
                distinct.Add(source);
            }
            else if (source.Id == primary)
            {
                distinct[twin] = source; // the title's own primary survives a collapse
            }
        }

        if (distinct.Count == 0)
        {
            return [];
        }

        var chosen = distinct.FirstOrDefault(s => s.Id == primary) ?? Rank(distinct).First();
        var rest = Rank(distinct.Where(s => s.Id != chosen.Id)).Take(MaxPerTitle - 1);
        return [chosen, .. rest];
    }

    /// <summary>
    /// The label of an extra version: <c>2160p</c>, <c>1080p</c>, <c>720p</c>, <c>480p</c> or <c>SD</c> from the height; on a clash the codec is added
    /// (<c>1080p hevc</c>), then a number (<c>1080p 2</c>, <c>1080p 3</c>). Letters, digits and spaces only.
    /// </summary>
    /// <param name="source">The source.</param>
    /// <param name="taken">The labels the title already has (case is ignored).</param>
    /// <returns>A label that is not in <paramref name="taken"/>.</returns>
    public static string Label(SanitizedSource source, ISet<string> taken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(taken);
        var baseLabel = (source.Height ?? 0) switch
        {
            >= 2000 => "2160p",
            >= 1000 => "1080p",
            >= 700 => "720p",
            >= 450 => "480p",
            _ => "SD",
        };
        if (!IsTaken(taken, baseLabel))
        {
            return baseLabel;
        }

        var codec = new string([.. (source.VideoCodec ?? string.Empty).Where(static c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9')).Take(12)]);
        var withCodec = codec.Length > 0 ? baseLabel + " " + codec : baseLabel;
        if (!IsTaken(taken, withCodec))
        {
            return withCodec;
        }

        for (var n = 2; ; n++)
        {
            var numbered = withCodec + " " + n.ToString(CultureInfo.InvariantCulture);
            if (numbered.Length > MaxLabelChars)
            {
                numbered = baseLabel + " " + n.ToString(CultureInfo.InvariantCulture);
            }

            if (!IsTaken(taken, numbered))
            {
                return numbered;
            }
        }
    }

    private static bool IsTaken(ISet<string> taken, string label) => taken.Any(t => string.Equals(t, label, StringComparison.OrdinalIgnoreCase));

    private static bool SameFile(SanitizedSource a, SanitizedSource b) =>
        a.Width == b.Width && a.Height == b.Height && string.Equals(a.VideoCodec, b.VideoCodec, StringComparison.Ordinal) && a.Size == b.Size;

    private static IOrderedEnumerable<SanitizedSource> Rank(IEnumerable<SanitizedSource> sources) =>
        sources.OrderByDescending(static s => s.Height ?? 0).ThenByDescending(static s => s.Bitrate ?? 0).ThenByDescending(static s => s.Size ?? 0);
}

/// <summary>
/// The <see cref="ISourceSanitizer"/> of alternate versions (DESIGN v2.6.4): with the switch on, it examines up to 64 sources and keeps at most four.
/// With the switch off it changes nothing, which is v1.
/// </summary>
public sealed class AlternateVersionSourceSanitizer : ISourceSanitizer
{
    private readonly Func<bool> _enabled;

    /// <summary>
    /// Initializes a new instance of the <see cref="AlternateVersionSourceSanitizer"/> class.
    /// </summary>
    /// <param name="enabled">Reads the <c>AlternateVersionsEnabled</c> switch.</param>
    public AlternateVersionSourceSanitizer(Func<bool> enabled) => _enabled = enabled;

    /// <inheritdoc />
    public int MaxExamined => _enabled() ? SourceSelector.MaxExamined : 8;

    /// <inheritdoc />
    public IReadOnlyList<SanitizedSource> Refine(BaseItemDto dto, IReadOnlyList<SanitizedSource> sources) =>
        _enabled() ? SourceSelector.Select(sources, sources.Count > 0 ? sources[0].Id : null) : sources;
}
