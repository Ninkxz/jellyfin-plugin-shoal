using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.Shoal.Authorization;
using Jellyfin.Plugin.Shoal.Progress;
using Jellyfin.Plugin.Shoal.State;
using Jellyfin.Plugin.Shoal.Sync;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.Shoal.LocalRemoval;

/// <summary>Normal core reads, guarded Shoal import and explicit same-user personal-state handoff.</summary>
internal sealed partial class JellyfinRemovalLibrary : IRemovalLibrary, IDisposable
{
    private readonly ILibraryManager _library;
    private readonly IItemRepository _repository;
    private readonly IUserManager _users;
    private readonly IUserDataManager _data;
    private readonly ISessionManager _sessions;
    private readonly IFederatedItems _federated;
    private readonly IVisibleItems _visible;
    private readonly IStateStore _state;
    private readonly CatalogStore _catalogs;
    private readonly CatalogSyncService _sync;
    private readonly ILibraryRefresh _refresh;
    private readonly GroupBook _groups;
    private readonly ILogger _logger;
    private long _generation;

    /// <summary>Initializes a new instance of the <see cref="JellyfinRemovalLibrary"/> class.</summary>
    /// <param name="library">Indexed core movies.</param>
    /// <param name="repository">Supported complete userdata hydration.</param>
    /// <param name="users">Current local users.</param>
    /// <param name="data">Supported personal-state API.</param>
    /// <param name="sessions">Current playback sessions.</param>
    /// <param name="federated">Recorded Shoal classification.</param>
    /// <param name="visible">Current per-user visibility.</param>
    /// <param name="state">Current topology and access.</param>
    /// <param name="catalogs">Validated current catalogs.</param>
    /// <param name="sync">Ordinary guarded catalog synchronization.</param>
    /// <param name="refresh">Targeted core refresh.</param>
    /// <param name="groups">Current recorded source-group membership.</param>
    /// <param name="logger">Diagnostics without ids or paths (optional in tests).</param>
    public JellyfinRemovalLibrary(ILibraryManager library, IItemRepository repository, IUserManager users, IUserDataManager data, ISessionManager sessions, IFederatedItems federated, IVisibleItems visible, IStateStore state, CatalogStore catalogs, CatalogSyncService sync, ILibraryRefresh refresh, GroupBook groups, ILogger<JellyfinRemovalLibrary>? logger = null)
    {
        _library = library;
        _repository = repository;
        _users = users;
        _data = data;
        _sessions = sessions;
        _federated = federated;
        _visible = visible;
        _state = state;
        _catalogs = catalogs;
        _sync = sync;
        _refresh = refresh;
        _groups = groups;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        Retire = (target, current, token) => _sync.RetireRestoredMovieAsync(target.PeerKey, target.MappingKey, target.PeerItemId, target.ProviderIds, current, token);
        _data.UserDataSaved += OnSaved;
    }

    /// <summary>Gets or sets the pause between two looks at the index (tests replace it; real use waits <see cref="RemovalPolicy.IndexPoll"/>).</summary>
    internal Func<TimeSpan, CancellationToken, Task> Delay { get; set; } = Task.Delay;

    /// <summary>Gets or sets the guarded one-movie stub retirement after a verified restore (tests replace it; real use is <see cref="CatalogSyncService.RetireRestoredMovieAsync"/>).</summary>
    internal Func<RemovalTarget, Func<bool>, CancellationToken, Task<bool>> Retire { get; set; }

    /// <inheritdoc />
    public void Dispose() => _data.UserDataSaved -= OnSaved;

    /// <inheritdoc />
    public IReadOnlyList<RemovalTarget> Discover(string peerKey) => Discover(peerKey, holdingFolder: true);

    /// <summary>
    /// Discovers candidates. 2.10.4 K2: <paramref name="holdingFolder"/> false is the delete-like-Jellyfin discovery: no .shoal-recovery folder is checked
    /// or named (no parent-folder write is needed, which matters on SMB) and RecoveryRoot is empty. The legacy coordinator keeps the 2.10.3 rule until
    /// K4 removes its routes.
    /// </summary>
    /// <param name="peerKey">The friend.</param>
    /// <param name="holdingFolder">Whether the 2.10.3 holding-folder rule applies.</param>
    /// <returns>At most 100 candidates.</returns>
    public IReadOnlyList<RemovalTarget> Discover(string peerKey, bool holdingFolder)
    {
        Ready();
        var peer = _state.Current.Home.Peers.SingleOrDefault(p => p.PeerKey == peerKey && p.Enabled && p.Pairing?.Method == PairingMethod.Invite) ?? throw Refused();
        var entries = _catalogs.LoadForPreview(peerKey).Document.Entries.Where(e => e.Kind == EntryKind.Movie && e.State == EntryState.DuplicateLocal && peer.Libraries.Any(m => m.MappingKey == e.MappingKey && m.Import)).ToArray();
        var movies = _library.GetItemList(new InternalItemsQuery { IncludeItemTypes = [BaseItemKind.Movie], Recursive = true, Limit = 10001 }).OfType<Movie>().ToArray();
        if (movies.Length > 10000)
        {
            throw Refused();
        }

        var result = new List<RemovalTarget>();
        foreach (var entry in entries)
        {
            if (!Guid.TryParse(entry.PeerItemId, out var remote))
            {
                continue;
            }

            if (_state.Current.Home.Peers.Where(p => p.PeerKey != peerKey && p.Enabled && p.Pairing?.Method == PairingMethod.Invite).Any(p => _catalogs.LoadForPreview(p.PeerKey).Document.Entries.Any(e => e.Kind == EntryKind.Movie && p.Libraries.Any(m => m.Import && m.MappingKey == e.MappingKey) && Matches(e.ProviderIds, entry.ProviderIds))))
            {
                continue;
            }

            var matched = movies.Where(m => _federated.Find(m.Id) is null && Matches(m.ProviderIds, entry.ProviderIds)).Take(2).ToArray();
            if (matched.Length != 1 || !RemovalOriginProof.SupportsMovieShape(matched[0]) || matched[0].GetAdditionalParts(null).Any())
            {
                continue;
            }

            var movie = matched[0];
            var roots = _library.GetVirtualFolders().SelectMany(v => v.Locations.Select(root => (Root: Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)), v.ItemId))).Where(r => Inside(movie.Path, r.Root)).ToArray();
            if (roots.Length != 1 || !Guid.TryParse(roots[0].ItemId, out var libraryId) || Path.GetDirectoryName(roots[0].Root) is not { } parent)
            {
                continue;
            }

            var holding = holdingFolder ? Path.Combine(parent, ".shoal-recovery") : string.Empty;
            if (holdingFolder && _library.GetVirtualFolders().SelectMany(v => v.Locations).Append(_state.Current.StubRoot).Any(r => !string.IsNullOrWhiteSpace(r) && (Inside(holding, r) || Inside(r, holding))))
            {
                continue;
            }

            var info = new FileInfo(movie.Path);
            if (!info.Exists || !(holdingFolder ? info.Length is > 0 && info.Length <= RemovalPolicy.MaxFileSizeBytes : RemovalPolicy.DeleteSizeAllowed(info.Length)) || movie.RunTimeTicks is not > 0 || Busy(movie.Id))
            {
                continue;
            }

            var ids = new Dictionary<string, string>(movie.ProviderIds);
            result.Add(new RemovalTarget { PeerKey = peerKey, MappingKey = entry.MappingKey, PeerItemId = remote, LocalItemId = movie.Id, LibraryId = libraryId, Title = movie.Name, Year = movie.ProductionYear, Root = roots[0].Root, Path = Path.GetFullPath(movie.Path), RecoveryRoot = holding, ProviderIds = ids, RuntimeTicks = movie.RunTimeTicks.Value, Bytes = info.Length, Generation = Fingerprint(new { movie.Id, movie.Path, movie.RunTimeTicks, ids, peer.Enabled, peer.Libraries, _state.Current.Home.Visibility, Layout = _state.Current.Home.Libraries, Roots = _library.GetVirtualFolders().SelectMany(v => v.Locations) }) });
            if (result.Count == 100)
            {
                break;
            }
        }

        return result;
    }

    /// <inheritdoc />
    public RemovalTarget Revalidate(RemovalTarget target) => Discover(target.PeerKey).SingleOrDefault(t => t.LocalItemId == target.LocalItemId && t.PeerItemId == target.PeerItemId && t.Generation == target.Generation) ?? throw Refused();

    /// <inheritdoc />
    public IReadOnlyList<RemovalUserState> Capture(Guid itemId)
    {
        var generation = Interlocked.Read(ref _generation);
        var item = _repository.RetrieveItem(itemId) ?? throw Refused();
        if (item.UserData is null || Busy(itemId))
        {
            throw Refused();
        }

        var users = _users.GetUsers().Take(101).ToArray();
        if (users.Length > 100)
        {
            throw Refused();
        }

        var captured = users.Where(u => Eligible(u) && _visible.IsVisible(u.Id, itemId)).Select(u => Read(u, item)).ToArray();
        if (generation != Interlocked.Read(ref _generation))
        {
            throw Refused();
        }

        return captured;
    }

    /// <inheritdoc />
    public void VerifyAccess(RemovalTarget target, IReadOnlyList<RemovalUserState> history)
    {
        Ready();
        var state = _state.Current;
        var access = DestinationAccess(state, target);
        if (access is null || history.Count == 0 || Busy(target.LocalItemId))
        {
            throw Refused();
        }

        var shares = state.PeerRole.Shares.Select(s => s.ShareUserId).ToHashSet();
        foreach (var saved in history)
        {
            var user = _users.GetUserById(saved.UserId);
            if (user is null || !Eligible(user) || !_visible.IsVisible(user.Id, target.LocalItemId))
            {
                throw Refused();
            }

            // P30-EXTRA-USER-REFUSES: the refusal is right (moving the movie would take it away from someone who cannot see the friend's copy),
            // but it now says why, so the admin knows to give that person access to the friend's library instead of retrying.
            if (state.Home.Visibility.HiddenFriendTitles.Contains(user.Id) || !AccessPolicy.Admitted(JellyfinCallerUsers.Describe(user), access, shares))
            {
                throw new InvalidOperationException("accessNotShared");
            }
        }
    }

    /// <inheritdoc />
    public void Deindex(RemovalTarget target)
    {
        // W23-5 sibling: the row goes only after a proven absence, never on a stat error.
        if (LocalFileStates.Read(target.Path, FileAttributesRule) != LocalFileState.Absent || Busy(target.LocalItemId))
        {
            throw Refused();
        }

        var current = Indexed(target.LocalItemId);
        if (current is null)
        {
            return;
        }

        if (!SamePath(current.Path, target.Path) || !Matches(current.ProviderIds, target.ProviderIds))
        {
            throw Refused();
        }

        _library.DeleteItem(current, new DeleteOptions { DeleteFileLocation = false });
    }

    /// <inheritdoc />
    public async Task<Guid> ImportAsync(RemovalTarget target, IReadOnlyList<RemovalUserState> history, CancellationToken cancellationToken)
    {
        // 2.10.3 P2-SIMPLE-SELF-ROLLBACK-COMMON: wait RemovalPolicy.IndexWait (was a fixed 60 s), and after each sync ask core to index the shared
        // copy's own folder now instead of waiting for a throttled scan or core's delayed change report. The checks below are unchanged.
        for (var attempt = 0; attempt < RemovalPolicy.IndexPolls; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (attempt % RemovalPolicy.IndexPollsPerSync == 0)
            {
                if (!_refresh.IsScanRunning)
                {
                    await _sync.SyncAsync(target.PeerKey, SyncTrigger.Scheduled, cancellationToken).ConfigureAwait(false);
                }

                if (SharedFolder(target) is { } folder)
                {
                    _refresh.RefreshNearest(folder);
                }
            }

            if (FindShared(target) is { } destination)
            {
                foreach (var user in _users.GetUsers().Where(Eligible).Where(u => _visible.IsVisible(u.Id, target.LibraryId)))
                {
                    if (!Admitted(user, destination.Id))
                    {
                        throw new InvalidOperationException("accessNotShared"); // P30: someone who sees your library cannot see the friend's copy
                    }
                }

                return destination.Id;
            }

            await Delay(RemovalPolicy.IndexPoll, cancellationToken).ConfigureAwait(false);
        }

        throw new InvalidOperationException("replacementUnavailable");
    }

    /// <inheritdoc />
    public void Handoff(RemovalTarget target, Guid itemId, IReadOnlyList<RemovalUserState> history)
    {
        VerifyShared(target, itemId, history);
        foreach (var saved in history)
        {
            Apply(itemId, saved, restore: false, baseline: saved);
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<RemovalUserState> CaptureShared(RemovalTarget target, Guid itemId, IReadOnlyList<RemovalUserState> baseline)
    {
        var item = _repository.RetrieveItem(itemId);
        if (item is null)
        {
            return [];
        }

        if (item is not Movie movie || movie.UserData is null || !movie.Path.EndsWith(".strm", StringComparison.OrdinalIgnoreCase) || movie.RunTimeTicks != target.RuntimeTicks || !Matches(movie.ProviderIds, target.ProviderIds) || Busy(itemId))
        {
            throw Refused();
        }

        var shares = _state.Current.PeerRole.Shares.Select(s => s.ShareUserId).ToHashSet();
        return baseline.Select(s => _users.GetUserById(s.UserId)).Where(u => u is not null && !shares.Contains(u.Id)).Select(u => Read(u!, movie)).ToArray();
    }

    /// <inheritdoc />
    public void VerifyShared(RemovalTarget target, Guid itemId, IReadOnlyList<RemovalUserState> history)
    {
        if (FindShared(target)?.Id != itemId || Busy(itemId))
        {
            throw Refused();
        }

        foreach (var saved in history)
        {
            var user = _users.GetUserById(saved.UserId);
            if (user is not null && !Admitted(user, itemId))
            {
                throw new InvalidOperationException("accessNotShared");
            }
        }
    }

    /// <inheritdoc />
    public async Task<Guid> RestoreAsync(RemovalTarget target, IReadOnlyList<RemovalUserState> baseline, IReadOnlyList<RemovalUserState> shared, CancellationToken cancellationToken)
    {
        // 2.10.3 P2-UI2103-RESTORE-FIRST-ATTEMPT: index the restored movie's own folder now (was a refresh of the whole library, which a real library
        // cannot finish in time), and wait RemovalPolicy.IndexWait (was a fixed 60 s). The checks below are unchanged.
        _refresh.RefreshNearest(Path.GetDirectoryName(target.Path)!);
        for (var attempt = 0; attempt < RemovalPolicy.IndexPolls; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candidates = _library.GetItemList(new InternalItemsQuery { IncludeItemTypes = [BaseItemKind.Movie], Recursive = true, HasAnyProviderId = target.ProviderIds.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase), Limit = 33 }).OfType<Movie>().Where(m => SamePath(m.Path, target.Path) && _federated.Find(m.Id) is null && Matches(m.ProviderIds, target.ProviderIds)).Take(2).ToArray();
            if (candidates.Length == 1)
            {
                foreach (var saved in baseline)
                {
                    var latest = shared.SingleOrDefault(s => s.UserId == saved.UserId) ?? saved;
                    Apply(candidates[0].Id, latest, restore: true, baseline: saved);
                }

                await RetireSharedAsync(target, candidates[0].Id, cancellationToken).ConfigureAwait(false);
                return candidates[0].Id;
            }

            await Delay(RemovalPolicy.IndexPoll, cancellationToken).ConfigureAwait(false);
        }

        throw new InvalidOperationException("restoredItemNotIndexed");
    }

    /// <summary>
    /// 2.10.3 P2-ROLLBACK-TIMING-7DEF195 (stale stub folders): the stub the sync wrote for this move is retired only once the restored movie is fully
    /// indexed (core saves a new item before it probes its runtime, so a single early try saw <see cref="RestoredCurrent"/> false and gave up
    /// silently), and a try refused while a sync holds the gate is repeated. Every check of the retirement is unchanged (catalog record, sentinel,
    /// sole origin, retention and mass guards); a stub still not retired after the wait is left and logged, never forced.
    /// </summary>
    private async Task RetireSharedAsync(RemovalTarget target, Guid restored, CancellationToken cancellationToken)
    {
        for (var look = 1; look <= RemovalPolicy.RetireLooks && !cancellationToken.IsCancellationRequested; look++)
        {
            if (Settled(restored, target) && await Retire(target, () => RestoredCurrent(restored, target), cancellationToken).ConfigureAwait(false))
            {
                return;
            }

            if (look == RemovalPolicy.RetireLooks)
            {
                break;
            }

            try
            {
                await Delay(RemovalPolicy.IndexPoll, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break; // cleanup never turns a verified restore into a failed one
            }
        }

        _logger.LogWarning("Shoal restored a movie but left its shared copy in place; the next sync retires it when its checks pass");
    }

    /// <summary>The same checks the retirement asks for; a core read that throws counts as not settled yet (the retirement swallowed it before, too).</summary>
    private bool Settled(Guid restored, RemovalTarget target)
    {
        try
        {
            return RestoredCurrent(restored, target);
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException))
        {
            return false;
        }
    }

    private static bool Matches(IReadOnlyDictionary<string, string> left, IReadOnlyDictionary<string, string> right) => KeepMyPlacePolicy.Matches(new(KeepMyPlaceKind.Movie, left), new(KeepMyPlaceKind.Movie, right));

    private static string Fingerprint<T>(T value) => Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));

    private static bool SamePath(string? left, string right) => left is not null && string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static bool Inside(string path, string root) => SamePath(path, root) || Path.GetFullPath(path).StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static InvalidOperationException Refused() => new("removalChanged");

    private static LibraryAccess? DestinationAccess(ShoalState state, RemovalTarget target)
    {
        var peers = state.Home.Peers.Where(p => p.PeerKey == target.PeerKey && p.Enabled).Take(2).ToArray();
        var mappings = peers.Length == 1 ? peers[0].Libraries.Where(m => m.MappingKey == target.MappingKey && m.Import && m.CollectionType == "movies").Take(2).ToArray() : [];
        if (mappings.Length != 1)
        {
            return null;
        }

        var mapping = mappings[0];
        var modern = state.Home.Libraries.Where(l => !l.IsLegacy && l.Type == mapping.CollectionType && l.Feeds.Contains(target.PeerKey, StringComparer.Ordinal)).Take(2).ToArray();
        var legacy = state.Home.Libraries.Where(l => l.IsLegacy && l.PathRel == target.PeerKey + "/" + mapping.Subfolder).Take(2).ToArray();
        if (modern.Length > 1 || (modern.Length == 0 && legacy.Length != 1))
        {
            return null;
        }

        var library = LibraryFolders.LibraryOf(state, target.PeerKey, mapping);
        return library is null || string.IsNullOrEmpty(library.LibKey) ? null
            : LibraryFolders.AccessOfItem(state, target.PeerKey, target.MappingKey, library.IsLegacy ? string.Empty : library.LibKey);
    }

    private void OnSaved(object? sender, UserDataSaveEventArgs e) => Interlocked.Increment(ref _generation);

    private bool Busy(Guid itemId) => _sessions.Sessions.Any(s => s.NowPlayingItem?.Id == itemId);

    private bool RestoredCurrent(Guid itemId, RemovalTarget target)
    {
        var movie = Indexed(itemId);
        return movie is not null && !Busy(itemId) && RemovalOriginProof.SupportsMovieShape(movie) && SamePath(movie.Path, target.Path)
            && movie.RunTimeTicks == target.RuntimeTicks && Matches(movie.ProviderIds, target.ProviderIds) && File.Exists(target.Path)
            && !_sessions.Sessions.Any(s => s.NowPlayingItem is { } playing && Indexed(playing.Id) is { } indexed && Matches(indexed.ProviderIds, target.ProviderIds));
    }

    private Movie? Indexed(Guid itemId) => _library.GetItemList(new InternalItemsQuery { ItemIds = [itemId], IncludeItemTypes = [BaseItemKind.Movie], Limit = 2 }).OfType<Movie>().SingleOrDefault();

    private bool Eligible(User user) => !user.HasPermission(PermissionKind.IsDisabled) && user.HasPermission(PermissionKind.EnableMediaPlayback) && !_state.Current.PeerRole.Shares.Any(s => s.ShareUserId == user.Id);

    private void Ready()
    {
        if (_state.Status != StateStatus.Ready || _refresh.IsScanRunning || _state.Current.Home.Peers.Any(p =>
        {
            var c = _catalogs.LoadForPreview(p.PeerKey);
            return c.Unreadable || c.Dropped > 0 || c.Document.LoadedWithLoss;
        }))
        {
            throw Refused();
        }
    }

    private bool Admitted(User user, Guid itemId)
    {
        if (!Eligible(user) || !_visible.IsVisible(user.Id, itemId) || _state.Status != StateStatus.Ready || _state.Current.Home.Visibility.HiddenFriendTitles.Contains(user.Id) || _federated.Find(itemId) is not { } owner)
        {
            return false;
        }

        var peer = _state.Current.Home.Peers.SingleOrDefault(p => p.PeerKey == owner.PeerKey && p.Enabled);
        var access = LibraryFolders.AccessOfItem(_state.Current, owner.PeerKey, owner.MappingKey, owner.LibKey, owner.IsCopy);
        return peer is not null && peer.Libraries.Any(m => m.Import && m.MappingKey == owner.MappingKey) && access is not null && AccessPolicy.Admitted(JellyfinCallerUsers.Describe(user), access, _state.Current.PeerRole.Shares.Select(s => s.ShareUserId).ToHashSet());
    }

    /// <summary>The folder of this movie's shared copy once the sync wrote it, else the friend library's folder; null when the mapping is gone.</summary>
    private string? SharedFolder(RemovalTarget target)
    {
        var state = _state.Current;
        var mapping = state.Home.Peers.SingleOrDefault(p => p.PeerKey == target.PeerKey)?.Libraries.SingleOrDefault(m => m.MappingKey == target.MappingKey);
        if (mapping is null || LibraryFolders.FolderOf(state, target.PeerKey, mapping) is not { } folder)
        {
            return null;
        }

        var entry = _catalogs.LoadForPreview(target.PeerKey).Document.Entries.FirstOrDefault(e => e.Kind == EntryKind.Movie && e.MappingKey == target.MappingKey
            && e.StubRelPath is not null && Guid.TryParse(e.PeerItemId, out var id) && id == target.PeerItemId);
        return entry is not null && CatalogStore.IsSafeStubPath(entry.StubRelPath!) ? Path.GetDirectoryName(Path.Combine(folder, entry.StubRelPath!)) : folder;
    }

    private Movie? FindShared(RemovalTarget target)
    {
        var matches = _library.GetItemList(new InternalItemsQuery { IncludeItemTypes = [BaseItemKind.Movie], Recursive = true, HasAnyProviderId = target.ProviderIds.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase), Limit = 33 }).OfType<Movie>().Where(m => _federated.Find(m.Id) is not null && m.RunTimeTicks == target.RuntimeTicks && Matches(m.ProviderIds, target.ProviderIds)).Take(3).ToArray();
        return matches.Length == 1 && SelectedOrigin(matches[0], target) ? matches[0] : null;
    }

    private bool SelectedOrigin(Movie movie, RemovalTarget target)
    {
        using var stream = File.OpenRead(movie.Path);
        var buffer = new byte[1025];
        var count = stream.Read(buffer, 0, buffer.Length);
        if (count > 1024 || !StubSentinel.TryParse(Encoding.UTF8.GetString(buffer, 0, count), out var key))
        {
            return false;
        }

        var group = _groups.ByCopy(key);
        return group is not null ? RemovalSelection.UniqueOrigin(group.Members, target.PeerKey, target.PeerItemId)
            : _federated.Find(movie.Id) is { } owner && owner.PeerKey == target.PeerKey && owner.MappingKey == target.MappingKey && _catalogs.LoadForPreview(target.PeerKey).Document.Entries.Any(e => e.StubKey == key && Guid.TryParse(e.PeerItemId, out var id) && id == target.PeerItemId);
    }

    private RemovalUserState Read(User user, BaseItem item)
    {
        var data = _data.GetUserData(user, item) ?? throw Refused();
        return new(user.Id, item.UserData!.Any(row => row.UserId == user.Id), data.Played, data.PlaybackPositionTicks, data.PlayCount, data.LastPlayedDate, data.IsFavorite, data.Rating, data.Likes, data.AudioStreamIndex, data.SubtitleStreamIndex);
    }

    private void Apply(Guid itemId, RemovalUserState requested, bool restore, RemovalUserState baseline)
    {
        var generation = Interlocked.Read(ref _generation);
        var user = _users.GetUserById(requested.UserId);
        if (user is null)
        {
            return;
        }

        var item = _repository.RetrieveItem(itemId) ?? throw Refused();
        if (item.UserData is null || !Eligible(user) || !_visible.IsVisible(user.Id, itemId) || (!restore && !Admitted(user, itemId)) || Busy(itemId))
        {
            throw Refused();
        }

        var current = Read(user, item);
        var desired = restore ? RemovalHistory.Restore(baseline, current, requested) : RemovalHistory.Handoff(requested, current);
        var cached = CachedForSave(itemId);
        if (cached?.UserData is null || !SamePath(cached.Path, item.Path) || cached.RunTimeTicks != item.RunTimeTicks || !Matches(cached.ProviderIds, item.ProviderIds))
        {
            throw Refused();
        }

        if (RemovalHistory.SameValues(current, desired) && RemovalHistory.SameValues(Read(user, cached), desired))
        {
            return;
        }

        var final = _repository.RetrieveItem(itemId) ?? throw Refused();
        if (final.UserData is null || Read(user, final) != current || generation != Interlocked.Read(ref _generation) || Busy(itemId) || !_visible.IsVisible(user.Id, itemId) || !ReferenceEquals(cached, CachedForSave(itemId)))
        {
            throw Refused();
        }

        var existing = _data.GetUserData(user, final) ?? throw Refused();
        _data.SaveUserData(user, cached, new UserItemData { Key = existing.Key, Played = desired.Played, PlaybackPositionTicks = desired.Position, PlayCount = desired.PlayCount, LastPlayedDate = desired.LastPlayedUtc, IsFavorite = desired.Favorite, Likes = desired.Likes, Rating = desired.Rating, AudioStreamIndex = desired.AudioIndex, SubtitleStreamIndex = desired.SubtitleIndex }, UserDataSaveReason.UpdateUserData, CancellationToken.None);
        var verify = _repository.RetrieveItem(itemId) ?? throw Refused();
        if (!RemovalHistory.SameValues(Read(user, verify), desired) || CachedForSave(itemId) is not { UserData: not null } live || !RemovalHistory.SameValues(Read(user, live), desired))
        {
            throw Refused();
        }
    }

    private Movie? CachedForSave(Guid itemId)
    {
#pragma warning disable RS0030 // Cache identity only after fresh repository, per-user visibility and current ownership checks. Never authorization.
        return _library.GetItemById(itemId) as Movie;
#pragma warning restore RS0030
    }
}
