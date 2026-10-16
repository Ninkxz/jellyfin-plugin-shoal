using System;
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
using Microsoft.Extensions.Logging;

#pragma warning disable SA1600 // internal plumbing of the sync; the public surface is documented on CatalogSyncService
#pragma warning disable SA1202 // SyncPrimary and KeepTrickplay are internal for the TV engine and stay next to the code they belong with

namespace Jellyfin.Plugin.Shoal.Sync;

/// <summary>
/// One sync of one peer (DESIGN 5.2): phases A to E. Created by <see cref="CatalogSyncService"/> under the global sync lock.
/// </summary>
internal sealed class SyncRun
{
    private const int FlushEvery = 200;
    private const string KeyAlphabet = "abcdefghijklmnopqrstuvwxyz0123456789";
    private const int MaxFolderChangesForPerFolderRefresh = 50;

    private readonly CatalogSyncService.Dependencies _d;
    private readonly PeerState _peer;
    private readonly PeerEndpoint _endpoint;
    private readonly ShoalState _state;
    private readonly CatalogDocument _doc;
    private readonly SyncSettings _settings;
    private readonly SyncReport _report = new();
    private readonly DiskQuota _quota;
    private readonly DateTime _now;
    private readonly bool _bypassRemovals;
    private readonly bool _bypassAdditions;
    private readonly HashSet<string> _changedFolders = new(StringComparer.Ordinal);
    private readonly HashSet<string> _alerts = [];
    private readonly NameBudget _names;
    private bool _firstStubs;
    private bool _promoted;
    private HashSet<string> _libraryWithoutStubs = new(StringComparer.Ordinal);
    private int _sinceFlush;
    private TrickplayRecheck? _recheck;

    public SyncRun(CatalogSyncService.Dependencies dependencies, ShoalState state, PeerState peer, PeerEndpoint endpoint, CatalogDocument document, SyncSettings settings, DateTime now, bool bypassRemovals, bool bypassAdditions)
    {
        _d = dependencies;
        _state = state;
        _peer = peer;
        _endpoint = endpoint;
        _doc = document;
        _settings = settings;
        _now = now;
        _bypassRemovals = bypassRemovals;
        _bypassAdditions = bypassAdditions;
        _quota = new DiskQuota(settings.QuotaBytes ?? DiskQuota.LimitFromGigabytes(settings.DiskQuotaGbPerPeer), document.Usage.DiskBytes);
        _names = new NameBudget(document.Names, settings.NameCap);
    }

    public async Task<SyncReport> RunAsync(CancellationToken userToken, CancellationToken budgetToken)
    {
        var started = _now;
        try
        {
            var failure = await PhaseAAndBAsync(budgetToken).ConfigureAwait(false);
            return Finish(started, failure);
        }
        catch (OperationCanceledException) when (!userToken.IsCancellationRequested)
        {
            // The 15 minute budget ran out. What is done is saved; the rest happens at the next sync.
            _report.Error = "budget";
            _report.Complete = false;
            return Finish(started, null);
        }
    }

    private static string NewStubKey() => RandomNumberGenerator.GetString(KeyAlphabet, 16);

    private static string ItemKey(string mappingKey, string peerItemId) => mappingKey + "/" + peerItemId;

    private static bool IsActiveWithStub(CatalogEntry e) => e.State is EntryState.Active or EntryState.HeldRemoval;

    private static CanonicalIds IdsOf(CatalogEntry e) => MetadataSanitizer.CanonicalProviderIds(e.ProviderIds);

    private static string Fingerprint(IEnumerable<string> stubKeys) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join(',', stubKeys.OrderBy(static k => k, StringComparer.Ordinal)))))[..32];

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

    private static void FillFromListing(CatalogEntry entry, ListedMovie listed)
    {
        entry.PeerEtag = listed.Etag;
        entry.Title = listed.Title;
        entry.Year = listed.Year;
        entry.ProviderIds = ProviderMap(listed.ProviderIds);
    }

    private static void FillFromDetails(CatalogEntry entry, SanitizedMovie movie, bool alternates = false)
    {
        entry.Title = movie.Title;
        entry.Year = movie.Year;
        entry.ProviderIds = ProviderMap(movie.ProviderIds);
        entry.RuntimeTicks = movie.RunTimeTicks ?? 0;
        var source = alternates ? ChoosePrimary(entry, movie) : (movie.Sources.Count > 0 ? movie.Sources[0] : null);
        entry.PeerMediaSourceId = (source?.Id ?? movie.PeerItemId).ToString("N");
        entry.SizeBytes = source?.Size ?? 0;
        entry.Bitrate = source?.Bitrate ?? 0;
        entry.Container = source?.Container;
        entry.VideoCodec = source?.VideoCodec;
        entry.Width = source?.Width ?? 0;
        entry.Height = source?.Height ?? 0;
        if (entry.RuntimeTicks == 0 && source?.RunTimeTicks is { } ticks)
        {
            entry.RuntimeTicks = ticks;
        }

        SyncPrimary(entry);
        KeepTrickplay(entry, source);
    }

    // The source the primary stub is bound to: the one it already has when the peer still lists it, the peer's first for a new title, the best one
    // when the primary's source is gone (the stub is rebound in place; its path, key and watch state do not change).
    private static SanitizedSource? ChoosePrimary(CatalogEntry entry, SanitizedMovie movie)
    {
        Guid? current = MetadataSanitizer.TryParseId(entry.PeerMediaSourceId, out var known) ? known : null;
        var first = current ?? (movie.Examined.Count > 0 ? movie.Examined[0].Id : (Guid?)null);
        var chosen = SourceSelector.Select(movie.Examined, first);
        return chosen.Count > 0 ? chosen[0] : (movie.Sources.Count > 0 ? movie.Sources[0] : null);
    }

    // Keeps the first source of the entry equal to the entry's own fields: the primary, whose key and path never change.
    internal static void SyncPrimary(CatalogEntry entry)
    {
        if (entry.Sources.Count == 0)
        {
            entry.Sources.Add(new CatalogSource());
        }

        var primary = entry.Sources[0];
        primary.StubKey = entry.StubKey;
        primary.PeerMediaSourceId = entry.PeerMediaSourceId;
        primary.Label = string.Empty;
        primary.Container = entry.Container;
        primary.VideoCodec = entry.VideoCodec;
        primary.Width = entry.Width;
        primary.Height = entry.Height;
        primary.Bitrate = entry.Bitrate;
        primary.SizeBytes = entry.SizeBytes;
        primary.RuntimeTicks = entry.RuntimeTicks;
        primary.StubRelPath = entry.StubRelPath;
    }

    // The trickplay levels (already range-checked by the sanitizer) go to the primary source of the entry (schema 3, DESIGN v2.6.5); every extra
    // version gets its own source's levels in FillSource.
    internal static void KeepTrickplay(CatalogEntry entry, SanitizedSource? source)
    {
        if (entry.Sources.Count == 0)
        {
            entry.Sources.Add(new CatalogSource { StubKey = entry.StubKey, PeerMediaSourceId = entry.PeerMediaSourceId });
        }

        entry.Sources[0].PeerMediaSourceId = entry.PeerMediaSourceId;
        entry.Sources[0].Trickplay = ToLevels(source?.Trickplay);
    }

    private static List<TrickplayLevel> ToLevels(IReadOnlyList<SanitizedTrickplay>? levels) =>
        [.. (levels ?? []).Select(static t => new TrickplayLevel
        {
            Width = t.Width,
            Height = t.Height,
            TileWidth = t.TileWidth,
            TileHeight = t.TileHeight,
            ThumbnailCount = t.ThumbnailCount,
            IntervalMs = t.IntervalMs,
            Bandwidth = t.Bandwidth,
        })];

    private static void FillSource(CatalogSource target, SanitizedSource source, long runtimeTicks)
    {
        target.Container = source.Container;
        target.VideoCodec = source.VideoCodec;
        target.Width = source.Width ?? 0;
        target.Height = source.Height ?? 0;
        target.Bitrate = source.Bitrate ?? 0;
        target.SizeBytes = source.Size ?? 0;
        target.RuntimeTicks = source.RunTimeTicks ?? runtimeTicks;
        target.Trickplay = ToLevels(source.Trickplay);
    }

    private async Task<SyncReport?> PhaseAAndBAsync(CancellationToken ct)
    {
        // ---------- Phase A: identity, views, listing (network only, no disk writes)
        var pin = await _d.Trust.VerifyPinAsync(_endpoint, ct).ConfigureAwait(false);
        if (pin != PinOutcome.Verified)
        {
            return SyncReport.Failed(pin switch { PinOutcome.Mismatch => "identityMismatch", PinOutcome.NotPinned => "notPinned", _ => "unreachable" });
        }

        IReadOnlyList<PeerView> views;
        try
        {
            views = await _d.Reader.ViewsAsync(_endpoint, ct).ConfigureAwait(false);
        }
        catch (PeerCallException ex)
        {
            _d.Logger.LogInformation("The libraries of peer {Peer} could not be listed ({Kind})", Redact.Id(_peer.PeerKey), ex.Kind);
            return SyncReport.Failed("viewsFailed");
        }

        var libraries = new List<LibraryRun>();
        foreach (var mapping in _peer.Libraries.Where(static m => m.Import && !TvSyncEngine.IsTv(m)))
        {
            var folder = VisibilityPath(mapping);
            if (folder is null)
            {
                return SyncReport.Failed("invalidPath");
            }

            var library = new LibraryRun(mapping, folder);
            var known = _doc.Libraries.FirstOrDefault(l => l.MappingKey == mapping.MappingKey);
            var id = ResolveLibraryId(known, mapping, views);
            if (id is null)
            {
                library.Missing = true;
                _alerts.Add(nameof(SyncAlert.LibraryMissing));
            }
            else
            {
                library.PeerLibraryId = id.Value;
                if (known is null)
                {
                    _doc.Libraries.Add(new CatalogLibrary { MappingKey = mapping.MappingKey, PeerLibraryId = id.Value.ToString("N") });
                }
                else
                {
                    known.PeerLibraryId = id.Value.ToString("N");
                }
            }

            libraries.Add(library);
        }

        var total = 0;
        foreach (var library in libraries.Where(static l => !l.Missing))
        {
            library.Listing = await _d.Reader.ListAsync(_endpoint, library.PeerLibraryId, CatalogDocument.MaxEntries, ct).ConfigureAwait(false);
            total += library.Listing.TotalRecordCount;
            _report.SkippedItems += library.Listing.Skipped;
            if (library.Listing.TotalRecordCount > CatalogDocument.MaxEntries || total > CatalogDocument.MaxEntries)
            {
                _alerts.Add(nameof(SyncAlert.EntryCap));
                return SyncReport.Failed("entryCap");
            }
        }

        if (libraries.Count > 0 && libraries.All(static l => l.Missing || l.Listing is { Items.Count: 0, Complete: false }) && libraries.Any(static l => l.Listing is not null))
        {
            return SyncReport.Failed("listingFailed"); // nothing could be read at all
        }

        _report.Complete = libraries.Where(static l => !l.Missing).All(static l => l.Listing is { Complete: true }) && libraries.All(static l => !l.Missing);

        // ---------- Phase B: diff, dedup, guards (CPU only)
        await PlanAndApplyAsync(libraries, ct).ConfigureAwait(false);
        return null;
    }

    private string? VisibilityPath(LibraryMapping mapping) =>
        Visibility.VisibilityService.MappingPath(_state.StubRoot, _peer.PeerKey, mapping.Subfolder);

    private static Guid? ResolveLibraryId(CatalogLibrary? known, LibraryMapping mapping, IReadOnlyList<PeerView> views)
    {
        if (known is not null && MetadataSanitizer.TryParseId(known.PeerLibraryId, out var stored) && views.Any(v => v.Id == stored))
        {
            return stored;
        }

        var byName = views.Where(v => string.Equals(v.Name, MetadataSanitizer.DisplayName(mapping.RemoteName), StringComparison.Ordinal)
            && (v.CollectionType.Length == 0 || v.CollectionType == mapping.CollectionType)).ToList();
        return byName.Count == 1 ? byName[0].Id : null;
    }

    private async Task PlanAndApplyAsync(List<LibraryRun> libraries, CancellationToken ct)
    {
        var dedup = BuildDedup();
        var byItem = _doc.Entries.ToDictionary(static e => ItemKey(e.MappingKey, e.PeerItemId), StringComparer.Ordinal);
        var removalsAllowed = _report.Complete || libraries.Where(static l => !l.Missing).All(static l => l.Listing is { Complete: true });
        var adds = new List<PlannedAdd>();
        var duplicatesNew = new List<(LibraryRun Library, ListedMovie Listed, EntryState State, string? DuplicateOf, long LocalSize, string StubKey)>();
        var changed = new List<(CatalogEntry Entry, ListedMovie Listed)>();
        var demotions = new List<(CatalogEntry Entry, DedupVerdict Verdict)>();
        var dupUpdates = new List<(CatalogEntry Entry, DedupVerdict Verdict)>();
        var emptyHeld = new List<CatalogEntry>();
        var reevaluate = _settings.AlternateVersions && !_doc.AlternateVersionsApplied; // the switch was turned on: every title is looked at once

        foreach (var library in libraries.Where(static l => l.Listing is not null))
        {
            var listing = library.Listing!;
            var listed = listing.Items.ToDictionary(static i => i.Id.ToString("N"), StringComparer.Ordinal);
            var existing = _doc.Entries.Where(e => e.MappingKey == library.Mapping.MappingKey).ToList();

            foreach (var entry in existing)
            {
                if (!listed.TryGetValue(entry.PeerItemId, out var line))
                {
                    if (IsActiveWithStub(entry))
                    {
                        dedup.Claim(IdsOf(entry), entry.Title, entry.Year, entry.StubKey);
                    }

                    if (listing.Complete)
                    {
                        library.Gone.Add(entry);
                    }

                    continue;
                }

                entry.LastSeenUtc = _now;
                var verdict = dedup.Judge(IdsOf(entry), entry.Title, entry.Year);
                if (IsActiveWithStub(entry))
                {
                    if (verdict.State == EntryState.Active)
                    {
                        dedup.Claim(IdsOf(entry), entry.Title, entry.Year, entry.StubKey);
                        if (entry.State == EntryState.HeldRemoval)
                        {
                            entry.State = EntryState.Active; // the peer lists it again
                        }

                        if (reevaluate || !string.Equals(line.Etag, entry.PeerEtag, StringComparison.Ordinal) || TrickplayDue(entry))
                        {
                            changed.Add((entry, line));
                        }
                    }
                    else
                    {
                        demotions.Add((entry, verdict));
                    }
                }
                else if (verdict.State == EntryState.Active)
                {
                    adds.Add(new PlannedAdd(library, line, entry.StubKey, entry)); // the other copy is gone: promote
                    dedup.Claim(IdsOf(entry), entry.Title, entry.Year, entry.StubKey);
                }
                else
                {
                    dupUpdates.Add((entry, verdict));
                }
            }
        }

        foreach (var library in libraries.Where(static l => l.Listing is not null))
        {
            var listing = library.Listing!;
            var known = _doc.Entries.Where(e => e.MappingKey == library.Mapping.MappingKey).Select(static e => e.PeerItemId).ToHashSet(StringComparer.Ordinal);
            var held = _bypassAdditions ? [] : _doc.HeldAdditions.ToHashSet(StringComparer.Ordinal);
            var fresh = listing.Items.Where(i => !known.Contains(i.Id.ToString("N")) && !held.Contains(i.Id.ToString("N"))).ToList();

            // moved: a gone entry whose provider ids match a new line keeps its stub; only the peer id changes
            foreach (var gone in library.Gone.Where(static g => g.ProviderIds.Count > 0).ToList())
            {
                var keys = IdsOf(gone).DedupKeys();
                var match = fresh.FirstOrDefault(f => f.ProviderIds.DedupKeys().Intersect(keys, StringComparer.Ordinal).Any());
                if (match is null)
                {
                    continue;
                }

                fresh.Remove(match);
                library.Gone.Remove(gone);
                byItem.Remove(ItemKey(gone.MappingKey, gone.PeerItemId));
                gone.PeerItemId = match.Id.ToString("N");
                gone.LastSeenUtc = _now;
                byItem[ItemKey(gone.MappingKey, gone.PeerItemId)] = gone;
                _report.Moved++;
                changed.Add((gone, match));
            }

            foreach (var line in fresh)
            {
                var verdict = dedup.Judge(line.ProviderIds, line.Title, line.Year);
                var stubKey = NewStubKey();
                if (verdict.State == EntryState.Active)
                {
                    adds.Add(new PlannedAdd(library, line, stubKey, null));
                    dedup.Claim(line.ProviderIds, line.Title, line.Year, stubKey);
                }
                else
                {
                    duplicatesNew.Add((library, line, verdict.State, verdict.DuplicateOf, verdict.LocalSizeBytes, stubKey));
                }
            }

            // Held additions that the peer no longer lists are forgotten.
            _doc.HeldAdditions = [.. _doc.HeldAdditions.Where(id => listing.Items.Any(i => i.Id.ToString("N") == id) || !listing.Complete)];
        }

        // ---- confirm what is gone: a listing is only a snapshot, so ask the peer about each removal before acting on it
        var goneEntries = libraries.SelectMany(static l => l.Gone).ToList();
        if (goneEntries.Count > 0 && removalsAllowed)
        {
            try
            {
                var present = await _d.Reader.PresentAsync(_endpoint, [.. goneEntries.Select(static e => Guid.ParseExact(e.PeerItemId, "N"))], ct).ConfigureAwait(false);
                foreach (var entry in goneEntries.Where(e => present.Contains(Guid.ParseExact(e.PeerItemId, "N"))).ToList())
                {
                    entry.LastSeenUtc = _now;
                    libraries.ForEach(l => l.Gone.Remove(entry));
                    goneEntries.Remove(entry);
                }
            }
            catch (PeerCallException)
            {
                removalsAllowed = false;
                _report.Complete = false;
            }
        }

        var confirmedGone = removalsAllowed ? goneEntries : [];
        var activeBefore = _doc.Entries.Count(static e => e.Kind == EntryKind.Movie && IsActiveWithStub(e));
        var removalSet = confirmedGone.Where(IsActiveWithStub).Concat(demotions.Select(static d => d.Entry)).ToList();
        foreach (var library in libraries.Where(l => l.Listing is { Complete: true, Items.Count: 0 }))
        {
            var lastCount = _doc.Libraries.FirstOrDefault(l => l.MappingKey == library.Mapping.MappingKey)?.LastCount ?? 0;
            if (lastCount > 0 || _doc.Entries.Any(e => e.MappingKey == library.Mapping.MappingKey && IsActiveWithStub(e)))
            {
                emptyHeld.AddRange(confirmedGone.Where(e => e.MappingKey == library.Mapping.MappingKey && IsActiveWithStub(e)));
            }
        }

        var holdRemovals = !_bypassRemovals && (emptyHeld.Count > 0 || removalSet.Count > Math.Max(_settings.MassDeletionMinCount, CeilPercent(activeBefore, _settings.MassDeletionPercent)));
        if (holdRemovals)
        {
            holdRemovals = !TrackAndMaybeRelease(removalSet);
        }
        else if (!_bypassRemovals)
        {
            _doc.HeldTracker = null;
        }

        // The first sync of a mapping is exempt from the new-items guard, not only the first sync of the peer (a library added later fills at once).
        var guardedAdds = adds.Where(a => MappingHasCompleted(a.Library.Mapping.MappingKey)).ToList();
        var importable = guardedAdds.Count;
        var holdAdditions = !_bypassAdditions && _doc.CompletedOnce && importable > Math.Max(_settings.NewItemsMinCount, CeilPercent(activeBefore, _settings.NewItemsPercent));
        if (holdAdditions)
        {
            foreach (var add in guardedAdds.Where(static a => a.Promoted is null))
            {
                var id = add.Listed.Id.ToString("N");
                if (!_doc.HeldAdditions.Contains(id))
                {
                    _doc.HeldAdditions.Add(id);
                }
            }

            adds.RemoveAll(a => guardedAdds.Contains(a));
            _alerts.Add(nameof(SyncAlert.HeldAdditions));
        }
        else if (_bypassAdditions)
        {
            _doc.HeldAdditions.Clear();
        }

        // ---------- Phase C: details for what needs them
        var needDetails = new List<Guid>();
        needDetails.AddRange(adds.Select(static a => a.Listed.Id));
        needDetails.AddRange(duplicatesNew.Select(static d => d.Listed.Id));
        needDetails.AddRange(changed.Select(static c => c.Listed.Id));
        var details = new Dictionary<Guid, SanitizedMovie>();
        foreach (var batch in needDetails.Distinct().Chunk(PeerCatalogReader.DetailsBatchSize))
        {
            try
            {
                var (movies, skipped) = await _d.Reader.DetailsAsync(_endpoint, batch, ct).ConfigureAwait(false);
                _report.SkippedItems += skipped;
                foreach (var movie in movies)
                {
                    details[movie.PeerItemId] = FilterNames(movie);
                }
            }
            catch (PeerCallException ex)
            {
                _d.Logger.LogInformation("A details request to peer {Peer} failed ({Kind})", Redact.Id(_peer.PeerKey), ex.Kind);
                _report.Complete = false;
            }
        }

        // ---------- Phase D: apply (local disk only, apart from the images)
        await ApplyAsync(libraries, confirmedGone, holdRemovals, demotions, dupUpdates, changed, adds, duplicatesNew, details, ct).ConfigureAwait(false);
        _ = byItem;
    }

    // A peer may generate its thumbnails long after the title was synced, and the etag of the item need not change. A title without trickplay is therefore looked at
    // again about once a day, at most 50 titles per sync, through the normal details path (DESIGN v2.6.5).
    private bool TrickplayDue(CatalogEntry entry)
    {
        return _settings.TrickplayRefresh && (_recheck ??= new TrickplayRecheck(_now)).Due(entry);
    }

    // A mapping that has synced completely before, or that held titles under the old per-peer flag, is guarded; a mapping that never filled is not.
    private bool MappingHasCompleted(string mappingKey) =>
        _doc.Libraries.FirstOrDefault(l => l.MappingKey == mappingKey) is { } known && (known.CompletedOnce || (_doc.CompletedOnce && known.LastCount > 0));

    private static int CeilPercent(int count, int percent) => (int)(((long)count * percent) + 99) / 100;

    private DedupIndex BuildDedup()
    {
        var local = _d.Local.List(_state.StubRoot);
        var index = new DedupIndex(local);
        foreach (var other in _state.Home.Peers.Where(p => p.Enabled && p.PeerKey != _peer.PeerKey && _d.IsUp(p.PeerKey) && CatalogSyncService.Outranks(p, _peer)))
        {
            var load = _d.Store.Load(other.PeerKey);
            if (load.Unreadable)
            {
                continue;
            }

            foreach (var entry in load.Document.Entries.Where(static e => e.Kind == EntryKind.Movie && IsActiveWithStub(e)))
            {
                index.Claim(IdsOf(entry), entry.Title, entry.Year, entry.StubKey);
            }
        }

        return index;
    }

    private bool TrackAndMaybeRelease(List<CatalogEntry> removalSet)
    {
        var fingerprint = Fingerprint(removalSet.Select(static e => e.StubKey));
        var tracker = _doc.HeldTracker;
        if (tracker is null || !string.Equals(tracker.Fingerprint, fingerprint, StringComparison.Ordinal))
        {
            _doc.HeldTracker = new HeldRemovalTracker { FirstUtc = _now, CompleteSyncs = 1, Fingerprint = fingerprint };
            return false;
        }

        if (_report.Complete)
        {
            tracker.CompleteSyncs++;
        }

        // The same set, still gone after 3 complete syncs spanning at least 24 hours: it is not a glitch, release it.
        if (tracker.CompleteSyncs >= 3 && _now - tracker.FirstUtc >= TimeSpan.FromHours(24))
        {
            _doc.HeldTracker = null;
            return true;
        }

        return false;
    }

    private SanitizedMovie FilterNames(SanitizedMovie movie)
    {
        var genres = movie.Genres.Where(g => _names.Admit(_names.Genres, g)).ToArray();
        var studios = movie.Studios.Where(s => _names.Admit(_names.Studios, s)).ToArray();
        var actors = movie.Actors.Where(a => _names.Admit(_names.People, a.Name)).ToArray();
        if (genres.Length != movie.Genres.Count || studios.Length != movie.Studios.Count || actors.Length != movie.Actors.Count)
        {
            _alerts.Add(nameof(SyncAlert.NameCap));
        }

        return movie with { Genres = genres, Studios = studios, Actors = actors };
    }

    private async Task ApplyAsync(
        List<LibraryRun> libraries,
        List<CatalogEntry> gone,
        bool holdRemovals,
        List<(CatalogEntry Entry, DedupVerdict Verdict)> demotions,
        List<(CatalogEntry Entry, DedupVerdict Verdict)> dupUpdates,
        List<(CatalogEntry Entry, ListedMovie Listed)> changed,
        List<PlannedAdd> adds,
        List<(LibraryRun Library, ListedMovie Listed, EntryState State, string? DuplicateOf, long LocalSize, string StubKey)> duplicatesNew,
        Dictionary<Guid, SanitizedMovie> details,
        CancellationToken ct)
    {
        var libraryOf = libraries.ToDictionary(static l => l.Mapping.MappingKey, StringComparer.Ordinal);
        StubTarget TargetOf(LibraryRun library) => new(_state.StubRoot, library.Folder, _endpoint);

        // A library with no stub on disk yet (new, re-added, or all its titles held back by a better-ranked server) shows nothing until core is told: its first stubs ask for a scan.
        _libraryWithoutStubs = libraries.Select(static l => l.Mapping.MappingKey).Where(key => !_doc.Entries.Any(e => e.MappingKey == key && e.StubRelPath is not null)).ToHashSet(StringComparer.Ordinal);

        foreach (var library in libraries.Where(static l => l.Listing is not null))
        {
            Directory.CreateDirectory(library.Folder);
        }

        // The switch is off: the extra versions go (the normal removal of a file; titles and guards are untouched).
        if (!_settings.AlternateVersions)
        {
            foreach (var entry in _doc.Entries.Where(static e => e.Sources.Count > 1 && IsActiveWithStub(e) && e.StubRelPath is not null).ToList())
            {
                if (libraryOf.TryGetValue(entry.MappingKey, out var extrasLibrary) && !gone.Contains(entry))
                {
                    RemoveExtras(extrasLibrary, entry);
                }
            }
        }

        // Removals (also frees folder names for the additions below).
        foreach (var entry in gone)
        {
            if (!libraryOf.TryGetValue(entry.MappingKey, out var library))
            {
                continue;
            }

            if (!IsActiveWithStub(entry))
            {
                _doc.Entries.Remove(entry); // a duplicate had no stub; it is simply forgotten
                continue;
            }

            if (holdRemovals)
            {
                entry.State = EntryState.HeldRemoval;
                continue;
            }

            if (entry.StubRelPath is null || _d.Writer.Remove(TargetOf(library), entry.StubRelPath, _quota))
            {
                MarkFolderChanged(library, entry.StubRelPath);
                _doc.Entries.Remove(entry);
                _report.Removed++;
            }
            else
            {
                _alerts.Add(nameof(SyncAlert.WriteFailures));
            }
        }

        foreach (var (entry, verdict) in demotions)
        {
            var library = libraryOf[entry.MappingKey];
            if (holdRemovals)
            {
                entry.State = EntryState.HeldRemoval;
                continue;
            }

            if (entry.StubRelPath is null || _d.Writer.Remove(TargetOf(library), entry.StubRelPath, _quota))
            {
                MarkFolderChanged(library, entry.StubRelPath);
                entry.State = verdict.State;
                entry.DuplicateOf = verdict.DuplicateOf;
                entry.LocalSizeBytes = verdict.LocalSizeBytes;
                entry.StubRelPath = null;
                entry.ImageTags = [];
                _report.Removed++;
                _report.Duplicates++;
                _promoted = true; // the better-ranked copy is back: the stub is gone and core is asked once (one scan, as for a promotion)
            }
            else
            {
                _alerts.Add(nameof(SyncAlert.WriteFailures));
            }
        }

        foreach (var (entry, verdict) in dupUpdates)
        {
            entry.State = verdict.State;
            entry.DuplicateOf = verdict.DuplicateOf;
            entry.LocalSizeBytes = verdict.LocalSizeBytes;
        }

        // Updates: the NFO when it changed, the images whose tag changed. Never a rename.
        foreach (var (entry, line) in changed)
        {
            if (!details.TryGetValue(line.Id, out var movie))
            {
                continue; // no details this time; the etag stays old, so the next sync tries again
            }

            var library = libraryOf[entry.MappingKey];
            var stubOk = true;
            if (entry.StubRelPath is not null && IsActiveWithStub(entry))
            {
                var fetchPoster = movie.PrimaryImageTag is not null && !string.Equals(entry.ImageTags.GetValueOrDefault("Primary"), movie.PrimaryImageTag, StringComparison.Ordinal);
                var fetchBackdrop = movie.BackdropImageTag is not null && !string.Equals(entry.ImageTags.GetValueOrDefault("Backdrop"), movie.BackdropImageTag, StringComparison.Ordinal);
                var result = await _d.Writer.UpdateAsync(TargetOf(library), entry.StubRelPath, movie, fetchPoster, fetchBackdrop, _settings.ArtQuality, _quota, ct).ConfigureAwait(false);
                stubOk = result.Outcome == StubWriteOutcome.Written;
                if (stubOk)
                {
                    if (result.PosterStored && movie.PrimaryImageTag is not null)
                    {
                        entry.ImageTags["Primary"] = movie.PrimaryImageTag;
                    }

                    if (result.BackdropStored && movie.BackdropImageTag is not null)
                    {
                        entry.ImageTags["Backdrop"] = movie.BackdropImageTag;
                    }

                    _report.ImagesStored += result.Images;
                    if (result.Changed)
                    {
                        _report.Updated++;
                        MarkFolderChanged(library, entry.StubRelPath);
                    }

                    if (result.QuotaReached || _quota.Exhausted)
                    {
                        _alerts.Add(nameof(SyncAlert.QuotaReached));
                    }
                }
                else
                {
                    _alerts.Add(nameof(SyncAlert.WriteFailures));
                }
            }

            if (stubOk)
            {
                FillFromDetails(entry, movie, _settings.AlternateVersions);
                entry.PeerEtag = line.Etag;
                entry.LastSeenUtc = _now;
                if (_settings.AlternateVersions)
                {
                    ReconcileVersions(library, entry, movie);
                }
            }
        }

        // Additions (new titles and promoted duplicates).
        var used = new HashSet<string>(
            _doc.Entries.Where(static e => e.StubRelPath is not null).Select(static e => PathSanitizer.CollisionKey(e.StubRelPath!.Split('/')[0])),
            StringComparer.Ordinal);
        foreach (var add in adds)
        {
            if (!details.TryGetValue(add.Listed.Id, out var movie))
            {
                continue; // retried at the next sync
            }

            var stubKey = add.StubKey;
            StubWriteResult result = StubWriteResult.Failed;
            string folderName = string.Empty;
            for (var attempt = 0; attempt < 3; attempt++)
            {
                folderName = PathSanitizer.Unique(PathSanitizer.FolderName(movie.Title, movie.Year, movie.ProviderIds, stubKey), stubKey, used);
                result = await _d.Writer.AddAsync(TargetOf(add.Library), folderName, stubKey, movie, _settings.ArtQuality, _quota, ct).ConfigureAwait(false);
                if (result.Outcome != StubWriteOutcome.Exists)
                {
                    break;
                }

                stubKey = NewStubKey(); // something unknown is on disk under that name
            }

            if (result.Outcome == StubWriteOutcome.QuotaFull)
            {
                used.Remove(PathSanitizer.CollisionKey(folderName));
                _alerts.Add(nameof(SyncAlert.QuotaReached));
                break; // the budget is spent: no more titles this run, the rest are retried when room is made (W6 L-8)
            }

            if (result.Outcome != StubWriteOutcome.Written)
            {
                used.Remove(PathSanitizer.CollisionKey(folderName));
                _alerts.Add(nameof(SyncAlert.WriteFailures));
                continue;
            }

            var entry = add.Promoted ?? new CatalogEntry { FirstSeenUtc = _now, MappingKey = add.Library.Mapping.MappingKey };
            if (add.Promoted is not null)
            {
                _promoted = true; // a title a better-ranked server held back now has its stub
            }

            entry.StubKey = stubKey;
            entry.PeerItemId = add.Listed.Id.ToString("N");
            entry.State = EntryState.Active;
            entry.DuplicateOf = null;
            entry.LocalSizeBytes = 0;
            entry.StubRelPath = result.RelPath;
            entry.LastSeenUtc = _now;
            entry.ImageTags = [];
            if (result.PosterStored && movie.PrimaryImageTag is not null)
            {
                entry.ImageTags["Primary"] = movie.PrimaryImageTag;
            }

            if (result.BackdropStored && movie.BackdropImageTag is not null)
            {
                entry.ImageTags["Backdrop"] = movie.BackdropImageTag;
            }

            FillFromDetails(entry, movie, _settings.AlternateVersions);
            entry.PeerEtag = add.Listed.Etag;
            if (add.Promoted is null)
            {
                _doc.Entries.Add(entry);
            }

            if (_settings.AlternateVersions)
            {
                ReconcileVersions(add.Library, entry, movie);
            }

            _report.Added++;
            _report.ImagesStored += result.Images;
            MarkFolderChanged(add.Library, result.RelPath);
            if (result.QuotaReached || _quota.Exhausted)
            {
                _alerts.Add(nameof(SyncAlert.QuotaReached));
            }

            if (++_sinceFlush >= FlushEvery)
            {
                _sinceFlush = 0;
                Save();
            }
        }

        // Duplicates of Home's own copy or of a better peer are recorded without a stub.
        foreach (var (library, line, state, duplicateOf, localSize, stubKey) in duplicatesNew)
        {
            var entry = new CatalogEntry
            {
                StubKey = stubKey,
                PeerItemId = line.Id.ToString("N"),
                MappingKey = library.Mapping.MappingKey,
                State = state,
                DuplicateOf = duplicateOf,
                LocalSizeBytes = localSize,
                FirstSeenUtc = _now,
                LastSeenUtc = _now,
            };
            FillFromListing(entry, line);
            if (details.TryGetValue(line.Id, out var movie))
            {
                FillFromDetails(entry, movie);
            }
            else
            {
                entry.PeerMediaSourceId = line.Id.ToString("N");
                SyncPrimary(entry);
            }

            _doc.Entries.Add(entry);
            _report.Duplicates++;
        }

        // Entries that never changed still carry the listing's etag when it was empty before.
        foreach (var entry in _doc.Entries.Where(static e => e.PeerEtag.Length == 0))
        {
            entry.PeerEtag = string.Empty;
        }

        if (_alerts.Contains(nameof(SyncAlert.QuotaReached)) is false && _quota.Exhausted)
        {
            _alerts.Add(nameof(SyncAlert.QuotaReached));
        }
    }

    // Brings the extra versions of a title in line with what the peer lists now (DESIGN v2.6.4): the primary stays, extras the peer no longer lists
    // (or that became the primary) are deleted, new ones are written. A label is never renamed once written.
    private void ReconcileVersions(LibraryRun library, CatalogEntry entry, SanitizedMovie movie)
    {
        if (entry.StubRelPath is null || !IsActiveWithStub(entry))
        {
            return;
        }

        var target = new StubTarget(_state.StubRoot, library.Folder, _endpoint);
        Guid? primary = MetadataSanitizer.TryParseId(entry.PeerMediaSourceId, out var id) ? id : null;
        var wanted = SourceSelector.Select(movie.Examined, primary).Skip(1).ToList();
        var wantedIds = wanted.Select(static s => s.Id.ToString("N")).ToHashSet(StringComparer.Ordinal);
        foreach (var extra in entry.Sources.Skip(1).Where(e => !wantedIds.Contains(e.PeerMediaSourceId)).ToList())
        {
            if (extra.StubRelPath is null || _d.Writer.RemoveFile(target, extra.StubRelPath, _quota))
            {
                entry.Sources.Remove(extra);
                MarkFolderChanged(library, entry.StubRelPath);
                _report.Updated++;
            }
            else
            {
                _alerts.Add(nameof(SyncAlert.WriteFailures));
            }
        }

        var taken = entry.Sources.Skip(1).Select(static s => s.Label).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var source in wanted)
        {
            var sourceId = source.Id.ToString("N");
            if (entry.Sources.Skip(1).FirstOrDefault(s => s.PeerMediaSourceId == sourceId) is { } have)
            {
                FillSource(have, source, entry.RuntimeTicks);
                continue;
            }

            if (_doc.Entries.Sum(static e => e.Sources.Count) >= CatalogDocument.MaxEntries)
            {
                _alerts.Add(nameof(SyncAlert.EntryCap));
                break;
            }

            var label = SourceSelector.Label(source, taken);
            var stubKey = NewStubKey();
            var result = _d.Writer.AddExtra(target, entry.StubRelPath, label, stubKey, _quota);
            if (result.Outcome == StubWriteOutcome.QuotaFull)
            {
                _alerts.Add(nameof(SyncAlert.QuotaReached));
                break;
            }

            if (result.Outcome != StubWriteOutcome.Written)
            {
                _alerts.Add(nameof(SyncAlert.WriteFailures));
                continue;
            }

            taken.Add(label);
            var extra = new CatalogSource { StubKey = stubKey, PeerMediaSourceId = sourceId, Label = label, StubRelPath = result.RelPath };
            FillSource(extra, source, entry.RuntimeTicks);
            entry.Sources.Add(extra);
            MarkFolderChanged(library, entry.StubRelPath);
            _report.Updated++;
        }
    }

    private void RemoveExtras(LibraryRun library, CatalogEntry entry)
    {
        var target = new StubTarget(_state.StubRoot, library.Folder, _endpoint);
        foreach (var extra in entry.Sources.Skip(1).ToList())
        {
            if (extra.StubRelPath is null || _d.Writer.RemoveFile(target, extra.StubRelPath, _quota))
            {
                entry.Sources.Remove(extra);
                MarkFolderChanged(library, entry.StubRelPath);
                _report.Updated++;
            }
            else
            {
                _alerts.Add(nameof(SyncAlert.WriteFailures));
            }
        }
    }

    private void MarkFolderChanged(LibraryRun library, string? relPath)
    {
        if (_libraryWithoutStubs.Contains(library.Mapping.MappingKey))
        {
            _firstStubs = true;
        }

        _changedFolders.Add(relPath is null ? library.Folder : Path.Combine(library.Folder, relPath.Split('/')[0]));
    }

    private void Save() => _d.Store.Save(_doc);

    private SyncReport Finish(DateTime started, SyncReport? failure)
    {
        var report = failure ?? _report;
        if (failure is null)
        {
            report.HeldRemovals = _doc.Entries.Count(static e => e.State == EntryState.HeldRemoval);
            report.HeldAdditions = _doc.HeldAdditions.Count;
            if (report.HeldRemovals > 0)
            {
                _alerts.Add(nameof(SyncAlert.HeldRemovals));
            }

            if (report.SkippedItems > 0)
            {
                _alerts.Add(nameof(SyncAlert.ItemsSkipped));
            }

            report.Result = !report.Complete ? SyncResultKind.Partial : (report.HeldRemovals > 0 || report.HeldAdditions > 0 ? SyncResultKind.Held : SyncResultKind.Ok);
            report.ChangedFolders = _changedFolders.Count;
            _doc.HeldRemovals = [.. _doc.Entries.Where(static e => e.State == EntryState.HeldRemoval).Select(static e => e.StubKey)];
            _doc.Usage.DiskBytes = _quota.UsedBytes;
            _doc.CompletedOnce = true;
            _doc.AlternateVersionsApplied = _settings.AlternateVersions && report.Complete;
            ComputeReport();
        }
        else
        {
            report.Complete = false;
        }

        var alertNames = _alerts.OrderBy(static a => a, StringComparer.Ordinal).ToList();
        foreach (var name in alertNames)
        {
            if (Enum.TryParse<SyncAlert>(name, out var alert))
            {
                report.Alerts.Add(alert);
            }
        }

        _doc.LastSync = new LastSyncInfo
        {
            StartedUtc = started,
            EndedUtc = _d.Time.GetUtcNow().UtcDateTime,
            Result = report.Result,
            Added = report.Added,
            Updated = report.Updated,
            Removed = report.Removed,
            HeldRemovals = report.HeldRemovals,
            HeldAdditions = report.HeldAdditions,
            Error = report.Error,
            Alerts = alertNames,
        };

        if (failure is not null)
        {
            // A failed sync still records itself, but it never changes the entries.
            Save();
            return report;
        }

        foreach (var library in _doc.Libraries)
        {
            library.LastCount = _doc.Entries.Count(e => e.MappingKey == library.MappingKey && e.State != EntryState.Removed);
            if (report.Complete && _peer.Libraries.FirstOrDefault(m => m.MappingKey == library.MappingKey) is { } mapped && !TvSyncEngine.IsTv(mapped))
            {
                library.CompletedOnce = true;
            }
        }

        Save();
        Refresh();
        return report;
    }

    private void ComputeReport()
    {
        _doc.Report = OverlapReports.Compute(_doc, _now);
    }

    private void Refresh()
    {
        if (_changedFolders.Count == 0)
        {
            return;
        }

        if ((_firstStubs || _promoted) && _d.TryStartScan(_peer.PeerKey, _now, promotion: !_firstStubs))
        {
            // A library with no stubs yet, or titles that were held back by a better-ranked server and now have theirs: core does not see the new folders without a scan
            // (realtime monitoring is off). One scan, at most one in five minutes per server, never a loop.
            _d.Refresh.QueueScan();
        }

        if (_changedFolders.Count > MaxFolderChangesForPerFolderRefresh)
        {
            // Too many for one-by-one: tell core about each library folder (never a scan of every library).
            foreach (var library in _peer.Libraries.Where(static m => m.Import && !TvSyncEngine.IsTv(m)))
            {
                if (VisibilityPath(library) is { } folder)
                {
                    _d.Refresh.ReportChanged(folder);
                }
            }

            return;
        }

        foreach (var folder in _changedFolders)
        {
            _d.Refresh.ReportChanged(folder);
        }
    }

    /// <summary>One mapped library during a run.</summary>
    private sealed class LibraryRun
    {
        public LibraryRun(LibraryMapping mapping, string folder)
        {
            Mapping = mapping;
            Folder = folder;
        }

        public LibraryMapping Mapping { get; }

        public string Folder { get; }

        public Guid PeerLibraryId { get; set; }

        public Listing? Listing { get; set; }

        public bool Missing { get; set; }

        public List<CatalogEntry> Gone { get; } = [];
    }

    /// <summary>A title the plan wants a stub for.</summary>
    private sealed class PlannedAdd
    {
        public PlannedAdd(LibraryRun library, ListedMovie listed, string stubKey, CatalogEntry? promoted)
        {
            Library = library;
            Listed = listed;
            StubKey = stubKey;
            Promoted = promoted;
        }

        public LibraryRun Library { get; }

        public ListedMovie Listed { get; }

        public string StubKey { get; }

        public CatalogEntry? Promoted { get; }
    }
}

/// <summary>
/// Bounds the distinct people, genre and studio names kept per peer (DESIGN 5.10): a name that is already known is always admitted, a new one
/// only while there is room. The title still imports without the names that were dropped.
/// </summary>
internal sealed class NameBudget
{
    private readonly CatalogNames _names;
    private readonly int _cap;

    public NameBudget(CatalogNames names, int cap)
    {
        _names = names;
        _cap = cap;
        People = new HashSet<string>(names.People, StringComparer.OrdinalIgnoreCase);
        Genres = new HashSet<string>(names.Genres, StringComparer.OrdinalIgnoreCase);
        Studios = new HashSet<string>(names.Studios, StringComparer.OrdinalIgnoreCase);
    }

    public HashSet<string> People { get; }

    public HashSet<string> Genres { get; }

    public HashSet<string> Studios { get; }

    public bool Admit(HashSet<string> set, string name)
    {
        if (set.Contains(name))
        {
            return true;
        }

        if (set.Count >= _cap)
        {
            return false;
        }

        set.Add(name);
        var list = ReferenceEquals(set, People) ? _names.People : ReferenceEquals(set, Genres) ? _names.Genres : _names.Studios;
        list.Add(name);
        return true;
    }
}
