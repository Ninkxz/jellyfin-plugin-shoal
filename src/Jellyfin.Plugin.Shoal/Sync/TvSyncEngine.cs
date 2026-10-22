using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.Peers;
using Jellyfin.Plugin.Shoal.Security;
using Jellyfin.Plugin.Shoal.State;
using MediaBrowser.Model.Dto;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Shoal.Sync;

/// <summary>
/// The TV part of a sync (DESIGN v2.7.2, K36). For every mapping of type <c>tvshows</c> it lists the peer (three listings), sanitizes, plans through
/// <see cref="ITvPlanner"/>, confirms removals with the peer, fetches details in batches inside the structure budget (the rest waits in <c>pendingDetail</c>),
/// writes through <see cref="TvStubWriter"/> in the specified order, and reports the changed series folders to core one by one (never a library scan; deferred while
/// the peer has streams). The movie sync is untouched and runs first; this runs under the same global sync lock.
/// </summary>
public sealed class TvSyncEngine
{
    private const string KeyAlphabet = "abcdefghijklmnopqrstuvwxyz0123456789";
    private const int FlushEvery = 200;

    private readonly ITvSanitizer _sanitizer;
    private readonly ITvLayout _layout;
    private readonly ITvNfoWriter _nfo;
    private readonly IPeerTvReader _reader;
    private readonly ITvPlanner _planner;
    private readonly TvStubWriter _writer;
    private readonly ILocalSeries _local;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, Deferred> _deferred = new(StringComparer.Ordinal);

    /// <summary>
    /// Initializes a new instance of the <see cref="TvSyncEngine"/> class.
    /// </summary>
    /// <param name="sanitizer">The TV sanitizer.</param>
    /// <param name="layout">The layout rules.</param>
    /// <param name="nfo">The NFO writer.</param>
    /// <param name="reader">The peer reader.</param>
    /// <param name="planner">The planner.</param>
    /// <param name="writer">The file writer.</param>
    /// <param name="local">Home's own series.</param>
    /// <param name="logger">The logger.</param>
    public TvSyncEngine(ITvSanitizer sanitizer, ITvLayout layout, ITvNfoWriter nfo, IPeerTvReader reader, ITvPlanner planner, TvStubWriter writer, ILocalSeries local, ILogger<TvSyncEngine> logger)
        : this(sanitizer, layout, nfo, reader, planner, writer, local, (ILogger)logger)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="TvSyncEngine"/> class (tests: any logger).
    /// </summary>
    /// <param name="sanitizer">The TV sanitizer.</param>
    /// <param name="layout">The layout rules.</param>
    /// <param name="nfo">The NFO writer.</param>
    /// <param name="reader">The peer reader.</param>
    /// <param name="planner">The planner.</param>
    /// <param name="writer">The file writer.</param>
    /// <param name="local">Home's own series.</param>
    /// <param name="logger">The logger.</param>
    internal TvSyncEngine(ITvSanitizer sanitizer, ITvLayout layout, ITvNfoWriter nfo, IPeerTvReader reader, ITvPlanner planner, TvStubWriter writer, ILocalSeries local, ILogger logger)
    {
        _sanitizer = sanitizer;
        _layout = layout;
        _nfo = nfo;
        _reader = reader;
        _planner = planner;
        _writer = writer;
        _local = local;
        _logger = logger;
    }

    /// <summary>Gets or sets how many streams a peer has right now; above 0 the library refresh waits (DESIGN v2.7.2, Phase E).</summary>
    public Func<string, int> ActiveStreams { get; set; } = static _ => 0;

    /// <summary>Gets or sets how long a refresh waits for the streams of a peer to end.</summary>
    public TimeSpan RefreshDeferral { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>Gets or sets the image work of a TV library (K37); with <see cref="Images"/> unset no images are fetched.</summary>
    public ITvImageWork? ImageWork { get; set; }

    /// <summary>Gets or sets the runner of the image stage (K37), or null for none.</summary>
    public TvImageRunner? Images { get; set; }

    /// <summary>Gets or sets a value indicating whether episode thumbnails are wanted (for the image work of K37).</summary>
    public Func<bool> EpisodeThumbs { get; set; } = static () => true;

    /// <summary>
    /// Gets or sets whether the Jellyfin library holding a folder merges series with the same ids (<c>EnableAutomaticSeriesGrouping</c>), or null when no
    /// library holds it. 2.9.4: a series another server shows in the same folder is joined (a second folder with the same ids) only when this is not false.
    /// </summary>
    public Func<string, bool?> SeriesGrouping { get; set; } = static _ => null;

    /// <summary>
    /// Runs the TV part of a sync for one peer.
    /// </summary>
    /// <param name="d">The services.</param>
    /// <param name="state">The state.</param>
    /// <param name="peer">The peer.</param>
    /// <param name="endpoint">The peer with its credentials.</param>
    /// <param name="doc">The catalog; the movie sync saved it just before.</param>
    /// <param name="settings">The settings in force.</param>
    /// <param name="now">The time of the sync.</param>
    /// <param name="bypassRemovals">Whether held removals are released now.</param>
    /// <param name="bypassAdditions">Whether held additions are released now.</param>
    /// <param name="userToken">The caller's cancellation.</param>
    /// <param name="budgetToken">The budget of the whole sync.</param>
    /// <param name="releaseSeries">The peer series ids the admin released by hand, or null.</param>
    /// <returns>The report of the TV part, or null when the peer has no TV mapping.</returns>
    internal async Task<SyncReport?> RunAsync(CatalogSyncService.Dependencies d, ShoalState state, PeerState peer, PeerEndpoint endpoint, CatalogDocument doc, SyncSettings settings, DateTime now, bool bypassRemovals, bool bypassAdditions, CancellationToken userToken, CancellationToken budgetToken, IReadOnlySet<Guid>? releaseSeries = null)
    {
        var mappings = peer.Libraries.Where(static m => m.Import && IsTv(m)).ToList();
        if (mappings.Count == 0)
        {
            return null;
        }

        var run = new Run(this, d, state, peer, endpoint, doc, settings, now, bypassRemovals, bypassAdditions) { ReleaseSeries = releaseSeries };
        try
        {
            await run.RunAsync(mappings, budgetToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!userToken.IsCancellationRequested)
        {
            run.Report.Error ??= "budget"; // what is written is saved; the rest happens at the next sync
            run.Report.Complete = false;
        }

        run.Finish();
        return run.Report;
    }

    /// <summary>
    /// Whether a mapping is a TV library.
    /// </summary>
    /// <param name="mapping">The mapping.</param>
    /// <returns>True for <c>tvshows</c>.</returns>
    internal static bool IsTv(LibraryMapping mapping) => string.Equals(mapping.CollectionType, "tvshows", StringComparison.Ordinal);

    private static string NewKey() => RandomNumberGenerator.GetString(KeyAlphabet, 16);

    private static int CeilPercent(int count, int percent) => (int)(((long)count * percent) + 99) / 100;

    // What counts against the 50,000 playable entries of a catalog (the same rule as CatalogStore.Sanitize).
    private static bool IsPlayable(CatalogEntry e) => e.Kind is EntryKind.Movie or EntryKind.Episode && e.State is not EntryState.Removed;

    private static bool IsLive(CatalogEntry e) => e.State is EntryState.Active or EntryState.HeldRemoval;

    private static Dictionary<string, string> ProviderMap(CanonicalIds ids)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (ids.Tmdb is { } tmdb)
        {
            map["Tmdb"] = tmdb.ToString(CultureInfo.InvariantCulture);
        }

        if (ids.Imdb is { } imdb)
        {
            map["Imdb"] = imdb;
        }

        if (ids.Tvdb is { } tvdb)
        {
            map["Tvdb"] = tvdb.ToString(CultureInfo.InvariantCulture);
        }

        return map;
    }

    private void Flush(string peerKey)
    {
        if (_deferred.TryRemove(peerKey, out var waiting))
        {
            try
            {
                waiting.Refresh(waiting.Folders);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Nothing observes the timer task, so a failure here would lose the folders without a trace (Warden 12 L3).
                _logger.LogWarning("The deferred library refresh of peer {Peer} failed ({Error})", Redact.Id(peerKey), ex.GetType().Name);
            }
        }
    }

    private void Refresh(CatalogSyncService.Dependencies d, string peerKey, string libraryFolder, HashSet<string> seriesFolders, bool first)
    {
        void Report(IReadOnlyCollection<string> folders)
        {
            if (folders.Count > TvLimits.MaxSeriesRefreshes || first)
            {
                d.Refresh.ReportChanged(libraryFolder); // one report for the library root
                if (first)
                {
                    // Core does not see the folders of a library with no stubs yet without a scan (realtime monitoring is off): the same single scan the movie path queues. Only the first sync of a library does this (Pilot 06).
                    d.Refresh.QueueScan();
                }

                return;
            }

            foreach (var folder in folders)
            {
                d.Refresh.ReportChanged(Path.Combine(libraryFolder, folder));
            }
        }

        if (ActiveStreams(peerKey) > 0 && RefreshDeferral > TimeSpan.Zero)
        {
            // Streams are running: a scan of thousands of stubs would stall them (owner feedback 01). The refresh waits, up to RefreshDeferral.
            var firstForPeer = false;
            _deferred.AddOrUpdate(
                peerKey,
                _ =>
                {
                    firstForPeer = true;
                    return new Deferred(new HashSet<string>(seriesFolders, StringComparer.Ordinal), Report);
                },
                (_, old) =>
                {
                    old.Folders.UnionWith(seriesFolders);
                    return old;
                });

            // One timer per peer: a later sync only adds its folders to the waiting set, which the one timer then reports.
            if (firstForPeer)
            {
                _ = Task.Run(async () =>
                {
                    await Task.Delay(RefreshDeferral).ConfigureAwait(false);
                    Flush(peerKey);
                });
            }

            return;
        }

        Report(seriesFolders);
    }

    private sealed record Deferred(HashSet<string> Folders, Action<IReadOnlyCollection<string>> Refresh);

    private sealed class Run
    {
        private readonly TvSyncEngine _e;
        private readonly CatalogSyncService.Dependencies _d;
        private readonly ShoalState _state;
        private readonly PeerState _peer;
        private readonly PeerEndpoint _endpoint;
        private readonly CatalogDocument _doc;
        private readonly SyncSettings _settings;
        private readonly DateTime _now;
        private readonly bool _bypassRemovals;
        private readonly bool _bypassAdditions;
        private readonly DiskQuota _quota;
        private readonly HashSet<SyncAlert> _alerts = [];
        private readonly Dictionary<string, HashSet<string>> _changed = new(StringComparer.Ordinal); // library folder -> changed series folders
        private readonly IReadOnlyList<string> _tags;
        private readonly HashSet<string> _listedAll = new(StringComparer.Ordinal);
        private List<string> _dueKeys = [];
        private Dictionary<string, int> _episodesPerSeries = [];
        private bool _listingComplete;
        private HashSet<string>? _pendingImages;
        private int _sinceFlush;
        private bool _firstStubs;

        public Run(TvSyncEngine engine, CatalogSyncService.Dependencies d, ShoalState state, PeerState peer, PeerEndpoint endpoint, CatalogDocument doc, SyncSettings settings, DateTime now, bool bypassRemovals, bool bypassAdditions)
        {
            _e = engine;
            _d = d;
            _state = state;
            _peer = peer;
            _endpoint = endpoint;
            _doc = doc;
            _settings = settings;
            _now = now;
            _bypassRemovals = bypassRemovals;
            _bypassAdditions = bypassAdditions;
            _quota = new DiskQuota(settings.QuotaBytes ?? DiskQuota.LimitFromGigabytes(settings.DiskQuotaGbPerPeer), doc.Usage.DiskBytes);
            _tags = ["Shoal", "Shoal: " + MetadataSanitizer.DisplayName(peer.DisplayName)];
            Report = new SyncReport { Complete = true };
        }

        public SyncReport Report { get; }

        /// <summary>Gets the peer series ids the admin released by hand, or null.</summary>
        public IReadOnlySet<Guid>? ReleaseSeries { get; init; }

        public async Task RunAsync(List<LibraryMapping> mappings, CancellationToken ct)
        {
            IReadOnlyList<PeerView> views;
            try
            {
                views = (await _d.Reader.SharedViewsAsync(_endpoint, ct).ConfigureAwait(false)).Views; // 2.9.2 K2: out of scope = missing, held
            }
            catch (PeerCallException ex)
            {
                _d.Logger.LogInformation("The libraries of peer {Peer} could not be listed for TV ({Kind})", Redact.Id(_peer.PeerKey), ex.Kind);
                Report.Complete = false;
                Report.Error ??= "viewsFailed";
                return;
            }

            foreach (var mapping in mappings)
            {
                await MappingAsync(mapping, views, ct).ConfigureAwait(false);
            }
        }

        public void Finish()
        {
            if (Report.Complete)
            {
                _doc.TvHeldAdditions.RemoveAll(id => !_listedAll.Contains(id)); // the peer no longer lists them: forgotten, as the movie sync forgets its own
                _doc.Report = OverlapReports.Compute(_doc, _now); // the movie sync computed it before the TV titles existed
            }

            _doc.Usage.DiskBytes = _quota.UsedBytes;
            _doc.HeldRemovals = [.. _doc.Entries.Where(static e => e.State == EntryState.HeldRemoval).Select(static e => e.StubKey)];
            Report.HeldRemovals = _doc.Entries.Count(static e => e.State == EntryState.HeldRemoval && e.Kind == EntryKind.Episode);
            Report.HeldAdditions = _doc.TvHeldAdditions.Count;
            foreach (var alert in _alerts)
            {
                Report.Alerts.Add(alert);
            }

            if (Report.HeldRemovals > 0)
            {
                Report.Alerts.Add(SyncAlert.HeldRemovals);
            }

            Report.ChangedFolders = _changed.Values.Sum(static s => s.Count);
            _d.Store.Save(_doc);
            UpdateSeriesGroups();
            foreach (var (library, folders) in _changed)
            {
                _e.Refresh(_d, _peer.PeerKey, library, folders, _firstStubs);
            }
        }

        // K43: the series groups follow this server's series entries.
        private void UpdateSeriesGroups()
        {
            if (_d.Groups is not { } groups)
            {
                return;
            }

            string LibKeyOf(PeerState peer, string mappingKey) =>
                peer.Libraries.FirstOrDefault(m => m.MappingKey == mappingKey) is { } mapping
                    ? LibraryFolders.LibraryOf(_state, peer.PeerKey, mapping)?.LibKey ?? StateUpgrade.LegacyLibKey(peer.PeerKey, mappingKey)
                    : string.Empty;

            Dictionary<string, OwnedCopy>? owners = null;
            OwnedCopy? OwnerOf(string stubKey)
            {
                if (owners is null)
                {
                    owners = new Dictionary<string, OwnedCopy>(StringComparer.Ordinal);
                    foreach (var other in _state.Home.Peers.Where(p => p.PeerKey != _peer.PeerKey))
                    {
                        var load = _d.Store.Load(other.PeerKey);
                        if (load.Unreadable)
                        {
                            continue;
                        }

                        foreach (var entry in load.Document.Entries.Where(static e => e.Kind == EntryKind.Series && e.State == EntryState.Active && e.FolderRel is not null))
                        {
                            owners.TryAdd(entry.StubKey, new OwnedCopy(other.PeerKey, entry, LibKeyOf(other, entry.MappingKey)));
                        }
                    }
                }

                return owners.GetValueOrDefault(stubKey);
            }

            var known = _state.Home.Peers.Select(static p => p.PeerKey).ToHashSet(StringComparer.Ordinal);
            try
            {
                groups.Update(document => GroupPlanner.ApplyTvPeer(document, _peer.PeerKey, [.. _doc.Entries.Where(static e => e.Kind == EntryKind.Series)], m => LibKeyOf(_peer, m), OwnerOf, known, NewKey));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
            {
                _d.Logger.LogWarning("The Shoal series groups could not be updated after a sync ({Error})", ex.GetType().Name);
            }
        }

        private HashSet<string> LocalKeys()
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var local in _e._local.List(_state.StubRoot))
            {
                keys.UnionWith(TvPlanner.SeriesKeys(local.ProviderIds, local.Title, local.Year));
            }

            return keys;
        }

        // K43: the series a higher-ranked server that can be reached shows now, by dedup key, with its episodes (2.9.4: those of every such server); such a
        // series is grouped instead of hidden whole.
        private Dictionary<string, TvOwnedSeries> PeerOwned(string? folder)
        {
            var owned = new Dictionary<string, TvOwnedSeries>(StringComparer.Ordinal);
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

            // 2.9.5: a higher-ranked server that is down still owns the series it holds the group copy of (as K42 does for movies): a play falls over to
            // this server, a member of the group, so nothing on disk changes; before, this server wrote a second copy of every such series for one sync.
            var groups = _d.Groups;
            bool Reachable(PeerState p) => p.Enabled && _d.IsUp(p.PeerKey);
            var others = _state.Home.Peers.Where(p => p.PeerKey != _peer.PeerKey && (Reachable(p) || groups is not null) && CatalogSyncService.Outranks(p, _peer)).ToList();
            others.Sort(static (a, b) => CatalogSyncService.Outranks(a, b) ? -1 : CatalogSyncService.Outranks(b, a) ? 1 : 0);

            // 2.9.4: joining writes a second folder with the same ids; only a library that merges series shows it as one entry. Without that, the extra
            // episodes stay hidden sources (one entry, fewer episodes, never a duplicate entry). No library holding the folder: as before.
            var merges = others.Count == 0 || folder is null || _e.SeriesGrouping(folder) != false;
            foreach (var other in others)
            {
                var load = _d.Store.Load(other.PeerKey);
                if (load.Unreadable)
                {
                    continue;
                }

                var reachable = Reachable(other);
                var episodes = load.Document.Entries.Where(static e => e.Kind == EntryKind.Episode && IsLive(e) && e.ParentKey is not null).ToLookup(static e => e.ParentKey!, StringComparer.Ordinal);
                foreach (var entry in load.Document.Entries.Where(static e => e.Kind == EntryKind.Series && e.State == EntryState.Active))
                {
                    if (!reachable && !(groups?.ByCopy(entry.StubKey) is { } group && string.Equals(group.MetaOwner, other.PeerKey, StringComparison.Ordinal)))
                    {
                        continue;
                    }

                    var mapping = other.Libraries.FirstOrDefault(m => m.MappingKey == entry.MappingKey);
                    var otherFolder = mapping is null ? null : LibraryFolders.FolderOf(_state, other.PeerKey, mapping);
                    var same = merges && folder is not null && otherFolder is not null && string.Equals(Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(otherFolder).TrimEnd(Path.DirectorySeparatorChar), comparison);
                    var series = new TvOwnedSeries(other.PeerKey, entry.StubKey, same, [.. episodes[entry.StubKey].Where(static e => e.Season is not null && e.Episode is not null).Select(static e => new TvOwnedEpisode(e.Season!.Value, e.Episode!.Value, e.EpisodeEnd ?? e.Episode!.Value, e.StubKey))]);
                    foreach (var key in TvPlanner.SeriesKeys(MetadataSanitizer.CanonicalProviderIds(entry.ProviderIds), entry.Title, entry.Year))
                    {
                        // 2.9.4: every reachable higher-ranked owner counts, in rank order (the peers are sorted above): an episode overlapping any of
                        // them is hidden, and Overlap names the first owner that has it. The series link and SameFolder stay the first owner's.
                        owned[key] = owned.TryGetValue(key, out var first) ? first with { Episodes = [.. first.Episodes, .. series.Episodes] } : series;
                    }
                }
            }

            return owned;
        }

        private async Task MappingAsync(LibraryMapping mapping, IReadOnlyList<PeerView> views, CancellationToken ct)
        {
            var folder = LibraryFolders.FolderOf(_state, _peer.PeerKey, mapping);
            if (folder is null)
            {
                _alerts.Add(SyncAlert.WriteFailures);
                Report.Complete = false;
                return;
            }

            var known = _doc.Libraries.FirstOrDefault(l => l.MappingKey == mapping.MappingKey);
            var id = SyncRun.ResolveLibraryId(known, mapping, views); // 2.9.2 K1: by key or stored id, never by name
            if (id is null)
            {
                _alerts.Add(SyncAlert.LibraryMissing); // its titles are held, never deleted
                Report.Complete = false;
                if (known is null)
                {
                    _doc.Libraries.Add(new CatalogLibrary { MappingKey = mapping.MappingKey });
                }

                return;
            }

            if (known is null)
            {
                known = new CatalogLibrary { MappingKey = mapping.MappingKey };
                _doc.Libraries.Add(known);
            }

            known.PeerLibraryId = id.Value.ToString("N");
            Directory.CreateDirectory(folder);
            var target = new StubTarget(_state.StubRoot, folder, _endpoint, LibraryFolders.NfoTags(_state, _peer.PeerKey, mapping)) { OmitFacets = LibraryFolders.HidesFacets(_state, _peer.PeerKey, mapping), FriendName = _peer.ShownName() };

            TvListings listings;
            try
            {
                listings = await _e._reader.ListAsync(_endpoint, id.Value, ct).ConfigureAwait(false);
            }
            catch (PeerCallException ex)
            {
                _d.Logger.LogInformation("A TV listing of peer {Peer} failed ({Kind})", Redact.Id(_peer.PeerKey), ex.Kind);
                Report.Complete = false;
                return;
            }

            if (!listings.Complete)
            {
                Report.Complete = false;
            }

            // ---- sanitize
            var series = new List<SanitizedSeries>();
            var seasons = new List<SanitizedSeason>();
            var episodes = new List<SanitizedEpisode>();
            var noNumber = 0;
            var virtualCount = 0;
            foreach (var dto in listings.Series)
            {
                if (_e._sanitizer.Series(dto) is { } s)
                {
                    series.Add(s);
                }
                else
                {
                    Report.SkippedItems++;
                }
            }

            foreach (var dto in listings.Seasons)
            {
                if (_e._sanitizer.Season(dto) is { } s)
                {
                    seasons.Add(s);
                }
            }

            foreach (var dto in listings.Episodes)
            {
                var result = _e._sanitizer.Episode(dto);
                if (result.Episode is { } episode)
                {
                    episodes.Add(episode);
                }
                else if (result.Skip == TvSkip.NoNumber)
                {
                    noNumber++;
                }
                else if (result.Skip == TvSkip.Virtual)
                {
                    virtualCount++;
                }
                else
                {
                    Report.SkippedItems++;
                }
            }

            _listingComplete = listings.Complete;
            _listedAll.UnionWith(series.Select(static s => s.PeerItemId.ToString("N")));
            _listedAll.UnionWith(episodes.Select(static e => e.PeerItemId.ToString("N")));
            _episodesPerSeries = episodes.GroupBy(static e => e.PeerSeriesId).ToDictionary(static g => g.Key.ToString("N"), static g => g.Count(), StringComparer.Ordinal);

            // ---- plan
            var existing = _doc.Entries.Where(e => e.MappingKey == mapping.MappingKey).ToList();
            var guards = new TvGuardSettings(_settings.MassDeletionMinCount, _settings.MassDeletionPercent, _settings.NewItemsMinCount, _settings.NewItemsPercent);
            var plan = _e._planner.Plan(new TvPlanInput(mapping.MappingKey, existing, series, seasons, episodes, listings.Complete, !known.CompletedOnce, LocalKeys(), guards, _bypassRemovals, _bypassAdditions, _doc.TvHeldAdditions.ToHashSet(StringComparer.Ordinal), _doc.Entries.Count(e => e.MappingKey != mapping.MappingKey && IsPlayable(e)), ReleaseSeries, PeerOwned(folder)));
            foreach (var name in plan.Alerts)
            {
                if (Enum.TryParse<SyncAlert>(name, out var alert))
                {
                    _alerts.Add(alert);
                }
            }

            known.SkippedNoNumber = noNumber;
            known.SkippedDuplicate = plan.Counts.SkippedDuplicate;
            known.DroppedOrphans = plan.Counts.DroppedOrphans;
            known.DroppedOverCap = plan.Counts.DroppedOverCap;
            if (noNumber + virtualCount + plan.Counts.SkippedDuplicate + plan.Counts.DroppedOrphans > 0)
            {
                _alerts.Add(SyncAlert.TvSkipped);
            }

            // ---- removals: ask the peer before acting on a listing; held ones wait for the 3 complete syncs over 24 hours (or the admin)
            var removeKeys = plan.RemoveKeys.ToList();
            var heldKeys = plan.HeldRemovalKeys.ToList();
            if (heldKeys.Count > 0 && TrackAndMaybeRelease(heldKeys))
            {
                removeKeys.AddRange(heldKeys);
                heldKeys.Clear();
            }
            else if (heldKeys.Count == 0 && !_bypassRemovals)
            {
                _doc.TvHeldTracker = null;
            }

            var byKey = _doc.Entries.ToDictionary(static e => e.StubKey, StringComparer.Ordinal);

            // An episode the peer still lists (renumbered, or hidden because Home has the series) is gone from Home's view by our own decision: nothing to ask.
            var listedAll = episodes.Select(static e => e.PeerItemId.ToString("N")).ToHashSet(StringComparer.Ordinal);
            var toConfirm = removeKeys.Where(k => !listedAll.Contains(byKey[k].PeerItemId)).ToList();
            if (toConfirm.Count > 0)
            {
                try
                {
                    var asked = toConfirm.Select(k => Guid.ParseExact(byKey[k].PeerItemId, "N")).ToList();
                    var present = await _d.Reader.PresentAsync(_endpoint, asked, ct).ConfigureAwait(false);
                    removeKeys = [.. removeKeys.Where(k => listedAll.Contains(byKey[k].PeerItemId) || !present.Contains(Guid.ParseExact(byKey[k].PeerItemId, "N")))];
                }
                catch (PeerCallException)
                {
                    removeKeys.Clear();
                    Report.Complete = false;
                }
            }

            // ---- details for what needs them, inside the structure budget; the rest queues
            // An episode without trickplay is asked about again about once a week, at most 200 per sync (DESIGN v2.7.8), through the same details path.
            var recheck = new TrickplayRecheck(_now);
            _dueKeys = _settings.TrickplayRefresh
                ? existing.Where(e => e.Kind == EntryKind.Episode && IsLive(e) && e.ContentHash is not null && recheck.Due(e)).Select(static e => e.StubKey).ToList()
                : [];
            var wanted = plan.PendingDetail.Concat(_doc.PendingDetail).Concat(_dueKeys.Select(k => byKey[k].PeerItemId)).Distinct(StringComparer.Ordinal).Take(TvLimits.MaxEpisodesPerSeries * 5).ToList();
            var details = new Dictionary<Guid, BaseItemDto>();
            try
            {
                foreach (var batch in wanted.Select(static w => Guid.ParseExact(w, "N")).Chunk(TvLimits.DetailBatch))
                {
                    var fetched = await _e._reader.DetailsAsync(_endpoint, batch, ct).ConfigureAwait(false);
                    foreach (var dto in fetched)
                    {
                        details[dto.Id] = dto;
                    }
                }
            }
            catch (PeerCallException ex)
            {
                _d.Logger.LogInformation("A TV details request to peer {Peer} failed ({Kind})", Redact.Id(_peer.PeerKey), ex.Kind);
                Report.Complete = false;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                Report.Complete = false;
            }

            // ---- apply
            await ApplyAsync(mapping, known, target, folder, plan, existing, removeKeys, heldKeys, details, series, episodes, ct).ConfigureAwait(false);
            if (listings.Complete)
            {
                known.CompletedOnce = true;
            }

            known.LastCount = _doc.Entries.Count(e => e.MappingKey == mapping.MappingKey && e.Kind == EntryKind.Episode && IsLive(e));
        }

        private bool TrackAndMaybeRelease(List<string> held)
        {
            var fingerprint = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join(',', held.OrderBy(static k => k, StringComparer.Ordinal)))))[..32];
            var tracker = _doc.TvHeldTracker;
            if (tracker is null || !string.Equals(tracker.Fingerprint, fingerprint, StringComparison.Ordinal))
            {
                _doc.TvHeldTracker = new HeldRemovalTracker { FirstUtc = _now, CompleteSyncs = 1, Fingerprint = fingerprint };
                return false;
            }

            if (Report.Complete)
            {
                tracker.CompleteSyncs++;
            }

            if (tracker.CompleteSyncs >= 3 && _now - tracker.FirstUtc >= TimeSpan.FromHours(24))
            {
                _doc.TvHeldTracker = null;
                return true;
            }

            return false;
        }

        private void Changed(string libraryFolder, string seriesFolder)
        {
            if (!_changed.TryGetValue(libraryFolder, out var set))
            {
                _changed[libraryFolder] = set = new HashSet<string>(StringComparer.Ordinal);
            }

            set.Add(seriesFolder);
        }

        private void MaybeSave()
        {
            if (++_sinceFlush >= FlushEvery)
            {
                _sinceFlush = 0;
                _d.Store.Save(_doc);
            }
        }

        private async Task ApplyAsync(
            LibraryMapping mapping,
            CatalogLibrary lib,
            StubTarget target,
            string libraryFolder,
            TvPlan plan,
            List<CatalogEntry> existing,
            List<string> removeKeys,
            List<string> heldKeys,
            Dictionary<Guid, BaseItemDto> details,
            List<SanitizedSeries> listedSeries,
            List<SanitizedEpisode> listedEpisodes,
            CancellationToken ct)
        {
            await Task.CompletedTask.ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            var byKey = _doc.Entries.ToDictionary(static e => e.StubKey, StringComparer.Ordinal);

            // ---- moves: the stub, the local id and the watch state stay; only the peer ids change
            foreach (var move in plan.Moves)
            {
                if (!byKey.TryGetValue(move.StubKey, out var entry))
                {
                    continue;
                }

                var oldId = entry.PeerItemId;
                entry.PeerItemId = move.NewPeerItemId.ToString("N");
                entry.PeerEtag = string.Empty;
                entry.ContentHash = null;
                entry.LastSeenUtc = _now;
                if (entry.Kind == EntryKind.Series)
                {
                    foreach (var child in _doc.Entries.Where(e => e.MappingKey == entry.MappingKey && e.PeerSeriesId == oldId))
                    {
                        child.PeerSeriesId = entry.PeerItemId;
                    }
                }

                Report.Moved++;
            }

            // ---- an episode the peer lists again is no longer held
            var listedIds = listedEpisodes.Select(static e => e.PeerItemId.ToString("N")).ToHashSet(StringComparer.Ordinal);
            var held = heldKeys.ToHashSet(StringComparer.Ordinal);
            foreach (var entry in existing.Where(e => e.Kind == EntryKind.Episode && e.State == EntryState.HeldRemoval && !held.Contains(e.StubKey) && listedIds.Contains(e.PeerItemId)))
            {
                entry.State = EntryState.Active;
            }

            foreach (var key in heldKeys)
            {
                if (byKey.TryGetValue(key, out var entry))
                {
                    entry.State = EntryState.HeldRemoval;
                }
            }

            // ---- removals
            foreach (var key in removeKeys)
            {
                if (!byKey.TryGetValue(key, out var entry) || entry.Kind != EntryKind.Episode)
                {
                    continue;
                }

                if (entry.StubRelPath is null || _e._writer.RemoveEpisode(target, entry.StubRelPath, _quota))
                {
                    _doc.Entries.Remove(entry);
                    Report.Removed++;
                    if (entry.StubRelPath is not null)
                    {
                        Changed(libraryFolder, entry.StubRelPath.Split('/')[0]);
                    }
                }
                else
                {
                    _alerts.Add(SyncAlert.WriteFailures);
                }
            }

            // ---- series-level dedup: the whole series is hidden while Home has it
            var duplicateIds = plan.DuplicateLocalSeriesKeys.ToHashSet(StringComparer.Ordinal);
            foreach (var s in listedSeries.Where(s => duplicateIds.Contains(s.PeerItemId.ToString("N"))))
            {
                var entry = _doc.Entries.FirstOrDefault(e => e.MappingKey == mapping.MappingKey && e.Kind == EntryKind.Series && e.PeerItemId == s.PeerItemId.ToString("N"));
                if (entry is null)
                {
                    _doc.Entries.Add(SeriesRecord(mapping, s, EntryState.DuplicateLocal, folder: null));
                    Report.Duplicates++;
                }
                else if (entry.State != EntryState.DuplicateLocal)
                {
                    entry.State = EntryState.DuplicateLocal;
                    Report.Duplicates++;
                }
            }

            CleanContainers(mapping, target, libraryFolder);

            // ---- updates: rewrite only what changed (the content hash decides, not the etag)
            byKey = _doc.Entries.ToDictionary(static e => e.StubKey, StringComparer.Ordinal);
            foreach (var key in plan.UpdateKeys.Concat(_dueKeys).Distinct(StringComparer.Ordinal))
            {
                if (!byKey.TryGetValue(key, out var entry) || !Guid.TryParseExact(entry.PeerItemId, "N", out var peerId) || !details.TryGetValue(peerId, out var dto))
                {
                    continue; // no details this time; the etag stays old, so the next sync tries again
                }

                if (entry.Kind == EntryKind.Series && _e._sanitizer.Series(dto) is { } s)
                {
                    UpdateSeries(entry, s, target, libraryFolder);
                }
                else if (entry.Kind == EntryKind.Episode && _e._sanitizer.Episode(dto).Episode is { } ep)
                {
                    UpdateEpisode(entry, ep, target, libraryFolder);
                }

                MaybeSave();
            }

            // ---- additions
            await AddAsync(mapping, target, libraryFolder, plan, details, ct).ConfigureAwait(false);
            ApplyHidden(mapping, plan, listedSeries);
            SweepLeftoverCopies(mapping, target, libraryFolder);

            // ---- what is still without details waits for the next run
            var stillPending = new List<string>();
            foreach (var id in plan.PendingDetail.Concat(_doc.PendingDetail).Distinct(StringComparer.Ordinal))
            {
                if (!Guid.TryParseExact(id, "N", out var guid) || !details.ContainsKey(guid))
                {
                    stillPending.Add(id);
                }
            }

            var withHash = _doc.Entries.Where(static e => e.ContentHash is not null).Select(static e => e.PeerItemId).ToHashSet(StringComparer.Ordinal);
            _doc.PendingDetail = [.. stillPending.Where(id => !withHash.Contains(id)).Take(TvLimits.MaxEpisodesPerSeries * 5)];

            // ---- held additions are remembered by peer id; a release clears them
            if (_bypassAdditions)
            {
                _doc.TvHeldAdditions.Clear();
            }

            var alreadyHeld = _doc.TvHeldAdditions.ToHashSet(StringComparer.Ordinal);
            foreach (var id in plan.HeldAdditionPeerIds)
            {
                if (alreadyHeld.Add(id))
                {
                    _doc.TvHeldAdditions.Add(id);
                }
            }

            UpdateHeldSeries(mapping, plan);

            // ---- how many episodes the peer lists per series, for the overlap report (a series Home has hides them all)
            if (_listingComplete)
            {
                foreach (var seriesEntry in _doc.Entries.Where(e => e.MappingKey == mapping.MappingKey && e.Kind == EntryKind.Series))
                {
                    seriesEntry.PeerEpisodeCount = _episodesPerSeries.GetValueOrDefault(seriesEntry.PeerItemId);
                }
            }

            // ---- images, in their own time budget; what does not fit waits for the follow-up run (DESIGN v2.7.3)
            await ImagesAsync(mapping, target, libraryFolder, ct).ConfigureAwait(false);

            lib.LastCount = _doc.Entries.Count(e => e.MappingKey == mapping.MappingKey && e.Kind == EntryKind.Episode && IsLive(e));
            _ = existing;
        }

        // The peer tags plus the library tag of a shared library folder (K44); a legacy folder gets exactly the tags it always had.
        // Warden 11 L3: a library not visible to everyone gets no genres or studios.
        private static SanitizedSeries Facets(SanitizedSeries series, StubTarget target) => target.OmitFacets ? series with { Genres = [], Studios = [] } : series;

        private IReadOnlyList<string> TagsOf(StubTarget target) => target.ExtraTags is { Count: > 0 } extra ? [.. _tags, .. extra] : _tags;

        private static Dictionary<string, string> Tags(params (string Key, string? Tag)[] tags) =>
            tags.Where(static t => t.Tag is not null).ToDictionary(static t => t.Key, static t => t.Tag!, StringComparer.Ordinal);

        private static Dictionary<string, string> SeriesTags(SanitizedSeries s) =>
            Tags(("Primary", s.PosterTag), ("Backdrop", s.BackdropTag), ("Banner", s.BannerTag), ("Thumb", s.LandscapeTag));

        private static Dictionary<string, string> EpisodeTags(SanitizedEpisode e) => Tags(("Primary", e.ThumbTag));

        // An entry whose image tags are new or changed has images to fetch; the set is the catalog's ImagesPending, kept as a set for the run.
        private HashSet<string> PendingImages() => _pendingImages ??= new HashSet<string>(_doc.ImagesPending, StringComparer.Ordinal);

        private void SetTags(CatalogEntry entry, Dictionary<string, string> tags)
        {
            if (tags.Count == entry.ImageTags.Count && tags.All(p => entry.ImageTags.TryGetValue(p.Key, out var old) && string.Equals(old, p.Value, StringComparison.Ordinal)))
            {
                return;
            }

            entry.ImageTags = tags;
            if (tags.Count > 0 && PendingImages().Add(entry.StubKey))
            {
                _doc.ImagesPending.Add(entry.StubKey);
            }
        }

        private async Task ImagesAsync(LibraryMapping mapping, StubTarget target, string libraryFolder, CancellationToken ct)
        {
            if (_e.ImageWork is not { } work || _e.Images is not { } runner)
            {
                return;
            }

            var pending = PendingImages();
            var mine = _doc.Entries.Where(e => e.MappingKey == mapping.MappingKey).ToList();
            var byKey = new Dictionary<string, CatalogEntry>(StringComparer.Ordinal);
            foreach (var e in mine)
            {
                byKey[e.StubKey] = e;
            }

            var jobs = work.Queue(mine, pending, _e.EpisodeThumbs());
            if (jobs.Count == 0)
            {
                pending.ExceptWith(byKey.Keys); // nothing to fetch for these (no tags, or the entries are gone)
                _doc.ImagesPending = [.. pending];
                return;
            }

            // Where an image goes comes from the entry's own validated path: the series or season folder, or the folder of the episode's stub; never from a job.
            (string Folder, string BaseName)? Locate(TvImageJob job)
            {
                if (!byKey.TryGetValue(job.StubKey, out var e))
                {
                    return null;
                }

                string? relative;
                string baseName;
                if (job.Kind == TvImageKind.EpisodeThumb)
                {
                    if (e.StubRelPath is null || e.Season is null || e.Episode is null || e.StubRelPath.LastIndexOf('/') <= 0)
                    {
                        return null;
                    }

                    relative = e.StubRelPath[..e.StubRelPath.LastIndexOf('/')];
                    baseName = TvImageNames.Thumb(_e._layout, e.Season.Value, e.Episode.Value, e.EpisodeEnd is { } end && end > e.Episode ? end : null);
                }
                else
                {
                    relative = e.FolderRel;
                    baseName = TvImageNames.For(job.Kind);
                }

                // One component at a time, each confined and none a link: the folder is below the library folder and exists already (the stage creates nothing).
                var folder = libraryFolder;
                foreach (var part in relative?.Split('/') ?? [])
                {
                    if (!PathSanitizer.TryConfine(folder, part, out folder))
                    {
                        return null;
                    }
                }

                return relative is not null && folder.Length > libraryFolder.Length && Directory.Exists(folder) && !PathSanitizer.HasLinkBelow(target.StubRoot, folder) ? (folder, baseName) : null;
            }

            var run = await runner.RunAsync(target.Peer, jobs, Locate, target.StubRoot, _quota, null, ct).ConfigureAwait(false);
            Report.ImagesStored += run.Stored;
            Report.ImagesPending |= run.Pending.Count > 0;
            if (run.QuotaReached)
            {
                _alerts.Add(SyncAlert.QuotaReached);
            }

            if (run.Stored > 0)
            {
                foreach (var folder in jobs.Select(j => byKey.GetValueOrDefault(j.StubKey)).Where(static e => e is not null).Select(static e => (e!.FolderRel ?? e.StubRelPath ?? string.Empty).Split('/')[0]).Where(static f => f.Length > 0).Distinct(StringComparer.Ordinal))
                {
                    Changed(libraryFolder, folder);
                }
            }

            pending.ExceptWith(byKey.Keys);
            pending.UnionWith(run.Pending);
            _doc.ImagesPending = [.. pending];
        }

        // The held additions of a mapping, one group per series, so the admin can release one alone. A group goes when it was released, when the whole mapping was,
        // or when the peer no longer lists episodes of the series; an id that is an entry now is no longer held.
        private void UpdateHeldSeries(LibraryMapping mapping, TvPlan plan)
        {
            if (_bypassAdditions)
            {
                _doc.HeldSeries.RemoveAll(h => h.MappingKey == mapping.MappingKey);
            }

            var releasedN = (ReleaseSeries ?? new HashSet<Guid>()).Select(static g => g.ToString("N")).ToHashSet(StringComparer.Ordinal);
            _doc.HeldSeries.RemoveAll(h => h.MappingKey == mapping.MappingKey && (releasedN.Contains(h.PeerSeriesId) || (_listingComplete && !_episodesPerSeries.ContainsKey(h.PeerSeriesId))));
            foreach (var group in plan.HeldAdditionGroups ?? [])
            {
                var id = group.PeerSeriesId.ToString("N");
                var have = _doc.HeldSeries.FirstOrDefault(h => h.MappingKey == mapping.MappingKey && h.PeerSeriesId == id);
                if (have is null)
                {
                    have = new HeldSeries { Key = NewKey(), MappingKey = mapping.MappingKey, PeerSeriesId = id };
                    _doc.HeldSeries.Add(have);
                }

                have.Title = group.Title;
                have.Year = group.Year;
                have.Episodes = group.Episodes;
            }

            var present = _doc.Entries.Select(static e => e.PeerItemId).ToHashSet(StringComparer.Ordinal);
            _doc.TvHeldAdditions.RemoveAll(present.Contains);
        }

        private CatalogEntry SeriesRecord(LibraryMapping mapping, SanitizedSeries s, EntryState state, string? folder) => new()
        {
            Kind = EntryKind.Series,
            StubKey = NewKey(),
            PeerItemId = s.PeerItemId.ToString("N"),
            MappingKey = mapping.MappingKey,
            State = state,
            Title = s.Title,
            Year = s.Year,
            ProviderIds = ProviderMap(s.ProviderIds),
            PeerEtag = s.Etag,
            ContentHash = s.ContentHash.Length == 0 ? null : s.ContentHash,
            FolderRel = folder,
            DuplicateOf = state == EntryState.DuplicateLocal ? "local" : null,
            FirstSeenUtc = _now,
            LastSeenUtc = _now,
        };

        // Series and seasons exist only while an episode does: a container with no live episode goes (its fixed files, then the folder if it is empty).
        private void CleanContainers(LibraryMapping mapping, StubTarget target, string libraryFolder)
        {
            var mine = _doc.Entries.Where(e => e.MappingKey == mapping.MappingKey).ToList();
            var liveEpisodes = mine.Where(e => e.Kind == EntryKind.Episode && IsLive(e)).ToList();
            foreach (var season in mine.Where(static e => e.Kind == EntryKind.Season).ToList())
            {
                if (liveEpisodes.Any(e => e.ParentKey == season.ParentKey && e.Season == season.Season))
                {
                    continue;
                }

                if (season.FolderRel is null || _e._writer.RemoveContainerIfEmpty(target, season.FolderRel, isSeries: false, _quota))
                {
                    _doc.Entries.Remove(season);
                }
            }

            foreach (var series in mine.Where(static e => e.Kind == EntryKind.Series && IsLive(e)).ToList())
            {
                if (liveEpisodes.Any(e => e.ParentKey == series.StubKey) || _doc.Entries.Any(e => e.Kind == EntryKind.Season && e.ParentKey == series.StubKey))
                {
                    continue;
                }

                if (series.FolderRel is null || _e._writer.RemoveContainerIfEmpty(target, series.FolderRel, isSeries: true, _quota))
                {
                    _doc.Entries.Remove(series);
                    if (series.FolderRel is not null)
                    {
                        Changed(libraryFolder, series.FolderRel);
                    }
                }
            }
        }

        // 2.9.3: the extra "[shoal-...]" copies of a live series that 2.9.0 to 2.9.2 wrote every sync (CreateSeriesAsync) go. Only a folder TvDuplicateSweep
        // calls a leftover (the series' own name, only stub files, sentinels no catalog or group knows) is deleted; when any catalog cannot be read, nothing is.
        private void SweepLeftoverCopies(LibraryMapping mapping, StubTarget target, string libraryFolder)
        {
            if (!Report.Complete || !_listingComplete)
            {
                return; // Warden 19 L1: only after a complete sync so far (whole listing, every details call answered)
            }

            var live = _doc.Entries.Where(e => e.MappingKey == mapping.MappingKey && e.Kind == EntryKind.Series && e.State == EntryState.Active && e.FolderRel is not null).Select(static e => e.FolderRel!).ToList();
            List<string> candidates;
            try
            {
                candidates = TvDuplicateSweep.Candidates(libraryFolder, live);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return;
            }

            if (candidates.Count == 0)
            {
                return;
            }

            var keys = new HashSet<string>(StringComparer.Ordinal);
            var folders = new HashSet<string>(StringComparer.Ordinal);
            void Know(IEnumerable<CatalogEntry> entries)
            {
                foreach (var e in entries)
                {
                    keys.Add(e.StubKey);
                    foreach (var path in new[] { e.FolderRel, e.StubRelPath }.Concat(e.Sources.Select(static s => s.StubRelPath)))
                    {
                        if (path is { Length: > 0 })
                        {
                            folders.Add(PathSanitizer.CollisionKey(path.Split('/')[0]));
                        }
                    }
                }
            }

            // Warden 19 M1: every catalog must be whole. Our own read from the .bak or with dropped entries, or another friend's catalog missing, unreadable,
            // read from the .bak or with dropped entries, means some stubs are unknown: nothing is deleted this time.
            if (_doc.LoadedWithLoss)
            {
                return;
            }

            Know(_doc.Entries);
            foreach (var other in _state.Home.Peers.Where(p => p.PeerKey != _peer.PeerKey))
            {
                if (_d.Store.Stamp(other.PeerKey) is null)
                {
                    if (other.Libraries.Count > 0)
                    {
                        return; // a friend with libraries and no catalog file: lost or never synced, its stubs are unknown
                    }

                    continue;
                }

                var load = _d.Store.Load(other.PeerKey);
                if (load.Unreadable || load.Dropped > 0 || load.Document.LoadedWithLoss)
                {
                    return;
                }

                Know(load.Document.Entries);
            }

            if (_d.Groups is { } groups)
            {
                foreach (var copy in groups.All().SelectMany(static g => g.Copies).Concat(groups.RetiredCopies()))
                {
                    keys.Add(copy.StubKey);
                    if (copy.FolderRel.Length > 0)
                    {
                        folders.Add(PathSanitizer.CollisionKey(copy.FolderRel.Split('/')[0]));
                    }
                }
            }

            var removed = 0;
            foreach (var name in candidates)
            {
                if (folders.Contains(PathSanitizer.CollisionKey(name)) || TvStubWriter.Confine(target, name) is not { } path)
                {
                    continue;
                }

                try
                {
                    if (!TvDuplicateSweep.IsLeftover(path, keys, out var bytes))
                    {
                        continue;
                    }

                    Directory.Delete(path, recursive: true);
                    _quota.Add(-bytes);
                    Changed(libraryFolder, name);
                    removed++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _d.Logger.LogWarning("A leftover copy of a TV series folder could not be removed ({Error}); the next sync tries again", ex.GetType().Name);
                }
            }

            if (removed > 0)
            {
                _d.Logger.LogInformation("Removed {Count} leftover copies of TV series folders for peer {Peer}", removed, Redact.Id(_peer.PeerKey));
            }
        }

        // K43: the hidden sources of a series another server shows. The series entry links to that server's series stub (DuplicateOf); an episode overlapping
        // one of that server's is kept as a record with no stub (DuplicateOf names that server's episode), so a play can fail over to it. Records are re-planned
        // every run: a record the plan no longer wants goes (no file is touched), and a hidden-only series with no record left goes too.
        private void ApplyHidden(LibraryMapping mapping, TvPlan plan, List<SanitizedSeries> listedSeries)
        {
            var owners = plan.PeerOwnedSeries ?? new Dictionary<string, string>(StringComparer.Ordinal);
            var mine = _doc.Entries.Where(e => e.MappingKey == mapping.MappingKey).ToList();
            var seriesById = mine.Where(static e => e.Kind == EntryKind.Series).GroupBy(static e => e.PeerItemId, StringComparer.Ordinal).ToDictionary(static g => g.Key, static g => g.First(), StringComparer.Ordinal);
            foreach (var series in seriesById.Values)
            {
                var owner = owners.GetValueOrDefault(series.PeerItemId);
                if (series.State == EntryState.DuplicateLocal && owner is not null && string.Equals(series.DuplicateOf, "local", StringComparison.Ordinal) && !plan.DuplicateLocalSeriesKeys.Contains(series.PeerItemId))
                {
                    series.State = EntryState.DuplicatePeer; // hidden whole by an older build because another server had it: now a group member
                }

                if (series.State is EntryState.Active or EntryState.HeldRemoval or EntryState.DuplicatePeer)
                {
                    series.DuplicateOf = owner;
                }
            }

            var live = mine.Where(static e => e.Kind == EntryKind.Episode && IsLive(e)).Select(static e => e.PeerItemId).ToHashSet(StringComparer.Ordinal);
            var existingHidden = mine.Where(static e => e.Kind == EntryKind.Episode && e.State == EntryState.DuplicatePeer).GroupBy(static e => e.PeerItemId, StringComparer.Ordinal).ToDictionary(static g => g.Key, static g => g.First(), StringComparer.Ordinal);
            var wanted = new HashSet<string>(StringComparer.Ordinal);
            foreach (var hidden in plan.HiddenEpisodes ?? [])
            {
                var episode = hidden.Episode;
                var seriesId = episode.PeerSeriesId.ToString("N");
                var id = episode.PeerItemId.ToString("N");
                if (!owners.TryGetValue(seriesId, out var ownerKey) || live.Contains(id))
                {
                    continue; // a stub of it is still there (its removal is held): one entry per peer item
                }

                if (!seriesById.TryGetValue(seriesId, out var series))
                {
                    if (listedSeries.FirstOrDefault(s => s.PeerItemId == episode.PeerSeriesId) is not { } listed)
                    {
                        continue;
                    }

                    series = SeriesRecord(mapping, listed, EntryState.DuplicatePeer, folder: null);
                    series.DuplicateOf = ownerKey;
                    _doc.Entries.Add(series);
                    seriesById[seriesId] = series;
                    Report.Duplicates++;
                }

                if (!existingHidden.TryGetValue(id, out var entry))
                {
                    entry = new CatalogEntry { Kind = EntryKind.Episode, StubKey = NewKey(), PeerItemId = id, MappingKey = mapping.MappingKey, State = EntryState.DuplicatePeer, FirstSeenUtc = _now };
                    _doc.Entries.Add(entry);
                    existingHidden[id] = entry;
                }

                wanted.Add(id);
                entry.ParentKey = series.StubKey;
                entry.PeerSeriesId = seriesId;
                entry.Season = episode.Season;
                entry.Episode = episode.Number;
                entry.EpisodeEnd = episode.NumberEnd;
                entry.DuplicateOf = hidden.DuplicateOf;
                entry.PeerEtag = episode.Etag;
                entry.StubRelPath = null;
                entry.LastSeenUtc = _now;
                FillEpisode(entry, episode);
            }

            _doc.Entries.RemoveAll(e => e.MappingKey == mapping.MappingKey && e.Kind == EntryKind.Episode && e.State == EntryState.DuplicatePeer && !wanted.Contains(e.PeerItemId));
            var parents = _doc.Entries.Where(e => e.MappingKey == mapping.MappingKey && e.Kind is EntryKind.Episode or EntryKind.Season).Select(static e => e.ParentKey).ToHashSet(StringComparer.Ordinal);
            _doc.Entries.RemoveAll(e => e.MappingKey == mapping.MappingKey && e.Kind == EntryKind.Series && e.State == EntryState.DuplicatePeer && !parents.Contains(e.StubKey));
        }

        private void UpdateSeries(CatalogEntry entry, SanitizedSeries s, StubTarget target, string libraryFolder)
        {
            SetTags(entry, SeriesTags(s));
            if (entry.FolderRel is not null && (!string.Equals(entry.ContentHash, s.ContentHash, StringComparison.Ordinal) || entry.LabelVersion < FriendLabel.Version))
            {
                var result = _e._writer.RewriteNfo(target, entry.FolderRel + "/tvshow.nfo", _e._nfo.Series(Facets(s, target), TagsOf(target), target.FriendName), _quota);
                if (result.Outcome != StubWriteOutcome.Written)
                {
                    _alerts.Add(SyncAlert.WriteFailures);
                    return;
                }

                entry.LabelVersion = FriendLabel.Version;

                if (result.Changed)
                {
                    Report.Updated++;
                    Changed(libraryFolder, entry.FolderRel);
                }

                entry.Title = s.Title;
                entry.Year = s.Year;
                entry.ProviderIds = ProviderMap(s.ProviderIds);
                entry.ContentHash = s.ContentHash;
            }

            entry.PeerEtag = s.Etag;
            entry.LastSeenUtc = _now;
        }

        private void UpdateEpisode(CatalogEntry entry, SanitizedEpisode ep, StubTarget target, string libraryFolder)
        {
            SetTags(entry, EpisodeTags(ep));
            if (entry.StubRelPath is not null && (!string.Equals(entry.ContentHash, ep.ContentHash, StringComparison.Ordinal) || entry.LabelVersion < FriendLabel.Version))
            {
                var nfoPath = entry.StubRelPath[..^".strm".Length] + ".nfo";
                var result = _e._writer.RewriteNfo(target, nfoPath, _e._nfo.Episode(ep, TagsOf(target), target.FriendName), _quota);
                if (result.Outcome != StubWriteOutcome.Written)
                {
                    _alerts.Add(SyncAlert.WriteFailures);
                    return;
                }

                entry.LabelVersion = FriendLabel.Version;

                if (result.Changed)
                {
                    Report.Updated++;
                    Changed(libraryFolder, entry.StubRelPath.Split('/')[0]);
                }

                FillEpisode(entry, ep);
            }

            if (entry.Sources.Count == 0 || entry.Sources[0].Trickplay.Count == 0)
            {
                SyncRun.KeepTrickplay(entry, ep.Source); // the peer may have generated its thumbnails since (the recheck above)
            }

            entry.PeerEtag = ep.Etag;
            entry.LastSeenUtc = _now;
            if (entry.State == EntryState.HeldRemoval)
            {
                entry.State = EntryState.Active;
            }
        }

        private static void FillEpisode(CatalogEntry entry, SanitizedEpisode ep)
        {
            var source = ep.Source;
            entry.Title = ep.Title;
            entry.RuntimeTicks = ep.RunTimeTicks ?? source?.RunTimeTicks ?? 0;
            entry.PeerMediaSourceId = (source?.Id ?? ep.PeerItemId).ToString("N");
            entry.SizeBytes = source?.Size ?? 0;
            entry.Bitrate = source?.Bitrate ?? 0;
            entry.Container = source?.Container;
            entry.VideoCodec = source?.VideoCodec;
            entry.Width = source?.Width ?? 0;
            entry.Height = source?.Height ?? 0;
            entry.ContentHash = ep.ContentHash.Length == 0 ? null : ep.ContentHash;
            SyncRun.SyncPrimary(entry);
            SyncRun.KeepTrickplay(entry, source);
        }

        private async Task AddAsync(LibraryMapping mapping, StubTarget target, string libraryFolder, TvPlan plan, Dictionary<Guid, BaseItemDto> details, CancellationToken ct)
        {
            await Task.CompletedTask.ConfigureAwait(false);
            var taken = new HashSet<string>(
                _doc.Entries.Where(static e => e.Kind == EntryKind.Series && e.FolderRel is not null).Select(static e => PathSanitizer.CollisionKey(e.FolderRel!)),
                StringComparer.Ordinal);
            var seasonLines = plan.AddSeasons.ToDictionary(static s => (SeriesId: s.PeerSeriesId, s.Number));
            var playable = _doc.Entries.Count(IsPlayable);
            foreach (var group in plan.AddEpisodes.GroupBy(static e => e.PeerSeriesId))
            {
                ct.ThrowIfCancellationRequested();
                var peerSeriesId = group.Key.ToString("N");
                var seriesEntry = _doc.Entries.FirstOrDefault(e => e.MappingKey == mapping.MappingKey && e.Kind == EntryKind.Series && e.PeerItemId == peerSeriesId);
                if (seriesEntry is null || seriesEntry.State == EntryState.DuplicateLocal || seriesEntry.FolderRel is null)
                {
                    var planned = plan.AddSeries.FirstOrDefault(s => s.PeerItemId == group.Key);
                    if (planned is null || !details.TryGetValue(group.Key, out var seriesDto) || _e._sanitizer.Series(seriesDto) is not { } detailed)
                    {
                        continue; // without the series' details the NFO would be empty: retried next sync
                    }

                    var created = await CreateSeriesAsync(mapping, target, taken, detailed, seriesEntry).ConfigureAwait(false);
                    if (created is null)
                    {
                        if (_quota.Exhausted)
                        {
                            return;
                        }

                        continue;
                    }

                    seriesEntry = created;
                }

                foreach (var episode in group.OrderBy(static e => e.Season).ThenBy(static e => e.Number))
                {
                    if (playable >= CatalogDocument.MaxEntries)
                    {
                        _alerts.Add(SyncAlert.EntryCap); // the next load of the catalog would drop the tail and orphan its stubs (Warden 12 M2)
                        return;
                    }

                    if (!details.TryGetValue(episode.PeerItemId, out var episodeDto) || _e._sanitizer.Episode(episodeDto).Episode is not { } detailed)
                    {
                        continue;
                    }

                    var seasonEntry = EnsureSeason(mapping, target, seriesEntry, episode, seasonLines);
                    if (seasonEntry is null)
                    {
                        if (_quota.Exhausted)
                        {
                            return;
                        }

                        continue;
                    }

                    var seasonFolder = seasonEntry.FolderRel![(seriesEntry.FolderRel!.Length + 1)..];
                    var stubKey = NewKey();
                    var stem = _e._layout.EpisodeStem(detailed.Season, detailed.Number, detailed.NumberEnd);
                    var result = _e._writer.WriteEpisode(target, seriesEntry.FolderRel, seasonFolder, stem, _e._nfo.Episode(detailed, TagsOf(target), target.FriendName), stubKey, _quota);
                    if (result.Outcome == StubWriteOutcome.QuotaFull)
                    {
                        _alerts.Add(SyncAlert.QuotaReached);
                        return;
                    }

                    if (result.Outcome != StubWriteOutcome.Written)
                    {
                        _alerts.Add(SyncAlert.WriteFailures);
                        continue;
                    }

                    var entry = new CatalogEntry
                    {
                        Kind = EntryKind.Episode,
                        StubKey = stubKey,
                        PeerItemId = detailed.PeerItemId.ToString("N"),
                        MappingKey = mapping.MappingKey,
                        State = EntryState.Active,
                        ParentKey = seriesEntry.StubKey,
                        PeerSeriesId = peerSeriesId,
                        Season = detailed.Season,
                        Episode = detailed.Number,
                        EpisodeEnd = detailed.NumberEnd,
                        StubRelPath = result.RelPath,
                        PeerEtag = episode.Etag,
                        LabelVersion = FriendLabel.Version,
                        FirstSeenUtc = _now,
                        LastSeenUtc = _now,
                    };
                    FillEpisode(entry, detailed);
                    SetTags(entry, EpisodeTags(detailed));
                    _doc.Entries.Add(entry);
                    Report.Added++;
                    playable++;
                    if ((_doc.Libraries.FirstOrDefault(l => l.MappingKey == mapping.MappingKey)?.LastCount ?? 0) == 0)
                    {
                        _firstStubs = true;
                    }

                    Changed(libraryFolder, seriesEntry.FolderRel);
                    MaybeSave();
                }
            }
        }

        private async Task<CatalogEntry?> CreateSeriesAsync(LibraryMapping mapping, StubTarget target, HashSet<string> taken, SanitizedSeries detailed, CatalogEntry? promoted)
        {
            await Task.CompletedTask.ConfigureAwait(false);

            // 2.9.3: one key for every attempt, and a promoted record stays the entry. Dropping it here wrote a second entry for the same peer series; the next
            // catalog load kept the record and dropped the new entry, so every sync wrote another "[shoal-...]" copy of the series (friend report, 19 Oct).
            var stubKey = promoted?.StubKey ?? NewKey();
            for (var attempt = 0; attempt < 3; attempt++)
            {
                var scratch = new HashSet<string>(taken, StringComparer.Ordinal);
                var folderName = _e._layout.SeriesFolder(detailed, stubKey, scratch);
                var result = _e._writer.WriteSeries(target, folderName, _e._nfo.Series(Facets(detailed, target), TagsOf(target), target.FriendName), _quota);
                if (result.Outcome == StubWriteOutcome.Exists)
                {
                    taken.Add(PathSanitizer.CollisionKey(folderName)); // something unknown is on disk under that name (another server's copy): the tagged name next
                    continue;
                }

                if (result.Outcome == StubWriteOutcome.QuotaFull)
                {
                    _alerts.Add(SyncAlert.QuotaReached);
                    return null;
                }

                if (result.Outcome != StubWriteOutcome.Written)
                {
                    _alerts.Add(SyncAlert.WriteFailures);
                    return null;
                }

                taken.Add(PathSanitizer.CollisionKey(folderName));
                var entry = promoted ?? SeriesRecord(mapping, detailed, EntryState.Active, folderName);
                entry.StubKey = stubKey;
                entry.State = EntryState.Active;
                entry.DuplicateOf = null;
                entry.FolderRel = folderName;
                entry.Title = detailed.Title;
                entry.Year = detailed.Year;
                entry.ProviderIds = ProviderMap(detailed.ProviderIds);
                entry.PeerEtag = detailed.Etag;
                entry.ContentHash = detailed.ContentHash.Length == 0 ? null : detailed.ContentHash;
                entry.LabelVersion = FriendLabel.Version;
                SetTags(entry, SeriesTags(detailed));
                if (promoted is null)
                {
                    _doc.Entries.Add(entry);
                }

                return entry;
            }

            _alerts.Add(SyncAlert.WriteFailures);
            return null;
        }

        private CatalogEntry? EnsureSeason(LibraryMapping mapping, StubTarget target, CatalogEntry series, SanitizedEpisode episode, IReadOnlyDictionary<(Guid SeriesId, int Number), SanitizedSeason> lines)
        {
            var have = _doc.Entries.FirstOrDefault(e => e.Kind == EntryKind.Season && e.ParentKey == series.StubKey && e.Season == episode.Season);
            if (have is not null)
            {
                return have;
            }

            var line = lines.GetValueOrDefault((episode.PeerSeriesId, episode.Season)) ?? new SanitizedSeason { PeerSeriesId = episode.PeerSeriesId, Number = episode.Season, PeerItemId = TvPlanner.DerivedSeasonId(episode.PeerSeriesId, episode.Season) };
            var folderName = _e._layout.SeasonFolder(episode.Season);
            var result = _e._writer.WriteSeason(target, series.FolderRel!, folderName, _e._nfo.Season(line, TagsOf(target)), _quota);
            if (result.Outcome == StubWriteOutcome.QuotaFull)
            {
                _alerts.Add(SyncAlert.QuotaReached);
                return null;
            }

            if (result.Outcome != StubWriteOutcome.Written)
            {
                _alerts.Add(SyncAlert.WriteFailures);
                return null;
            }

            var entry = new CatalogEntry
            {
                Kind = EntryKind.Season,
                StubKey = NewKey(),
                PeerItemId = line.PeerItemId.ToString("N"),
                MappingKey = mapping.MappingKey,
                State = EntryState.Active,
                ParentKey = series.StubKey,
                PeerSeriesId = episode.PeerSeriesId.ToString("N"),
                Season = episode.Season,
                FolderRel = series.FolderRel + "/" + folderName,
                Title = line.Name,
                ContentHash = line.ContentHash.Length == 0 ? null : line.ContentHash,
                FirstSeenUtc = _now,
                LastSeenUtc = _now,
            };
            SetTags(entry, Tags(("Primary", line.PosterTag)));
            _doc.Entries.Add(entry);
            return entry;
        }
    }
}
