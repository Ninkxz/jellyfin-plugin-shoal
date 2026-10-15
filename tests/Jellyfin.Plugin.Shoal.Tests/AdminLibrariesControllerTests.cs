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
