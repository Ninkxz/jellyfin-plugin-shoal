using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Jellyfin.Plugin.Shoal.Authorization;
using Jellyfin.Plugin.Shoal.State;
using Jellyfin.Plugin.Shoal.Sync;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Shoal.Tests;

/// <summary>
/// K48 (PLAN v2.9, owner decision 4): the friend label on the movie, series and episode plot, the label version rewrite and the enricher re-apply.
/// </summary>
public sealed class FriendLabelTests
{
    private const string Label = "From a friend's server. May buffer or be unavailable sometimes. If it won't play, try again later.";

    [Fact]
    public void TheLabel_IsByteExact_WithTheAsciiApostrophe()
    {
        Assert.Equal(Label, FriendLabel.Text);
        var bytes = Encoding.UTF8.GetBytes(FriendLabel.Text);
        Assert.Equal(Encoding.ASCII.GetBytes(Label), bytes); // pure ASCII: no curly quote
        Assert.Equal(2, bytes.Count(static b => b == 0x27));
        Assert.DoesNotContain(FriendLabel.Text, static c => c > 0x7F);
        Assert.DoesNotContain('’', FriendLabel.Text);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n\n  ")]
    public void AnEmptyOverview_IsTheLabelAlone(string? overview)
    {
        Assert.Equal(Label, FriendLabel.Apply(overview));
    }

    [Fact]
    public void AnOverview_FollowsTheLabelAfterABlankLine()
    {
        Assert.Equal(Label + "\n\nA hacker learns the truth.\n\nSecond paragraph.", FriendLabel.Apply("A hacker learns the truth.\n\nSecond paragraph."));
    }

    [Theory]
    [InlineData(Label + "\n\nA film.")]
    [InlineData(Label + " A film.")]
    [InlineData(Label + Label + "\n\nA film.")]
    [InlineData("  " + Label + "\n\n" + Label + "\n\nA film.")]
    public void LeadingCopies_AreNeverDoubled(string overview)
    {
        Assert.Equal(Label + "\n\nA film.", FriendLabel.Apply(overview));
    }

    [Fact]
    public void TheLabelAlone_StaysTheLabelAlone()
    {
        Assert.Equal(Label, FriendLabel.Apply(Label));
        Assert.Equal(Label, FriendLabel.Apply(Label + "\n\n" + Label + "  "));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("A film.")]
    [InlineData("Line one.\nLine two.")]
    [InlineData(Label)]
    [InlineData("Not the label: from a friend’s server.")]
    public void Apply_IsIdempotent(string? overview)
    {
        var once = FriendLabel.Apply(overview);
        Assert.Equal(once, FriendLabel.Apply(once));
        Assert.Equal(once, FriendLabel.Apply(FriendLabel.Apply(once)));
    }

    [Fact]
    public void ALabelInTheMiddle_IsLeftAlone()
    {
        Assert.Equal(Label + "\n\nIntro. " + Label, FriendLabel.Apply("Intro. " + Label));
    }

    [Fact]
    public void TheMovieNfo_PlotStartsWithTheLabel_ByteExact()
    {
        var movie = new SanitizedMovie { Title = "The Matrix", Overview = "A hacker." };
        var bytes = NfoWriter.Write(movie, "Hops");
        var text = Encoding.UTF8.GetString(bytes);

        Assert.Contains("<plot>From a friend's server. May buffer or be unavailable sometimes. If it won't play, try again later.\n\nA hacker.</plot>", text, StringComparison.Ordinal);
        var needle = Encoding.ASCII.GetBytes("friend's server");
        Assert.True(bytes.AsSpan().IndexOf(needle) >= 0, "the apostrophe is written as the single byte 0x27, not as an entity or a curly quote");
    }

    [Fact]
    public void TheMovieNfo_WithoutAnOverview_HasTheLabelAlone()
    {
        var movie = XDocument.Parse(Encoding.UTF8.GetString(NfoWriter.Write(new SanitizedMovie { Title = "T" }, string.Empty))).Root!;
        Assert.Equal(Label, (string?)movie.Element("plot"));
    }

    [Fact]
    public void TheTvNfos_LabelSeriesAndEpisodes_ButNeverSeasons()
    {
        var writer = new TvNfoWriter();
        IReadOnlyList<string> tags = ["Shoal"];

        var series = XDocument.Parse(writer.Series(new SanitizedSeries { Title = "Show", Overview = "A show." }, tags)).Root!;
        var season = XDocument.Parse(writer.Season(new SanitizedSeason { Name = "Season 1", Number = 1, Overview = "A season." }, tags)).Root!;
        var episode = XDocument.Parse(writer.Episode(new SanitizedEpisode { Title = "Pilot", Season = 1, Number = 1 }, tags)).Root!;

        Assert.Equal(Label + "\n\nA show.", (string?)series.Element("plot"));
        Assert.Equal("A season.", (string?)season.Element("plot"));
        Assert.Equal(Label, (string?)episode.Element("plot"));
    }

    [Fact]
    public async Task ASync_RecordsTheLabelVersion_AndAnOlderVersionRewritesTheNfoOnce()
    {
        await using var rig = await SyncRig.CreateAsync();
        rig.Peer.Add("The Matrix", 1999, tmdb: 603);
        await rig.SyncAsync();
        var entry = rig.Entries.Single();
        Assert.Equal(FriendLabel.Version, entry.LabelVersion);

        // An install from before the label: NFO without it, entry at version 0, same etag.
        var nfo = Path.Combine(rig.LibraryFolder, entry.StubRelPath!.Split('/')[0], entry.StubRelPath.Split('/')[0] + ".nfo");
        var labelled = await File.ReadAllTextAsync(nfo);
        Assert.Contains(Label, labelled, StringComparison.Ordinal);
        await File.WriteAllTextAsync(nfo, labelled.Replace(Label + "\n\n", string.Empty, StringComparison.Ordinal));
        var doc = rig.Catalog();
        doc.Entries.Single().LabelVersion = 0;
        rig.Catalogs.Save(doc);

        await rig.SyncAsync();

        Assert.Contains(Label, await File.ReadAllTextAsync(nfo), StringComparison.Ordinal);
        Assert.Equal(FriendLabel.Version, rig.Entries.Single().LabelVersion);

        var before = File.GetLastWriteTimeUtc(nfo);
        await rig.SyncAsync();
        Assert.Equal(before, File.GetLastWriteTimeUtc(nfo)); // once: an up-to-date entry is not rewritten
    }

    // --- enricher re-apply

    private sealed class LabelItems : IStubItems
    {
        public Dictionary<Guid, (string Path, string? Overview, IReadOnlyList<string> Tags)> Items { get; } = [];

        public int Saves { get; private set; }

        public IReadOnlyList<StubItemView> List() => [];

        public IReadOnlyList<LabelItemView> ListLabelled() => [.. Items.Keys.Select(id => GetLabelled(id)!)];

        public LabelItemView? GetLabelled(Guid itemId) => Items.TryGetValue(itemId, out var item)
            ? new LabelItemView(itemId, item.Path, item.Overview, item.Tags, (overview, tags, _) =>
            {
                Saves++;
                Items[itemId] = (item.Path, overview, tags);
                return Task.CompletedTask;
            })
            : null;
    }

    private static async Task<(SyncRig Rig, FakeItemPaths Paths, ShoalIndex Index, LabelItems Items, StubEnricher Enricher)> EnricherRigAsync()
    {
        var rig = await SyncRig.CreateAsync();
        var paths = new FakeItemPaths();
        var index = new ShoalIndex(paths, rig.State, NullLogger.Instance);
        var items = new LabelItems();
        return (rig, paths, index, items, new StubEnricher(index, items, new StubCatalog(rig.Catalogs), rig.State, NullLogger.Instance));
    }

    [Fact]
    public async Task TheEnricher_PutsTheLabelBack_ReassertsTheTags_AndANoOpIsNotSaved()
    {
        var (rig, _, index, items, enricher) = await EnricherRigAsync();
        await using (rig)
        {
            using (index)
            {
                var stub = Guid.NewGuid();
                items.Items[stub] = (Path.Combine(rig.LibraryFolder, "M (2000)", "M (2000).strm"), "A provider's overview.", ["Drama"]);

                Assert.Equal(1, await enricher.ApplyLabelsAsync());
                var after = items.Items[stub];
                Assert.Equal(Label + "\n\nA provider's overview.", after.Overview);
                Assert.Contains("Shoal", after.Tags);
                Assert.Contains("Drama", after.Tags);
                Assert.Contains(after.Tags, static t => t.StartsWith("Shoal: ", StringComparison.Ordinal));

                Assert.Equal(0, await enricher.ApplyLabelsAsync()); // no-op guard
                Assert.Equal(1, items.Saves);
            }
        }
    }

    [Fact]
    public async Task TheEnricher_NeverTouchesAnItemOutsideTheStubRoot()
    {
        var (rig, _, index, items, enricher) = await EnricherRigAsync();
        await using (rig)
        {
            using (index)
            {
                var local = Guid.NewGuid();
                items.Items[local] = (Path.Combine(rig.Dir.Combine("own-movies"), "Mine (2001)", "Mine (2001).mkv"), "My own film.", []);
                var unmapped = Guid.NewGuid();
                items.Items[unmapped] = (Path.Combine(rig.StubRoot, StateSamples.PeerKeyB, "movies", "X", "X.strm"), "Other.", []);

                Assert.Equal(0, await enricher.ApplyLabelsAsync());
                Assert.Equal("My own film.", items.Items[local].Overview);
                Assert.Equal(0, items.Saves);
            }
        }
    }

    [Fact]
    public async Task Reapply_OnItemUpdated_OnlyForIndexedItems_AndEndsItsOwnLoop()
    {
        var (rig, paths, index, items, enricher) = await EnricherRigAsync();
        await using (rig)
        {
            using (index)
            {
                var stub = Guid.NewGuid();
                var path = Path.Combine(rig.LibraryFolder, "M (2000)", "M (2000).strm");
                items.Items[stub] = (path, "Replaced by a refresh.", ["Shoal"]);

                Assert.False(await enricher.ReapplyLabelAsync(stub)); // not in the index yet: nothing is looked up

                paths.Items[stub] = path;
                await index.RebuildAsync();
                Assert.True(await enricher.ReapplyLabelAsync(stub));
                Assert.False(await enricher.ReapplyLabelAsync(stub, CancellationToken.None)); // the save raised the event again: no-op
                Assert.Equal(1, items.Saves);
                Assert.StartsWith(Label + "\n\n", items.Items[stub].Overview, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void WantedTags_AddsOnlyWhatIsMissing()
    {
        IReadOnlyList<string> full = ["Shoal", "Shoal: Hops", "Drama"];
        Assert.Same(full, StubEnricher.WantedTags(full, "Hops"));
        Assert.Equal(["Drama", "Shoal", "Shoal: Hops"], StubEnricher.WantedTags(["Drama"], "Hops"));
        Assert.Equal(["Shoal"], StubEnricher.WantedTags([], string.Empty));
    }
}
