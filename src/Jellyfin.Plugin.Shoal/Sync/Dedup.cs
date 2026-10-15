using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Jellyfin.Plugin.Shoal.Sync;

/// <summary>
/// The result of asking whether a title is already covered.
/// </summary>
/// <param name="State">Active when the title is new to Home, otherwise why it is not imported.</param>
/// <param name="DuplicateOf">The stub key of the entry that wins, for <see cref="EntryState.DuplicatePeer"/>.</param>
/// <param name="LocalSizeBytes">The size of Home's own copy, for <see cref="EntryState.DuplicateLocal"/>.</param>
public readonly record struct DedupVerdict(EntryState State, string? DuplicateOf, long LocalSizeBytes);

/// <summary>
/// How two titles are compared (DESIGN 5.3).
/// </summary>
public static class TitleKey
{
    /// <summary>
    /// Normalizes a title for comparison: NFKC, case folded, everything that is not a letter or a digit removed.
    /// </summary>
    /// <param name="title">The title.</param>
    /// <returns>The key, or null when nothing is left.</returns>
    public static string? Normalize(string? title)
    {
        var normalized = PeerText.NormalizeNfkc(title, 1000);
        if (normalized.Length == 0)
        {
            return null;
        }

        var builder = new StringBuilder(normalized.Length);
        foreach (var rune in normalized.EnumerateRunes())
        {
            if (Rune.IsLetterOrDigit(rune))
            {
                builder.Append(Rune.ToLowerInvariant(rune).ToString());
            }
        }

        return builder.Length == 0 ? null : builder.ToString();
    }

    /// <summary>
    /// The weak key <c>title|year</c>, or null when either part is missing (a title without a year never matches).
    /// </summary>
    /// <param name="title">The title.</param>
    /// <param name="year">The year.</param>
    /// <returns>The key, or null.</returns>
    public static string? Weak(string? title, int? year)
    {
        var normalized = Normalize(title);
        return normalized is null || year is null ? null : normalized + "|" + year.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
}

/// <summary>
/// Decides which copy of a title wins (DESIGN 5.3): Home's own copy always, then the better-ranked peer. The rule is the same for stubs that
/// exist and stubs that would be created.
/// <para>
/// A match is any shared provider id. With no provider id on either side, an exact normalized title plus an equal year; without a year a
/// title never matches.
/// </para>
/// </summary>
public sealed class DedupIndex
{
    private readonly Dictionary<string, long> _localByProvider = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _localByWeakKey = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _claims = new(StringComparer.Ordinal);

    /// <summary>
    /// Initializes a new instance of the <see cref="DedupIndex"/> class.
    /// </summary>
    /// <param name="localMovies">Home's own movies.</param>
    public DedupIndex(IEnumerable<LocalMovie> localMovies)
    {
        ArgumentNullException.ThrowIfNull(localMovies);
        foreach (var movie in localMovies)
        {
            if (movie.ProviderIds.HasAny)
            {
                foreach (var key in movie.ProviderIds.DedupKeys())
                {
                    _localByProvider.TryAdd(key, movie.SizeBytes);
                }
            }
            else if (TitleKey.Weak(movie.Title, movie.Year) is { } weak)
            {
                _localByWeakKey.TryAdd(weak, movie.SizeBytes);
            }
        }
    }

    /// <summary>
    /// Registers the keys of a title that a better-ranked peer (or an earlier library) carries, so a worse-ranked copy becomes a duplicate.
    /// </summary>
    /// <param name="ids">The canonical provider ids.</param>
    /// <param name="title">The title.</param>
    /// <param name="year">The year.</param>
    /// <param name="stubKey">The winner's stub key.</param>
    public void Claim(CanonicalIds ids, string? title, int? year, string stubKey)
    {
        foreach (var key in KeysOf(ids, title, year))
        {
            _claims.TryAdd(key, stubKey);
        }
    }

    /// <summary>
    /// Judges a title.
    /// </summary>
    /// <param name="ids">The canonical provider ids.</param>
    /// <param name="title">The title.</param>
    /// <param name="year">The year.</param>
    /// <returns>The verdict.</returns>
    public DedupVerdict Judge(CanonicalIds ids, string? title, int? year)
    {
        var keys = KeysOf(ids, title, year);
        foreach (var key in keys)
        {
            if (_localByProvider.TryGetValue(key, out var size) || (!ids.HasAny && _localByWeakKey.TryGetValue(key, out size)))
            {
                return new DedupVerdict(EntryState.DuplicateLocal, null, size);
            }
        }

        foreach (var key in keys)
        {
            if (_claims.TryGetValue(key, out var winner))
            {
                return new DedupVerdict(EntryState.DuplicatePeer, winner, 0);
            }
        }

        return new DedupVerdict(EntryState.Active, null, 0);
    }

    private static List<string> KeysOf(CanonicalIds ids, string? title, int? year)
    {
        var keys = new List<string>(ids.DedupKeys());
        if (!ids.HasAny && TitleKey.Weak(title, year) is { } weak)
        {
            keys.Add(weak);
        }

        return keys;
    }
}
