using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.Authorization;
using Jellyfin.Plugin.Shoal.State;
using Microsoft.Extensions.Logging;
using LayoutLibrary = Jellyfin.Plugin.Shoal.State.ShoalLibrary;

namespace Jellyfin.Plugin.Shoal.Visibility;

/// <summary>
/// What one tag reconcile did.
/// </summary>
/// <param name="Accounts">How many accounts got a new <c>BlockedTags</c> list.</param>
/// <param name="Skipped">True when the state is in recovery mode and nothing was evaluated.</param>
/// <param name="Failed">How many account writes failed or did not hold (Warden 11 L2: the copy writer writes no file while any did).</param>
public sealed record TagReconcileResult(int Accounts, bool Skipped = false, int Failed = 0)
{
    /// <summary>Gets a value indicating whether every wanted tag is on every account now.</summary>
    public bool AllHeld => !Skipped && Failed == 0;
}

/// <summary>
/// Keeps the Shoal tags in each account's <c>BlockedTags</c> in line with the layout (PLAN v2.9 K45). Tags only ever HIDE; Shoal never removes a tag it did not add
/// (<see cref="VisibilityState.ManagedBlockedTags"/>), and every other entry of the list stays as the admin left it.
/// <list type="bullet">
/// <item><c>shoal-lib-&lt;libKey8&gt;</c> for each library added into a main library (placement Main) that does not admit the account: the main library is the
/// admin's, so its friends' titles are hidden per account instead of per library.</item>
/// <item><c>Shoal</c> for an account that hides friends' titles.</item>
/// <item><c>shoal-alt-&lt;libKey8&gt;</c> for each library the account can open that some lower-ordered copy of a title names: the account then sees the title once,
/// in the first library it can open.</item>
/// </list>
/// Sharing accounts are not touched here: their whole policy, <c>BlockedTags</c> included, belongs to the share service (it blocks <c>Shoal</c> and every library tag).
/// Playback does not rely on any of this: CallerContext checks the same rules on every request.
/// </summary>
public sealed class TagService : IDisposable
{
    private readonly IStateStore _state;
    private readonly IAccountSource _accounts;
    private readonly ILibrarySource _libraries;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// Initializes a new instance of the <see cref="TagService"/> class.
    /// </summary>
    /// <param name="state">The state store.</param>
    /// <param name="accounts">Jellyfin's accounts.</param>
    /// <param name="libraries">Jellyfin's libraries.</param>
    /// <param name="logger">The logger.</param>
    public TagService(IStateStore state, IAccountSource accounts, ILibrarySource libraries, ILogger<TagService> logger)
        : this(state, accounts, libraries, (ILogger)logger)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="TagService"/> class (tests: any logger).
    /// </summary>
    /// <param name="state">The state store.</param>
    /// <param name="accounts">Jellyfin's accounts.</param>
    /// <param name="libraries">Jellyfin's libraries.</param>
    /// <param name="logger">The logger.</param>
    internal TagService(IStateStore state, IAccountSource accounts, ILibrarySource libraries, ILogger logger)
    {
        _state = state;
        _accounts = accounts;
        _libraries = libraries;
        _logger = logger;
    }

    /// <summary>
    /// Gets or sets the <c>shoal-alt-</c> tags that some copy of a title carries (from the title groups), or null for none.
    /// </summary>
    public Func<IReadOnlySet<string>>? AltTagsInUse { get; set; }

    /// <summary>Gets or sets what brings the sharing accounts' policies in line after a reconcile (Warden 10 L5: a new library tag reaches them at once), or null.</summary>
    public Func<CancellationToken, Task>? SharePolicies { get; set; }

    /// <summary>Gets or sets the park tags of the title groups whose every server is in the given parked set (K47), or null.</summary>
    public Func<IReadOnlySet<string>, IReadOnlyCollection<string>>? ParkedGroupTags { get; set; }

    /// <summary>
    /// The Shoal tags an account must have blocked.
    /// </summary>
    /// <param name="state">The state.</param>
    /// <param name="account">The account.</param>
    /// <param name="shareUsers">The sharing accounts (they get nothing here).</param>
    /// <param name="altTags">The <c>shoal-alt-</c> tags some copy of a title carries.</param>
    /// <param name="libraryIds">The Jellyfin library of each layout library, where known (library key to folder id).</param>
    /// <param name="parkedGroupTags">K47: the park tags of title groups whose every server is parked, or null.</param>
    /// <returns>The tags.</returns>
    public static IReadOnlySet<string> Desired(ShoalState state, AccountInfo account, IReadOnlySet<Guid> shareUsers, IReadOnlySet<string> altTags, IReadOnlyDictionary<string, Guid> libraryIds, IReadOnlyCollection<string>? parkedGroupTags = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(shareUsers);
        ArgumentNullException.ThrowIfNull(altTags);
        ArgumentNullException.ThrowIfNull(libraryIds);
        var tags = new HashSet<string>(StringComparer.Ordinal);
        if (shareUsers.Contains(account.Id))
        {
            return tags;
        }

        var user = account.ToCallerUser();
        foreach (var library in state.Home.Libraries.Where(static l => !l.IsLegacy && l.Placement == LibraryPlacement.Main))
        {
            if (!AccessPolicy.Admitted(user, library.Access, shareUsers))
            {
                tags.Add(LibraryFolders.LibTag(library.LibKey));
            }
        }

        if (state.Home.Visibility.HiddenFriendTitles.Contains(account.Id))
        {
            tags.Add(LibraryFolders.HideAllTag);
        }

        // K47: a parked server's own stubs are hidden; a title group's copy only when every server of the group is parked.
        foreach (var parked in state.Home.Visibility.Parked.Keys)
        {
            tags.Add(LibraryFolders.ParkPeerTag(parked));
        }

        tags.UnionWith(parkedGroupTags ?? []);

        foreach (var library in state.Home.Libraries)
        {
            var alt = LibraryFolders.AltTag(library.LibKey);
            if (altTags.Contains(alt) && CanOpen(state, account, library, shareUsers, libraryIds))
            {
                tags.Add(alt);
            }
        }

        return tags;
    }

    /// <summary>
    /// Whether an account can open a layout library: its access admits the account and, where the Jellyfin library is known, core lets the account open it (an account
    /// that cannot open it must keep seeing a lower-ordered copy).
    /// </summary>
    /// <param name="state">The state.</param>
    /// <param name="account">The account.</param>
    /// <param name="library">The layout library.</param>
    /// <param name="shareUsers">The sharing accounts.</param>
    /// <param name="libraryIds">The Jellyfin library of each layout library, where known.</param>
    /// <returns>True when it can.</returns>
    public static bool CanOpen(ShoalState state, AccountInfo account, LayoutLibrary library, IReadOnlySet<Guid> shareUsers, IReadOnlyDictionary<string, Guid> libraryIds)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(libraryIds);
        if (!AccessPolicy.Admitted(account.ToCallerUser(), LibraryFolders.AccessOf(state, library), shareUsers))
        {
            return false;
        }

        return libraryIds.TryGetValue(library.LibKey, out var id) && account.CanOpen(id);
    }

    /// <summary>
    /// The Jellyfin library of each layout library: found by its folder for one of its own (and for legacy libraries), the recorded main library for placement Main.
    /// </summary>
    /// <param name="state">The state.</param>
    /// <param name="libraries">Jellyfin's libraries.</param>
    /// <returns>Library key to folder id.</returns>
    public static IReadOnlyDictionary<string, Guid> LibraryIds(ShoalState state, IReadOnlyList<LibraryInfo> libraries)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(libraries);
        var ids = new Dictionary<string, Guid>(StringComparer.Ordinal);
        foreach (var library in state.Home.Libraries)
        {
            if (LibraryFolders.PathOf(state.StubRoot, library) is not { } path)
            {
                continue;
            }

            var found = libraries.FirstOrDefault(l => l.Locations.Any(location => LibraryFolders.SamePath(location, path)));
            if (found is not null)
            {
                ids[library.LibKey] = found.Id;
            }
        }

        return ids;
    }

    /// <summary>
    /// Brings every account's Shoal tags in line. The record of what Shoal adds is written before any account is changed, so a failure half way never leaves a tag
    /// Shoal added without a record (it could then never take it away).
    /// </summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>What was changed.</returns>
    public async Task<TagReconcileResult> ReconcileAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_state.Status != StateStatus.Ready)
            {
                return new TagReconcileResult(0, Skipped: true);
            }

            var state = _state.Current;
            var shareUsers = VisibilityService.ShareUserIds(state);
            var alt = AltTagsInUse?.Invoke() ?? new HashSet<string>(StringComparer.Ordinal);
            var parkedGroups = ParkedGroupTags?.Invoke(state.Home.Visibility.Parked.Keys.ToHashSet(StringComparer.Ordinal)) ?? [];
            var ids = LibraryIds(state, _libraries.List());
            var accounts = _accounts.List();
            var managed = state.Home.Visibility.ManagedBlockedTags;
            var newManaged = new Dictionary<Guid, List<string>>();
            var writes = new List<(Guid UserId, HashSet<string> Remove, List<string> Add)>();
            foreach (var account in accounts)
            {
                var mine = managed.TryGetValue(account.Id, out var recorded) ? recorded.ToHashSet(StringComparer.Ordinal) : new HashSet<string>(StringComparer.Ordinal);
                if (shareUsers.Contains(account.Id))
                {
                    continue; // a sharing account belongs to the share service
                }

                if (account.BlockedTags is not { } current)
                {
                    if (recorded is { Count: > 0 })
                    {
                        newManaged[account.Id] = recorded; // a source that cannot read the list writes nothing and forgets nothing
                    }

                    continue;
                }

                var desired = Desired(state, account, shareUsers, alt, ids, parkedGroups);
                var remove = mine.Where(t => !desired.Contains(t)).ToHashSet(StringComparer.Ordinal);
                var add = desired.Where(t => !current.Contains(t, StringComparer.Ordinal)).ToList();
                var next = current.Where(t => !remove.Contains(t)).Concat(add).ToList();

                // Shoal owns what it added before and still wants, and what it adds now; a wanted tag the admin had already set stays the admin's.
                var owned = mine.Where(desired.Contains).Concat(add).Distinct(StringComparer.Ordinal).OrderBy(static t => t, StringComparer.Ordinal).ToList();
                if (owned.Count > 0)
                {
                    newManaged[account.Id] = owned;
                }

                if (!next.SequenceEqual(current, StringComparer.Ordinal))
                {
                    writes.Add((account.Id, remove, add));
                }
            }

            if (!SameManaged(managed, newManaged))
            {
                await _state.UpdateAsync(s => s.Home.Visibility.ManagedBlockedTags = newManaged.ToDictionary(static p => p.Key, static p => p.Value.ToList()), cancellationToken).ConfigureAwait(false);
            }

            var failed = 0;
            var done = 0;
            foreach (var (userId, remove, add) in writes)
            {
                try
                {
                    // Warden 10 M4: only these tags change, from the list as it is at the write (another writer's change in between is kept), and the list read back
                    // must hold them; if not, the next reconcile (after a scan or hourly) tries again and the log says so now.
                    var after = await _accounts.ChangeBlockedTagsAsync(userId, remove, add, cancellationToken).ConfigureAwait(false);
                    if (!PolicyGate.Holds(after, remove, add))
                    {
                        _logger.LogWarning("The hidden tags of an account did not hold after Shoal wrote them; the next check tries again");
                        failed++;
                        continue;
                    }

                    done++;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning("Shoal could not update the hidden tags of an account ({Error})", ex.GetType().Name);
                    failed++;
                }
            }

            if (done > 0)
            {
                _logger.LogInformation("Shoal updated the hidden tags of {Count} accounts", done);
            }

            if (SharePolicies is { } repair)
            {
                try
                {
                    await repair(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning("Shoal could not bring the sharing accounts in line after a tag change ({Error}); the sweep tries again", ex.GetType().Name);
                }
            }

            return new TagReconcileResult(done, Failed: failed);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Takes every tag Shoal added away again (uninstall, "Remove everything"): only the recorded ones, and only those still on the account.
    /// </summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>How many accounts were changed.</returns>
    public async Task<int> RemoveAllAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_state.Status != StateStatus.Ready)
            {
                return 0;
            }

            var managed = _state.Current.Home.Visibility.ManagedBlockedTags;
            var changed = 0;
            var kept = new Dictionary<Guid, List<string>>();
            foreach (var account in _accounts.List())
            {
                if (!managed.TryGetValue(account.Id, out var mine) || account.BlockedTags is not { } current)
                {
                    continue;
                }

                if (!current.Any(t => mine.Contains(t, StringComparer.Ordinal)))
                {
                    continue;
                }

                try
                {
                    // Warden 11 L4: only Shoal's recorded tags go, from the list as it is at the write; a write that did not hold keeps its record for a later try.
                    var after = await _accounts.ChangeBlockedTagsAsync(account.Id, mine, [], cancellationToken).ConfigureAwait(false);
                    if (PolicyGate.Holds(after, mine, []))
                    {
                        changed++;
                    }
                    else
                    {
                        kept[account.Id] = mine;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    kept[account.Id] = mine;
                    _logger.LogWarning("Shoal could not remove its hidden tags from an account ({Error})", ex.GetType().Name);
                }
            }

            await _state.UpdateAsync(s => s.Home.Visibility.ManagedBlockedTags = kept, cancellationToken).ConfigureAwait(false);
            return changed;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose() => _gate.Dispose();

    private static bool SameManaged(Dictionary<Guid, List<string>> before, Dictionary<Guid, List<string>> after) =>
        before.Count == after.Count && before.All(p => after.TryGetValue(p.Key, out var other) && other.ToHashSet(StringComparer.Ordinal).SetEquals(p.Value));
}
