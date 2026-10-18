using System;
using System.Collections.Generic;
using System.Linq;
using MediaBrowser.Model.Configuration;

namespace Jellyfin.Plugin.Shoal.Visibility;

/// <summary>
/// The locked options of a Shoal library (DESIGN 5.6). The library holds only stubs Shoal wrote, so core must not rewrite the NFO, reach the
/// internet, extract anything from the (nonexistent) media files or download subtitles.
/// </summary>
public static class LibraryOptionsFactory
{
    /// <summary>The item type the type options apply to.</summary>
    public const string MovieType = "Movie";

    /// <summary>The item types that can sit under a Shoal path; each gets a type entry with no fetchers, because an item type without an entry falls back to the server-wide providers (12.1).</summary>
    public static readonly IReadOnlyList<string> LockedTypes = ["Movie", "Video", "Trailer", "BoxSet"];

    /// <summary>The item types that can sit under the path of a TV library (DESIGN v2.7.6): each gets a type entry with no fetchers.</summary>
    public static readonly IReadOnlyList<string> TvLockedTypes = ["Series", "Season", "Episode", "Video"];

    /// <summary>
    /// Creates the options of a Shoal movie library.
    /// </summary>
    /// <param name="path">The library's only path: <c>&lt;StubRoot&gt;/&lt;peerKey&gt;/&lt;Subfolder&gt;</c>.</param>
    /// <param name="subtitleFetcherNames">The names of every subtitle provider core knows, to be listed as disabled.</param>
    /// <param name="mediaSegmentProviders">The names and ids of every media segment provider core knows, to be listed as disabled (a segment provider would analyze the stub), or null.</param>
    /// <param name="tvShows">True for the options of a TV library (Series, Season and Episode locked as well, automatic series grouping on: PLAN v2.9 K43).</param>
    /// <returns>The options.</returns>
    public static LibraryOptions Create(string path, IEnumerable<string> subtitleFetcherNames, IEnumerable<string>? mediaSegmentProviders = null, bool tvShows = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(subtitleFetcherNames);
        return new LibraryOptions
        {
            Enabled = true,
            PathInfos = [new MediaPathInfo { Path = path }],

            // Core would rewrite our NFO, or fetch from the internet, if any of these were on.
            EnableRealtimeMonitor = false,
            SaveLocalMetadata = false,
            MetadataSavers = [],
#pragma warning disable CS0618 // Obsolete in 12.1 (remote providers are disabled per type below); still set for servers that read it.
            EnableInternetProviders = false,
#pragma warning restore CS0618
            TypeOptions =
            [
                .. (tvShows ? TvLockedTypes : LockedTypes).Select(static type => new TypeOptions
                {
                    Type = type,
                    MetadataFetchers = [],
                    MetadataFetcherOrder = [],
                    ImageFetchers = [],
                    ImageFetcherOrder = [],
                }),
            ],

            // There is no media file to extract anything from.
            EnableChapterImageExtraction = false,
            ExtractChapterImagesDuringLibraryScan = false,
            EnableTrickplayImageExtraction = false,
            ExtractTrickplayImagesDuringLibraryScan = false,
            SaveTrickplayWithMedia = false,
            EnableAutomaticSeriesGrouping = tvShows, // K43: a friend's series and another friend's extra episodes of it, in one shared library, show as one series
            EnableLUFSScan = false,
            EnableEmbeddedTitles = false,

            DisabledSubtitleFetchers = [.. subtitleFetcherNames.Distinct(StringComparer.Ordinal)],
            DisabledMediaSegmentProviders = [.. (mediaSegmentProviders ?? []).Distinct(StringComparer.Ordinal)],
            SubtitleDownloadLanguages = [],
            AutomaticallyAddToCollection = false,
            AutomaticRefreshIntervalDays = 0,
        };
    }

    /// <summary>
    /// Puts the locked parts of a Shoal library's options back (Pilot 14 LBL3): no metadata savers, no local metadata saving, no internet metadata or image
    /// fetchers for any type, no realtime monitor. Everything else stays as the admin set it.
    /// </summary>
    /// <param name="options">The options, changed in place.</param>
    /// <returns>True when something was put back.</returns>
    public static bool Lock(LibraryOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var changed = false;

        // Warden 12 L1: null counts as locked (an empty array may not round-trip, and a save would loop).
        if (options.MetadataSavers is { Length: > 0 })
        {
            options.MetadataSavers = [];
            changed = true;
        }

        if (options.SaveLocalMetadata)
        {
            options.SaveLocalMetadata = false;
            changed = true;
        }

        if (options.EnableRealtimeMonitor)
        {
            options.EnableRealtimeMonitor = false;
            changed = true;
        }

#pragma warning disable CS0618 // Obsolete in 12.1; still read by older servers.
        if (options.EnableInternetProviders)
        {
            options.EnableInternetProviders = false;
            changed = true;
        }
#pragma warning restore CS0618

        foreach (var type in options.TypeOptions ?? [])
        {
            if (type.MetadataFetchers is { Length: > 0 } || type.ImageFetchers is { Length: > 0 })
            {
                type.MetadataFetchers = [];
                type.MetadataFetcherOrder = [];
                type.ImageFetchers = [];
                type.ImageFetcherOrder = [];
                changed = true;
            }
        }

        return changed;
    }
}
