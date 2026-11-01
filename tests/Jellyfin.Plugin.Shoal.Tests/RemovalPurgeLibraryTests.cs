using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.Shoal.Authorization;
using Jellyfin.Plugin.Shoal.LocalRemoval;
using Jellyfin.Plugin.Shoal.State;
using Jellyfin.Plugin.Shoal.Storage;
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

public sealed class RemovalPurgeLibraryTests
{
    [Fact]
    public void DeindexedOriginalUsesFreshReplacementAndDoesNotWriteHistory()
    {
        using var f = new Fixture();
        var proof = f.Prove();
        Assert.Equal(f.History, proof.History);
        Assert.Equal(64, proof.Fingerprint.Length);
        Assert.Equal(0, f.Writes);
    }

    [Fact]
    public void EveryPersonalFieldAndAccessChangeInvalidatesTheProof()
    {
        using var f = new Fixture();
        var before = f.Prove().Fingerprint;
        f.History[0] = f.History[0] with { Rating = 7, Likes = true, Position = 40_000_000 };
        Assert.NotEqual(before, f.Prove().Fingerprint);
        before = f.Prove().Fingerprint;
        f.Library.Access.IncludeRestricted = true;
        Assert.NotEqual(before, f.Prove().Fingerprint);
        Assert.Equal(0, f.Writes);
    }

    [Theory]
    [InlineData(false, false, "equal")]
    [InlineData(false, false, "ancestor")]
    [InlineData(false, false, "descendant")]
    [InlineData(false, true, "equal")]
    [InlineData(false, true, "ancestor")]
    [InlineData(false, true, "descendant")]
    [InlineData(true, false, "equal")]
    [InlineData(true, false, "ancestor")]
    [InlineData(true, false, "descendant")]
    [InlineData(true, true, "equal")]
    [InlineData(true, true, "ancestor")]
    [InlineData(true, true, "descendant")]
    public void RecoveryOverlapRefusesAtInitialReviewAndFreshCommitRecheck(bool afterReview, bool stubRoot, string relationship)
    {
        using var f = new Fixture();
        if (afterReview) f.Prove();
        var root = relationship switch
        {
            "ancestor" => Path.GetDirectoryName(f.Target.RecoveryRoot)!,
            "descendant" => Path.Combine(f.Target.RecoveryRoot, "scanned"),
            _ => Path.Combine(f.Target.RecoveryRoot, "."),
        };
        if (stubRoot) f.State.StubRoot = root;
        else f.Folders.Add(new() { ItemId = Guid.NewGuid().ToString("N"), Locations = [root] });
        Assert.Throws<InvalidOperationException>(() => f.Prove());
        Assert.Equal(0, f.Writes);
        Assert.True(File.Exists(f.Shared.Path));
    }

    [Theory]
    [InlineData("removedUser")]
    [InlineData("disabledUser")]
    [InlineData("revokedAccess")]
    [InlineData("hidden")]
    [InlineData("scan")]
    [InlineData("playback")]
    [InlineData("originalFile")]
    [InlineData("originalIndex")]
    [InlineData("runtime")]
    [InlineData("provider")]
    [InlineData("source")]
    [InlineData("partialCatalog")]
    [InlineData("heldEntry")]
    [InlineData("feed")]
    public void ChangedEvidenceRefusesWithoutWrites(string change)
    {
        using var f = new Fixture();
        switch (change)
        {
            case "removedUser": f.Users.RemoveAt(0); break;
            case "disabledUser": f.Users[0].Permissions.Add(new Permission(PermissionKind.IsDisabled, true)); break;
            case "revokedAccess": f.Library.Access.SelectedUserIds.Remove(f.Users[0].Id); break;
            case "hidden": f.State.Home.Visibility.HiddenFriendTitles.Add(f.Users[0].Id); break;
            case "scan": f.Refresh.IsScanRunning = true; break;
            case "playback": f.Sessions.Add(new SessionInfo(null!, NullLogger.Instance) { NowPlayingItem = new MediaBrowser.Model.Dto.BaseItemDto { Id = f.Shared.Id } }); break;
            case "originalFile": File.WriteAllBytes(f.Target.Path, [1]); break;
            case "originalIndex": f.OriginalIndexed = true; break;
            case "runtime": f.Shared.RunTimeTicks++; break;
            case "provider": f.Shared.ProviderIds["Tmdb"] = "456"; break;
            case "source": File.WriteAllText(f.Shared.Path, StubSentinel.Content("cccccccccccccccc")); break;
            case "partialCatalog": f.Catalog.LastSync!.Result = SyncResultKind.Partial; f.SaveCatalog(); break;
            case "heldEntry": f.Catalog.Entries[0].State = EntryState.HeldRemoval; f.SaveCatalog(); break;
            case "feed": f.Library.Feeds.Clear(); break;
        }

        Assert.Throws<InvalidOperationException>(() => f.Prove());
        Assert.Equal(0, f.Writes);
    }

    private sealed class Fixture : IDisposable
    {
        private const string Peer = "11111111111111111111111111111111";
        private readonly TempDirectory _dir = new();
        private readonly CatalogStore _catalogs;
        internal readonly ShoalState State = new();
        internal readonly List<User> Users = [Account(), Account()];
        internal readonly ShoalLibrary Library = new() { LibKey = "aaaaaaaaaaaaaaaa", PathRel = "libs/aaaaaaaaaaaaaaaa", Type = "movies", Feeds = [Peer] };
        internal readonly FakeRefresh Refresh = new();
        internal readonly List<SessionInfo> Sessions = [];
        internal readonly List<VirtualFolderInfo> Folders = [];
        internal readonly Movie Shared;
        internal readonly RemovalTarget Target;
        internal readonly RemovalUserState[] History;
        internal readonly RemovalUserState[] Baseline;
        internal readonly CatalogDocument Catalog;
        internal readonly JellyfinRemovalLibrary Adapter;
        internal bool OriginalIndexed;
        internal int Writes;

        internal Fixture()
        {
            var root = _dir.Combine("movies");
            Directory.CreateDirectory(root);
            Folders.Add(new() { ItemId = Guid.NewGuid().ToString("N"), Locations = [root] });
            State.StubRoot = _dir.Combine("stubs");
            Directory.CreateDirectory(State.StubRoot);
            var mapping = new LibraryMapping { MappingKey = "movies-1", Subfolder = "movies", CollectionType = "movies", Import = true };
            State.Home.Peers.Add(new() { PeerKey = Peer, Enabled = true, Pairing = new() { Method = PairingMethod.Invite }, Libraries = [mapping] });
            Library.Access = new() { Mode = AccessMode.Selected, SelectedUserIds = Users.Select(u => u.Id).ToList() };
            State.Home.Libraries.Add(Library);
            Target = new() { PeerKey = Peer, MappingKey = mapping.MappingKey, PeerItemId = Guid.NewGuid(), LocalItemId = Guid.NewGuid(), LibraryId = Guid.NewGuid(), Title = "Fixture", Root = root, Path = Path.Combine(root, "original.mp4"), RecoveryRoot = _dir.Combine("held"), ProviderIds = new Dictionary<string, string> { ["Tmdb"] = "123" }, RuntimeTicks = 200_000_000, Bytes = 3, Generation = "fixture" };
            Shared = new() { Id = Guid.NewGuid(), Path = Path.Combine(State.StubRoot, "shared.strm"), RunTimeTicks = Target.RuntimeTicks, ProviderIds = new(Target.ProviderIds), UserData = [] };
            File.WriteAllText(Shared.Path, StubSentinel.Content("bbbbbbbbbbbbbbbb"));
            History = Users.Select(u => new RemovalUserState(u.Id, true, false, 1, 2, null, true, 9, true, 1, 2)).OrderBy(s => s.UserId).ToArray();
            Baseline = History.ToArray();
            Shared.UserData = History.Select(h => new UserData { CustomDataKey = "fixture", UserId = h.UserId, ItemId = Shared.Id, Item = null!, User = null! }).ToArray();
            Catalog = new() { PeerKey = Peer, CompletedOnce = true, LastSync = new() { Result = SyncResultKind.Ok }, Entries = [new() { Kind = EntryKind.Movie, State = EntryState.Active, MappingKey = mapping.MappingKey, PeerItemId = Target.PeerItemId.ToString("N"), StubKey = "bbbbbbbbbbbbbbbb", StubRelPath = "Fixture/Fixture.strm", Title = "Fixture", RuntimeTicks = Target.RuntimeTicks, ProviderIds = new(Target.ProviderIds) }] };
            _catalogs = new(new ShoalPaths(_dir.Path), NullLogger<CatalogStore>.Instance);
            SaveCatalog();
            var library = RemoveServerGhostTests.CoreProxy<ILibraryManager>.Create((m, args) => m.Name switch
            {
                "GetItemList" => ((InternalItemsQuery)args[0]!).ItemIds is { Length: > 0 } ids ? new List<BaseItem>(ids.Contains(Target.LocalItemId) && OriginalIndexed ? [new Movie { Id = Target.LocalItemId }] : []) : new List<BaseItem> { Shared },
                "GetVirtualFolders" => Folders,
                _ => RemoveServerGhostTests.CoreProxy<ILibraryManager>.Empty(m.ReturnType),
            });
            var repository = RemoveServerGhostTests.CoreProxy<IItemRepository>.Create((m, _) => m.Name == "RetrieveItem" ? Shared : null);
            var users = RemoveServerGhostTests.CoreProxy<IUserManager>.Create((m, args) => m.Name == "GetUsers" ? Users : m.Name == "GetUserById" ? Users.SingleOrDefault(u => u.Id == (Guid)args[0]!) : null);
            var data = RemoveServerGhostTests.CoreProxy<IUserDataManager>.Create((m, args) =>
            {
                if (m.Name == "SaveUserData") Writes++;
                if (m.Name != "GetUserData") return null;
                var row = History.Single(h => h.UserId == ((User)args[0]!).Id);
                return new UserItemData { Key = "fixture", Played = row.Played, PlaybackPositionTicks = row.Position, PlayCount = row.PlayCount, LastPlayedDate = row.LastPlayedUtc, IsFavorite = row.Favorite, Likes = row.Likes, Rating = row.Rating, AudioStreamIndex = row.AudioIndex, SubtitleStreamIndex = row.SubtitleIndex };
            });
            var sessions = RemoveServerGhostTests.CoreProxy<ISessionManager>.Create((m, _) => m.Name == "get_Sessions" ? Sessions : null);
            var state = RemoveServerGhostTests.CoreProxy<IStateStore>.Create((m, _) => m.Name == "get_Status" ? StateStatus.Ready : m.Name == "get_Current" ? State : null);
            var federated = RemoveServerGhostTests.CoreProxy<IFederatedItems>.Create((_, args) => (Guid)args[0]! == Shared.Id ? new FederatedItem(Peer, mapping.MappingKey, Library.LibKey) : null);
            Adapter = new(library, repository, users, data, sessions, federated, RemoveServerGhostTests.CoreProxy<IVisibleItems>.Create((_, _) => true), state, _catalogs, null!, Refresh, new GroupBook(new MemoryGroupStore(), NullLogger.Instance));
        }

        internal RemovalPurgeState Prove() => Adapter.VerifyPurgeState(Target, Shared.Id, Baseline);
        internal void SaveCatalog() => _catalogs.Save(Catalog);
        public void Dispose() { Adapter.Dispose(); _dir.Dispose(); }

        private static User Account()
        {
            var user = new User("normal", "test", "test") { Id = Guid.NewGuid() };
            user.Permissions.Add(new Permission(PermissionKind.EnableMediaPlayback, true));
            return user;
        }
    }
}
