using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Shoal.Authorization;
using Jellyfin.Plugin.Shoal.Playback;
using Jellyfin.Plugin.Shoal.Registrations;
using Jellyfin.Plugin.Shoal.Storage;
using Jellyfin.Plugin.Shoal.Sync;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Jellyfin.Plugin.Shoal.Tests;

/// <summary>
/// The fallback of DESIGN v2.6.5 (Pilot round 7, T-TRK-0): core reported <c>Trickplay {}</c> for stubs although Shoal saved the rows, so Shoal fills the field
/// in the item answers itself, from its catalog.
/// </summary>
public sealed class TrickplayManifestTests : IClassFixture<PlaybackFixture>
{
    private static readonly Guid Federated = Guid.Parse("b0000000-0000-0000-0000-000000000001");
    private static readonly Guid Local = Guid.Parse("c0000000-0000-0000-0000-000000000009");

    private readonly PlaybackFixture _fixture;

    public TrickplayManifestTests(PlaybackFixture fixture) => _fixture = fixture;

    private static Dictionary<string, Dictionary<int, TrickplayInfoDto>> Manifest(Guid id) => new()
    {
        [id.ToString("N")] = new() { [320] = new TrickplayInfoDto(new TrickplayInfo { Width = 320, Height = 180, TileWidth = 10, TileHeight = 10, ThumbnailCount = 60, Interval = 10000, Bandwidth = 1 }) },
    };

    private static async Task<object?> RunAsync(TrickplayManifestFilter filter, object? value, string query = "")
    {
        var http = new DefaultHttpContext();
        http.Request.QueryString = new QueryString(query);
        var action = new ActionContext(http, new RouteData(), new ActionDescriptor());
        var result = value is null ? (IActionResult)new EmptyResult() : new OkObjectResult(value);
        var context = new ResultExecutingContext(action, [], result, new object());
        await filter.OnResultExecutionAsync(context, () => Task.FromResult(new ResultExecutedContext(action, [], result, new object())));
        return value;
    }

    private static TrickplayManifestFilter Filter(ITrickplayManifestSource source) => new(source, NullLogger<TrickplayManifestFilter>.Instance);

    private sealed class FixedSource : ITrickplayManifestSource
    {
        public List<(Guid Id, string[] Sources)> Asked { get; } = [];

        public Dictionary<string, Dictionary<int, TrickplayInfoDto>>? ManifestFor(Guid itemId, IEnumerable<string> mediaSourceIds)
        {
            Asked.Add((itemId, [.. mediaSourceIds]));
            return itemId == Federated ? Manifest(itemId) : null;
        }
    }

    private sealed class ThrowingSource : ITrickplayManifestSource
    {
        public Dictionary<string, Dictionary<int, TrickplayInfoDto>>? ManifestFor(Guid itemId, IEnumerable<string> mediaSourceIds) => throw new InvalidOperationException("boom");
    }

    [Fact]
    public async Task AFederatedItemWithAnEmptyManifest_GetsTheCatalogsLevels()
    {
        var dto = new BaseItemDto { Id = Federated, Name = "Movie" };

        await RunAsync(Filter(new FixedSource()), dto);

        var level = Assert.Single(Assert.Single(dto.Trickplay!).Value);
        Assert.Equal((320, 60), (level.Key, level.Value.ThumbnailCount));
    }

    [Fact]
    public async Task AManifestThatCoreAlreadyHolds_IsLeftAlone()
    {
        var core = Manifest(Guid.NewGuid());
        var dto = new BaseItemDto { Id = Federated, Trickplay = core };

        await RunAsync(Filter(new FixedSource()), dto);

        Assert.Same(core, dto.Trickplay);
    }

    [Fact]
    public async Task APageOfItems_IsFilledForTheFederatedOnesOnly()
    {
        var federated = new BaseItemDto { Id = Federated };
        var local = new BaseItemDto { Id = Local };

        await RunAsync(Filter(new FixedSource()), new QueryResult<BaseItemDto>([federated, local]));
        var list = new[] { new BaseItemDto { Id = Federated } };
        await RunAsync(Filter(new FixedSource()), list);

        Assert.NotNull(federated.Trickplay);
        Assert.Null(local.Trickplay);
        Assert.NotNull(list[0].Trickplay);
    }

    [Theory]
    [InlineData("", true)]
    [InlineData("?Fields=Trickplay", true)]
    [InlineData("?fields=Overview,trickplay", true)]
    [InlineData("?Fields=Overview&Fields=Genres,Trickplay", true)]
    [InlineData("?Fields=Overview,Genres", false)]
    [InlineData("?Fields=TrickplayX", false)]
    public async Task TheField_IsFilledWhenTheClientAskedForItOrForEverything(string query, bool filled)
    {
        var dto = new BaseItemDto { Id = Federated };

        await RunAsync(Filter(new FixedSource()), dto, query);

        Assert.Equal(filled, dto.Trickplay is not null);
    }

    [Fact]
    public async Task TheVersionsListedInTheAnswer_AreOfferedToTheSource()
    {
        var source = new FixedSource();
        var dto = new BaseItemDto { Id = Federated, MediaSources = [new MediaSourceInfo { Id = "aaaa" }, new MediaSourceInfo { Id = "bbbb" }] };

        await RunAsync(Filter(source), dto);

        Assert.Equal(["aaaa", "bbbb"], Assert.Single(source.Asked).Sources);
    }

    [Fact]
    public async Task OtherAnswers_AndASourceThatFails_AreLeftAsTheyAre_AndTheAnswerStillGoesOut()
    {
        var dto = new BaseItemDto { Id = Federated };
        var text = "not an item";

        await RunAsync(Filter(new FixedSource()), text);
        await RunAsync(Filter(new FixedSource()), null);
        await RunAsync(Filter(new ThrowingSource()), dto);

        Assert.Null(dto.Trickplay);
    }

    [Fact]
    public void TheRegistration_AddsTheFilterToMvc_AndTheSourceToTheServices()
    {
        using var temp = new TempDirectory();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddShoalTrickplay();

        using var provider = services.BuildServiceProvider();

        Assert.Contains(provider.GetRequiredService<IOptions<MvcOptions>>().Value.Filters, f => f is TypeFilterAttribute { ImplementationType: var type } && type == typeof(TrickplayManifestFilter));
        Assert.Contains(services, d => d.ServiceType == typeof(ITrickplayManifestSource));
    }

    // --- the source over the catalog (the real stack of the playback harness)

    [Fact]
    public async Task TheCatalogSource_BuildsTheManifest_KeyedByTheStubsOwnMediaSourceId()
    {
        var h = _fixture.H;
        await h.Filter!.Index.RebuildAsync();
        var source = Source(h, () => true);

        var manifest = source.ManifestFor(MatrixWorld.Item, []);

        var level = Assert.Single(Assert.Single(manifest!).Value);
        Assert.Equal(MatrixWorld.Item.ToString("N"), Assert.Single(manifest!).Key);
        Assert.Equal((320, 180, 10, 10, 250, 10000), (level.Key, level.Value.Height, level.Value.TileWidth, level.Value.TileHeight, level.Value.ThumbnailCount, level.Value.Interval));
    }

    [Fact]
    public async Task TheCatalogSource_AddsTheOtherVersionsOfTheAnswer_AndIgnoresLocalItemsAndTheSwitch()
    {
        var h = _fixture.H;
        await h.Filter!.Index.RebuildAsync();

        var both = Source(h, () => true).ManifestFor(MatrixWorld.Item, [MatrixWorld.ItemB.ToString("N"), Local.ToString("N"), "garbage"]);

        Assert.Equal([MatrixWorld.Item.ToString("N"), MatrixWorld.ItemB.ToString("N")], both!.Keys.Order(StringComparer.Ordinal));
        Assert.Null(Source(h, () => true).ManifestFor(MatrixWorld.LocalItem, []));
        Assert.Null(Source(h, () => false).ManifestFor(MatrixWorld.Item, []));
    }

    private static CatalogTrickplayManifest Source(MatrixHarness h, Func<bool> enabled)
    {
        var catalog = new StubCatalog(new CatalogStore(new ShoalPaths(h.Dir.Path), NullLogger<CatalogStore>.Instance));
        return new CatalogTrickplayManifest(h.Filter!.Index, catalog, new OwnSourceVersionResolver(h.Filter.Index, catalog), enabled);
    }
}
