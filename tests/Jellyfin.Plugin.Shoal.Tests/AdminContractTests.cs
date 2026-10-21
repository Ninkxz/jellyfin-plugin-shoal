using System;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Text;
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
/// Golden JSON for the layout routes shoal.js reads most (GET Layouts/Libraries, POST Layouts/Preview for Combine into my libraries, with a pick and without).
/// See <see cref="ContractGolden"/> for how to update a golden file on purpose. Settings and RemoteLibraries have theirs in their own test classes.
/// </summary>
public sealed class AdminContractTests
{
    private static void Seed(FakeLibraryPeer p)
    {
        p.Add("Alien", 1979, tmdb: 348);
        p.Add("Heat", 1995, tmdb: 949);
    }

    private static AdminLayoutsController Controller(JobRig rig)
    {
        rig.Layout.Options = new LibraryOptionsGuard(rig.State, rig.Libraries, NullLogger.Instance);
        var presets = new LayoutPresetService(rig.State, rig.Accounts, rig.Job, rig.Groups, rig.Libraries, rig.Layout.Options);
        return new AdminLayoutsController(rig.State, rig.Layout, new AccessService(rig.State, rig.Accounts, rig.Visibility, TimeProvider.System), rig.Job, presets)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("Jellyfin-UserId", rig.Admin.Id.ToString("N"))], "test")) },
            },
        };
    }

    private static void Send(ControllerBase controller, string json)
    {
        controller.Request.ContentType = "application/json";
        controller.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(json));
    }

    private static string Ok(IActionResult result)
    {
        var rendered = AdminTestSupport.Render(result);
        Assert.Equal(200, rendered.Status);
        return rendered.Text;
    }

    [Fact]
    public async Task Contract_PreviewCombine_WithItsWarnings_MatchesTheGoldenJson()
    {
        await using var rig = await JobRig.CreateAsync(Seed);
        var movies = rig.Libraries.AddMain("Movies", "movies");
        rig.Libraries.SetSavesNfo(movies.Id, true); // targetsSavingNfo and targetsRewritingTitles hold a name
        var shoal = rig.Libraries.List().Single(l => l.Name != "Movies");
        rig.Accounts.Edit(rig.Alice.Id, all: false, folders: [shoal.Id]); // usersLosingAccess holds Alice (Warden 13 L3)
        var controller = Controller(rig);

        Send(controller, """{"preset":"CombineIntoMine"}""");
        var text = Ok(await controller.Preview(default));

        ContractGolden.Match("layouts.preview.combine", text);
    }

    [Fact]
    public async Task Contract_PreviewCombine_WaitingForAPick_MatchesTheGoldenJson()
    {
        await using var rig = await JobRig.CreateAsync(Seed);
        rig.Libraries.AddMain("Movies", "movies");
        rig.Libraries.AddMain("Kids movies", "movies");
        var controller = Controller(rig);

        Send(controller, """{"preset":"CombineIntoMine"}""");
        var text = Ok(await controller.Preview(default));

        ContractGolden.Match("layouts.preview.combine-pick", text);
    }

    [Fact]
    public async Task Contract_LayoutsLibraries_AfterCombine_MatchesTheGoldenJson()
    {
        await using var rig = await JobRig.CreateAsync(Seed);
        var movies = rig.Libraries.AddMain("Movies", "movies");
        var controller = Controller(rig);
        Send(controller, """{"preset":"CombineIntoMine"}""");
        var hash = AdminTestSupport.Body(await controller.Preview(default)).GetProperty("planHash").GetString();
        Send(controller, "{\"preset\":\"CombineIntoMine\",\"planHash\":\"" + hash + "\"}");
        Assert.Equal(202, AdminTestSupport.Render(await controller.Apply(default)).Status);
        await rig.Job.Running;
        rig.Libraries.SetSavesNfo(movies.Id, true); // mainLibrariesSavingNfo holds a name

        var text = Ok(controller.GetLibraries());

        ContractGolden.Match("layouts.libraries", text);
    }
}
