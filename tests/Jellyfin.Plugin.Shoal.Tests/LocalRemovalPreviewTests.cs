using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.Api.Admin;
using Jellyfin.Plugin.Shoal.LocalRemoval;
using Jellyfin.Plugin.Shoal.State;
using Jellyfin.Plugin.Shoal.Sync;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Shoal.Tests;

public sealed class LocalRemovalPreviewTests
{
    private static readonly DateTime At = new(2026, 10, 25, 8, 0, 0, DateTimeKind.Utc);

    private static async Task<PairingRig> RigAsync()
    {
        var rig = new PairingRig();
        await rig.State.UpdateAsync(s =>
        {
            StateSamples.Fill(s);
            s.StubRoot = Path.Combine(rig.Dir.Path, "stubs");
            s.Home.Peers[0].Pairing!.Method = PairingMethod.Invite;
        });
        return rig;
    }

    private static CatalogEntry Entry(int n = 1, EntryState state = EntryState.DuplicateLocal) => new()
    {
        StubKey = "stub" + n.ToString("x8", System.Globalization.CultureInfo.InvariantCulture),
        PeerItemId = n.ToString("x32", System.Globalization.CultureInfo.InvariantCulture),
        MappingKey = "movies-1",
        Title = n == 1 ? "Sintel" : "Movie " + n,
        Year = 2010,
        LocalSizeBytes = 8000,
        State = state,
        ProviderIds = new() { ["Tmdb"] = "123" },
    };

    private static CatalogStore Catalogs(PairingRig rig, params CatalogEntry[] entries)
    {
        var catalogs = new CatalogStore(rig.Paths, NullLogger<CatalogStore>.Instance);
        catalogs.Save(new CatalogDocument
        {
            PeerKey = StateSamples.PeerKeyA,
            CompletedOnce = true,
            Report = new OverlapReport { ComputedUtc = At },
            Entries = entries.ToList(),
        });
        return catalogs;
    }

    private static string[] Inventory(string root) => Directory.GetFiles(root, "*", SearchOption.AllDirectories)
        .OrderBy(static p => p, StringComparer.Ordinal)
        .Select(p => Path.GetRelativePath(root, p) + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))) + ":" + File.GetLastWriteTimeUtc(p).Ticks)
        .Concat(Directory.GetDirectories(root, "*", SearchOption.AllDirectories).OrderBy(static p => p, StringComparer.Ordinal).Select(p => "dir:" + Path.GetRelativePath(root, p))).ToArray();

    [Fact]
    public async Task CachedMovieOverlap_IsBlockedAndMatchesTheGoldenContract()
    {
        using var rig = await RigAsync();
        var catalogs = Catalogs(rig, Entry());
        var controller = new AdminLocalRemovalController(rig.State, catalogs);
        var rendered = AdminTestSupport.Render(controller.Preview(StateSamples.PeerKeyA));
        Assert.Equal(200, rendered.Status);
        ContractGolden.Match("local-removal-preview.get", rendered.Text);
        var body = AdminTestSupport.Body(controller.Preview(StateSamples.PeerKeyA));
        Assert.True(body.GetProperty("previewOnly").GetBoolean());
        Assert.False(body.GetProperty("removalEnabled").GetBoolean());
        Assert.Equal(0, body.GetProperty("reclaimableBytes").GetInt64());
        var row = Assert.Single(body.GetProperty("items").EnumerateArray());
        Assert.False(row.GetProperty("safeToRemove").GetBoolean());
        Assert.Equal(6, row.GetProperty("blockers").GetArrayLength());
        Assert.DoesNotContain(StateSamples.PeerKeyA, rendered.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(Entry().PeerItemId, rendered.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("stub00000001", rendered.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("providerIds", rendered.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(rig.Dir.Path, rendered.Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0, 50, 50, true)]
    [InlineData(50, 50, 50, true)]
    [InlineData(100, 50, 3, false)]
    [InlineData(103, 100, 0, false)]
    [InlineData(int.MaxValue, 100, 0, false)]
    public void PaginationIsBoundedAndDoesNotOverflow(int offset, int limit, int count, bool hasMore)
    {
        var document = new CatalogDocument { Entries = Enumerable.Range(1, 103).Select(n => Entry(n)).ToList() };
        var result = LocalRemovalPreview.Build(document, "ready", offset, limit);
        Assert.Equal(103, result.Total);
        Assert.Equal(count, result.Items.Count);
        Assert.Equal(hasMore, result.HasMore);
        Assert.All(result.Items, row => Assert.False(row.SafeToRemove));
    }

    [Theory]
    [InlineData(-1, 50)]
    [InlineData(0, 0)]
    [InlineData(0, 101)]
    [InlineData(int.MinValue, int.MaxValue)]
    public async Task InvalidPageBoundsAreRejected(int offset, int limit)
    {
        using var rig = await RigAsync();
        var controller = new AdminLocalRemovalController(rig.State, Catalogs(rig, Entry()));
        AdminTestSupport.Body(controller.Preview(StateSamples.PeerKeyA, offset, limit), 400);
    }

    [Fact]
    public async Task UnknownAndTraversalHandlesAreNotUsedAsPaths()
    {
        using var rig = await RigAsync();
        var controller = new AdminLocalRemovalController(rig.State, Catalogs(rig, Entry()));
        AdminTestSupport.Body(controller.Preview("../outside"), 404);
        AdminTestSupport.Body(controller.Preview(new string('a', 32)), 404);
    }

    [Fact]
    public async Task UnpairedOrDisabledFriendsDoNotExposeCachedRows()
    {
        using var rig = await RigAsync();
        var controller = new AdminLocalRemovalController(rig.State, Catalogs(rig, Entry()));
        await rig.State.UpdateAsync(s => s.Home.Peers[0].Enabled = false);
        var disabled = AdminTestSupport.Render(controller.Preview(StateSamples.PeerKeyA));
        ContractGolden.Match("local-removal-preview.disabled", disabled.Text);
        Assert.Empty(disabled.Json.GetProperty("items").EnumerateArray());
        await rig.State.UpdateAsync(s => { s.Home.Peers[0].Enabled = true; s.Home.Peers[0].Pairing!.Method = PairingMethod.Qc; });
        var unpaired = AdminTestSupport.Body(controller.Preview(StateSamples.PeerKeyA));
        Assert.Equal("needsPairing", unpaired.GetProperty("catalogStatus").GetString());
        Assert.Empty(unpaired.GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task NoCompletedSyncHasNoRowsAndMatchesEmptyContract()
    {
        using var rig = await RigAsync();
        var controller = new AdminLocalRemovalController(rig.State, new CatalogStore(rig.Paths, NullLogger<CatalogStore>.Instance));
        var rendered = AdminTestSupport.Render(controller.Preview(StateSamples.PeerKeyA));
        ContractGolden.Match("local-removal-preview.empty", rendered.Text);
        Assert.Empty(rendered.Json.GetProperty("items").EnumerateArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RepeatedPreviewNeverCreatesLegacyMarkerOrChangesAnyFile(bool corruptPrimary)
    {
        using var rig = await RigAsync();
        var catalogs = Catalogs(rig, Entry());
        var folder = rig.Paths.PeerDir(StateSamples.PeerKeyA);
        File.Delete(Path.Combine(folder, "catalog-established"));
        if (corruptPrimary)
        {
            File.Copy(Path.Combine(folder, "catalog.json"), Path.Combine(folder, "catalog.json.bak"));
            File.WriteAllText(Path.Combine(folder, "catalog.json"), "broken");
        }

        var before = Inventory(rig.Dir.Path);
        var controller = new AdminLocalRemovalController(rig.State, catalogs);
        for (var i = 0; i < 3; i++)
        {
            AdminTestSupport.Body(controller.Preview(StateSamples.PeerKeyA), corruptPrimary ? 409 : 200);
        }

        Assert.Equal(before, Inventory(rig.Dir.Path));
        Assert.False(File.Exists(Path.Combine(folder, "catalog-established")));
    }

    [Fact]
    public async Task CorruptEstablishedCatalogIs409AndRemainsUntouched()
    {
        using var rig = await RigAsync();
        var catalogs = Catalogs(rig, Entry());
        File.WriteAllText(Path.Combine(rig.Paths.PeerDir(StateSamples.PeerKeyA), "catalog.json"), "broken");
        var before = Inventory(rig.Dir.Path);
        var controller = new AdminLocalRemovalController(rig.State, catalogs);
        AdminTestSupport.Body(controller.Preview(StateSamples.PeerKeyA), 409);
        Assert.Equal(before, Inventory(rig.Dir.Path));
    }

    [Fact]
    public async Task PreviewCannotCreateOrClearAnInMemoryRecoveryDecision()
    {
        using var rig = await RigAsync();
        var catalogs = Catalogs(rig, Entry());
        var folder = rig.Paths.PeerDir(StateSamples.PeerKeyA);
        var primary = Path.Combine(folder, "catalog.json");
        var valid = File.ReadAllBytes(primary);
        File.Delete(Path.Combine(folder, "catalog-established"));
        File.WriteAllText(primary, "broken");
        Assert.True(catalogs.LoadForPreview(StateSamples.PeerKeyA).Unreadable);
        File.Delete(primary);
        // A preview did not establish recovery; an ordinary writer still owns its normal decision.
        catalogs.Save(new CatalogDocument { PeerKey = StateSamples.PeerKeyA });
        File.WriteAllText(primary, "broken");
        Assert.True(catalogs.Load(StateSamples.PeerKeyA).Unreadable);
        File.WriteAllBytes(primary, valid);
        Assert.False(catalogs.LoadForPreview(StateSamples.PeerKeyA).Unreadable);
        // A successful preview also must not clear a recovery decision made by an ordinary load.
        Assert.Throws<InvalidOperationException>(() => catalogs.Save(new CatalogDocument { PeerKey = StateSamples.PeerKeyA }));
    }

    [Fact]
    public async Task StateRecoveryBlocksPreviewBeforeReadingAnyCatalog()
    {
        using var rig = await RigAsync();
        var catalogs = Catalogs(rig, Entry());
        File.WriteAllText(rig.Paths.StateFile, "broken");
        File.WriteAllText(rig.Paths.StateBackup, "broken");
        using var recovered = new StateStore(rig.Paths, NullLogger.Instance, TimeProvider.System, null);
        Assert.Equal(StateStatus.Recovery, recovered.Status);
        var before = Inventory(rig.Dir.Path);
        AdminTestSupport.Body(new AdminLocalRemovalController(recovered, catalogs).Preview(StateSamples.PeerKeyA), 409);
        Assert.Equal(before, Inventory(rig.Dir.Path));
    }

    [Fact]
    public void OnlyMoviesMarkedDuplicateLocalAreListedEvenIfTheyHaveStrongIds()
    {
        var episode = Entry(4);
        episode.Kind = EntryKind.Episode;
        var document = new CatalogDocument { Entries = [Entry(), Entry(2, EntryState.Active), Entry(3, EntryState.DuplicatePeer), episode] };
        var result = LocalRemovalPreview.Build(document, "ready", 0, 50);
        var row = Assert.Single(result.Items);
        Assert.Contains("identityUnverified", row.Blockers);
        Assert.Contains("qualityUnverified", row.Blockers);
        Assert.False(row.SafeToRemove);
    }

    [Fact]
    public void PreviewHasOnlyAnElevatedGetAndNoMutationRoute()
    {
        var type = typeof(AdminLocalRemovalController);
        Assert.Equal(Policies.RequiresElevation, Assert.Single(type.GetCustomAttributes<AuthorizeAttribute>()).Policy);
        Assert.Empty(type.GetCustomAttributes<AllowAnonymousAttribute>());
        var routes = type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .SelectMany(static m => m.GetCustomAttributes<HttpMethodAttribute>()).ToArray();
        var route = Assert.Single(routes);
        Assert.Equal("GET", Assert.Single(route.HttpMethods));
        Assert.Equal("Peers/{handle}/LocalRemovalPreview", route.Template);
    }
}
