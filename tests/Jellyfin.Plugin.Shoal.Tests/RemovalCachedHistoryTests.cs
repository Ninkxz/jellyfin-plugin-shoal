using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.Shoal.Authorization;
using Jellyfin.Plugin.Shoal.LocalRemoval;
using Jellyfin.Plugin.Shoal.Sync;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Shoal.Tests;

public sealed class RemovalCachedHistoryTests
{
    [Fact]
    public async Task RestoreUpdatesDetachedDurableRowsAndTheApiFacingCachedItemForEachUser()
    {
        await using var f = await Fixture.Create();
        await f.Restore();
        Assert.Equal(f.Shared, f.Durable.Values.ToArray());
        Assert.Equal(f.Shared, f.Cached.UserData!.Select(Fixture.State).ToArray());
        Assert.Same(f.Cached, Assert.Single(f.SaveTargets));
        Assert.NotSame(f.Cached, f.LastRepositoryItem);
    }

    [Fact]
    public async Task AlreadyCorrectDurableHistoryStillRefreshesStaleCachedRowsWithoutRewindingNewerLocalEdits()
    {
        await using var f = await Fixture.Create();
        var independent = f.Baseline[0] with { PlayCount = 6, LastPlayedUtc = f.Shared[0].LastPlayedUtc!.Value.AddMinutes(2), Favorite = false, Rating = 7, Likes = true, Position = 30_000_000 };
        f.Durable[independent.UserId] = independent;
        await f.Restore();
        Assert.Equal(independent, f.Durable[independent.UserId]);
        Assert.Equal(independent, Fixture.State(f.Cached.UserData!.Single(r => r.UserId == independent.UserId)));
        Assert.Same(f.Cached, Assert.Single(f.SaveTargets));
        Assert.Equal(f.Baseline[1], f.Durable[f.Baseline[1].UserId]);
    }

    [Fact]
    public async Task DurableDesiredValueWithOldCacheIsSynchronizedEvenWhenHistoryPolicyNeedsNoWrite()
    {
        await using var f = await Fixture.Create();
        f.Durable[f.Shared[0].UserId] = f.Shared[0];
        await f.Restore();
        Assert.Equal(f.Shared[0], Fixture.State(f.Cached.UserData!.First()));
        Assert.Same(f.Cached, Assert.Single(f.SaveTargets));
    }

    [Fact]
    public async Task CachedItemWithDifferentPhysicalBindingCannotReceivePersonalState()
    {
        await using var f = await Fixture.Create();
        f.Cached.Path += ".different";
        await Assert.ThrowsAsync<InvalidOperationException>(f.Restore);
        Assert.Empty(f.SaveTargets);
        Assert.Equal(f.Baseline, f.Durable.Values.ToArray());
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SyncRig _rig;
        private readonly User[] _users;
        internal readonly RemovalUserState[] Baseline;
        internal readonly RemovalUserState[] Shared;
        internal readonly Dictionary<Guid, RemovalUserState> Durable = new();
        internal readonly List<BaseItem> SaveTargets = [];
        internal readonly Movie Cached;
        internal readonly RemovalTarget Target;
        internal readonly JellyfinRemovalLibrary Adapter;
        internal BaseItem? LastRepositoryItem;

        private Fixture(SyncRig rig)
        {
            _rig = rig;
            _users = [Account(), Account()];
            Baseline = [new(_users[0].Id, true, true, 0, 4, new DateTime(2026, 10, 26, 0, 57, 52, DateTimeKind.Utc), true, 9, true, 1, 2), new(_users[1].Id, true, false, 40_000_000, 1, null, false, 5, false, 0, 1)];
            Shared = [Baseline[0] with { PlayCount = 5, LastPlayedUtc = new DateTime(2026, 10, 26, 1, 31, 12, DateTimeKind.Utc) }, Baseline[1]];
            foreach (var row in Baseline) Durable.Add(row.UserId, row);
            var root = rig.Dir.Combine("physical");
            Directory.CreateDirectory(root);
            var path = Path.Combine(root, "movie.mp4");
            File.WriteAllBytes(path, [1, 2, 3]);
            Target = new() { PeerKey = SyncRig.PeerKey, MappingKey = SyncRig.MappingKey, PeerItemId = Guid.NewGuid(), LocalItemId = Guid.NewGuid(), LibraryId = Guid.NewGuid(), Title = "Fixture", Root = root, Path = path, RecoveryRoot = rig.Dir.Combine("held"), ProviderIds = new Dictionary<string, string> { ["Tmdb"] = "123" }, RuntimeTicks = 200_000_000, Bytes = 3, Generation = "fixture" };
            Cached = Item();
            var library = RemoveServerGhostTests.CoreProxy<ILibraryManager>.Create((m, _) => m.Name switch
            {
                "GetItemList" => new List<BaseItem> { Item() },
                "GetItemById" => Cached,
                _ => RemoveServerGhostTests.CoreProxy<ILibraryManager>.Empty(m.ReturnType),
            });
            var repository = RemoveServerGhostTests.CoreProxy<IItemRepository>.Create((m, _) => m.Name == "RetrieveItem" ? LastRepositoryItem = Item() : RemoveServerGhostTests.CoreProxy<IItemRepository>.Empty(m.ReturnType));
            var users = RemoveServerGhostTests.CoreProxy<IUserManager>.Create((m, args) => m.Name == "GetUserById" ? _users.SingleOrDefault(u => u.Id == (Guid)args[0]!) : RemoveServerGhostTests.CoreProxy<IUserManager>.Empty(m.ReturnType));
            var data = RemoveServerGhostTests.CoreProxy<IUserDataManager>.Create((m, args) =>
            {
                if (m.Name == "GetUserData") return Data(State(((BaseItem)args[1]!).UserData!.Single(r => r.UserId == ((User)args[0]!).Id)));
                if (m.Name == "SaveUserData")
                {
                    var user = (User)args[0]!;
                    var passedItem = (BaseItem)args[1]!;
                    var update = (UserItemData)args[2]!;
                    Durable[user.Id] = new(user.Id, true, update.Played, update.PlaybackPositionTicks, update.PlayCount, update.LastPlayedDate, update.IsFavorite, update.Rating, update.Likes, update.AudioStreamIndex, update.SubtitleStreamIndex);
                    passedItem.UserData = Durable.Values.Select(Row).ToArray(); // Core refreshes only the exact passed instance.
                    SaveTargets.Add(passedItem);
                }

                return null;
            });
            Adapter = new(library, repository, users, data, RemoveServerGhostTests.CoreProxy<ISessionManager>.Create(), RemoveServerGhostTests.CoreProxy<IFederatedItems>.Create(), RemoveServerGhostTests.CoreProxy<IVisibleItems>.Create((_, _) => true), rig.State, rig.Catalogs, rig.Sync, rig.Refresh, new GroupBook(new MemoryGroupStore(), NullLogger.Instance)) { Delay = (_, _) => Task.CompletedTask };
        }

        internal static async Task<Fixture> Create() => new(await SyncRig.CreateAsync());

        internal Task Restore() => Adapter.RestoreAsync(Target, Baseline, Shared, default);

        internal static RemovalUserState State(UserData row) => new(row.UserId, true, row.Played, row.PlaybackPositionTicks, row.PlayCount, row.LastPlayedDate, row.IsFavorite, row.Rating, row.Likes, row.AudioStreamIndex, row.SubtitleStreamIndex);

        public async ValueTask DisposeAsync() { Adapter.Dispose(); await _rig.DisposeAsync(); }

        private static User Account()
        {
            var user = new User("normal", "test", "test") { Id = Guid.NewGuid() };
            user.Permissions.Add(new Permission(PermissionKind.EnableMediaPlayback, true));
            return user;
        }

        private static UserItemData Data(RemovalUserState row) => new() { Key = "fixture", Played = row.Played, PlaybackPositionTicks = row.Position, PlayCount = row.PlayCount, LastPlayedDate = row.LastPlayedUtc, IsFavorite = row.Favorite, Likes = row.Likes, Rating = row.Rating, AudioStreamIndex = row.AudioIndex, SubtitleStreamIndex = row.SubtitleIndex };

        private UserData Row(RemovalUserState row) => new() { CustomDataKey = "fixture", ItemId = Target.LocalItemId, Item = null!, User = null!, UserId = row.UserId, Played = row.Played, PlaybackPositionTicks = row.Position, PlayCount = row.PlayCount, LastPlayedDate = row.LastPlayedUtc, IsFavorite = row.Favorite, Likes = row.Likes, Rating = row.Rating, AudioStreamIndex = row.AudioIndex, SubtitleStreamIndex = row.SubtitleIndex };

        private Movie Item() => new() { Id = Target.LocalItemId, Path = Target.Path, RunTimeTicks = Target.RuntimeTicks, ProviderIds = new(Target.ProviderIds), UserData = Durable.Values.Select(Row).ToArray() };
    }
}
