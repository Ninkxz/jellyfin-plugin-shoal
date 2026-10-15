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

    /// <summary>
    /// Creates the options of a Shoal movie library.
    /// </summary>
    /// <param name="path">The library's only path: <c>&lt;StubRoot&gt;/&lt;peerKey&gt;/&lt;Subfolder&gt;</c>.</param>
    /// <param name="subtitleFetcherNames">The names of every subtitle provider core knows, to be listed as disabled.</param>
    /// <param name="mediaSegmentProviders">The names and ids of every media segment provider core knows, to be listed as disabled (a segment provider would analyze the stub), or null.</param>
    /// <returns>The options.</returns>
    public static LibraryOptions Create(string path, IEnumerable<string> subtitleFetcherNames, IEnumerable<string>? mediaSegmentProviders = null)
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
                .. LockedTypes.Select(static type => new TypeOptions
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
            EnableLUFSScan = false,
            EnableEmbeddedTitles = false,

            DisabledSubtitleFetchers = [.. subtitleFetcherNames.Distinct(StringComparer.Ordinal)],
            DisabledMediaSegmentProviders = [.. (mediaSegmentProviders ?? []).Distinct(StringComparer.Ordinal)],
            SubtitleDownloadLanguages = [],
            AutomaticallyAddToCollection = false,
            AutomaticRefreshIntervalDays = 0,
        };
    }
}
