using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.Api.Admin;
using Jellyfin.Plugin.Shoal.State;
using Jellyfin.Plugin.Shoal.Visibility;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Shoal.Tests;

/// <summary>
/// Warden 08 M7: after "start over" or a restore, sharing accounts that no share manages are reported to the admin and deleted only when
/// the admin says so.
/// </summary>
public sealed class OrphanAccountsTests
{
    [Fact]
    public async Task OnlyShoalNamedAccountsThatNoShareUses_AreListed_AndAdminsAreNeverListed()
    {
        using var rig = new VisibilityRig(createLibrary: false);
        var used = rig.Accounts.Add("shoal-bob-1a2b");
        var left = rig.Accounts.Add("shoal-carol-3c4d", all: false, folders: []);
        rig.Accounts.Add("Alex");
        rig.Accounts.Add("shoal-admin", admin: true);
        await rig.State.UpdateAsync(s => s.PeerRole.Shares.Add(new ShareState { ShareId = "s1", FriendName = "Bob", ShareUserId = used.Id }));

        var found = OrphanAccounts.Find(rig.State, rig.Accounts.List());

        Assert.Equal([left.Id], found.Select(static a => a.Id));
    }

    [Fact]
    public async Task AfterTheSharesAreForgotten_EveryShareAccountIsALeftover_ButNothingIsDeleted()
    {
        using var rig = new VisibilityRig(createLibrary: false);
        var bob = rig.Accounts.Add("shoal-bob-1a2b");
        await rig.State.UpdateAsync(s => s.PeerRole.Shares.Add(new ShareState { ShareId = "s1", FriendName = "Bob", ShareUserId = bob.Id }));
        Assert.Empty(OrphanAccounts.Find(rig.State, rig.Accounts.List()));

        await rig.State.UpdateAsync(static s => s.PeerRole.Shares.Clear()); // what a restore of an older state does

        Assert.Equal([bob.Id], OrphanAccounts.Find(rig.State, rig.Accounts.List()).Select(static a => a.Id));
        Assert.Empty(rig.Accounts.Writes);
    }

    [Fact]
    public async Task DeletingALeftover_NeedsTheAdminsCall_AndRefusesAnythingElse()
    {
        using var rig = new VisibilityRig(createLibrary: false);
        var users = new FakeShareUsers();
        var left = rig.Accounts.Add("shoal-carol-3c4d");
        var alex = rig.Accounts.Add("Alex");
        var used = rig.Accounts.Add("shoal-bob-1a2b");
        await rig.State.UpdateAsync(s => s.PeerRole.Shares.Add(new ShareState { ShareId = "s1", FriendName = "Bob", ShareUserId = used.Id }));
        var controller = new AdminOrphanAccountsController(rig.State, rig.Accounts, users, NullLogger<AdminOrphanAccountsController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

        AdminTestSupport.Body(await controller.Delete(alex.Id.ToString("N"), CancellationToken.None), 404); // not a Shoal account
        AdminTestSupport.Body(await controller.Delete(used.Id.ToString("N"), CancellationToken.None), 404); // a share uses it
        AdminTestSupport.Body(await controller.Delete("not-a-guid", CancellationToken.None), 404);
        Assert.DoesNotContain("delete", users.Calls);

        AdminTestSupport.Body(await controller.Delete(left.Id.ToString("N"), CancellationToken.None));
        Assert.Equal(["delete"], users.Calls);
    }
}
