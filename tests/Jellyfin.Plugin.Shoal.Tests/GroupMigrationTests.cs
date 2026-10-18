using System;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.State;
using Jellyfin.Plugin.Shoal.Storage;
using Jellyfin.Plugin.Shoal.Sync;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Shoal.Tests;

/// <summary>
/// PLAN v2.9 K55: existing duplicate-peer movies become groups now; the preview writes nothing; a run records the groups (no file changes) before the copy writer
/// runs; a second run finds nothing to do.
/// </summary>
public sealed class GroupMigrationTests
{
    private const string Owner = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Member = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    [Fact]
    public async Task ThePreviewCounts_TheRunGroups_AndASecondRunHasNothingToDo()
    {
        using var dir = new TempDirectory();
        using var state = new StateStore(new ShoalPaths(dir.Path), NullLogger.Instance, new ManualTimeProvider(), null);
        await state.UpdateAsync(s =>
        {
            s.StubRoot = dir.Combine("stubs");
            foreach (var key in new[] { Owner, Member })
            {
                s.Home.Peers.Add(new PeerState { PeerKey = key, DisplayName = key[..4], BaseUrl = "http://" + key[..4] + ".tail1234.ts.net:8096", Libraries = { new LibraryMapping { MappingKey = "movies-1", RemoteName = "Movies", LocalLibraryName = key[..4], Subfolder = "movies" } } });
            }
        });
        var catalogs = new CatalogStore(new ShoalPaths(dir.Path), NullLogger.Instance);
        var owner = new CatalogDocument { PeerKey = Owner, CompletedOnce = true };
        owner.Entries.Add(new CatalogEntry { StubKey = "ownerstub0000001", PeerItemId = new string('1', 32), PeerMediaSourceId = new string('1', 32), MappingKey = "movies-1", Title = "Alien", State = EntryState.Active, StubRelPath = "Alien (1979)/Alien (1979).strm" });
        catalogs.Save(owner);
        var member = new CatalogDocument { PeerKey = Member, CompletedOnce = true };
        member.Entries.Add(new CatalogEntry { StubKey = "memberstub000001", PeerItemId = new string('2', 32), PeerMediaSourceId = new string('2', 32), MappingKey = "movies-1", Title = "Alien", State = EntryState.DuplicatePeer, DuplicateOf = "ownerstub0000001" });
        catalogs.Save(member);
        var book = new GroupBook(new MemoryGroupStore(), NullLogger.Instance);
        var migration = new GroupMigration(state, catalogs, book);

        var preview = migration.Preview();
        Assert.Equal(new GroupMigrationReport(1, 1, 0, false), preview);
        Assert.Empty(book.All()); // a preview writes nothing

        var run = await migration.RunAsync();
        Assert.Equal(1, run.NewGroups);
        Assert.True(run.Ran);
        var group = Assert.Single(book.All());
        Assert.Equal(Owner, group.MetaOwner);
        Assert.Equal([Owner, Member], group.Members.Select(static m => m.PeerKey));
        Assert.Equal("ownerstub0000001", group.Copies.Single().StubKey); // the owner's stub is the copy: no file changes

        Assert.Equal(new GroupMigrationReport(0, 0, 0, false), migration.Preview());

        // Warden 11 L5: never during a layout change or a sync.
        using var layoutLock = new Jellyfin.Plugin.Shoal.Visibility.LayoutLock();
        using (layoutLock.TryEnter())
        {
            Assert.Equal("busy", (await new GroupMigration(state, catalogs, book, null, layoutLock, new FreeSync()).RunAsync()).Refused);
        }

        Assert.Equal("syncing", (await new GroupMigration(state, catalogs, book, null, layoutLock, new BusySync()).RunAsync()).Refused);
        Assert.Null((await new GroupMigration(state, catalogs, book, null, layoutLock, new FreeSync()).RunAsync()).Refused);
    }

    private sealed class BusySync : ISyncHold
    {
        public IDisposable? TryHold() => null;
    }

    private sealed class FreeSync : ISyncHold, IDisposable
    {
        public IDisposable? TryHold() => this;

        public void Dispose()
        {
        }
    }
}
