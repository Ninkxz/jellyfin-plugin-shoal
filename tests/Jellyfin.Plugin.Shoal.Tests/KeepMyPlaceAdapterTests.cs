using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.Shoal.Api.Admin;
using Jellyfin.Plugin.Shoal.Authorization;
using Jellyfin.Plugin.Shoal.Configuration;
using Jellyfin.Plugin.Shoal.Progress;
using Jellyfin.Plugin.Shoal.Registrations;
using Jellyfin.Plugin.Shoal.State;
using Jellyfin.Plugin.Shoal.Storage;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Jellyfin.Plugin.Shoal.Tests;

public sealed class KeepMyPlaceAdapterTests
{
    [Fact]
    public async Task RegistrationResolvesOneHostAndOneRetentionSingleton()
    {
        using var dir = new TempDirectory();
        var services = new ServiceCollection();
        services.AddSingleton(new ShoalPaths(dir.Path));
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<ISettingsAccessor>(new Settings());
        services.AddSingleton(RemoveServerGhostTests.CoreProxy<ILibraryManager>.Create());
        services.AddSingleton(RemoveServerGhostTests.CoreProxy<IItemRepository>.Create());
        services.AddSingleton(RemoveServerGhostTests.CoreProxy<IUserManager>.Create());
        services.AddSingleton(RemoveServerGhostTests.CoreProxy<IUserDataManager>.Create());
        services.AddSingleton(RemoveServerGhostTests.CoreProxy<ISessionManager>.Create());
        services.AddSingleton(RemoveServerGhostTests.CoreProxy<IFederatedItems>.Create());
        services.AddSingleton(RemoveServerGhostTests.CoreProxy<IVisibleItems>.Create());
        services.AddSingleton(RemoveServerGhostTests.CoreProxy<IStateStore>.Create());
        services.AddSingleton<ILogger<KeepMyPlaceHost>>(NullLogger<KeepMyPlaceHost>.Instance);
        services.AddShoalKeepMyPlace();
        services.AddShoalKeepMyPlace();
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        Assert.Single(provider.GetServices<IHostedService>());
        Assert.Same(provider.GetRequiredService<KeepMyPlaceService>(), provider.GetRequiredService<IKeepMyPlaceRetention>());
        Assert.False(File.Exists(Path.Combine(dir.Path, "shoal", "keep-my-place.json")));
    }

    [Fact]
    public void SameProviderKeyDistinctItemsTransfersWithoutMutatingCachedData()
    {
        var rig = new Rig();
        Assert.True(rig.Apply());
        Assert.NotNull(rig.Saved);
        Assert.NotSame(rig.Current, rig.Saved);
        Assert.Equal(0, rig.Current.PlaybackPositionTicks);
        Assert.Equal(100, rig.Saved.PlaybackPositionTicks);
        Assert.Equal(2, rig.Saved.AudioStreamIndex);
        Assert.Equal(3, rig.Saved.SubtitleStreamIndex);
    }

    [Fact]
    public void ConcurrentMutationOfSameCachedObjectIsDetected()
    {
        var rig = new Rig { MutateAtSecondRead = true };
        Assert.False(rig.Apply());
        Assert.Null(rig.Saved);
        Assert.Equal(99, rig.Current.PlaybackPositionTicks);
    }

    [Fact]
    public void ManualResetRowAndUnknownHydrationNeverGetOverwritten()
    {
        var reset = new Rig();
        reset.Destination.UserData.Add(new UserData { UserId = reset.User.Id, ItemId = reset.Destination.Id, CustomDataKey = "123", Item = null!, User = reset.User });
        Assert.False(reset.Apply());
        var unknown = new Rig();
        unknown.Destination.UserData = null!;
        Assert.False(unknown.Apply());
    }

    [Fact]
    public void AuthoritativeRepositoryRowOverridesEmptyCachedCollection()
    {
        var rig = new Rig { RepositoryHasReset = true };
        Assert.Empty(rig.Destination.UserData);
        Assert.False(rig.Apply());
        Assert.Null(rig.Saved);
    }

    [Theory]
    [InlineData("identity")]
    [InlineData("runtime")]
    [InlineData("shortcut")]
    public void DestinationMetadataChangeDuringFinalReadDenies(string change)
    {
        var rig = new Rig { FinalChange = change };
        Assert.False(rig.Apply());
        Assert.Null(rig.Saved);
    }

    [Fact]
    public void RevokedSourceHiddenDestinationDisabledPeerAndRemoteFileDeny()
    {
        var source = new Rig { Visible = false };
        Assert.False(source.Apply());
        var peer = new Rig();
        peer.State.Home.Peers[0].Enabled = false;
        Assert.False(peer.Apply());
        var mapping = new Rig();
        mapping.State.Home.Peers[0].Libraries[0].Import = false;
        Assert.False(mapping.Apply());
        var remote = new Rig();
        remote.Destination.IsShortcut = true;
        Assert.False(remote.Apply());
    }

    [Fact]
    public void RemovedSourceRequiresOriginalFolderAndUnrestrictedUser()
    {
        var ordinary = new Rig { SourceRemoved = true };
        Assert.True(ordinary.Apply());
        var restricted = new Rig { SourceRemoved = true };
        restricted.User.MaxParentalRatingScore = 100;
        Assert.False(restricted.Apply());
        var blocked = new Rig { SourceRemoved = true };
        blocked.User.Preferences.Add(new Preference(PreferenceKind.BlockedTags, "blocked"));
        Assert.False(blocked.Apply());
        var unknownFolder = new Rig { SourceRemoved = true };
        Assert.False(unknownFolder.Adapter.ApplyFresh(unknownFolder.Snapshot with { SourceFolderIds = [] }, unknownFolder.Destination.Id, CancellationToken.None));
    }

    [Fact]
    public async Task DisabledHostStartStopDisposeUnsubscribesOnlyOnce()
    {
        var rig = new Rig();
        rig.Settings.Current.KeepMyPlaceEnabled = false;
        var added = 0;
        var removed = 0;
        var core = RemoveServerGhostTests.CoreProxy<ILibraryManager>.Create((method, _) =>
        {
            if (method.Name.StartsWith("add_", StringComparison.Ordinal))
            {
                added++;
            }
            else if (method.Name.StartsWith("remove_", StringComparison.Ordinal))
            {
                removed++;
            }

            return null;
        });
        using var dir = new TempDirectory();
        using var service = new KeepMyPlaceService(rig.Adapter, new KeepMyPlaceStore(dir.Combine("history.json"), TimeProvider.System), () => false);
        var host = new KeepMyPlaceHost(core, rig.Data, rig.Adapter, service, rig.Settings, NullLogger<KeepMyPlaceHost>.Instance);
        await host.StartAsync(default);
        await host.StartAsync(default);
        await host.StopAsync(default);
        await host.StopAsync(default);
        await host.DisposeAsync();
        await host.DisposeAsync();
        Assert.Equal(2, added);
        Assert.Equal(2, removed);
        Assert.False(File.Exists(dir.Combine("history.json")));
    }

    private sealed class Rig
    {
        private int _destinationReads;
        private int _repositoryReads;
        private readonly TestMovie _source;

        public Rig()
        {
            User = new User("test", "test", "test") { Id = Guid.NewGuid() };
            User.Permissions.Add(new Permission(PermissionKind.EnableMediaPlayback, true));
            StateSamples.Fill(State);
            State.StubRoot = Path.Combine(Path.GetTempPath(), "shoal-stubs");
            _source = new TestMovie { Id = Guid.NewGuid(), Path = Path.Combine(State.StubRoot, "source.strm"), RunTimeTicks = TimeSpan.TicksPerHour, UserData = [] };
            Destination = new TestMovie { Id = Guid.NewGuid(), Path = Path.Combine(Path.GetTempPath(), "media", "local.mkv"), RunTimeTicks = TimeSpan.TicksPerHour, UserData = [] };
            _source.ProviderIds["Tmdb"] = "123";
            Destination.ProviderIds["Tmdb"] = "123";
            var owner = new FederatedItem(StateSamples.PeerKeyA, "movies-1");
            Snapshot = new(new(new(_source.Id, new(KeepMyPlaceKind.Movie, new Dictionary<string, string> { ["Tmdb"] = "123" }), TimeSpan.TicksPerHour), new(User.Id, PositionTicks: 100)), owner, DateTime.UtcNow, [Guid.NewGuid()]);
            var library = RemoveServerGhostTests.CoreProxy<ILibraryManager>.Create((m, args) => m.Name == "GetItemById" ? (Guid)args[0]! == Destination.Id ? Destination : SourceRemoved ? null : _source : RemoveServerGhostTests.CoreProxy<ILibraryManager>.Empty(m.ReturnType));
            var users = RemoveServerGhostTests.CoreProxy<IUserManager>.Create((m, _) => m.Name == "GetUserById" ? User : RemoveServerGhostTests.CoreProxy<IUserManager>.Empty(m.ReturnType));
            Data = RemoveServerGhostTests.CoreProxy<IUserDataManager>.Create((m, args) =>
            {
                if (m.Name == "GetUserData")
                {
                    if (((BaseItem)args[1]!).Id != Destination.Id)
                    {
                        return new UserItemData { Key = "123", PlaybackPositionTicks = 100 };
                    }

                    if (++_destinationReads == 2 && MutateAtSecondRead)
                    {
                        Current.PlaybackPositionTicks = 99;
                    }

                    return Current;
                }

                if (m.Name == "SaveUserData")
                {
                    Saved = (UserItemData)args[2]!;
                }

                return null;
            });
            var state = RemoveServerGhostTests.CoreProxy<IStateStore>.Create((m, _) => m.Name == "get_Status" ? StateStatus.Ready : m.Name == "get_Current" ? State : null);
            var visible = RemoveServerGhostTests.CoreProxy<IVisibleItems>.Create((_, _) => Visible);
            var federated = RemoveServerGhostTests.CoreProxy<IFederatedItems>.Create((_, args) => (Guid)args[0]! == _source.Id ? owner : null);
            var repository = RemoveServerGhostTests.CoreProxy<IItemRepository>.Create((m, args) =>
            {
                if (m.Name != "RetrieveItem")
                {
                    return RemoveServerGhostTests.CoreProxy<IItemRepository>.Empty(m.ReturnType);
                }

                if ((Guid)args[0]! != Destination.Id)
                {
                    return SourceRemoved ? null : _source;
                }

                if (RepositoryHasReset)
                {
                    return new TestMovie { Id = Destination.Id, Path = Destination.Path, RunTimeTicks = Destination.RunTimeTicks, ProviderIds = Destination.ProviderIds, UserData = [new UserData { UserId = User.Id, ItemId = Destination.Id, CustomDataKey = "123", Item = null!, User = User }] };
                }

                if (++_repositoryReads == 2)
                {
                    if (FinalChange == "identity")
                    {
                        Destination.ProviderIds["Tmdb"] = "456";
                    }
                    else if (FinalChange == "runtime")
                    {
                        Destination.RunTimeTicks = TimeSpan.TicksPerMinute;
                    }
                    else if (FinalChange == "shortcut")
                    {
                        Destination.IsShortcut = true;
                    }
                }

                return Destination;
            });
            Adapter = new(library, repository, users, Data, RemoveServerGhostTests.CoreProxy<ISessionManager>.Create(), federated, visible, state, Settings, TimeProvider.System);
        }

        public User User { get; }

        public ShoalState State { get; } = new();

        public TestMovie Destination { get; }

        public UserItemData Current { get; } = new() { Key = "123", AudioStreamIndex = 2, SubtitleStreamIndex = 3 };

        public UserItemData? Saved { get; private set; }

        public KeepMyPlaceSnapshot Snapshot { get; }

        public JellyfinKeepMyPlaceLibrary Adapter { get; }

        public IUserDataManager Data { get; }

        public Settings Settings { get; } = new();

        public bool MutateAtSecondRead { get; init; }

        public bool SourceRemoved { get; init; }

        public bool RepositoryHasReset { get; init; }

        public string? FinalChange { get; init; }

        public bool Visible { get; init; } = true;

        public bool Apply() => Adapter.ApplyFresh(Snapshot, Destination.Id, default);
    }

    private sealed class TestMovie : Movie
    {
        public override LocationType LocationType => LocationType.FileSystem;
    }

    private sealed class Settings : ISettingsAccessor
    {
        public PluginConfiguration Current { get; } = new() { KeepMyPlaceEnabled = true };

        public void Save(PluginConfiguration configuration) => throw new NotSupportedException();
    }
}
