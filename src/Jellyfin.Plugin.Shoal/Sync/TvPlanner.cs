using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Jellyfin.Plugin.Shoal.Sync;

/// <summary>
/// The TV planner (DESIGN v2.7.2, K36): a pure function from what the peer lists and what the catalog holds to a <see cref="TvPlan"/>. It counts <b>episodes</b> for the
/// guards (series and seasons never count), applies the caps before anything is stored, resolves overlaps through <see cref="ITvLayout"/>, finds series and episodes the
/// peer re-imported (a move keeps the stub, the local id and the watch state) and hides a whole series when Home has it (series-level dedup, owner default).
/// </summary>
public sealed class TvPlanner : ITvPlanner
{
    /// <summary>The prefix of the weak <c>title|year</c> key among <see cref="SeriesKeys"/>.</summary>
    public const string TitleKeyPrefix = "title:";

    private readonly ITvLayout _layout;

    /// <summary>
    /// Initializes a new instance of the <see cref="TvPlanner"/> class.
    /// </summary>
    /// <param name="layout">The layout rules (overlap resolution and move matching).</param>
    public TvPlanner(ITvLayout layout) => _layout = layout;

    /// <summary>
    /// The peer id a season entry gets when the peer did not list the season: derived from the series id and the number, so it is stable and unique.
    /// </summary>
    /// <param name="seriesId">The peer's series id.</param>
    /// <param name="number">The season number.</param>
    /// <returns>A GUID that is the same on every run.</returns>
    public static Guid DerivedSeasonId(Guid seriesId, int number)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes("season|" + seriesId.ToString("N") + "|" + number.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        return new Guid(hash.AsSpan(0, 16));
    }

    /// <summary>
    /// The dedup keys of a series: its provider ids, then the weak <c>title|year</c> key.
    /// </summary>
    /// <param name="ids">The canonical ids.</param>
    /// <param name="title">The title.</param>
    /// <param name="year">The year, or null.</param>
    /// <returns>The keys.</returns>
    public static IReadOnlyList<string> SeriesKeys(CanonicalIds ids, string? title, int? year)
    {
        var keys = new List<string>(ids.DedupKeys());
        if (TitleKey.Weak(title, year) is { } weak)
        {
            keys.Add(TitleKeyPrefix + weak);
        }

        return keys;
    }

    /// <inheritdoc />
    public TvPlan Plan(TvPlanInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var alerts = new SortedSet<string>(StringComparer.Ordinal);
        var overCap = 0;
        var orphans = 0;
        var duplicatePeer = 0;

        // ---- series: the cap, then dedup
        var listedSeries = new List<SanitizedSeries>();
        var seenSeries = new HashSet<Guid>();
        foreach (var s in input.Series)
        {
            if (!seenSeries.Add(s.PeerItemId))
            {
                continue;
            }

            if (listedSeries.Count >= TvLimits.MaxSeriesPerPeer)
            {
                overCap++;
                continue;
            }

            listedSeries.Add(s);
        }

        var duplicateLocal = listedSeries.Where(s => SeriesKeys(s.ProviderIds, s.Title, s.Year).Any(input.LocalSeriesKeys.Contains)).Select(static s => s.PeerItemId).ToHashSet();

        // ---- K43: a series another server already shows: its episodes that overlap that server's are hidden sources; the others join the series (same library
        // folder, merged by the library's series grouping) or, in another folder, stay hidden too
        var ownedBySeries = new Dictionary<Guid, TvOwnedSeries>();
        foreach (var s in listedSeries.Where(s => !duplicateLocal.Contains(s.PeerItemId)))
        {
            foreach (var key in SeriesKeys(s.ProviderIds, s.Title, s.Year))
            {
                if (input.PeerOwned?.GetValueOrDefault(key) is { } owned)
                {
                    // 2.9.5: a library merges series by provider id only; a series matched by title and year alone would show as a second entry, so its
                    // extra episodes stay hidden sources instead of joining.
                    ownedBySeries[s.PeerItemId] = key.StartsWith(TitleKeyPrefix, StringComparison.Ordinal) ? owned with { SameFolder = false } : owned;
                    break;
                }
            }
        }

        var hidden = new List<TvHiddenEpisode>();

        // ---- episodes: orphans, overlaps, caps
        var existingSeries = input.Existing.Where(static e => e.Kind == EntryKind.Series).ToList();
        var existingEpisodes = input.Existing.Where(static e => e.Kind == EntryKind.Episode && e.State != EntryState.DuplicatePeer).ToList(); // K43: hidden sources have no stub and are re-planned each run
        var listedSeriesIds = listedSeries.Select(static s => s.PeerItemId).ToHashSet();
        var accepted = new List<SanitizedEpisode>();
        var seasonsPerPeer = 0;
        foreach (var group in input.Episodes.Where(e => listedSeriesIds.Contains(e.PeerSeriesId)).GroupBy(static e => e.PeerSeriesId))
        {
            if (duplicateLocal.Contains(group.Key))
            {
                continue;
            }

            var resolved = _layout.Resolve([.. group.DistinctBy(static e => e.PeerItemId)]);
            duplicatePeer += resolved.DuplicatePeer;
            var visibleEpisodes = resolved.Accepted;
            if (ownedBySeries.TryGetValue(group.Key, out var ownedSeries))
            {
                var visible = new List<SanitizedEpisode>();
                foreach (var episode in resolved.Accepted)
                {
                    var over = ownedSeries.Overlap(episode.Season, episode.Number, episode.NumberEnd);
                    if (over is not null || !ownedSeries.SameFolder)
                    {
                        if (hidden.Count < CatalogDocument.MaxEntries)
                        {
                            hidden.Add(new TvHiddenEpisode(episode, over?.StubKey));
                        }
                    }
                    else
                    {
                        visible.Add(episode);
                    }
                }

                visibleEpisodes = visible;
            }

            var seasons = 0;
            var inSeries = 0;
            foreach (var season in visibleEpisodes.GroupBy(static e => e.Season).OrderBy(static g => g.Key))
            {
                if (seasons >= TvLimits.MaxSeasonsPerSeries || seasonsPerPeer >= TvLimits.MaxSeasonsPerPeer)
                {
                    overCap += season.Count();
                    continue;
                }

                seasons++;
                seasonsPerPeer++;
                var inSeason = 0;
                foreach (var episode in season)
                {
                    if (inSeason >= TvLimits.MaxEpisodesPerSeason || inSeries >= TvLimits.MaxEpisodesPerSeries)
                    {
                        overCap++;
                        continue;
                    }

                    inSeason++;
                    inSeries++;
                    accepted.Add(episode);
                }
            }
        }

        orphans += input.Episodes.Count(e => !listedSeriesIds.Contains(e.PeerSeriesId));
        var room = Math.Max(0, CatalogDocument.MaxEntries - Math.Max(0, input.OtherPlayable)); // the cap is per catalog, not per mapping
        if (accepted.Count > room)
        {
            overCap += accepted.Count - room;
            accepted = [.. accepted.Take(room)];
            if (input.OtherPlayable > 0)
            {
                alerts.Add(nameof(SyncAlert.EntryCap));
            }
        }

        if (overCap > 0)
        {
            alerts.Add(nameof(SyncAlert.TvCaps));
        }

        // ---- series moves: a series the peer re-imported under a new id
        var listedById = listedSeries.ToDictionary(static s => s.PeerItemId);
        var existingSeriesById = existingSeries.Where(static e => e.State is EntryState.Active or EntryState.HeldRemoval).ToDictionary(static e => e.PeerItemId, StringComparer.Ordinal);
        var goneSeries = existingSeries.Where(e => e.State is EntryState.Active or EntryState.HeldRemoval && !listedById.ContainsKey(Guid.ParseExact(e.PeerItemId, "N"))).ToList();
        var newSeries = listedSeries.Where(s => !existingSeriesById.ContainsKey(s.PeerItemId.ToString("N")) && !duplicateLocal.Contains(s.PeerItemId)).ToList();
        var moves = new List<TvMove>();
        var movedSeries = new Dictionary<string, Guid>(StringComparer.Ordinal); // old series peer id -> new
        var movedSeriesTargets = new HashSet<Guid>();
        if (goneSeries.Count > 0 && newSeries.Count > 0)
        {
            // Lookups, not scans (Warden 12 M1): a hostile peer can re-id thousands of series at once.
            var goneByStub = goneSeries.ToDictionary(static e => e.StubKey, StringComparer.Ordinal);
            var newIds = newSeries.Select(static s => s.PeerItemId).ToHashSet();
            foreach (var move in _layout.MatchSeriesMoves(goneSeries, newSeries))
            {
                if (!goneByStub.TryGetValue(move.StubKey, out var entry) || !newIds.Contains(move.NewPeerItemId) || !movedSeriesTargets.Add(move.NewPeerItemId))
                {
                    continue;
                }

                moves.Add(move);
                movedSeries[entry.PeerItemId] = move.NewPeerItemId;
            }
        }

        var movedFrom = existingSeries.Where(e => movedSeries.ContainsKey(e.PeerItemId)).GroupBy(e => movedSeries[e.PeerItemId]).ToDictionary(static g => g.Key, static g => g.First());
        var episodesOf = existingEpisodes.Where(static e => e.State is EntryState.Active or EntryState.HeldRemoval && e.ParentKey is not null).ToLookup(static e => e.ParentKey!, StringComparer.Ordinal);

        var movedAway = movedSeries.Keys.ToHashSet(StringComparer.Ordinal);
        var movedTo = movedSeries.Values.ToHashSet();
        goneSeries = [.. goneSeries.Where(e => !movedAway.Contains(e.PeerItemId))];
        newSeries = [.. newSeries.Where(s => !movedTo.Contains(s.PeerItemId))];

        // ---- episodes: match each existing series (still there, or moved) with its listed series
        var acceptedBySeries = accepted.GroupBy(static e => e.PeerSeriesId).ToDictionary(static g => g.Key, static g => g.ToList());
        var newEpisodes = new List<SanitizedEpisode>();
        var goneEpisodes = new List<CatalogEntry>();
        var knownKeys = new HashSet<string>(StringComparer.Ordinal); // episode entries that are listed and keep their stub
        var updateKeys = new List<string>();
        var heldAdditionIds = input.HeldAdditionIds ?? new HashSet<string>(StringComparer.Ordinal);
        var existingByPeerId = existingEpisodes.GroupBy(static e => e.PeerItemId, StringComparer.Ordinal).ToDictionary(static g => g.Key, static g => g.First(), StringComparer.Ordinal);
        foreach (var series in listedSeries.Where(s => !duplicateLocal.Contains(s.PeerItemId)))
        {
            var seriesEntry = existingSeriesById.GetValueOrDefault(series.PeerItemId.ToString("N"))
                ?? movedFrom.GetValueOrDefault(series.PeerItemId);
            var listedEpisodes = acceptedBySeries.GetValueOrDefault(series.PeerItemId) ?? [];
            if (seriesEntry is null)
            {
                newEpisodes.AddRange(listedEpisodes);
                continue;
            }

            var mine = episodesOf[seriesEntry.StubKey].ToList();
            var listedIds = listedEpisodes.Select(static e => e.PeerItemId.ToString("N")).ToHashSet(StringComparer.Ordinal);
            var gone = mine.Where(e => !listedIds.Contains(e.PeerItemId)).ToList();
            var fresh = listedEpisodes.Where(e => !existingByPeerId.ContainsKey(e.PeerItemId.ToString("N"))).ToList();
            if (gone.Count > 0 && fresh.Count > 0)
            {
                var goneByStub = gone.ToDictionary(static e => e.StubKey, StringComparer.Ordinal);
                var freshById = fresh.GroupBy(static e => e.PeerItemId).ToDictionary(static g => g.Key, static g => g.First());
                var movedStubs = new HashSet<string>(StringComparer.Ordinal);
                var movedTargets = new HashSet<Guid>();
                foreach (var move in _layout.MatchEpisodeMoves(gone, fresh))
                {
                    if (!goneByStub.ContainsKey(move.StubKey) || !freshById.ContainsKey(move.NewPeerItemId) || movedStubs.Contains(move.StubKey) || movedTargets.Contains(move.NewPeerItemId))
                    {
                        continue;
                    }

                    moves.Add(move);
                    movedStubs.Add(move.StubKey);
                    movedTargets.Add(move.NewPeerItemId);
                    updateKeys.Add(move.StubKey);
                }

                if (movedStubs.Count > 0)
                {
                    gone = [.. gone.Where(e => !movedStubs.Contains(e.StubKey))];
                    fresh = [.. fresh.Where(e => !movedTargets.Contains(e.PeerItemId))];
                }
            }

            goneEpisodes.AddRange(gone);
            newEpisodes.AddRange(fresh);
            foreach (var episode in listedEpisodes)
            {
                if (existingByPeerId.TryGetValue(episode.PeerItemId.ToString("N"), out var known) && known.ParentKey == seriesEntry.StubKey)
                {
                    knownKeys.Add(known.StubKey);

                    // A renumbered episode (same peer id, other numbers) is one gone and one new: the old stub goes, a new one is written.
                    if (known.Season != episode.Season || known.Episode != episode.Number || known.EpisodeEnd != episode.NumberEnd)
                    {
                        goneEpisodes.Add(known);
                        newEpisodes.Add(episode);
                    }
                    else if (known.PeerEtag != episode.Etag || known.ContentHash is null || known.State == EntryState.HeldRemoval || known.LabelVersion < FriendLabel.Version)
                    {
                        updateKeys.Add(known.StubKey);
                    }
                }
            }
        }

        // episodes of a series that is gone
        foreach (var series in goneSeries)
        {
            goneEpisodes.AddRange(episodesOf[series.StubKey]);
        }

        // a series entry the listing no longer shows because Home now has the series itself, or whose episodes are all gone, is handled by the writer through the removal of its episodes
        foreach (var series in listedSeries)
        {
            if (existingSeriesById.GetValueOrDefault(series.PeerItemId.ToString("N")) is { } entry && !duplicateLocal.Contains(series.PeerItemId)
                && (entry.PeerEtag != series.Etag || entry.ContentHash is null || entry.LabelVersion < FriendLabel.Version))
            {
                updateKeys.Add(entry.StubKey);
            }
        }

        // a series Home has now: its episodes go (series-level dedup), like a demoted movie
        foreach (var id in duplicateLocal)
        {
            if (existingSeriesById.GetValueOrDefault(id.ToString("N")) is { } entry)
            {
                goneEpisodes.AddRange(episodesOf[entry.StubKey]);
            }
        }

        goneEpisodes = [.. goneEpisodes.DistinctBy(static e => e.StubKey)];
        updateKeys = [.. updateKeys.Distinct(StringComparer.Ordinal)];
        var updateSet = updateKeys.ToHashSet(StringComparer.Ordinal);

        // ---- previously held additions are kept apart unless released (all of them, or the series the admin released)
        var released = input.ReleasedSeries ?? new HashSet<Guid>();
        if (!input.BypassAdditions)
        {
            newSeries = [.. newSeries.Where(s => released.Contains(s.PeerItemId) || !heldAdditionIds.Contains(s.PeerItemId.ToString("N")))];
            newEpisodes = [.. newEpisodes.Where(e => released.Contains(e.PeerSeriesId) || (!heldAdditionIds.Contains(e.PeerItemId.ToString("N")) && (!heldAdditionIds.Contains(e.PeerSeriesId.ToString("N")) || existingSeriesById.ContainsKey(e.PeerSeriesId.ToString("N")))))];
        }

        // ---- guards, counting episodes
        var activeEpisodes = existingEpisodes.Count(static e => e.State is EntryState.Active or EntryState.HeldRemoval);
        var activeSeries = existingSeries.Count(static e => e.State is EntryState.Active or EntryState.HeldRemoval);
        var removeKeys = new List<string>();
        var heldRemovals = new List<string>();
        var removalsAllowed = input.Complete;
        if (removalsAllowed && goneEpisodes.Count > 0)
        {
            var emptyListing = input.Episodes.Count == 0 && activeEpisodes > 0;
            var minCount = Math.Max(1, input.Guards.MassDeletionMinCount) * TvLimits.TvFactor;
            var tooMany = goneEpisodes.Count > Math.Max(minCount, CeilPercent(activeEpisodes, input.Guards.MassDeletionPercent))
                || goneSeries.Count > Math.Max(TvLimits.GoneSeriesFloor, CeilPercent(activeSeries, input.Guards.MassDeletionPercent));
            if (!input.BypassRemovals && (emptyListing || tooMany))
            {
                // A series the admin released goes through; the others stay held.
                var releasedN = released.Select(static g => g.ToString("N")).ToHashSet(StringComparer.Ordinal);
                removeKeys.AddRange(goneEpisodes.Where(e => releasedN.Contains(e.PeerSeriesId)).Select(static e => e.StubKey));
                heldRemovals.AddRange(goneEpisodes.Where(e => !releasedN.Contains(e.PeerSeriesId)).Select(static e => e.StubKey));
                if (heldRemovals.Count > 0)
                {
                    alerts.Add(nameof(SyncAlert.HeldRemovals));
                }
            }
            else
            {
                removeKeys.AddRange(goneEpisodes.Select(static e => e.StubKey));
            }
        }

        var heldAdditions = new List<string>();
        var seriesWithNewEpisodes = newEpisodes.Select(static e => e.PeerSeriesId).ToHashSet();
        var addSeries = newSeries.Where(s => seriesWithNewEpisodes.Contains(s.PeerItemId)).ToList();
        var addEpisodes = newEpisodes;
        var heldGroups = new List<TvHeldGroup>();
        var releasedAddSeries = addSeries.Where(s => released.Contains(s.PeerItemId)).ToList();
        var releasedAddEpisodes = addEpisodes.Where(e => released.Contains(e.PeerSeriesId)).ToList();
        if (releasedAddSeries.Count + releasedAddEpisodes.Count > 0)
        {
            addSeries = [.. addSeries.Where(s => !released.Contains(s.PeerItemId))]; // they skip the guard below and join the additions after it
            addEpisodes = [.. addEpisodes.Where(e => !released.Contains(e.PeerSeriesId))];
        }

        var newSeriesCount = addSeries.Count;
        if (!input.BypassAdditions && !input.FirstSyncOfMapping)
        {
            var tooManyEpisodes = addEpisodes.Count > Math.Max(TvLimits.TvFactor * Math.Max(1, input.Guards.NewItemsMinCount), CeilPercent(activeEpisodes, input.Guards.NewItemsPercent));
            var tooManySeries = newSeriesCount > Math.Max(TvLimits.NewSeriesFloor, CeilPercent(activeSeries, input.Guards.NewItemsPercent));
            if (tooManyEpisodes || tooManySeries)
            {
                heldAdditions.AddRange(addSeries.Select(static s => s.PeerItemId.ToString("N")));
                heldAdditions.AddRange(addEpisodes.Select(static e => e.PeerItemId.ToString("N")));
                heldGroups.AddRange(addEpisodes.GroupBy(static e => e.PeerSeriesId).Select(g => new TvHeldGroup(g.Key, listedById.TryGetValue(g.Key, out var named) ? named.Title : string.Empty, listedById.TryGetValue(g.Key, out var dated) ? dated.Year : null, g.Count())));
                addSeries = [];
                addEpisodes = [];
                alerts.Add(nameof(SyncAlert.HeldAdditions));
            }
        }

        if (releasedAddSeries.Count + releasedAddEpisodes.Count > 0)
        {
            addSeries = [.. releasedAddSeries, .. addSeries];
            addEpisodes = [.. releasedAddEpisodes, .. addEpisodes];
        }

        // a renumbered episode is only added when its old stub goes this run
        var removing = removeKeys.ToHashSet(StringComparer.Ordinal);
        var blocked = addEpisodes.Where(e => existingByPeerId.TryGetValue(e.PeerItemId.ToString("N"), out var old) && !removing.Contains(old.StubKey)).Select(static e => e.PeerItemId).ToHashSet();
        if (blocked.Count > 0)
        {
            addEpisodes = [.. addEpisodes.Where(e => !blocked.Contains(e.PeerItemId))];
        }

        // ---- seasons that new episodes need
        var seasonLines = input.Seasons.GroupBy(static s => (s.PeerSeriesId, s.Number)).ToDictionary(static g => g.Key, static g => g.First());
        var addSeasons = new List<SanitizedSeason>();
        var haveSeasons = input.Existing.Where(static e => e.Kind == EntryKind.Season).Select(static e => (e.ParentKey, e.Season)).ToHashSet();
        var seriesEntryKeyById = existingSeriesById.ToDictionary(static p => p.Key, static p => p.Value.StubKey, StringComparer.Ordinal);
        foreach (var pair in addEpisodes.Select(static e => (e.PeerSeriesId, e.Season)).Distinct())
        {
            var parentKey = seriesEntryKeyById.GetValueOrDefault(pair.PeerSeriesId.ToString("N"));
            if (parentKey is not null && haveSeasons.Contains((parentKey, (int?)pair.Season)))
            {
                continue;
            }

            addSeasons.Add(seasonLines.GetValueOrDefault(pair) ?? new SanitizedSeason { PeerSeriesId = pair.PeerSeriesId, Number = pair.Season, PeerItemId = DerivedSeasonId(pair.PeerSeriesId, pair.Season) });
        }

        // ---- what needs details: new series, new episodes, changed ones; series first
        var pending = addSeries.Select(static s => s.PeerItemId.ToString("N"))
            .Concat(input.Existing.Where(e => e.Kind == EntryKind.Series && updateSet.Contains(e.StubKey)).Select(static e => e.PeerItemId))
            .Concat(addEpisodes.Select(static e => e.PeerItemId.ToString("N")))
            .Concat(input.Existing.Where(e => e.Kind == EntryKind.Episode && updateSet.Contains(e.StubKey)).Select(static e => e.PeerItemId))
            .Distinct(StringComparer.Ordinal).ToList();

        var counts = new TvCounts(0, 0, duplicatePeer, orphans, overCap);
        return new TvPlan(
            addSeries,
            addSeasons,
            addEpisodes,
            updateKeys,
            removeKeys,
            moves,
            heldRemovals,
            heldAdditions,
            [.. duplicateLocal.Select(static id => id.ToString("N"))],
            pending,
            counts,
            [.. alerts],
            heldGroups,
            hidden,
            ownedBySeries.ToDictionary(static p => p.Key.ToString("N"), static p => p.Value.OwnerSeriesStubKey, StringComparer.Ordinal));
    }

    private static int CeilPercent(int count, int percent) => (int)(((long)count * percent) + 99) / 100;
}
