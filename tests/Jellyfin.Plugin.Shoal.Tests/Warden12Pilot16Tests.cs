using System;
using System.Linq;
using System.Security.Claims;
using System.Text;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.Api.Admin;
using Jellyfin.Plugin.Shoal.State;
using Jellyfin.Plugin.Shoal.Sync;
using Jellyfin.Plugin.Shoal.Visibility;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Shoal.Tests;

/// <summary>
/// Warden 12 (L1 relock loop, L2 multi-folder library, L4 combined edit, L6 stub check budget) and Pilot 16 (a move into a main library scans just the Shoal
/// folder; a renamed library is scanned again).
/// </summary>
public sealed class Warden12Pilot16Tests
{
    [Fact]
    public void L1_NullArrays_CountAsLocked()
    {
        var options = new MediaBrowser.Model.Configuration.LibraryOptions { MetadataSavers = null!, TypeOptions = [new MediaBrowser.Model.Configuration.TypeOptions { Type = "Movie", MetadataFetchers = null!, ImageFetchers = null! }] };
        Assert.False(LibraryOptionsFactory.Lock(options));
    }

    [Fact]
    public async Task L1_ALibraryJellyfinNeverKeepsLocked_IsTriedThreeTimesInARow_ThenLeftAlone_AndTriedAgainLater()
    {
        using var rig = new LayoutRig();
        await rig.Layout.CreateAsync(new LibraryCreateRequest("Friends' movies", "movies", LibraryPlacement.Shoal), rig.Admin.Id);
        rig.Libraries.NeverKeeps = true;
        long now = 1_000_000;
        var guard = new LibraryOptionsGuard(rig.State, rig.Libraries, NullLogger.Instance, () => now);

        var passes = Enumerable.Range(0, 5).Select(_ => guard.Enforce()).ToList();
        Assert.Equal([1, 1, 1, 0, 0], passes);

        now += 61_000; // a minute later (the next scan): tried again
        Assert.Equal(1, guard.Enforce());
    }

    [Fact]
    public async Task L1_TheWatch_IgnoresSavesMadeWhileAPassRuns()
    {
        var passes = 0;
        using var watch = new LibraryOptionsWatch(() => ++passes, NullLogger.Instance, TimeProvider.System, TimeSpan.FromMilliseconds(50), () => true);

        watch.OnChanged();
        await watch.Running;
        await Task.Delay(100);

        Assert.Equal(0, passes);
    }

    [Fact]
    public async Task L2_AShoalLibraryThatHoldsOtherFolders_IsNotLocked_AndIsNamedInTheStatus()
    {
        using var rig = new LayoutRig();
        await rig.Layout.CreateAsync(new LibraryCreateRequest("Friends' movies", "movies", LibraryPlacement.Shoal), rig.Admin.Id);
        await rig.Libraries.AddPathAsync("Friends' movies", System.IO.Path.Combine(System.IO.Path.GetTempPath(), "admin-own-movies"), default);
        rig.Libraries.Unlocked.Add("Friends' movies");
        var guard = new LibraryOptionsGuard(rig.State, rig.Libraries, NullLogger.Instance);

        Assert.Equal(0, guard.Enforce());
        Assert.DoesNotContain("Friends' movies", rig.Libraries.Locked);
        Assert.Equal(["Friends' movies"], guard.ShoalLibrariesNotLocked());
    }

    [Fact]
    public async Task L2_ASingleFolderShoalLibrary_IsStillLocked_AndNotNamed()
    {
        using var rig = new LayoutRig();
        await rig.Layout.CreateAsync(new LibraryCreateRequest("Friends' movies", "movies", LibraryPlacement.Shoal), rig.Admin.Id);
        rig.Libraries.Unlocked.Add("Friends' movies");
        var guard = new LibraryOptionsGuard(rig.State, rig.Libraries, NullLogger.Instance);

        Assert.Equal(1, guard.Enforce());
        Assert.Empty(guard.ShoalLibrariesNotLocked());
    }

    [Fact]
    public async Task L4_ARefusedName_IsRefusedBeforeTheAccessChange()
    {
        var (controller, rig) = Controller();
        using (rig)
        {
            var key = (await rig.Layout.CreateAsync(new LibraryCreateRequest("Friends' movies", "movies", LibraryPlacement.Shoal), rig.Admin.Id)).LibKey!;
            var before = rig.Library(key).Access.Mode;

            Send(controller, "{\"name\":\"Shows\",\"access\":{\"mode\":\"Everyone\"}}");
            Assert.Equal("nameInUse", AdminTestSupport.Body(await controller.UpdateLibrary(key, default), 409).GetProperty("error").GetString());

            Assert.Equal(before, rig.Library(key).Access.Mode);
            Assert.Equal(LayoutError.Invalid, rig.Layout.CheckRename(key, " padded"));
            Assert.Equal(LayoutError.None, rig.Layout.CheckRename(key, "Pals"));
            Assert.Equal("Friends' movies", rig.Library(key).Name); // a check changes nothing
        }
    }

    [Fact]
    public async Task L4_ARenameThatFailsAfterTheAccessChange_SaysTheAccessChanged_WithWhoGained()
    {
        var (controller, rig) = Controller();
        using (rig)
        {
            var key = (await rig.Layout.CreateAsync(new LibraryCreateRequest("Friends' movies", "movies", LibraryPlacement.Shoal), rig.Admin.Id)).LibKey!;
            rig.Libraries.FailRename = true;

            Send(controller, "{\"name\":\"Pals\",\"access\":{\"mode\":\"Everyone\"}}");
            var body = AdminTestSupport.Body(await controller.UpdateLibrary(key, default), 500);

            Assert.Equal("failed", body.GetProperty("error").GetString());
            Assert.True(body.GetProperty("accessChanged").GetBoolean());
            Assert.Contains(rig.Alice.Id.ToString(), body.GetProperty("gaining").GetRawText(), StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void L6_ABigLibrary_IsSampled_AboutMaxChecksASync()
    {
        using var dir = new TempDirectory();
        var check = new StubCheck(4000, rotation: 7);
        var shoal = 0;
        for (var i = 0; i < 4000; i++)
        {
            var entry = new CatalogEntry { Kind = EntryKind.Movie, StubKey = "k" + i.ToString("D8", System.Globalization.CultureInfo.InvariantCulture), StubRelPath = "T" + i + "/T" + i + ".strm" };
            check.Sample(SyncRig.PeerKey, dir.Path, entry);
            shoal++;
        }

        Assert.Equal(4000, shoal);
        Assert.InRange(check.Checked, 200, 600);
    }

    [Fact]
    public void L6_ASpentTimeBudget_ChecksNothingMore()
    {
        using var dir = new TempDirectory();
        var check = new StubCheck(10, rotation: 0, maxTime: TimeSpan.Zero);
        check.Sample(SyncRig.PeerKey, dir.Path, new CatalogEntry { Kind = EntryKind.Movie, StubKey = "aaaaaaaaaaaaaaaa", StubRelPath = "A/A.strm" });
        Assert.Equal(0, check.Checked);
    }

    [Fact]
    public void L6_TheSameDamageAfterTwoHeals_IsLeftAlone_AndAWholeStubClearsTheMemory()
    {
        using var dir = new TempDirectory();
        var key = Guid.NewGuid().ToString("N")[..16];
        var entry = new CatalogEntry { Kind = EntryKind.Movie, StubKey = key, StubRelPath = "Odd/Odd.strm" };
        var folder = System.IO.Directory.CreateDirectory(System.IO.Path.Combine(dir.Path, "Odd")).FullName;
        System.IO.File.WriteAllText(System.IO.Path.Combine(folder, "Odd.nfo"), "<movie><plot>a label form the check does not know</plot></movie>");

        Assert.Equal(StubDamage.LabelMissing, StubCheck.Full(SyncRig.PeerKey, dir.Path, entry));
        StubCheck.NoteHealed(SyncRig.PeerKey, entry, StubDamage.LabelMissing);
        Assert.Equal(StubDamage.LabelMissing, StubCheck.Full(SyncRig.PeerKey, dir.Path, entry));
        StubCheck.NoteHealed(SyncRig.PeerKey, entry, StubDamage.LabelMissing);
        Assert.Equal(StubDamage.None, StubCheck.Full(SyncRig.PeerKey, dir.Path, entry)); // no third heal: no loop every sync

        System.IO.File.WriteAllText(System.IO.Path.Combine(folder, "Odd.nfo"), "<movie><plot>" + System.Security.SecurityElement.Escape(FriendLabel.Generic) + "</plot></movie>");
        Assert.Equal(StubDamage.None, StubCheck.Full(SyncRig.PeerKey, dir.Path, entry));
        System.IO.File.WriteAllText(System.IO.Path.Combine(folder, "Odd.nfo"), "<movie></movie>");
        Assert.Equal(StubDamage.LabelMissing, StubCheck.Full(SyncRig.PeerKey, dir.Path, entry)); // whole once: the memory was cleared
    }

    [Fact]
    public void P16_AFolderInAMainLibrary_IsScannedAlone_AShoalLibraryGetsTheLibraryRefresh()
    {
        // VirtualFolderInfo as GET /Library/VirtualFolders gave it in a test lab: ItemId as 32 hex digits, or missing for a library just created or renamed.
        var shoalId = Guid.NewGuid();
        var folders = new[]
        {
            new VirtualFolderInfo { Name = "Movies", ItemId = Guid.NewGuid().ToString("N"), Locations = ["/media/movies", "/config/data/shoal/stubs/libs/aaaaaaaaaaaaaaaa"] },
            new VirtualFolderInfo { Name = "Friend B movies", ItemId = shoalId.ToString("N"), Locations = ["/config/data/shoal/stubs/libs/bbbbbbbbbbbbbbbb"] },
            new VirtualFolderInfo { Name = "Friends mix", ItemId = null, Locations = ["/config/data/shoal/stubs/libs/cccccccccccccccc"] },
        };

        var (libraries, alone) = JellyfinLibraryRefresh.Plan(folders, ["/config/data/shoal/stubs/libs/aaaaaaaaaaaaaaaa", "/config/data/shoal/stubs/libs/bbbbbbbbbbbbbbbb", "/config/data/shoal/stubs/libs/bbbbbbbbbbbbbbbb", "/config/data/shoal/stubs/libs/cccccccccccccccc", "/config/data/shoal/stubs/libs/dddddddddddddddd"]);

        Assert.Equal([shoalId], libraries); // once, though named twice
        Assert.Equal(["/config/data/shoal/stubs/libs/aaaaaaaaaaaaaaaa", "/config/data/shoal/stubs/libs/cccccccccccccccc"], alone); // the main library's Shoal folder alone, never /media/movies; no id yet: by path
    }

    [Fact]
    public async Task P16_ARenamedLibrary_IsScannedAgain_ByItsFolder()
    {
        using var rig = new LayoutRig();
        var key = (await rig.Layout.CreateAsync(new LibraryCreateRequest("Friend B movies", "movies", LibraryPlacement.Shoal), rig.Admin.Id)).LibKey!;
        var scans = new FakeRefresh();
        rig.Layout.Scans = scans;

        Assert.Equal(LayoutError.None, (await rig.Layout.RenameAsync(key, "Friends mix")).Error);

        Assert.Equal([LibraryFolders.PathOf(rig.StubRoot, rig.Library(key))!], scans.LibraryScans);
    }

    [Fact]
    public async Task P16_AMoveIntoAMainLibrary_ScansItsShoalFolder()
    {
        await using var rig = await JobRig.CreateAsync(static p => p.Add("Alien", 1979, tmdb: 348));
        var main = rig.Libraries.AddMain("Movies", "movies");
        var created = await rig.Layout.CreateAsync(new LibraryCreateRequest("Friends in Movies", "movies", LibraryPlacement.Main, main.Id), rig.Admin.Id);
        Assert.True(created.Ok);
        rig.Sync.Refresh.LibraryScans.Clear();

        Assert.Equal(LayoutJobError.None, await rig.Job.StartAsync([new MoveRequest(SyncRig.PeerKey, "movies", created.LibKey)], rig.Admin.Id));
        await rig.Job.Running;

        Assert.Contains(rig.Sync.Refresh.LibraryScans, f => LibraryFolders.SamePath(f, rig.LibFolder(created.LibKey!)));
    }

    private static (AdminLayoutsController Controller, LayoutRig Rig) Controller()
    {
        var rig = new LayoutRig(LayoutPreset.PerFriend);
        var jobs = new LayoutJobService(rig.State, new CatalogStore(rig.Paths, NullLogger<CatalogStore>.Instance), rig.Layout, rig.Accounts, new FakeRefresh(), rig.Paths, TimeProvider.System, NullLogger.Instance);
        var controller = new AdminLayoutsController(rig.State, rig.Layout, rig.Access, jobs, new LayoutPresetService(rig.State, rig.Accounts, jobs))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("Jellyfin-UserId", rig.Admin.Id.ToString("N"))], "test")) },
            },
        };
        return (controller, rig);
    }

    private static void Send(ControllerBase controller, string json)
    {
        controller.Request.ContentType = "application/json";
        controller.Request.Body = new System.IO.MemoryStream(Encoding.UTF8.GetBytes(json));
    }
}
