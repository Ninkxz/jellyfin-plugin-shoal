using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.Api.Admin;
using Jellyfin.Plugin.Shoal.State;
using Jellyfin.Plugin.Shoal.Sync;
using Jellyfin.Plugin.Shoal.Storage;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Shoal.Tests;

/// <summary>Libraries and access (ui-spec 8.4) over the real visibility and access services.</summary>
public sealed class AdminLibrariesControllerTests
{
    private static (AdminLibrariesController Controller, VisibilityRig Rig, string Id) Create()
    {
        var rig = new VisibilityRig();
        var catalogs = new CatalogStore(new ShoalPaths(rig.Dir.Path), NullLogger<CatalogStore>.Instance);
        var view = new AdminLibraryView(rig.State, catalogs, rig.Libraries, rig.Accounts, rig.Visibility);
        var controller = new AdminLibrariesController(rig.State, view, rig.Access, rig.Visibility)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        return (controller, rig, AdminLibraryView.Compose(VisibilityRig.PeerKey, VisibilityRig.MappingKey));
    }

    private static void Send(ControllerBase controller, string json)
    {
        controller.Request.ContentType = "application/json";
        controller.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(json));
    }

    [Fact]
    public async Task TheList_HasTheLibraryUnderItsComposedId_AndNeverASharingAccount()
    {
        var (controller, rig, id) = Create();
        using (rig)
        {
            await rig.CreateLibraryAsync();
            var alex = rig.Accounts.Add("Alex");
            var share = rig.Accounts.Add("shoal-bob");
            await rig.State.UpdateAsync(s => s.PeerRole.Shares.Add(new ShareState { ShareId = "share-bob", FriendName = "Bob", ShareUserId = share.Id }));

            var json = AdminTestSupport.Body(controller.GetLibraries());

            var mapping = Assert.Single(json.GetProperty("mappings").EnumerateArray());
            Assert.Equal(id, mapping.GetProperty("mappingKey").GetString());
            Assert.Equal("Hops Movies", mapping.GetProperty("localLibraryName").GetString());
            Assert.Equal("Selected", mapping.GetProperty("access").GetProperty("mode").GetString());
            var names = json.GetProperty("users").EnumerateArray().Select(u => u.GetProperty("name").GetString()).ToList();
            Assert.Contains("Alex", names);
            Assert.DoesNotContain("shoal-bob", names);
            Assert.DoesNotContain(share.Id.ToString(), json.GetRawText(), StringComparison.OrdinalIgnoreCase);
            Assert.NotNull(alex);
        }
    }

    [Fact]
    public async Task N2_TheListGivesOneDefinitionOfTheCounts_RemoteLocalAndHidden()
    {
        var (controller, rig, _) = Create();
        using (rig)
        {
            await rig.CreateLibraryAsync();
            var catalogs = new CatalogStore(new ShoalPaths(rig.Dir.Path), NullLogger<CatalogStore>.Instance);
            CatalogEntry Entry(string key, EntryState state) => new() { StubKey = key, PeerItemId = Guid.NewGuid().ToString("N"), PeerMediaSourceId = Guid.NewGuid().ToString("N"), MappingKey = VisibilityRig.MappingKey, Title = "Title " + key, State = state, SizeBytes = 1000, Container = "mkv", RuntimeTicks = 1000, StubRelPath = state == EntryState.Active ? key + "/" + key + ".strm" : null };
            catalogs.Save(new CatalogDocument
            {
                PeerKey = VisibilityRig.PeerKey,
                CompletedOnce = true,
                Entries =
                [
                    Entry("aaaaaaaaaaaaaaa1", EntryState.Active),
                    Entry("aaaaaaaaaaaaaaa2", EntryState.Active),
                    Entry("aaaaaaaaaaaaaaa3", EntryState.DuplicatePeer),
                ]
            });

            var mapping = Assert.Single(AdminTestSupport.Body(controller.GetLibraries()).GetProperty("mappings").EnumerateArray());

            Assert.Equal(3, mapping.GetProperty("remoteCount").GetInt32()); // what the friend lists
            Assert.Equal(2, mapping.GetProperty("localCount").GetInt32());  // stubs here
            Assert.Equal(2, mapping.GetProperty("count").GetInt32());       // older pages: the same as localCount
            Assert.Equal(1, mapping.GetProperty("hiddenByDedup").GetInt32());
        }
    }

    [Fact]
    public async Task HeldAdditions_CountedPerLibrary()
    {
        var (controller, rig, _) = Create();
        using (rig)
        {
            await rig.CreateLibraryAsync();
            var catalogs = new CatalogStore(new ShoalPaths(rig.Dir.Path), NullLogger<CatalogStore>.Instance);
            var document = new CatalogDocument { PeerKey = VisibilityRig.PeerKey, CompletedOnce = true };
            for (var i = 0; i < 30; i++)
            {
                var id = Guid.NewGuid().ToString("N");
                document.HeldAdditions.Add(id);
                document.HeldAdditionInfo.Add(new HeldAddition { PeerItemId = id, MappingKey = VisibilityRig.MappingKey, Title = "Held " + i.ToString(System.Globalization.CultureInfo.InvariantCulture) });
            }

            var other = Guid.NewGuid().ToString("N"); // another library's hold is not counted here
            document.HeldAdditions.Add(other);
            document.HeldAdditionInfo.Add(new HeldAddition { PeerItemId = other, MappingKey = "other-mapping", Title = "Elsewhere" });
            catalogs.Save(document);

            var mapping = Assert.Single(AdminTestSupport.Body(controller.GetLibraries()).GetProperty("mappings").EnumerateArray());

            Assert.Equal(30, mapping.GetProperty("heldAdditions").GetInt32());
        }
    }

    [Fact]
    public async Task AccessChange_IsStored_ReturnsTheNewAccess_AndRefusesASharingAccount()
    {
        var (controller, rig, id) = Create();
        using (rig)
        {
            await rig.CreateLibraryAsync();
            var alex = rig.Accounts.Add("Alex");
            var share = rig.Accounts.Add("shoal-bob");
            await rig.State.UpdateAsync(s => s.PeerRole.Shares.Add(new ShareState { ShareId = "share-bob", FriendName = "Bob", ShareUserId = share.Id }));

            Send(controller, "{\"mode\":\"Everyone\"}");
            var ok = AdminTestSupport.Body(await controller.PutAccess(id, CancellationToken.None));

            Assert.Equal("Everyone", ok.GetProperty("access").GetProperty("mode").GetString());
            Assert.Equal(AccessMode.Everyone, rig.CurrentAccess.Mode);

            Send(controller, "{\"mode\":\"Selected\",\"selectedUserIds\":[\"" + share.Id + "\"]}");
            Assert.Equal("shareUser", AdminTestSupport.Body(await controller.PutAccess(id, CancellationToken.None), 400).GetProperty("error").GetString());
            Send(controller, "{\"mode\":\"Selected\",\"selectedUserIds\":[\"" + Guid.NewGuid() + "\"]}");
            Assert.Equal("unknownUser", AdminTestSupport.Body(await controller.PutAccess(id, CancellationToken.None), 400).GetProperty("error").GetString());
            Assert.Equal(AccessMode.Everyone, rig.CurrentAccess.Mode);
            Assert.NotNull(alex);
        }
    }

    [Fact]
    public async Task AParentalProfile_NeedsTheAcknowledgement_AndAnUnknownLibraryIs404()
    {
        var (controller, rig, id) = Create();
        using (rig)
        {
            await rig.CreateLibraryAsync();
            var kid = rig.Accounts.Add("Kit", restricted: true);

            Send(controller, "{\"mode\":\"Selected\",\"selectedUserIds\":[\"" + kid.Id + "\"]}");
            Assert.Equal("acknowledgementRequired", AdminTestSupport.Body(await controller.PutAccess(id, CancellationToken.None), 400).GetProperty("error").GetString());
            Send(controller, "{\"mode\":\"Selected\",\"selectedUserIds\":[\"" + kid.Id + "\"],\"ratingsAcknowledged\":true}");
            AdminTestSupport.Body(await controller.PutAccess(id, CancellationToken.None));

            Send(controller, "{}");
            AdminTestSupport.Body(await controller.PutAccess("nope", CancellationToken.None), 404);
            AdminTestSupport.Body(await controller.Reconcile("nope", CancellationToken.None), 404);
            AdminTestSupport.Body(await controller.Reconcile(id, CancellationToken.None));
        }
    }

    [Fact]
    public void EveryAction_RequiresTheAdminPolicy()
    {
        var type = typeof(AdminLibrariesController);

        Assert.Equal("RequiresElevation", type.GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AuthorizeAttribute), false).Cast<Microsoft.AspNetCore.Authorization.AuthorizeAttribute>().Single().Policy);
        Assert.Empty(type.GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AllowAnonymousAttribute), true));
        Assert.NotNull(JsonDocument.Parse("{}"));
    }
}
