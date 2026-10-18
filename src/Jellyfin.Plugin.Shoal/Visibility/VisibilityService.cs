using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.Authorization;
using Jellyfin.Plugin.Shoal.Security;
using Jellyfin.Plugin.Shoal.State;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Shoal.Visibility;

/// <summary>
/// A Jellyfin library that is a Shoal library, with the access rule that applies to it.
/// </summary>
/// <param name="Id">The library's folder id.</param>
/// <param name="PeerKey">The peer.</param>
/// <param name="MappingKey">The mapping.</param>
/// <param name="Access">The library's access mode and selection.</param>
/// <param name="LibKey">The layout library (v2.9) when it is one of those, else empty; then <paramref name="MappingKey"/> is empty and <paramref name="PeerKey"/> names its one
/// connection, or is empty when several connections feed it (such a library is never parked as a whole).</param>
internal sealed record ShoalLibrary(Guid Id, string PeerKey, string MappingKey, LibraryAccess Access, string LibKey = "");

/// <summary>
/// Layer 2 of the visibility rule (DESIGN 5.6): sets each account's core library access to the Shoal libraries to <c>Admitted(user, L)</c>, so
/// people see exactly the libraries they can play. Playback is enforced separately on every request by CallerContext; this service keeps lists,
/// search and home rows consistent with it.
/// <para>
/// Shoal owns per-user access to Shoal libraries only. It changes access to other libraries only to preserve the "all libraries" behavior of
/// an account it converted. Writes go through the account source and only when something changes; one reconcile runs at a time; a second
/// reconcile with nothing new writes nothing.
/// </para>
/// </summary>
public sealed class VisibilityService : IDisposable
{
    private readonly IStateStore _state;
    private readonly ILibrarySource _libraries;
    private readonly IAccountSource _accounts;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly TagService? _tags;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private HashSet<Guid> _stoppedManaging = [];

    /// <summary>
    /// Initializes a new instance of the <see cref="VisibilityService"/> class.
    /// </summary>
    /// <param name="state">The state store (access modes, share users, managed accounts).</param>
    /// <param name="libraries">Jellyfin's libraries.</param>
    /// <param name="accounts">Jellyfin's accounts.</param>
    /// <param name="time">The clock.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="tags">The Shoal tags in BlockedTags (K45): reconciled after every library access reconcile, or null.</param>
    public VisibilityService(IStateStore state, ILibrarySource libraries, IAccountSource accounts, TimeProvider time, ILogger<VisibilityService> logger, TagService? tags = null)
        : this(state, libraries, accounts, time, (ILogger)logger, tags)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="VisibilityService"/> class (tests: any logger).
    /// </summary>
    /// <param name="state">The state store.</param>
    /// <param name="libraries">Jellyfin libraries.</param>
    /// <param name="accounts">Jellyfin accounts.</param>
    /// <param name="time">The clock.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="tags">The tag service, or null.</param>
    internal VisibilityService(IStateStore state, ILibrarySource libraries, IAccountSource accounts, TimeProvider time, ILogger logger, TagService? tags = null)
    {
        _state = state;
        _libraries = libraries;
        _accounts = accounts;
        _time = time;
        _logger = logger;
        _tags = tags;
    }

    /// <summary>
    /// Brings every account's access to the Shoal libraries in line with the access modes.
    /// </summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>What was changed; empty when nothing needed doing.</returns>
    public async Task<ReconcileResult> ReconcileAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ReconcileCoreAsync(removedLibraryIds: null, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Removes a Shoal library's id from every account's list and reconciles the rest (DESIGN 5.6: removing a library undoes Shoal's own
    /// changes). Call it right after the library has been removed from Jellyfin.
    /// </summary>
    /// <param name="removedLibraryId">The id the library had.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>What was changed.</returns>
    public async Task<ReconcileResult> ForgetLibraryAsync(Guid removedLibraryId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ReconcileCoreAsync([removedLibraryId], cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// The accounts as the visibility rules see them (the add-friend wizard's gain report, Warden 10 M1).
    /// </summary>
    /// <returns>The accounts.</returns>
    public IReadOnlyList<AccountInfo> Accounts() => _accounts.List();

    /// <summary>
    /// The uninstall rule of DESIGN 5.6 (W6 L-5, "narrow, never widen"). It deletes nothing. An account Shoal manages whose list still is the
    /// one Shoal wrote gets "all libraries" back only when no Shoal library exists any more; otherwise it keeps that explicit list, so it sees
    /// what it saw and nothing more. An account an admin has edited since is skipped. One warning names the accounts left on explicit lists.
    /// Remove the libraries first (<see cref="ShoalLibraryService.RemoveAllAsync"/>) and every matching account is restored.
    /// </summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>How many accounts were restored.</returns>
    public async Task<int> RestoreManagedAccountsAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_state.Status != StateStatus.Ready)
            {
                return 0;
            }

            var state = _state.Current;
            var libraryExists = ShoalLibraries(state, _libraries.List()).Count > 0;
            var restored = 0;
            var left = new List<string>();
            var known = _accounts.List().ToDictionary(static a => a.Id);
            foreach (var (id, entry) in state.Home.Visibility.ManagedUsers.ToList())
            {
                if (!known.TryGetValue(id, out var account) || account.EnableAllFolders
                    || !string.Equals(HashFolders(account.EnabledFolders.ToList()), entry.LastWrittenFoldersHash, StringComparison.Ordinal))
                {
                    continue; // gone, already open, or an admin owns the list now
                }

                if (libraryExists)
                {
                    left.Add(account.Name);
                    continue;
                }

                try
                {
                    await _accounts.SetFoldersAsync(id, enableAllFolders: true, [], cancellationToken).ConfigureAwait(false);
                    restored++;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning("Shoal could not restore \"all libraries\" for an account ({Error})", ex.GetType().Name);
                }
            }

            if (left.Count > 0)
            {
                _logger.LogWarning(
                    "Shoal libraries still exist, so these accounts keep the explicit library list Shoal wrote (they do not get \"all libraries\" back): {Accounts}. Delete the Shoal libraries, then tick \"all libraries\" for them.",
                    string.Join(", ", left));
            }

            await _state.UpdateAsync(static s => s.Home.Visibility.ManagedUsers.Clear(), cancellationToken).ConfigureAwait(false);
            return restored;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Builds the drift list (DESIGN 5.6): accounts whose core access to a Shoal library differs from the rule, accounts Shoal manages, and
    /// accounts Shoal stopped managing since the server started.
    /// </summary>
    /// <returns>The report.</returns>
    public DriftReport Drift()
    {
        if (_state.Status != StateStatus.Ready)
        {
            return new DriftReport([], [], []);
        }

        var state = _state.Current;
        var shoal = ShoalLibraries(state, _libraries.List());
        var shareUsers = ShareUserIds(state);
        var differences = new List<AccessDrift>();
        foreach (var account in _accounts.List())
        {
            var user = account.ToCallerUser();
            foreach (var library in shoal)
            {
                var has = account.EnableAllFolders || account.EnabledFolders.Contains(library.Id);
                var should = AccessPolicy.Admitted(user, library.Access, shareUsers);
                if (has != should)
                {
                    differences.Add(new AccessDrift(account.Id, library.Id, has, should));
                }
            }
        }

        return new DriftReport(differences, [.. state.Home.Visibility.ManagedUsers.Keys], [.. _stoppedManaging]);
    }

    /// <summary>
    /// Whether a peer's libraries are hidden at the moment (offline parking).
    /// </summary>
    /// <param name="peerKey">The peer.</param>
    /// <returns>True while parked.</returns>
    public bool IsParked(string peerKey) => _state.Status == StateStatus.Ready && _state.Current.Home.Visibility.Parked.ContainsKey(peerKey);

    /// <summary>
    /// Every peer whose libraries are hidden right now.
    /// </summary>
    /// <returns>The peer keys.</returns>
    public IReadOnlyList<string> ParkedPeerKeys() => _state.Status == StateStatus.Ready ? [.. _state.Current.Home.Visibility.Parked.Keys] : [];

    /// <summary>
    /// Hides a peer's Shoal libraries while it is offline: removes them from the explicit library list of every account that has them. Nothing is deleted and no
    /// folder moves, so watch progress stays. An account with "all libraries" is left alone. The exact (account, library) pairs that were removed are recorded in the
    /// state before anything is written, so <see cref="UnparkPeerAsync"/> puts back those and only those, and a restart in between loses nothing.
    /// </summary>
    /// <param name="peerKey">The peer.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>How many (account, library) pairs were removed; 0 when nothing was done.</returns>
    public async Task<int> ParkPeerAsync(string peerKey, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_state.Status != StateStatus.Ready || _state.Current.Home.Visibility.Parked.ContainsKey(peerKey))
            {
                return 0;
            }

            var state = _state.Current;
            var libraries = ShoalLibraries(state, _libraries.List()).Where(l => string.Equals(l.PeerKey, peerKey, StringComparison.Ordinal)).ToList();
            if (libraries.Count == 0)
            {
                // K47: a server that feeds only shared libraries is parked by its stubs alone (the tag service hides them).
                if (state.Home.Peers.Any(p => string.Equals(p.PeerKey, peerKey, StringComparison.Ordinal) && p.Libraries.Count > 0))
                {
                    await _state.UpdateAsync(s => s.Home.Visibility.Parked[peerKey] = new ParkedPeer { ParkedUtc = _time.GetUtcNow().UtcDateTime }, cancellationToken).ConfigureAwait(false);
                    await ReconcileTagsAsync(cancellationToken).ConfigureAwait(false);
                }

                return 0;
            }

            var shareUsers = ShareUserIds(state);
            var ids = libraries.Select(static l => l.Id).ToHashSet();
            var pairs = new List<ParkedPair>();
            var writes = new List<(Guid UserId, List<Guid> Folders, string Before)>();
            var newManaged = state.Home.Visibility.ManagedUsers.ToDictionary(static p => p.Key, static p => p.Value);
            var stoppedNow = new HashSet<Guid>();
            var allFolders = 0;
            foreach (var account in _accounts.List())
            {
                if (shareUsers.Contains(account.Id))
                {
                    continue;
                }

                if (account.EnableAllFolders)
                {
                    allFolders++;
                    continue;
                }

                var current = account.EnabledFolders.ToList();
                var lost = current.Where(ids.Contains).ToList();
                if (lost.Count == 0)
                {
                    continue;
                }

                var kept = current.Where(id => !ids.Contains(id)).ToList();
                pairs.AddRange(lost.Select(id => new ParkedPair { UserId = account.Id, LibraryId = id }));
                writes.Add((account.Id, kept, HashFolders(current)));
                if (newManaged.TryGetValue(account.Id, out var entry))
                {
                    if (string.Equals(HashFolders(current), entry.LastWrittenFoldersHash, StringComparison.Ordinal))
                    {
                        newManaged[account.Id] = new ManagedUser { Since = entry.Since, LastWrittenFoldersHash = HashFolders(kept) };
                    }
                    else
                    {
                        // An admin edited this list since Shoal wrote it: it is theirs now, and reconcile must not put back what they removed.
                        newManaged.Remove(account.Id);
                        stoppedNow.Add(account.Id);
                    }
                }
            }

            // The record goes first: if a write then fails or the server stops, the pairs are known and the next start can still put them back.
            await _state.UpdateAsync(
                s =>
                {
                    s.Home.Visibility.Parked[peerKey] = new ParkedPeer { ParkedUtc = _time.GetUtcNow().UtcDateTime, Pairs = pairs };
                    s.Home.Visibility.ManagedUsers = newManaged.ToDictionary(static p => p.Key, static p => p.Value);
                },
                cancellationToken).ConfigureAwait(false);
            _stoppedManaging = [.. _stoppedManaging, .. stoppedNow];
            foreach (var (userId, kept, before) in writes)
            {
                if (ChangedSince(userId, before))
                {
                    continue; // edited in the dashboard while this ran: not overwritten
                }

                try
                {
                    await _accounts.SetFoldersAsync(userId, enableAllFolders: false, kept, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning("Shoal could not hide an offline server's libraries for an account ({Error})", ex.GetType().Name);
                }
            }

            await ReconcileTagsAsync(cancellationToken).ConfigureAwait(false); // K47: its stubs in shared libraries too
            _logger.LogInformation("Shoal hid the libraries of an offline server from {Accounts} accounts ({Pairs} pairs)", writes.Count, pairs.Count);
            if (allFolders > 0)
            {
                _logger.LogInformation("{Accounts} accounts see all libraries, so they still see the offline server's titles (they can only be hidden by listing libraries for them)", allFolders);
            }

            return pairs.Count;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Shows a peer's libraries again: puts back exactly the (account, library) pairs <see cref="ParkPeerAsync"/> removed. A pair is skipped when the account was
    /// deleted, now has "all libraries", already has the library back (an admin did it), or is no longer admitted by the access rules. Everything else in an
    /// account's list stays as the admin left it.
    /// </summary>
    /// <param name="peerKey">The peer.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>How many pairs were put back.</returns>
    public async Task<int> UnparkPeerAsync(string peerKey, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_state.Status != StateStatus.Ready || !_state.Current.Home.Visibility.Parked.TryGetValue(peerKey, out var parked))
            {
                return 0;
            }

            var state = _state.Current;
            var shoal = ShoalLibraries(state, _libraries.List()).ToDictionary(static l => l.Id);
            var shareUsers = ShareUserIds(state);
            var accounts = _accounts.List().ToDictionary(static a => a.Id);
            var restored = 0;
            var newManaged = state.Home.Visibility.ManagedUsers.ToDictionary(static p => p.Key, static p => p.Value);
            var writes = new List<(Guid UserId, List<Guid> Folders, string Before)>();
            var stoppedNow = new HashSet<Guid>();
            foreach (var group in parked.Pairs.GroupBy(static p => p.UserId))
            {
                if (!accounts.TryGetValue(group.Key, out var account) || account.EnableAllFolders || shareUsers.Contains(account.Id))
                {
                    continue;
                }

                var list = account.EnabledFolders.ToList();
                var user = account.ToCallerUser();
                var added = 0;
                foreach (var pair in group)
                {
                    if (list.Contains(pair.LibraryId) || !shoal.TryGetValue(pair.LibraryId, out var library) || !AccessPolicy.Admitted(user, library.Access, shareUsers))
                    {
                        continue;
                    }

                    list.Add(pair.LibraryId);
                    added++;
                }

                if (added == 0)
                {
                    continue;
                }

                restored += added;
                writes.Add((account.Id, list, HashFolders(account.EnabledFolders)));
                if (newManaged.TryGetValue(account.Id, out var entry))
                {
                    if (string.Equals(HashFolders(account.EnabledFolders), entry.LastWrittenFoldersHash, StringComparison.Ordinal))
                    {
                        newManaged[account.Id] = new ManagedUser { Since = entry.Since, LastWrittenFoldersHash = HashFolders(list) };
                    }
                    else
                    {
                        newManaged.Remove(account.Id);
                        stoppedNow.Add(account.Id);
                    }
                }
            }

            await _state.UpdateAsync(
                s =>
                {
                    s.Home.Visibility.Parked.Remove(peerKey);
                    s.Home.Visibility.ManagedUsers = newManaged.ToDictionary(static p => p.Key, static p => p.Value);
                },
                cancellationToken).ConfigureAwait(false);
            _stoppedManaging = [.. _stoppedManaging, .. stoppedNow];
            foreach (var (userId, folders, before) in writes)
            {
                if (ChangedSince(userId, before))
                {
                    continue;
                }

                try
                {
                    await _accounts.SetFoldersAsync(userId, enableAllFolders: false, folders, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning("Shoal could not show a server's libraries again for an account ({Error})", ex.GetType().Name);
                }
            }

            await ReconcileTagsAsync(cancellationToken).ConfigureAwait(false); // K47: its stubs come back too
            _logger.LogInformation("Shoal showed the libraries of a recovered server again ({Pairs} pairs)", restored);
            return restored;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose() => _gate.Dispose();

    /// <summary>
    /// Finds the Jellyfin libraries that are Shoal libraries: those with a path at <c>&lt;StubRoot&gt;/&lt;peerKey&gt;/&lt;Subfolder&gt;</c> of a mapping.
    /// A library that has such a path next to other paths still counts, so no federated title is ever outside the rules.
    /// </summary>
    /// <param name="state">The state.</param>
    /// <param name="libraries">Jellyfin's libraries.</param>
    /// <returns>The Shoal libraries.</returns>
    internal static List<ShoalLibrary> ShoalLibraries(ShoalState state, IReadOnlyList<LibraryInfo> libraries)
    {
        var result = new List<ShoalLibrary>();
        if (string.IsNullOrWhiteSpace(state.StubRoot))
        {
            return result;
        }

        foreach (var peer in state.Home.Peers)
        {
            foreach (var mapping in peer.Libraries)
            {
                var expected = MappingPath(state.StubRoot, peer.PeerKey, mapping.Subfolder);
                if (expected is null)
                {
                    continue;
                }

                var library = libraries.FirstOrDefault(l => l.Locations.Any(location => SamePath(location, expected)));
                if (library is not null && result.All(r => r.Id != library.Id))
                {
                    result.Add(new ShoalLibrary(library.Id, peer.PeerKey, mapping.MappingKey, mapping.Access));
                }
            }
        }

        // K44/K45: a layout library of its own (placement Shoal) is a Shoal library too; its access is its own. A library added into a main library (placement
        // Main) is not: the main library is the admin's, and its titles are hidden per user by the shoal-lib tag instead (TagService).
        foreach (var layout in state.Home.Libraries.Where(static l => !l.IsLegacy && l.Placement == LibraryPlacement.Shoal))
        {
            if (LibraryFolders.PathOf(state.StubRoot, layout) is not { } expected)
            {
                continue;
            }

            var library = libraries.FirstOrDefault(l => l.Locations.Any(location => SamePath(location, expected)));
            if (library is not null && result.All(r => r.Id != library.Id))
            {
                result.Add(new ShoalLibrary(library.Id, layout.Feeds.Count == 1 ? layout.Feeds[0] : string.Empty, string.Empty, layout.Access, layout.LibKey));
            }
        }

        return result;
    }

    /// <summary>
    /// The folder of a mapping: <c>&lt;StubRoot&gt;/&lt;peerKey&gt;/&lt;Subfolder&gt;</c>, or null when the parts do not form a path below the stub root.
    /// </summary>
    /// <param name="stubRoot">The stub root.</param>
    /// <param name="peerKey">The peer key.</param>
    /// <param name="subfolder">The subfolder (one plain segment).</param>
    /// <returns>The full path, or null.</returns>
    internal static string? MappingPath(string stubRoot, string peerKey, string subfolder)
    {
        if (string.IsNullOrWhiteSpace(stubRoot) || string.IsNullOrWhiteSpace(peerKey) || string.IsNullOrWhiteSpace(subfolder))
        {
            return null;
        }

        try
        {
            var peerFolder = Path.GetFullPath(Path.Combine(stubRoot, peerKey));
            return Sync.PathSanitizer.TryConfine(peerFolder, subfolder, out var full) ? full : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    /// <summary>
    /// The ids of the Shoal share users of this server.
    /// </summary>
    /// <param name="state">The state.</param>
    /// <returns>The ids.</returns>
    internal static HashSet<Guid> ShareUserIds(ShoalState state) => state.PeerRole.Shares.Select(static s => s.ShareUserId).ToHashSet();

    /// <summary>
    /// A hash of a folder list, order-independent: what Shoal last wrote to a managed account.
    /// </summary>
    /// <param name="folders">The library ids.</param>
    /// <returns>The hash, as lower-case hex.</returns>
    internal static string HashFolders(IEnumerable<Guid> folders)
    {
        var text = string.Join(',', folders.Select(static g => g.ToString("N")).OrderBy(static s => s, StringComparer.Ordinal));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..32];
    }

    // True when the list of the account is no longer the one read earlier (an admin edited it, or the account is gone or now sees everything).
    private bool ChangedSince(Guid userId, string beforeHash)
    {
        var fresh = _accounts.List().FirstOrDefault(a => a.Id == userId);
        return fresh is null || fresh.EnableAllFolders || !string.Equals(HashFolders(fresh.EnabledFolders), beforeHash, StringComparison.Ordinal);
    }

    private static bool SamePath(string location, string expected)
    {
        try
        {
            var a = Path.GetFullPath(location).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var b = expected.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return string.Equals(a, b, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private async Task<ReconcileResult> ReconcileCoreAsync(IReadOnlyCollection<Guid>? removedLibraryIds, CancellationToken cancellationToken)
    {
        if (_state.Status != StateStatus.Ready)
        {
            return new ReconcileResult([], Skipped: true); // recovery mode: nothing is known, so nothing is written
        }

        var state = _state.Current;
        var libraries = _libraries.List();
        var shoal = ShoalLibraries(state, libraries);
        var shoalIds = shoal.Select(static l => l.Id).ToHashSet();
        shoalIds.UnionWith(removedLibraryIds ?? []);
        var nonShoal = libraries.Where(l => !shoalIds.Contains(l.Id)).Select(static l => l.Id).ToList();
        var shareUsers = ShareUserIds(state);
        var managed = state.Home.Visibility.ManagedUsers;
        var newManaged = managed.ToDictionary(static p => p.Key, static p => p.Value);
        var changes = new List<VisibilityChange>();
        var writes = new List<(Guid UserId, bool All, List<Guid> Folders)>();
        var stopped = new HashSet<Guid>(_stoppedManaging);
        var now = _time.GetUtcNow().UtcDateTime;
        var accounts = _accounts.List();
        stopped.IntersectWith(accounts.Select(static a => a.Id));
        var parkedPairs = state.Home.Visibility.Parked.Values.SelectMany(static p => p.Pairs).Select(static p => (p.UserId, p.LibraryId)).ToHashSet();

        foreach (var account in accounts)
        {
            var user = account.ToCallerUser();
            var admittedAll = shoal.Where(l => AccessPolicy.Admitted(user, l.Access, shareUsers)).Select(static l => l.Id).ToList();
            var current = account.EnabledFolders.ToList();

            // A library Shoal hid while its server is offline stays hidden from that account; one an admin put back by hand is left alone.
            var admitted = parkedPairs.Count == 0 || account.EnableAllFolders ? admittedAll : admittedAll.Where(id => !parkedPairs.Contains((account.Id, id)) || current.Contains(id)).ToList();

            if (shareUsers.Contains(account.Id))
            {
                // A sharing account never sees a Shoal library, and Shoal never manages it (N-2).
                newManaged.Remove(account.Id);
                if (account.EnableAllFolders)
                {
                    // W6 L-6: "all libraries" would list the federated titles to a sharing account; narrow it to the non-Shoal libraries.
                    writes.Add((account.Id, false, nonShoal.ToList()));
                    changes.Add(new VisibilityChange(account.Id, VisibilityChangeKind.ShareUserCleaned));
                    _logger.LogWarning("A sharing account had access to all libraries; Shoal narrowed it to the non-federated ones");
                    continue;
                }

                var cleaned = current.Where(id => !shoalIds.Contains(id)).ToList();
                if (cleaned.Count != current.Count)
                {
                    writes.Add((account.Id, false, cleaned));
                    changes.Add(new VisibilityChange(account.Id, VisibilityChangeKind.ShareUserCleaned));
                    _logger.LogWarning("Shoal removed a federated library from a sharing account");
                }

                continue;
            }

            var entry = managed.TryGetValue(account.Id, out var existing) ? existing : null;
            if (entry is not null && (account.EnableAllFolders || !string.Equals(HashFolders(current), entry.LastWrittenFoldersHash, StringComparison.Ordinal)))
            {
                // The admin changed this account in the dashboard: its non-Shoal libraries are theirs again.
                newManaged.Remove(account.Id);
                stopped.Add(account.Id);
                changes.Add(new VisibilityChange(account.Id, VisibilityChangeKind.StoppedManaging));
                _logger.LogInformation("An admin edited the libraries of an account Shoal was managing; Shoal stopped managing it");
                entry = null;
            }

            if (entry is not null)
            {
                if (admittedAll.Count == shoal.Count && admitted.Count == admittedAll.Count)
                {
                    writes.Add((account.Id, true, []));
                    newManaged.Remove(account.Id);
                    changes.Add(new VisibilityChange(account.Id, VisibilityChangeKind.Restored));
                    continue;
                }

                var desired = nonShoal.Concat(admitted).ToList();
                if (!SameSet(desired, current))
                {
                    writes.Add((account.Id, false, desired));
                    newManaged[account.Id] = new ManagedUser { Since = entry.Since, LastWrittenFoldersHash = HashFolders(desired) };
                    changes.Add(new VisibilityChange(account.Id, VisibilityChangeKind.ManagedRefreshed));
                }

                continue;
            }

            if (account.EnableAllFolders)
            {
                if (admittedAll.Count == shoal.Count && admitted.Count == admittedAll.Count)
                {
                    continue; // sees everything, and is admitted to everything
                }

                var desired = nonShoal.Concat(admitted).ToList();
                writes.Add((account.Id, false, desired));
                newManaged[account.Id] = new ManagedUser { Since = now, LastWrittenFoldersHash = HashFolders(desired) };
                changes.Add(new VisibilityChange(account.Id, VisibilityChangeKind.Converted));
                continue;
            }

            var explicitList = current.Where(id => !shoalIds.Contains(id)).Concat(admitted).ToList();
            if (!SameSet(explicitList, current))
            {
                writes.Add((account.Id, false, explicitList));
                changes.Add(new VisibilityChange(account.Id, VisibilityChangeKind.ListUpdated));
            }
        }

        if (!SameManaged(managed, newManaged))
        {
            // The record goes first: if the policy write then fails, the next run finds an "all libraries" account with a record,
            // reads it as edited, and starts again.
            await _state.UpdateAsync(
                s =>
                {
                    s.Home.Visibility.ManagedUsers = newManaged.ToDictionary(static p => p.Key, static p => p.Value);
                },
                cancellationToken).ConfigureAwait(false);
        }

        var failed = new HashSet<Guid>();
        foreach (var (userId, all, folders) in writes)
        {
            try
            {
                await _accounts.SetFoldersAsync(userId, all, folders, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed.Add(userId);
                _logger.LogWarning("Shoal could not update the library access of an account ({Error})", ex.GetType().Name);
            }
        }

        _stoppedManaging = stopped;
        var done = changes.Where(c => !failed.Contains(c.UserId)).ToList();
        if (done.Count > 0)
        {
            _logger.LogInformation("Shoal re-applied library access ({Count} accounts)", done.Count(static c => c.Kind != VisibilityChangeKind.StoppedManaging));
        }

        // K45: the tags follow the same access (and the library lists just written, for the shoal-alt tags).
        if (_tags is not null)
        {
            await _tags.ReconcileAsync(cancellationToken).ConfigureAwait(false);
        }

        return new ReconcileResult(done);
    }

    private static bool SameSet(IEnumerable<Guid> a, IEnumerable<Guid> b) => a.ToHashSet().SetEquals(b);

    private static bool SameManaged(Dictionary<Guid, ManagedUser> before, Dictionary<Guid, ManagedUser> after)
    {
        if (before.Count != after.Count)
        {
            return false;
        }

        foreach (var (id, value) in before)
        {
            if (!after.TryGetValue(id, out var other) || other.LastWrittenFoldersHash != value.LastWrittenFoldersHash || other.Since != value.Since)
            {
                return false;
            }
        }

        return true;
    }

    // K47: the park tags follow the parked set (the tag service has its own gate).
    private async Task ReconcileTagsAsync(CancellationToken cancellationToken)
    {
        if (_tags is null)
        {
            return;
        }

        try
        {
            await _tags.ReconcileAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning("Shoal could not update the park tags ({Error})", ex.GetType().Name);
        }
    }
}
