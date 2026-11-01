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

public sealed class RemovalDestinationAccessTests
{
    [Fact]
    public void ModernLibraryAdmitsBothNormalUsersDespiteLegacyAdminOnlyMapping()
    {
        using var f = new Fixture();
        f.Mapping.Access = new() { Mode = AccessMode.Selected };
        f.Library.Access = f.Both();
        f.Adapter.VerifyAccess(f.Target, f.History);
    }

    [Fact]
    public void ModernLibraryDenialCannotBeBypassedByPermissiveLegacyMapping()
    {
        using var f = new Fixture();
        f.Mapping.Access = new() { Mode = AccessMode.Everyone, IncludeRestricted = true };
        f.Library.Access = new() { Mode = AccessMode.Selected, SelectedUserIds = [f.Users[0].Id] };
        // P30-EXTRA-USER-REFUSES: the second local user cannot see the friend's library; the refusal stays, and now names its reason.
        Assert.Equal("accessNotShared", Assert.Throws<InvalidOperationException>(() => f.Adapter.VerifyAccess(f.Target, f.History)).Message);
    }

    [Fact]
    public void LegacyDestinationUsesMappingRuleRatherThanItsStoredLibraryCopy()
    {
        using var f = new Fixture();
        f.Library.PathRel = Fixture.Peer + "/movies";
        f.Library.Access = new() { Mode = AccessMode.Selected };
        f.Mapping.Access = f.Both();
        f.Adapter.VerifyAccess(f.Target, f.History);
        f.Mapping.Access = new() { Mode = AccessMode.Selected };
        f.Library.Access = f.Both();
        Assert.Throws<InvalidOperationException>(() => f.Adapter.VerifyAccess(f.Target, f.History));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("ambiguous")]
    [InlineData("removedMapping")]
    public void UnknownOrAmbiguousDestinationOwnershipRefuses(string reason)
    {
        using var f = new Fixture();
        if (reason == "missing") f.State.Home.Libraries.Clear();
        if (reason == "removedMapping") f.State.Home.Peers[0].Libraries.Clear();
        if (reason == "ambiguous") f.State.Home.Libraries.Add(new() { LibKey = "bbbbbbbbbbbbbbbb", PathRel = "libs/bbbbbbbbbbbbbbbb", Type = "movies", Feeds = [Fixture.Peer], Access = f.Both() });
        Assert.Throws<InvalidOperationException>(() => f.Adapter.VerifyAccess(f.Target, f.History));
    }

    [Theory]
    [InlineData("access")]
    [InlineData("placement")]
    [InlineData("feed")]
    public void EffectiveLibraryChangeInvalidatesPreviouslyDiscoveredSelectionWithoutTouchingBytes(string change)
    {
        using var f = new Fixture();
        var target = Assert.Single(f.Adapter.Discover(Fixture.Peer));
        if (change == "access") f.Library.Access = new() { Mode = AccessMode.Selected };
        if (change == "placement") f.Library.Placement = LibraryPlacement.Main;
        if (change == "feed") f.Library.Feeds.Clear();
        Assert.Throws<InvalidOperationException>(() => f.Adapter.Revalidate(target));
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(f.Movie.Path));
    }

    private sealed class Fixture : IDisposable
    {
        internal const string Peer = "11111111111111111111111111111111";
        private readonly TempDirectory _dir = new();
        internal readonly ShoalState State = new();
        internal readonly User[] Users = [Account(), Account()];
        internal readonly LibraryMapping Mapping = new() { MappingKey = "movies-1", Subfolder = "movies", CollectionType = "movies", Import = true };
        internal readonly ShoalLibrary Library = new() { LibKey = "aaaaaaaaaaaaaaaa", PathRel = "libs/aaaaaaaaaaaaaaaa", Type = "movies", Feeds = [Peer] };
        internal readonly SingleFileMovie Movie;
        internal readonly RemovalTarget Target;
        internal readonly JellyfinRemovalLibrary Adapter;
        internal readonly RemovalUserState[] History;

        internal Fixture()
        {
            State.StubRoot = _dir.Combine("stubs");
            State.Home.Peers.Add(new() { PeerKey = Peer, Enabled = true, Pairing = new() { Method = PairingMethod.Invite }, Libraries = [Mapping] });
            State.Home.Libraries.Add(Library);
            Library.Access = Both();
            var root = _dir.Combine("media");
            Directory.CreateDirectory(root);
            var path = Path.Combine(root, "movie.mp4");
            File.WriteAllBytes(path, [1, 2, 3]);
            Movie = new() { Id = Guid.NewGuid(), Name = "Fixture", Path = path, VideoType = VideoType.VideoFile, RunTimeTicks = 200_000_000, ProviderIds = new() { ["Tmdb"] = "123" } };
            Target = new() { PeerKey = Peer, MappingKey = Mapping.MappingKey, PeerItemId = Guid.NewGuid(), LocalItemId = Movie.Id, LibraryId = Guid.NewGuid(), Title = "Fixture", Root = root, Path = path, RecoveryRoot = _dir.Combine(".shoal-recovery"), ProviderIds = Movie.ProviderIds, RuntimeTicks = Movie.RunTimeTicks.Value, Bytes = 3, Generation = "fixture" };
            History = Users.Select(u => new RemovalUserState(u.Id, true, false, 1, 1, null, false, null, null, null, null)).ToArray();
            var library = RemoveServerGhostTests.CoreProxy<ILibraryManager>.Create((m, _) => m.Name switch
            {
                "GetItemList" => new List<BaseItem> { Movie },
                "GetVirtualFolders" => new List<VirtualFolderInfo> { new() { ItemId = Target.LibraryId.ToString("N"), Locations = [root] } },
                _ => RemoveServerGhostTests.CoreProxy<ILibraryManager>.Empty(m.ReturnType),
            });
            var users = RemoveServerGhostTests.CoreProxy<IUserManager>.Create((m, args) => m.Name == "GetUserById" ? Users.SingleOrDefault(u => u.Id == (Guid)args[0]!) : RemoveServerGhostTests.CoreProxy<IUserManager>.Empty(m.ReturnType));
            var state = RemoveServerGhostTests.CoreProxy<IStateStore>.Create((m, _) => m.Name == "get_Status" ? StateStatus.Ready : m.Name == "get_Current" ? State : null);
            var catalogs = new CatalogStore(new ShoalPaths(_dir.Path), NullLogger<CatalogStore>.Instance);
            catalogs.Save(new() { PeerKey = Peer, Entries = [new() { Kind = EntryKind.Movie, State = EntryState.DuplicateLocal, MappingKey = Mapping.MappingKey, PeerItemId = Target.PeerItemId.ToString("N"), StubKey = "bbbbbbbbbbbbbbbb", Title = Movie.Name, ProviderIds = Movie.ProviderIds }] });
            Adapter = new(library, null!, users, RemoveServerGhostTests.CoreProxy<IUserDataManager>.Create(), RemoveServerGhostTests.CoreProxy<ISessionManager>.Create(), RemoveServerGhostTests.CoreProxy<IFederatedItems>.Create(), RemoveServerGhostTests.CoreProxy<IVisibleItems>.Create((_, _) => true), state, catalogs, null!, new FakeRefresh(), new GroupBook(new MemoryGroupStore(), NullLogger.Instance));
        }

        internal LibraryAccess Both() => new() { Mode = AccessMode.Selected, SelectedUserIds = Users.Select(u => u.Id).ToList() };

        public void Dispose() { Adapter.Dispose(); _dir.Dispose(); }

        private static User Account()
        {
            var user = new User("normal", "test", "test") { Id = Guid.NewGuid() };
            user.Permissions.Add(new Permission(PermissionKind.EnableMediaPlayback, true));
            return user;
        }
    }

    private sealed class SingleFileMovie : Movie
    {
        public override bool HasLocalAlternateVersions => false;
    }
}
