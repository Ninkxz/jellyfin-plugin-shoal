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
internal sealed record ShoalLibrary(Guid Id, string PeerKey, string MappingKey, LibraryAccess Access);

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
    public VisibilityService(IStateStore state, ILibrarySource libraries, IAccountSource accounts, TimeProvider time, ILogger<VisibilityService> logger)
        : this(state, libraries, accounts, time, (ILogger)logger)
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
    internal VisibilityService(IStateStore state, ILibrarySource libraries, IAccountSource accounts, TimeProvider time, ILogger logger)
    {
        _state = state;
        _libraries = libraries;
        _accounts = accounts;
        _time = time;
        _logger = logger;
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

        foreach (var account in accounts)
        {
            var user = account.ToCallerUser();
            var admitted = shoal.Where(l => AccessPolicy.Admitted(user, l.Access, shareUsers)).Select(static l => l.Id).ToList();
            var current = account.EnabledFolders.ToList();

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
                if (admitted.Count == shoal.Count)
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
                if (admitted.Count == shoal.Count)
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
}
