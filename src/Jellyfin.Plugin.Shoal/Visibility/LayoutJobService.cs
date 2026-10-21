using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.Authorization;
using Jellyfin.Plugin.Shoal.State;
using Jellyfin.Plugin.Shoal.Storage;
using Jellyfin.Plugin.Shoal.Sync;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LayoutLibrary = Jellyfin.Plugin.Shoal.State.ShoalLibrary;

namespace Jellyfin.Plugin.Shoal.Visibility;

/// <summary>
/// Why a layout job was not started.
/// </summary>
public enum LayoutJobError
{
    /// <summary>The job started.</summary>
    None,

    /// <summary>The state is in recovery mode.</summary>
    RecoveryMode,

    /// <summary>No such connection or library, or the library has another type.</summary>
    NotFound,

    /// <summary>The request is not valid.</summary>
    Invalid,

    /// <summary>A sync is running; switching waits until it ends.</summary>
    Syncing,

    /// <summary>Another layout change or job is running, or an unfinished job is waiting to resume.</summary>
    Busy,

    /// <summary>The target library could not be created.</summary>
    CreateFailed,

    /// <summary>The plan is not the one the admin previewed (Warden 10 L3): preview again.</summary>
    PlanChanged,

    /// <summary>The preset needs the admin to pick a main library for a type (Combine into my libraries with none or several candidates).</summary>
    TargetRequired,
}

/// <summary>
/// One requested move: a connection's titles of one type go into an existing library, or into a new one.
/// </summary>
/// <param name="PeerKey">The connection.</param>
/// <param name="Type"><c>movies</c> or <c>tvshows</c>.</param>
/// <param name="ToLibKey">The target library, or null for a new one.</param>
/// <param name="NewLibrary">The new library to create when <paramref name="ToLibKey"/> is null.</param>
public sealed record MoveRequest(string PeerKey, string Type, string? ToLibKey, LibraryCreateRequest? NewLibrary = null);

/// <summary>
/// What a layout job looks like to the admin page (<c>GET Shoal/Admin/Layouts/Job</c>): no keys of servers, only counts and the accounts that lost access.
/// </summary>
/// <param name="Running">Whether a job runs now.</param>
/// <param name="Phase">The phase of the running or last job, or null when there never was one.</param>
/// <param name="Moves">How many moves the job has.</param>
/// <param name="MovesDone">How many are done.</param>
/// <param name="Steps">How many folder steps the job has.</param>
/// <param name="StepsDone">How many have run.</param>
/// <param name="LostAccess">The accounts that could open a source library but cannot open the target.</param>
/// <param name="Error">A short fixed word for the error that stopped the job, or null.</param>
/// <param name="GainedAccess">The accounts that can open a moved connection's titles in the target but could not in its sources (Warden 10 M1: reported, never blocked), or null.</param>
/// <param name="GainedRestricted">Those of them that are restricted profiles, or null.</param>
/// <param name="CanResume">Warden 11 L6: an unfinished job waits and does not run: <c>POST Layouts/Job/Resume</c> applies (syncs wait meanwhile).</param>
/// <param name="CanAbandon">The same job can be given up with <c>POST Layouts/Job/Abandon</c>.</param>
public sealed record LayoutJobStatus(bool Running, LayoutJobPhase? Phase, int Moves, int MovesDone, int Steps, int StepsDone, IReadOnlyList<Guid> LostAccess, string? Error, IReadOnlyList<Guid>? GainedAccess = null, IReadOnlyList<Guid>? GainedRestricted = null, bool CanResume = false, bool CanAbandon = false);

/// <summary>
/// The layout move job (PLAN v2.9 K44): moves connections between libraries, locally and at once, one job at a time, journaled and resumable.
/// <list type="number">
/// <item>Refused while a sync runs (the job then holds the sync lock, so none starts) or another layout change runs.</item>
/// <item>The journal is written first, with every step planned; then the state says each connection feeds its target (a new library is created first, with the
/// intersection of its sources' access: fail closed).</item>
/// <item>One connection at a time: a sole-member title folder is renamed into the target folder and its NFOs get the target's library tag; a group copy another
/// member still needs in the old library is deleted (only this connection's recorded folder) and its title is added again at the next sync. Every step checks the
/// connection's catalog still records the folder, so nothing that is not Shoal's own is ever moved or deleted.</item>
/// <item>The catalog and the groups follow (folder names, library keys); a targeted scan of each affected library folder is reported.</item>
/// <item>Libraries the job left without a connection are removed (a Shoal library of v2.9, or a legacy per-friend library whose connection moved out), and the
/// access is reconciled.</item>
/// </list>
/// Item ids change for moved titles; watched marks follow only for titles with provider ids (Jellyfin's rule).
/// </summary>
public sealed class LayoutJobService : IHostedService, IDisposable
{
    /// <summary>The words of the admins' message when a move stops (Warden 11 L6; no names or keys).</summary>
    public const string FailedNotice = "A library move stopped before it finished. Friends' titles are not synced until it is resumed or abandoned: open Shoal, Library layouts.";

    private const int CheckpointEvery = 25;

    private readonly CancellationTokenSource _stopping = new();
    private readonly IStateStore _state;
    private readonly CatalogStore _catalogs;
    private readonly LibraryLayoutService _layout;
    private readonly IAccountSource _accounts;
    private readonly ILibraryRefresh _refresh;
    private readonly ISyncHold? _syncs;
    private readonly GroupBook? _groups;
    private readonly ShoalLibraryService? _legacyLibraries;
    private readonly LayoutJournalStore _store;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly object _statusLock = new();
    private LayoutJournal? _last;
    private Task _running = Task.CompletedTask;
    private volatile bool _isRunning;
    private Task _retry = Task.CompletedTask;

    /// <summary>
    /// Initializes a new instance of the <see cref="LayoutJobService"/> class.
    /// </summary>
    /// <param name="state">The state store.</param>
    /// <param name="catalogs">The catalogs.</param>
    /// <param name="layout">The library layout (creates and removes libraries, holds the layout lock).</param>
    /// <param name="accounts">Jellyfin's accounts (who loses access).</param>
    /// <param name="refresh">The library refresh (targeted scans).</param>
    /// <param name="paths">The data folder (the journal).</param>
    /// <param name="logger">The logger.</param>
    /// <param name="syncs">The sync lock, or null.</param>
    /// <param name="groups">The title groups, or null.</param>
    /// <param name="legacyLibraries">Removes the Jellyfin library of a legacy per-friend library, or null.</param>
    public LayoutJobService(IStateStore state, CatalogStore catalogs, LibraryLayoutService layout, IAccountSource accounts, ILibraryRefresh refresh, ShoalPaths paths, ILogger<LayoutJobService> logger, ISyncHold? syncs = null, GroupBook? groups = null, ShoalLibraryService? legacyLibraries = null)
        : this(state, catalogs, layout, accounts, refresh, paths, TimeProvider.System, logger, syncs, groups, legacyLibraries)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="LayoutJobService"/> class (tests: any logger and clock).
    /// </summary>
    /// <param name="state">The state store.</param>
    /// <param name="catalogs">The catalogs.</param>
    /// <param name="layout">The library layout.</param>
    /// <param name="accounts">Jellyfin's accounts.</param>
    /// <param name="refresh">The library refresh.</param>
    /// <param name="paths">The data folder.</param>
    /// <param name="time">The clock.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="syncs">The sync lock, or null.</param>
    /// <param name="groups">The title groups, or null.</param>
    /// <param name="legacyLibraries">Removes the Jellyfin library of a legacy library, or null.</param>
    internal LayoutJobService(IStateStore state, CatalogStore catalogs, LibraryLayoutService layout, IAccountSource accounts, ILibraryRefresh refresh, ShoalPaths paths, TimeProvider time, ILogger logger, ISyncHold? syncs = null, GroupBook? groups = null, ShoalLibraryService? legacyLibraries = null)
    {
        _state = state;
        _catalogs = catalogs;
        _layout = layout;
        _accounts = accounts;
        _refresh = refresh;
        _syncs = syncs;
        _groups = groups;
        _legacyLibraries = legacyLibraries;
        _store = new LayoutJournalStore(paths, logger);
        _time = time;
        _logger = logger;
    }

    /// <summary>Gets or sets what asks for a sync of a connection after its move (a deleted group copy is added again by its next sync), or null.</summary>
    public Action<string>? RequestSync { get; set; }

    /// <summary>Gets or sets what tells the admins that a move stopped (Warden 11 L6: syncs wait until it is resumed or abandoned), or null.</summary>
    public Jellyfin.Plugin.Shoal.Pairing.IAdminNotifier? Notifier { get; set; }

    /// <summary>Gets or sets what locks the options of Shoal's libraries again right after a move (Pilot 15 R6), or null.</summary>
    public LibraryOptionsGuard? Options { get; set; }

    /// <summary>
    /// Gets a value indicating whether an unfinished job waits (planned, running, cleaning up or failed, not abandoned). While it does, no sync starts
    /// (Warden 10 M2: a sync would write the same titles into the target the job is still moving into).
    /// </summary>
    public bool Unfinished
    {
        get
        {
            if (_isRunning)
            {
                return true;
            }

            lock (_statusLock)
            {
                return IsUnfinished(_last ?? _store.Load());
            }
        }
    }

    /// <summary>Gets or sets the copy writer (one stub per library per group), run when the moves are done; null for none.</summary>
    internal GroupCopyWriter? Copies { get; set; }

    /// <summary>Gets or sets a hook the tests use to stop a job after a number of steps, as a crash would (nothing more is written).</summary>
    internal Func<int, bool>? CrashAfterStep { get; set; }

    /// <summary>Gets the task of the running (or last) job; tests await it.</summary>
    internal Task Running => _running;

    /// <summary>Gets or sets how often the startup resume is tried again while a sync or a layout change holds a lock (tests shorten it).</summary>
    internal TimeSpan ResumeRetry { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The status for the admin page.
    /// </summary>
    /// <returns>The status.</returns>
    public LayoutJobStatus Status()
    {
        LayoutJournal? journal;
        lock (_statusLock)
        {
            journal = _last ?? _store.Load();
        }

        if (journal is null)
        {
            return new LayoutJobStatus(_isRunning, null, 0, 0, 0, 0, [], null);
        }

        return new LayoutJobStatus(
            _isRunning,
            journal.Phase,
            journal.Moves.Count,
            journal.Moves.Count(static m => m.Done),
            journal.Moves.Sum(static m => m.Ops.Count),
            journal.Moves.Sum(static m => Math.Min(m.NextOp, m.Ops.Count)),
            [.. journal.LostAccess],
            journal.Error,
            [.. journal.GainedAccess ?? []],
            [.. journal.GainedRestricted ?? []],
            IsUnfinished(journal) && !_isRunning,
            IsUnfinished(journal) && !_isRunning);
    }

    /// <summary>
    /// Starts a job. The job is planned and journaled before this returns; it then runs in the background.
    /// </summary>
    /// <param name="moves">The moves, run in this order.</param>
    /// <param name="adminUserId">The admin who asked.</param>
    /// <param name="kind">What started it (<c>move</c> or <c>preset</c>), for the journal.</param>
    /// <param name="cancellationToken">Cancellation (planning only).</param>
    /// <returns>Why it did not start, or <see cref="LayoutJobError.None"/>.</returns>
    public async Task<LayoutJobError> StartAsync(IReadOnlyList<MoveRequest> moves, Guid adminUserId, string kind = "move", CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(moves);
        if (_state.Status != StateStatus.Ready)
        {
            return LayoutJobError.RecoveryMode;
        }

        if (moves.Count == 0 || moves.Count > _state.Current.Home.Peers.Count * 2 || moves.Select(static m => (m.PeerKey, m.Type)).Distinct().Count() != moves.Count)
        {
            return LayoutJobError.Invalid;
        }

        if (IsUnfinished(_store.Load()))
        {
            return LayoutJobError.Busy; // an unfinished job resumes first (at the next start, or POST Layouts/Job/Resume), or the admin abandons it
        }

        var layoutHeld = _layout.Lock.TryEnter(job: true);
        if (layoutHeld is null)
        {
            return LayoutJobError.Busy;
        }

        var syncHeld = _syncs is null ? NoHold.Instance : _syncs.TryHold();
        if (syncHeld is null)
        {
            layoutHeld.Dispose();
            return LayoutJobError.Syncing;
        }

        LayoutJournal journal;
        try
        {
            var planned = await PlanAsync(moves, adminUserId, kind, cancellationToken).ConfigureAwait(false);
            if (planned.Error != LayoutJobError.None)
            {
                syncHeld.Dispose();
                layoutHeld.Dispose();
                return planned.Error;
            }

            journal = planned.Journal!;
        }
        catch
        {
            syncHeld.Dispose();
            layoutHeld.Dispose();
            throw;
        }

        Run(journal, layoutHeld, syncHeld);
        return LayoutJobError.None;
    }

    /// <summary>
    /// Resumes an unfinished job: every step is repeated from the last checkpoint.
    /// </summary>
    /// <returns>True when a job was resumed.</returns>
    public bool Resume() => TryResume() == LayoutJobError.None;

    /// <summary>
    /// Resumes an unfinished job (at startup, while the startup retry runs, or by the admin's <c>POST Layouts/Job/Resume</c>).
    /// </summary>
    /// <returns><see cref="LayoutJobError.None"/> when it runs again; <see cref="LayoutJobError.NotFound"/> when no unfinished job waits; Busy, Syncing or RecoveryMode.</returns>
    public LayoutJobError TryResume()
    {
        if (_state.Status != StateStatus.Ready)
        {
            return LayoutJobError.RecoveryMode;
        }

        var journal = _store.Load();
        if (!IsUnfinished(journal))
        {
            return LayoutJobError.NotFound;
        }

        var layoutHeld = _layout.Lock.TryEnter(job: true);
        if (layoutHeld is null)
        {
            return LayoutJobError.Busy;
        }

        var syncHeld = _syncs is null ? NoHold.Instance : _syncs.TryHold();
        if (syncHeld is null)
        {
            layoutHeld.Dispose();
            return LayoutJobError.Syncing;
        }

        _logger.LogInformation("Shoal resumes an unfinished library move");
        journal!.Error = null;
        lock (_statusLock)
        {
            _last = journal;
        }

        Run(journal, layoutHeld, syncHeld);
        return LayoutJobError.None;
    }

    /// <summary>
    /// Gives up an unfinished job that does not run now (the admin's <c>POST Layouts/Job/Abandon</c>). Nothing on disk is undone: the connections stay where the
    /// job's state switch put them, folders that were not moved yet stay where they are, and each moved connection is synced again so its titles are written
    /// into the library it feeds now. New layout changes and syncs may run again.
    /// </summary>
    /// <returns><see cref="LayoutJobError.None"/>, <see cref="LayoutJobError.NotFound"/> (nothing unfinished) or Busy (it runs now).</returns>
    public LayoutJobError Abandon()
    {
        var layoutHeld = _layout.Lock.TryEnter(job: true);
        if (layoutHeld is null || _isRunning)
        {
            layoutHeld?.Dispose();
            return LayoutJobError.Busy;
        }

        using (layoutHeld)
        {
            var journal = _store.Load();
            if (!IsUnfinished(journal))
            {
                return LayoutJobError.NotFound;
            }

            journal!.Phase = LayoutJobPhase.Abandoned;
            journal.Error = "abandoned";
            journal.EndedUtc = _time.GetUtcNow().UtcDateTime;
            _store.Save(journal);
            lock (_statusLock)
            {
                _last = journal;
            }

            _logger.LogWarning("The admin abandoned an unfinished library move ({Moves} connections)", journal.Moves.Count);
            foreach (var move in journal.Moves.Where(static m => m.StateApplied))
            {
                RequestSync?.Invoke(move.PeerKey);
            }

            return LayoutJobError.None;
        }
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        // A sync may hold the lock at startup: keep trying until the job runs (no new sync starts while it waits) or nothing waits any more.
        _retry = Task.Run(() => ResumeAtStartAsync(_stopping.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _stopping.CancelAsync().ConfigureAwait(false);
        try
        {
            await _retry.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
    }

    /// <inheritdoc />
    public void Dispose() => _stopping.Dispose();

    /// <summary>
    /// The access of a library that combines several sources: what all of them admit (fail closed). Everyone stays Everyone only when every source is Everyone; then
    /// restricted profiles are in only when every source lets them in. Otherwise "Only selected users" with the accounts every source admits today.
    /// </summary>
    /// <param name="sources">The sources' access.</param>
    /// <param name="accounts">The accounts.</param>
    /// <param name="shareUsers">The sharing accounts (never admitted).</param>
    /// <param name="now">When the access is set.</param>
    /// <returns>The access.</returns>
    internal static LibraryAccess Intersection(IReadOnlyList<LibraryAccess> sources, IReadOnlyList<AccountInfo> accounts, IReadOnlySet<Guid> shareUsers, DateTime now)
    {
        if (sources.Count > 0 && sources.All(static s => s.Mode == AccessMode.Everyone))
        {
            return new LibraryAccess { Mode = AccessMode.Everyone, IncludeRestricted = sources.All(static s => s.IncludeRestricted), ChangedUtc = now };
        }

        var admitted = accounts.Where(a => sources.Count > 0 && sources.All(s => AccessPolicy.Admitted(a.ToCallerUser(), s, shareUsers))).Select(static a => a.Id).ToList();
        return new LibraryAccess { Mode = AccessMode.Selected, SelectedUserIds = admitted, ChangedUtc = now };
    }

    private async Task ResumeAtStartAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            LayoutJobError outcome;
            try
            {
                outcome = TryResume();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _logger.LogWarning("An unfinished library move could not be resumed ({Error})", ex.GetType().Name);
                return;
            }

            if (outcome is LayoutJobError.None or LayoutJobError.NotFound)
            {
                return;
            }

            try
            {
                await Task.Delay(ResumeRetry, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task NotifyFailedAsync()
    {
        if (Notifier is not { } notifier)
        {
            return;
        }

        try
        {
            await notifier.NotifyAdminsAsync("Shoal", FailedNotice).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogDebug("The library move message could not be shown ({Error})", ex.GetType().Name);
        }
    }

    // Pilot 14 LAY-SCAN: a library the job created, or filled, has never been scanned and a folder change report does not reach it: scan each one (every Shoal
    // placement library, and a main library only when it was a move target), never every library of the server.
    // Pilot 15 R2-MOVE: every library a move filled (a new one or one that existed) or emptied is scanned, found by its folder (a stored id can be missing or
    // stale); then the options of Shoal's libraries are locked again.
    private void ScanTouched(LayoutJournal journal)
    {
        var state = _state.Current;
        var touched = journal.Moves.SelectMany(static m => m.Sources.Select(static s => s.LibKey).Append(m.ToLibKey)).Concat(journal.Created).ToHashSet(StringComparer.Ordinal);
        var folders = state.Home.Libraries.Where(l => touched.Contains(l.LibKey)).Select(l => LibraryFolders.PathOf(state.StubRoot, l)).OfType<string>().Distinct(StringComparer.Ordinal).ToList();
        _refresh.ScanLibrariesAt(folders);
        Options?.Enforce();
    }

    private static bool IsUnfinished(LayoutJournal? journal) => journal is not null && journal.Phase is not (LayoutJobPhase.Done or LayoutJobPhase.Abandoned);

    private static string NewJobId() => RandomNumberGenerator.GetString("abcdefghijklmnopqrstuvwxyz0123456789", 16);

    // The libraries a connection's titles of a type are in now: the shared one it feeds, or the legacy library of each of its mappings of that type.
    private static List<LayoutLibrary> SourcesOf(ShoalState state, PeerState peer, string type)
    {
        if (LibraryFolders.FedBy(state, peer.PeerKey, type) is { } fed)
        {
            return [fed];
        }

        return [.. peer.Libraries.Where(m => m.CollectionType == type).Select(m => LibraryFolders.LegacyOf(state, peer.PeerKey, m)).OfType<LayoutLibrary>()];
    }

    private static string Top(string? relPath) => relPath?.Split('/')[0] ?? string.Empty;

    private static string Unique(string name, string stubKey, ISet<string> used)
    {
        var key = stubKey.Length >= 8 && stubKey.All(char.IsAsciiLetterOrDigit) ? stubKey : "move0000";
        return PathSanitizer.Unique(name, key, used);
    }

    private async Task<(LayoutJobError Error, LayoutJournal? Journal)> PlanAsync(IReadOnlyList<MoveRequest> requests, Guid adminUserId, string kind, CancellationToken cancellationToken)
    {
        var state = _state.Current;
        if (!Path.IsPathRooted(state.StubRoot))
        {
            return (LayoutJobError.Invalid, null);
        }

        var accounts = _accounts.List();
        var shareUsers = VisibilityService.ShareUserIds(state);
        var now = _time.GetUtcNow().UtcDateTime;
        var journal = new LayoutJournal { JobId = NewJobId(), Kind = kind, Phase = LayoutJobPhase.Planned, StartedUtc = now, AdminUserId = adminUserId };
        var lost = new HashSet<Guid>();
        var gained = new List<AccountInfo>();

        // Check every request before anything is created.
        foreach (var request in requests)
        {
            var peer = state.Home.Peers.FirstOrDefault(p => p.PeerKey == request.PeerKey);
            if (peer is null || request.Type is not ("movies" or "tvshows") || !peer.Libraries.Any(m => m.CollectionType == request.Type))
            {
                return (LayoutJobError.NotFound, null);
            }

            if (request.ToLibKey is { } to)
            {
                var target = state.Home.Libraries.FirstOrDefault(l => l.LibKey == to);
                if (target is null || target.IsLegacy || target.Type != request.Type)
                {
                    return (LayoutJobError.NotFound, null);
                }
            }
            else if (request.NewLibrary is null || request.NewLibrary.Type != request.Type)
            {
                return (LayoutJobError.Invalid, null);
            }
        }

        // New libraries: one per distinct request (the preset groups connections by passing the same key after the first creates it).
        var created = new Dictionary<LibraryCreateRequest, string>();
        foreach (var request in requests.Where(static r => r.ToLibKey is null))
        {
            if (created.ContainsKey(request.NewLibrary!))
            {
                continue;
            }

            var sameTarget = requests.Where(r => r.ToLibKey is null && r.NewLibrary == request.NewLibrary).ToList();
            var sources = sameTarget.SelectMany(r => SourcesOf(state, state.Home.Peers.First(p => p.PeerKey == r.PeerKey), r.Type)).Select(l => LibraryFolders.AccessOf(state, l)).ToList();
            var access = Intersection(sources, accounts, shareUsers, now);
            access.ChangedByUserId = adminUserId;
            var result = await _layout.CreateLockedAsync(request.NewLibrary!, adminUserId, [], cancellationToken, access).ConfigureAwait(false);
            if (!result.Ok)
            {
                foreach (var key in created.Values)
                {
                    await _layout.DeleteLockedAsync(key, CancellationToken.None).ConfigureAwait(false);
                }

                return (LayoutJobError.CreateFailed, null);
            }

            created[request.NewLibrary!] = result.LibKey!;
            journal.Created.Add(result.LibKey!);
        }

        state = _state.Current;
        var jellyfinLibraries = _layout.Libraries.List();
        var accountsNow = _accounts.List(); // read again: creating a library reconciles, which rewrites the lists of the accounts Shoal manages
        var targetNames = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var request in requests)
        {
            var peer = state.Home.Peers.First(p => p.PeerKey == request.PeerKey);
            var toKey = request.ToLibKey ?? created[request.NewLibrary!];
            var target = state.Home.Libraries.First(l => l.LibKey == toKey);
            var sources = SourcesOf(state, peer, request.Type).Where(l => l.LibKey != toKey).ToList();
            var move = new LayoutMove
            {
                PeerKey = peer.PeerKey,
                Type = request.Type,
                ToLibKey = toKey,
                Sources = [.. sources.Select(static l => new LayoutSource { LibKey = l.LibKey, PathRel = l.PathRel })],
            };

            // Who loses access: sees the titles in a source today, not in the target. Who gains (Warden 10 M1): reported in the job status, never blocked
            // (owner decision: Everyone keeps meaning everyone). Warden 14: the same rule as Preview, so a placement-Main library also needs its main library
            // open in core (found by folder; the target's folder is already in it here).
            var targetMain = MainAccess.MainIdOf(state, target, jellyfinLibraries);
            var sourceMains = sources.Select(l => MainAccess.MainIdOf(state, l, jellyfinLibraries)).ToList();
            foreach (var account in accountsNow)
            {
                var user = account.ToCallerUser();
                var before = sources.Where((l, i) => AccessPolicy.Admitted(user, LibraryFolders.AccessOf(state, l), shareUsers) && MainAccess.Opens(state, account, sourceMains[i])).Any();
                var after = AccessPolicy.Admitted(user, target.Access, shareUsers) && MainAccess.Opens(state, account, targetMain);
                if (before && !after)
                {
                    lost.Add(account.Id);
                }
                else if (after && !before)
                {
                    gained.Add(account);
                }
            }

            if (!targetNames.TryGetValue(toKey, out var used))
            {
                used = ExistingNames(state, target);
                targetNames[toKey] = used;
            }

            var catalog = _catalogs.Load(peer.PeerKey);
            if (!catalog.Unreadable)
            {
                var mappingKeys = peer.Libraries.Where(m => m.CollectionType == request.Type).Select(static m => m.MappingKey).ToHashSet(StringComparer.Ordinal);
                foreach (var entry in catalog.Document.Entries.Where(e => mappingKeys.Contains(e.MappingKey)))
                {
                    if (LibraryTree.RecordedTop(entry) is not { } top)
                    {
                        continue;
                    }

                    var mapping = peer.Libraries.First(m => m.MappingKey == entry.MappingKey);
                    var source = sources.FirstOrDefault(l => l.IsLegacy ? LibraryFolders.LegacyOf(state, peer.PeerKey, mapping)?.LibKey == l.LibKey : true);
                    if (source is null)
                    {
                        continue; // already in the target
                    }

                    if (IsSharedCopy(entry, peer.PeerKey, source.LibKey, state))
                    {
                        move.Ops.Add(new LayoutOp { Kind = LayoutOpKind.Delete, StubKey = entry.StubKey, MappingKey = entry.MappingKey, FromLibKey = source.LibKey, FromTop = top });
                        continue;
                    }

                    move.Ops.Add(new LayoutOp { Kind = LayoutOpKind.Rename, StubKey = entry.StubKey, MappingKey = entry.MappingKey, FromLibKey = source.LibKey, FromTop = top, ToTop = Unique(top, entry.StubKey, used) });
                }
            }

            journal.Moves.Add(move);
            journal.RemoveIfEmpty.AddRange(sources.Select(static l => l.LibKey).Where(k => !journal.RemoveIfEmpty.Contains(k)));
        }

        journal.LostAccess = [.. lost];
        var gain = AccessGainReport.Of(gained);
        journal.GainedAccess = [.. gain.Users];
        journal.GainedRestricted = [.. gain.RestrictedUsers];
        journal.Phase = LayoutJobPhase.Running;
        _store.Save(journal); // the journal before any switch or file step
        lock (_statusLock)
        {
            _last = journal;
        }

        return (LayoutJobError.None, journal);
    }

    // A group copy another member still needs in the old library: the title has two or more servers, this connection's stub is the copy, and another member's
    // library is that old library (it keeps feeding it).
    private bool IsSharedCopy(CatalogEntry entry, string peerKey, string fromLibKey, ShoalState state)
    {
        if (entry.Kind != EntryKind.Movie || _groups?.ByCopy(entry.StubKey) is not { } group)
        {
            return false;
        }

        return group.Members.Any(m => m.PeerKey != peerKey && m.LibKey == fromLibKey && state.Home.Peers.Any(p => p.PeerKey == m.PeerKey));
    }

    private static HashSet<string> ExistingNames(ShoalState state, LayoutLibrary target)
    {
        var used = new HashSet<string>(StringComparer.Ordinal);
        if (LibraryFolders.PathOf(state.StubRoot, target) is { } folder && Directory.Exists(folder))
        {
            foreach (var dir in Directory.EnumerateFileSystemEntries(folder))
            {
                used.Add(PathSanitizer.CollisionKey(Path.GetFileName(dir)));
            }
        }

        return used;
    }

    private void Run(LayoutJournal journal, IDisposable layoutHeld, IDisposable syncHeld)
    {
        _isRunning = true;
        _running = Task.Run(async () =>
        {
            try
            {
                await ExecuteAsync(journal).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // a test's crash: nothing more is written, as after a real one
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                journal.Phase = LayoutJobPhase.Failed;
                journal.Error ??= ex is TargetTakenException ? "targetTaken" : ex is IOException or UnauthorizedAccessException ? "fileError" : "internal";
                TrySave(journal);
                _logger.LogWarning("A library move stopped ({Error}); it is resumed at the next start", ex.GetType().Name);
                _ = NotifyFailedAsync();
            }
            finally
            {
                _isRunning = false;
                syncHeld.Dispose();
                layoutHeld.Dispose();
            }
        });
    }

    private async Task ExecuteAsync(LayoutJournal journal)
    {
        var steps = 0;
        foreach (var move in journal.Moves.Where(static m => !m.Done))
        {
            if (!move.StateApplied)
            {
                await ApplyStateAsync(move).ConfigureAwait(false);
                move.StateApplied = true;
                _store.Save(journal);
            }

            var state = _state.Current;
            var target = state.Home.Libraries.FirstOrDefault(l => l.LibKey == move.ToLibKey);
            var toFolder = target is null ? null : LibraryFolders.PathOf(state.StubRoot, target);
            if (toFolder is null)
            {
                journal.Error = "targetGone";
                throw new InvalidOperationException("The target library is gone.");
            }

            SecureDirectory.Ensure(toFolder);
            var catalog = _catalogs.Load(move.PeerKey);
            var recorded = catalog.Unreadable ? [] : catalog.Document.Entries.Where(e => LibraryTree.RecordedTop(e) is not null).GroupBy(static e => e.StubKey, StringComparer.Ordinal).ToDictionary(static g => g.Key, static g => LibraryTree.RecordedTop(g.First())!, StringComparer.Ordinal);
            while (move.NextOp < move.Ops.Count)
            {
                var op = move.Ops[move.NextOp];
                RunStep(state.StubRoot, move, op, toFolder, recorded);
                move.NextOp++;
                steps++;
                if (CrashAfterStep?.Invoke(steps) == true)
                {
                    throw new OperationCanceledException("A test stopped the job, as a crash would.");
                }

                if (move.NextOp % CheckpointEvery == 0)
                {
                    _store.Save(journal);
                }
            }

            _store.Save(journal);
            if (!move.CatalogDone)
            {
                FixCatalog(move);
                FixGroups(move);
                move.CatalogDone = true;
                _store.Save(journal);
            }

            // A targeted scan of each library folder the move touched; never a scan of every library.
            foreach (var source in move.Sources)
            {
                if (LibraryFolders.PathOf(state.StubRoot, new LayoutLibrary { LibKey = source.LibKey, PathRel = source.PathRel }) is { } folder)
                {
                    _refresh.ReportChanged(folder);
                }
            }

            _refresh.ReportChanged(toFolder);
            move.Done = true;
            _store.Save(journal);
            RequestSync?.Invoke(move.PeerKey);
        }

        journal.Phase = LayoutJobPhase.Cleanup;
        _store.Save(journal);
        if (Copies is not null)
        {
            await Copies.RunAsync(CancellationToken.None).ConfigureAwait(false); // the members' libraries changed: copies follow (users' alt tags first)
        }

        await CleanupAsync(journal).ConfigureAwait(false);
        ScanTouched(journal);
        journal.Phase = LayoutJobPhase.Done;
        journal.EndedUtc = _time.GetUtcNow().UtcDateTime;
        _store.Save(journal);
        _logger.LogInformation("Shoal finished a library move ({Moves} connections, {Steps} folders)", journal.Moves.Count, journal.Moves.Sum(static m => m.Ops.Count));
    }

    private async Task ApplyStateAsync(LayoutMove move)
    {
        await _state.UpdateAsync(
            s =>
            {
                foreach (var library in s.Home.Libraries.Where(l => !l.IsLegacy && l.Type == move.Type && l.LibKey != move.ToLibKey))
                {
                    library.Feeds.RemoveAll(f => f == move.PeerKey);
                }

                var target = s.Home.Libraries.First(l => l.LibKey == move.ToLibKey);
                if (!target.Feeds.Contains(move.PeerKey, StringComparer.Ordinal))
                {
                    target.Feeds.Add(move.PeerKey);
                }
            },
            CancellationToken.None).ConfigureAwait(false);
    }

    // One step; repeating it is harmless (a crash between the file change and the checkpoint repeats the step).
    private static void RunStep(string stubRoot, LayoutMove move, LayoutOp op, string toFolder, Dictionary<string, string> recorded)
    {
        var source = move.Sources.First(s => s.LibKey == op.FromLibKey);
        var fromFolder = LibraryFolders.PathOf(stubRoot, new LayoutLibrary { LibKey = source.LibKey, PathRel = source.PathRel });
        if (fromFolder is null)
        {
            return;
        }

        var from = LibraryTree.Confine(stubRoot, fromFolder, op.FromTop);
        if (op.Kind == LayoutOpKind.Delete)
        {
            // Only a folder the connection's catalog still records under this entry (Warden: deletion confined to recorded paths).
            if (from is not null && recorded.TryGetValue(op.StubKey, out var top) && string.Equals(top, op.FromTop, StringComparison.Ordinal) && Directory.Exists(from))
            {
                Directory.Delete(from, recursive: true);
            }

            return;
        }

        var to = LibraryTree.Confine(stubRoot, toFolder, op.ToTop);
        if (to is null)
        {
            return;
        }

        if (from is not null && Directory.Exists(from) && recorded.TryGetValue(op.StubKey, out var recordedTop) && string.Equals(recordedTop, op.FromTop, StringComparison.Ordinal))
        {
            if (File.Exists(to) || (Directory.Exists(to) && !HoldsSentinel(stubRoot, to, op.StubKey)))
            {
                throw new TargetTakenException(); // planned unique; something that is not this title made it since: stop rather than merge
            }

            if (Directory.Exists(to))
            {
                // Warden 10 M2: the target already holds this very title (its sentinel names this entry, e.g. a sync wrote it after the switch): keep it and
                // drop the source copy, which the catalog records under the same entry.
                Directory.Delete(from, recursive: true);
            }
            else
            {
                Directory.Move(from, to);
            }
        }

        if (Directory.Exists(to))
        {
            NfoRetag.Apply(stubRoot, to, move.ToLibKey);
        }
    }

    // Whether a title folder holds a sentinel naming this stub key (a movie's .strm at the top, or an episode's a few levels down). Never through a link.
    private static bool HoldsSentinel(string stubRoot, string folder, string stubKey)
    {
        if (PathSanitizer.HasLinkBelow(stubRoot, folder))
        {
            return false;
        }

        var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, MaxRecursionDepth = 3 };
        foreach (var file in Directory.EnumerateFiles(folder, "*.strm", options))
        {
            var info = new FileInfo(file);
            if (info.Length <= 1024 && info.LinkTarget is null && StubSentinel.TryParse(File.ReadAllText(file), out var key) && string.Equals(key, stubKey, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private void FixCatalog(LayoutMove move)
    {
        var load = _catalogs.Load(move.PeerKey);
        if (load.Unreadable)
        {
            return;
        }

        var renames = move.Ops.Where(static o => o.Kind == LayoutOpKind.Rename).ToDictionary(static o => (o.MappingKey, o.FromTop), static o => o.ToTop);
        var deleted = move.Ops.Where(static o => o.Kind == LayoutOpKind.Delete).Select(static o => o.StubKey).ToHashSet(StringComparer.Ordinal);
        var document = load.Document;
        var changed = document.Entries.RemoveAll(e => deleted.Contains(e.StubKey)) > 0;
        foreach (var entry in document.Entries)
        {
            var top = Top(entry.StubRelPath ?? entry.FolderRel);
            if (top.Length == 0 || !renames.TryGetValue((entry.MappingKey, top), out var to) || string.Equals(top, to, StringComparison.Ordinal))
            {
                continue;
            }

            entry.StubRelPath = Retop(entry.StubRelPath, to);
            entry.FolderRel = Retop(entry.FolderRel, to);
            foreach (var source in entry.Sources)
            {
                source.StubRelPath = Retop(source.StubRelPath, to);
            }

            changed = true;
        }

        if (changed)
        {
            _catalogs.Save(document);
        }

        static string? Retop(string? path, string top) => path is null ? null : path.Contains('/', StringComparison.Ordinal) ? top + path[path.IndexOf('/', StringComparison.Ordinal)..] : top;
    }

    private void FixGroups(LayoutMove move)
    {
        if (_groups is null || move.Type != "movies")
        {
            return;
        }

        var renames = move.Ops.Where(static o => o.Kind == LayoutOpKind.Rename).ToDictionary(static o => o.StubKey, static o => o.ToTop, StringComparer.Ordinal);
        var deleted = move.Ops.Where(static o => o.Kind == LayoutOpKind.Delete).Select(static o => o.StubKey).ToHashSet(StringComparer.Ordinal);
        _groups.Update(document =>
        {
            var changed = false;
            foreach (var group in document.Groups)
            {
                foreach (var member in group.Members.Where(m => m.PeerKey == move.PeerKey && m.LibKey != move.ToLibKey))
                {
                    member.LibKey = move.ToLibKey;
                    changed = true;
                }

                changed |= group.Copies.RemoveAll(c => deleted.Contains(c.StubKey)) > 0;
                foreach (var copy in group.Copies.Where(c => renames.ContainsKey(c.StubKey)))
                {
                    copy.LibKey = move.ToLibKey;
                    copy.FolderRel = renames[copy.StubKey];
                    changed = true;
                }

                // The owner's own stub came into a library that held an extra copy of the title: one copy per library, so the extra one is retired.
                if (group.Copies.Any(c => !c.Extra && c.LibKey == move.ToLibKey))
                {
                    foreach (var extra in group.Copies.Where(c => c.Extra && c.LibKey == move.ToLibKey).ToList())
                    {
                        group.Copies.Remove(extra);
                        document.Retired.Add(extra);
                        changed = true;
                    }
                }
            }

            changed |= document.Groups.RemoveAll(static g => g.Copies.Count == 0 || g.Members.Count < 2) > 0;
            return changed;
        });
    }

    // Libraries the job left without a connection go: a Shoal library of v2.9 with no feeds, or a legacy library whose connection now feeds a shared library of its type.
    private async Task CleanupAsync(LayoutJournal journal)
    {
        foreach (var libKey in journal.RemoveIfEmpty.Concat(journal.Created).Distinct(StringComparer.Ordinal))
        {
            var state = _state.Current;
            var library = state.Home.Libraries.FirstOrDefault(l => l.LibKey == libKey);
            if (library is null)
            {
                continue;
            }

            if (!library.IsLegacy)
            {
                if (library.Feeds.Count == 0 && !(await _layout.DeleteLockedAsync(libKey, CancellationToken.None).ConfigureAwait(false)).Ok)
                {
                    _logger.LogWarning("Shoal could not remove a library a move left empty; delete it on the Library layouts page"); // Warden 13 L7
                }

                continue;
            }

            if (LibraryFolders.MappingOfLegacy(state, library) is not { } legacy || LibraryFolders.FedBy(state, legacy.Peer.PeerKey, library.Type) is null)
            {
                continue; // its connection still writes into it
            }

            if (_legacyLibraries is not null)
            {
                await _legacyLibraries.RemoveAsync(legacy.Peer.PeerKey, legacy.Mapping.MappingKey, CancellationToken.None).ConfigureAwait(false);
            }

            await _state.UpdateAsync(
                s =>
                {
                    s.Home.Libraries.RemoveAll(l => l.LibKey == libKey);
                    s.Home.LibraryOrder.RemoveAll(k => k == libKey);
                },
                CancellationToken.None).ConfigureAwait(false);
            DeleteLegacyFolderIfEmpty(state.StubRoot, library);
        }

        await _layout.ReconcileAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private void DeleteLegacyFolderIfEmpty(string stubRoot, LayoutLibrary library)
    {
        try
        {
            if (LibraryFolders.PathOf(stubRoot, library) is { } folder && Directory.Exists(folder) && !PathSanitizer.HasLinkBelow(stubRoot, folder) && !Directory.EnumerateFileSystemEntries(folder).Any())
            {
                Directory.Delete(folder, recursive: false);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("An emptied per-friend folder could not be deleted ({Error})", ex.GetType().Name);
        }
    }

    private void TrySave(LayoutJournal journal)
    {
        try
        {
            _store.Save(journal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("The layout journal could not be written ({Error})", ex.GetType().Name);
        }
    }

    // A planned target folder exists and is not this title: the job stops with "targetTaken" (the admin resumes after clearing it, or abandons).
    private sealed class TargetTakenException : IOException
    {
        public TargetTakenException()
            : base("The target folder is taken by another title.")
        {
        }

        public TargetTakenException(string message)
            : base(message)
        {
        }

        public TargetTakenException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }

    private sealed class NoHold : IDisposable
    {
        public static readonly NoHold Instance = new();

        public void Dispose()
        {
        }
    }
}
