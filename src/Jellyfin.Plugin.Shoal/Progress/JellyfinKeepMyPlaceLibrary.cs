using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Jellyfin.Data;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.Shoal.Api.Admin;
using Jellyfin.Plugin.Shoal.Authorization;
using Jellyfin.Plugin.Shoal.State;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.Shoal.Progress;

/// <summary>Local 12.1 userdata adapter. Supported core repository reads; no peer calls, direct database writes or synthetic authentication.</summary>
internal sealed class JellyfinKeepMyPlaceLibrary : IKeepMyPlaceLibrary
{
    private readonly ILibraryManager _library;
    private readonly IItemRepository _repository;
    private readonly IUserManager _users;
    private readonly IUserDataManager _data;
    private readonly ISessionManager _sessions;
    private readonly IFederatedItems _federated;
    private readonly IVisibleItems _visible;
    private readonly IStateStore _state;
    private readonly ISettingsAccessor _settings;
    private readonly TimeProvider _time;
    private long _saveGeneration;

    /// <summary>Initializes a new instance of the <see cref="JellyfinKeepMyPlaceLibrary"/> class.</summary>
    /// <param name="library">Core indexed items.</param>
    /// <param name="repository">Supported core read API; RetrieveItem always hydrates userdata in 12.1.</param>
    /// <param name="users">Core local users.</param>
    /// <param name="data">Core local userdata API.</param>
    /// <param name="sessions">Core active sessions.</param>
    /// <param name="federated">Authoritative Shoal classification.</param>
    /// <param name="visible">Core per-user visibility.</param>
    /// <param name="state">Current Shoal authorization.</param>
    /// <param name="settings">Current enable setting.</param>
    /// <param name="time">Clock.</param>
    public JellyfinKeepMyPlaceLibrary(ILibraryManager library, IItemRepository repository, IUserManager users, IUserDataManager data, ISessionManager sessions, IFederatedItems federated, IVisibleItems visible, IStateStore state, ISettingsAccessor settings, TimeProvider time)
    {
        _library = library;
        _repository = repository;
        _users = users;
        _data = data;
        _sessions = sessions;
        _federated = federated;
        _visible = visible;
        _state = state;
        _settings = settings;
        _time = time;
    }

    private static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>Observes a completed core save; any intervening save invalidates a pending decision.</summary>
    public void ObserveSave() => Interlocked.Increment(ref _saveGeneration);

    /// <inheritdoc />
    public bool UserExists(Guid userId) => _users.GetUserById(userId) is not null;

    /// <inheritdoc />
    public IReadOnlyList<KeepMyPlaceSnapshot>? Capture(Guid itemId)
    {
        var item = Indexed(itemId);
        return item is null ? null : CaptureItem(item);
    }

    /// <inheritdoc />
    public IReadOnlyList<KeepMyPlaceSnapshot>? CapturePath(string path)
    {
        if (_state.Status != StateStatus.Ready || !Path.IsPathFullyQualified(path))
        {
            return null;
        }

        var items = _library.GetItemList(new InternalItemsQuery { Path = path, Limit = 2 }).Where(i => string.Equals(i.Path, path, PathComparison)).ToArray();
        return items.Length switch
        {
            0 => [], // never indexed: there is no local core history to capture
            1 => CaptureItem(items[0]),
            _ => null
        };
    }

    /// <inheritdoc />
    public IReadOnlyList<Guid> SourceItems(int offset, int limit) => _library.GetItemIds(new InternalItemsQuery
    {
        Recursive = true,
        IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Episode],
        StartIndex = offset,
        Limit = Math.Clamp(limit, 1, 256)
    });

    /// <inheritdoc />
    public IReadOnlyList<KeepMyPlaceMedia> Destinations(KeepMyPlaceSnapshot snapshot)
    {
        var user = _users.GetUserById(snapshot.Source.History.UserId);
        if (!Eligible(user))
        {
            return [];
        }

        var identity = snapshot.Source.Media.Identity;
        if (identity.Ids.Count == 0)
        {
            return [];
        }

        // Strong provider filtering is executed by core, through the actual user's view.
        var parents = _library.GetItemList(new InternalItemsQuery(user!)
        {
            Recursive = true,
            IncludeItemTypes = [identity.Kind == KeepMyPlaceKind.Movie ? BaseItemKind.Movie : BaseItemKind.Series],
            HasAnyProviderId = identity.Ids.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase),
            Limit = 32
        });
        if (parents.Count >= 32)
        {
            return []; // bounded lookup could be incomplete, so do not choose a first match
        }

        var result = new List<KeepMyPlaceMedia>();
        foreach (var parent in parents)
        {
            IReadOnlyList<BaseItem> candidates;
            if (identity.Kind == KeepMyPlaceKind.Movie)
            {
                candidates = [parent];
            }
            else
            {
                if (!KeepMyPlacePolicy.Matches(identity with { Kind = KeepMyPlaceKind.Movie }, new(KeepMyPlaceKind.Movie, Ids(parent))))
                {
                    continue;
                }

                candidates = _library.GetItemList(new InternalItemsQuery(user!)
                {
                    Recursive = true,
                    IncludeItemTypes = [BaseItemKind.Episode],
                    AncestorIds = [parent.Id],
                    ParentIndexNumber = identity.Season,
                    IndexNumber = identity.Episode,
                    Limit = 32
                });
                if (candidates.Count >= 32)
                {
                    return [];
                }
            }

            foreach (var item in candidates)
            {
                if (RealLocal(item) && Media(item) is { } media && KeepMyPlacePolicy.Matches(snapshot.Source.Media.Identity, media.Identity))
                {
                    result.Add(media);
                    if (result.Count == 2)
                    {
                        return result;
                    }
                }
            }
        }

        return result;
    }

    /// <inheritdoc />
    public bool ApplyFresh(KeepMyPlaceSnapshot snapshot, Guid destinationItemId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var generation = Interlocked.Read(ref _saveGeneration);
        var user = _users.GetUserById(snapshot.Source.History.UserId);
        var destination = Indexed(destinationItemId);
        var hydrated = _repository.RetrieveItem(destinationItemId);
        if (!_settings.Current.KeepMyPlaceEnabled || !Eligible(user) || destination is null || !RealLocal(destination)
            || hydrated is null || !RealLocal(hydrated) || !_visible.IsVisible(user!.Id, destination.Id)
            || Media(hydrated) is not { } media || !SourceAdmitted(user, snapshot))
        {
            return false;
        }

        var source = snapshot.Source;
        var live = Indexed(source.Media.ItemId);
        if (live is not null)
        {
            var liveHistory = _repository.RetrieveItem(live.Id);

            // Metadata enrichment, reset or source progress after capture must be used, never stale state.
            if (liveHistory?.UserData is null || Media(liveHistory) is not { } liveMedia || !KeepMyPlacePolicy.Matches(source.Media.Identity, liveMedia.Identity))
            {
                return false;
            }

            source = new(liveMedia, History(user.Id, _data.GetUserData(user, liveHistory)));
        }

        // Loaded row existence is authoritative even for an explicit manual reset to zero.
        // Unknown hydration is not proof of a pristine destination.
        if (hydrated.UserData is null)
        {
            return false;
        }

        var current = _data.GetUserData(user, hydrated);
        if (current is null)
        {
            return false;
        }

        var original = History(user.Id, current);
        var active = _sessions.Sessions.Any(s => s.NowPlayingItem?.Id == destinationItemId);
        var context = new KeepMyPlaceContext(user.Id, true, true, true, true, true, active, DestinationHistoryObserved: hydrated.UserData.Any(r => r.UserId == user.Id));
        var decision = KeepMyPlacePolicy.Decide(source, media, original, context);
        if (!decision.Apply || generation != Interlocked.Read(ref _saveGeneration) || !_settings.Current.KeepMyPlaceEnabled
            || !Eligible(_users.GetUserById(user.Id)) || !_visible.IsVisible(user.Id, destination.Id) || !SourceAdmitted(user, snapshot)
            || _sessions.Sessions.Any(s => s.NowPlayingItem?.Id == destinationItemId))
        {
            return false;
        }

        // Recheck the detached pristine value and row marker immediately before the synchronous core call.
        var final = _repository.RetrieveItem(destinationItemId);
        if (final?.UserData is null || !RealLocal(final) || Media(final) is not { } finalMedia
            || final.UserData.Any(r => r.UserId == user.Id) || History(user.Id, _data.GetUserData(user, final)) != original
            || KeepMyPlacePolicy.Decide(source, finalMedia, original, context) != decision
            || !_visible.IsVisible(user.Id, destinationItemId)
            || generation != Interlocked.Read(ref _saveGeneration))
        {
            return false;
        }

        var update = new UserItemData
        {
            Key = current.Key,
            Played = decision.Played,
            PlaybackPositionTicks = decision.PositionTicks,
            PlayCount = decision.PlayCount,
            LastPlayedDate = decision.LastPlayedUtc,
            IsFavorite = current.IsFavorite,
            Rating = current.Rating,
            Likes = current.Likes,
            AudioStreamIndex = current.AudioStreamIndex,
            SubtitleStreamIndex = current.SubtitleStreamIndex
        };
        _data.SaveUserData(user, destination, update, UserDataSaveReason.UpdateUserData, cancellationToken);
        return true;
    }

    private static KeepMyPlaceHistory History(Guid userId, UserItemData? data) => data is null ? new(userId) : new(userId, data.Played, data.PlaybackPositionTicks, data.PlayCount, data.LastPlayedDate is { } date ? DateTime.SpecifyKind(date, DateTimeKind.Utc) : null, data.IsFavorite || data.Rating.HasValue || data.Likes.HasValue);

    private static Dictionary<string, string> Ids(BaseItem item) => item.ProviderIds
        .Where(p => p.Key is "Imdb" or "Tmdb" or "Tvdb" && !string.IsNullOrWhiteSpace(p.Value))
        .ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);

    private static KeepMyPlaceMedia? Media(BaseItem item) => item switch
    {
        Movie movie => new(movie.Id, new(KeepMyPlaceKind.Movie, Ids(movie)), movie.RunTimeTicks, Edition(movie.Path)),
        Episode { Series: { } series } episode => new(episode.Id, new(KeepMyPlaceKind.Episode, Ids(series), episode.ParentIndexNumber, episode.IndexNumber, episode.IndexNumberEnd, Ids(episode)), episode.RunTimeTicks, Edition(episode.Path)),
        _ => null
    };

    // Core 12.1 has no edition field. Recognize only the explicit filename marker; never infer cuts from titles.
    private static string? Edition(string? path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        if (name is null)
        {
            return null;
        }

        var start = name.IndexOf("{edition-", StringComparison.OrdinalIgnoreCase);
        var end = start < 0 ? -1 : name.IndexOf('}', start);
        return end > start + 9 ? name[(start + 9)..end] : null;
    }

    private List<KeepMyPlaceSnapshot>? CaptureItem(BaseItem item)
    {
        if (_state.Status != StateStatus.Ready || _federated.Find(item.Id) is not { } owner)
        {
            return null;
        }

        var hydrated = _repository.RetrieveItem(item.Id);

        if (hydrated?.UserData is null || Media(hydrated) is not { } media)
        {
            return null;
        }

        var users = _users.GetUsers().Take(501).ToArray();
        if (users.Length > 500)
        {
            return null;
        }

        var folderIds = _library.GetCollectionFolders(item).Select(f => f.Id).Distinct().Take(17).ToArray();
        if (folderIds.Length is 0 or > 16)
        {
            return null;
        }

        var captures = new List<KeepMyPlaceSnapshot>();
        foreach (var user in users)
        {
            if (!Eligible(user) || !Admitted(user, owner) || !_visible.IsVisible(user.Id, item.Id))
            {
                continue;
            }

            var progress = History(user.Id, _data.GetUserData(user, hydrated));
            if (progress.Played || progress.PositionTicks > 0 || hydrated.UserData.Any(r => r.UserId == user.Id))
            {
                captures.Add(new(new(media, progress), owner, _time.GetUtcNow().UtcDateTime, folderIds));
            }
        }

        return captures;
    }

    private bool Eligible(User? user) => user is not null && !user.HasPermission(PermissionKind.IsDisabled) && user.HasPermission(PermissionKind.EnableMediaPlayback)
        && !_state.Current.PeerRole.Shares.Any(s => s.ShareUserId == user.Id);

    private bool SourceAdmitted(User user, KeepMyPlaceSnapshot snapshot)
    {
        if (!Admitted(user, snapshot.Owner))
        {
            return false;
        }

        if (Indexed(snapshot.Source.Media.ItemId) is { } source)
        {
            return _federated.Find(source.Id) == snapshot.Owner && _visible.IsVisible(user.Id, source.Id);
        }

        // After removal, retain only unrestricted profiles with a currently visible original library.
        return !JellyfinCallerUsers.Describe(user).IsRestricted
            && user.GetPreferenceValues<string>(PreferenceKind.BlockedTags).Length == 0
            && snapshot.SourceFolderIds is { Count: > 0 } folders && folders.Any(id => _visible.IsVisible(user.Id, id));
    }

    private bool Admitted(User user, FederatedItem owner)
    {
        if (_state.Status != StateStatus.Ready || _state.Current.Home.Visibility.HiddenFriendTitles.Contains(user.Id))
        {
            return false;
        }

        var state = _state.Current;
        var peer = state.Home.Peers.FirstOrDefault(p => p.PeerKey == owner.PeerKey);
        if (peer is not { Enabled: true } || !peer.Libraries.Any(l => l.MappingKey == owner.MappingKey && l.Import))
        {
            return false;
        }

        var access = LibraryFolders.AccessOfItem(state, owner.PeerKey, owner.MappingKey, owner.LibKey, owner.IsCopy);
        return access is not null && AccessPolicy.Admitted(JellyfinCallerUsers.Describe(user), access, state.PeerRole.Shares.Select(s => s.ShareUserId).ToHashSet());
    }

    private bool RealLocal(BaseItem item) => item is Video { IsShortcut: false, IsPlaceHolder: false, VideoType: VideoType.VideoFile }
        && item.LocationType == LocationType.FileSystem && Path.IsPathFullyQualified(item.Path)
        && !string.Equals(Path.GetExtension(item.Path), ".strm", StringComparison.OrdinalIgnoreCase)
        && !item.Tags.Any(LibraryFolders.IsShoalTag)
        && _federated.Find(item.Id) is null && !UnderStubRoot(item.Path);

    private bool UnderStubRoot(string path)
    {
        var root = _state.Current.StubRoot;
        return string.IsNullOrWhiteSpace(root) || Path.GetFullPath(path).StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar, PathComparison);
    }

    private BaseItem? Indexed(Guid itemId)
    {
#pragma warning disable RS0030 // Classification/capture only. Every transfer separately requires live per-user visibility and Shoal admission.
        return _library.GetItemById(itemId);
#pragma warning restore RS0030
    }
}
