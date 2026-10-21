using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.State;
using Jellyfin.Plugin.Shoal.Visibility;
using Xunit;

namespace Jellyfin.Plugin.Shoal.Tests;

/// <summary>
/// 2.9.3 K4 (item 3): a fed mapping's own access ("Hidden from everyone") hides nothing; the shared library's access decides. Fixture: <see cref="LibraryViewFixture"/>.
/// </summary>
public sealed class LibraryFoldersTests
{
    [Fact]
    public async Task AccessOfItem_FedLibrary_IgnoresHiddenMappingAccess()
    {
        using var f = new LibraryViewFixture();
        await f.SeedAsync();
        var state = f.State.Current;

        var access = LibraryFolders.AccessOfItem(state, LibraryViewFixture.Hops, "movies-1", LibraryViewFixture.FedMovies);

        Assert.NotNull(access);
        Assert.Equal(AccessMode.Everyone, access.Mode); // the library's, not the mapping's Selected-with-nobody
        Assert.Equal(AccessMode.Everyone, LibraryFolders.AccessOfMapping(state, LibraryViewFixture.Hops, state.Home.Peers[0].Libraries[0]).Mode);

        // Item 3 case (b): a stub still in the legacy folder (no libKey in its path) answers with the mapping's access, so playback is refused for everyone.
        var legacy = LibraryFolders.AccessOfItem(state, LibraryViewFixture.Hops, "movies-1", string.Empty);
        Assert.Equal(AccessMode.Selected, legacy!.Mode);
        Assert.Empty(legacy.SelectedUserIds);
    }
}

/// <summary>2.9.3 K4: what the visibility service writes into accounts for a fed mapping whose own access is hidden.</summary>
public sealed class VisibilityServiceTests
{
    [Fact]
    public async Task FedMapping_HiddenMappingAccess_SharedLibraryAdmitsEveryone()
    {
        using var f = new LibraryViewFixture();
        await f.SeedAsync();
        var local = f.Libraries.AddLocal("Home videos");
        var bob = f.Accounts.Add("bob", all: false, folders: [local.Id]);

        await f.Visibility.ReconcileAsync();

        Assert.Contains(f.FriendsMovies!.Id, f.Accounts.Get(bob.Id).EnabledFolders);
    }

    [Fact]
    public async Task LegacyLibraryStillPresent_UsesMappingAccess()
    {
        // Item 3 case (a), the R3 question: a Jellyfin library left at the legacy path <StubRoot>/<peerKey>/<Subfolder> is still registered with the mapping's
        // own access, even while the connection feeds a shared library of the type. Documents today's behaviour (K9 is the reserved Plan B).
        using var f = new LibraryViewFixture();
        await f.SeedAsync();
        var local = f.Libraries.AddLocal("Home videos");
        var leftover = f.Libraries.AddMain("Hops Movies", "movies");
        f.Libraries.Change(leftover.Id, l => l with { Locations = [VisibilityService.MappingPath(f.StubRoot, LibraryViewFixture.Hops, "movies")!] });
        var bob = f.Accounts.Add("bob", all: false, folders: [local.Id]);

        await f.Visibility.ReconcileAsync();

        var folders = f.Accounts.Get(bob.Id).EnabledFolders;
        Assert.Contains(f.FriendsMovies!.Id, folders);  // the shared library admits him
        Assert.DoesNotContain(leftover.Id, folders);   // the leftover legacy library follows the mapping (nobody)
    }
}
