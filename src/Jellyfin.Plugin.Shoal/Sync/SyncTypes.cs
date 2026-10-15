using System;
using System.Collections.Generic;
using Jellyfin.Plugin.Shoal.Configuration;

namespace Jellyfin.Plugin.Shoal.Sync;

/// <summary>
/// What started a sync.
/// </summary>
public enum SyncTrigger
{
    /// <summary>The schedule.</summary>
    Scheduled,

    /// <summary>The admin pressed "Sync now": at least 5 minutes after the last run.</summary>
    Manual,
}

/// <summary>
/// Something the admin should hear about after a sync (DESIGN 5.2, 5.10). The admin screens read these from the catalog.
/// </summary>
public enum SyncAlert
{
    /// <summary>The mass-deletion guard held removals back; "Release" applies them.</summary>
    HeldRemovals,

    /// <summary>The new-items guard held additions back; "Release" imports them.</summary>
    HeldAdditions,

    /// <summary>The disk quota is reached: images stop, titles still import.</summary>
    QuotaReached,

    /// <summary>The peer has more than 50,000 titles: the sync stopped.</summary>
    EntryCap,

    /// <summary>A people, genre or studio budget (20,000 each) is full: new names are dropped.</summary>
    NameCap,

    /// <summary>A mapped library is not visible on the peer; its titles are held, never deleted.</summary>
    LibraryMissing,

    /// <summary>Items from the peer could not be read and were skipped.</summary>
    ItemsSkipped,

    /// <summary>Stubs could not be written.</summary>
    WriteFailures,
}

/// <summary>
/// Home's own movies: every <c>Movie</c> that is not under the stub root.
/// </summary>
public interface ILocalMovies
{
    /// <summary>
    /// Lists the movies.
    /// </summary>
    /// <param name="stubRoot">The stub root; items below it are federated and are left out.</param>
    /// <returns>The movies.</returns>
    IReadOnlyList<LocalMovie> List(string stubRoot);
}

/// <summary>
/// Tells Jellyfin that stub folders changed, and whether a library scan is running.
/// </summary>
public interface ILibraryRefresh
{
    /// <summary>Gets a value indicating whether a library scan is running (a sync waits then).</summary>
    bool IsScanRunning { get; }

    /// <summary>
    /// Reports that a folder changed; core's debounced refresher picks it up. Never queues a scan of every library.
    /// </summary>
    /// <param name="path">The movie folder, or the library folder.</param>
    void ReportChanged(string path);

    /// <summary>
    /// Queues one library scan so the new stubs of a first sync show up without a manual scan.
    /// </summary>
    void QueueScan();
}

/// <summary>
/// One of Home's own movies, for the dedup rule "local always wins" (DESIGN 5.3).
/// </summary>
/// <param name="ProviderIds">The canonical provider ids.</param>
/// <param name="Title">The title.</param>
/// <param name="Year">The production year, or null.</param>
/// <param name="SizeBytes">The size of the first media source, or 0.</param>
public sealed record LocalMovie(CanonicalIds ProviderIds, string Title, int? Year, long SizeBytes);

/// <summary>
/// The sync settings in force (the XML toggles of DESIGN 3.2, read at the start of each sync).
/// </summary>
/// <param name="MassDeletionMinCount">The smallest number of removals that the guard holds back.</param>
/// <param name="MassDeletionPercent">The percentage of active titles whose removal the guard holds back.</param>
/// <param name="NewItemsMinCount">The smallest number of additions that the guard holds back.</param>
/// <param name="NewItemsPercent">The percentage of active titles whose addition the guard holds back.</param>
/// <param name="DiskQuotaGbPerPeer">The disk quota per peer in GB, 1 to 50.</param>
/// <param name="ArtQuality">The size of posters and backdrops.</param>
public sealed record SyncSettings(
    int MassDeletionMinCount,
    int MassDeletionPercent,
    int NewItemsMinCount,
    int NewItemsPercent,
    int DiskQuotaGbPerPeer,
    ArtQuality ArtQuality)
{
    /// <summary>Gets the defaults of DESIGN 5.2 and 5.10.</summary>
    public static SyncSettings Default { get; } = new(25, 10, 500, 20, 2, ArtQuality.Standard);

    /// <summary>Gets a value indicating whether a title keeps one stub per media source (DESIGN v2.6.4; off by default, then the sync is exactly v1).</summary>
    public bool AlternateVersions { get; init; }

    /// <summary>Gets a value indicating whether titles without trickplay are looked at again now and then, because a Peer may generate its thumbnails later (DESIGN v2.6.5).</summary>
    public bool TrickplayRefresh { get; init; } = true;

    /// <summary>Gets the disk quota in bytes when it is set directly (tests scale it down); null uses <see cref="DiskQuotaGbPerPeer"/>.</summary>
    internal long? QuotaBytes { get; init; }

    /// <summary>Gets the cap on distinct people, genre and studio names (DESIGN 5.10: 20,000); tests scale it down.</summary>
    internal int NameCap { get; init; } = 20_000;

    /// <summary>
    /// Reads the settings from the plugin configuration.
    /// </summary>
    /// <param name="configuration">The configuration, or null for the defaults.</param>
    /// <returns>The settings.</returns>
    public static SyncSettings From(PluginConfiguration? configuration) => configuration is null
        ? Default
        : new SyncSettings(
            configuration.MassDeletionMinCount,
            configuration.MassDeletionPercent,
            configuration.NewItemsMinCount,
            configuration.NewItemsPercent,
            configuration.DiskQuotaGbPerPeer,
            configuration.ArtQuality)
        {
            AlternateVersions = configuration.AlternateVersionsEnabled,
            TrickplayRefresh = configuration.TrickplayEnabled
        };
}

/// <summary>
/// The report of one sync (also stored as <see cref="LastSyncInfo"/>).
/// </summary>
public sealed class SyncReport
{
    /// <summary>Gets or sets the outcome.</summary>
    public SyncResultKind Result { get; set; }

    /// <summary>Gets or sets a short code for a failure or a skip (never peer text), or null.</summary>
    public string? Error { get; set; }

    /// <summary>Gets or sets how many stubs were added.</summary>
    public int Added { get; set; }

    /// <summary>Gets or sets how many stubs were updated.</summary>
    public int Updated { get; set; }

    /// <summary>Gets or sets how many stubs were removed.</summary>
    public int Removed { get; set; }

    /// <summary>Gets or sets how many entries were moved to a new peer id.</summary>
    public int Moved { get; set; }

    /// <summary>Gets or sets how many removals are held back (after this sync).</summary>
    public int HeldRemovals { get; set; }

    /// <summary>Gets or sets how many additions are held back (after this sync).</summary>
    public int HeldAdditions { get; set; }

    /// <summary>Gets or sets how many titles were found to be duplicates (of Home's own copy or of another peer).</summary>
    public int Duplicates { get; set; }

    /// <summary>Gets or sets how many items of the peer could not be read and were skipped.</summary>
    public int SkippedItems { get; set; }

    /// <summary>Gets or sets how many images were stored.</summary>
    public int ImagesStored { get; set; }

    /// <summary>Gets or sets how many movie folders changed (for the library refresh).</summary>
    public int ChangedFolders { get; set; }

    /// <summary>Gets the alerts this sync raised.</summary>
    public List<SyncAlert> Alerts { get; } = [];

    /// <summary>Gets or sets a value indicating whether the listing was complete (removals are only decided on a complete listing).</summary>
    public bool Complete { get; set; }

    /// <summary>
    /// Creates a report for a sync that did not run.
    /// </summary>
    /// <param name="error">The reason code.</param>
    /// <returns>The report.</returns>
    public static SyncReport Skipped(string error) => new() { Result = SyncResultKind.Skipped, Error = error };

    /// <summary>
    /// Creates a report for a sync that could not do its work.
    /// </summary>
    /// <param name="error">The reason code.</param>
    /// <returns>The report.</returns>
    public static SyncReport Failed(string error) => new() { Result = SyncResultKind.Failed, Error = error };
}
