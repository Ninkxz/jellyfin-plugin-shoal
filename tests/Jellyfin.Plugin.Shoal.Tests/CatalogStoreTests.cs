using System;
using System.Linq;
using Jellyfin.Plugin.Shoal.Storage;
using Jellyfin.Plugin.Shoal.Sync;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Shoal.Tests;

/// <summary>
/// 2.9.2 K3: what a catalog file says about held additions is checked on load like the rest of it.
/// </summary>
public sealed class CatalogStoreTests
{
    [Fact]
    public void HeldAdditionInfo_PrunedAndCapped()
    {
        using var dir = new TempDirectory();
        var catalogs = new CatalogStore(new ShoalPaths(dir.Path), NullLogger<CatalogStore>.Instance);
        var document = new CatalogDocument { PeerKey = StateSamples.PeerKeyA };
        var held = new string('a', 32);
        document.HeldAdditions.Add(held);
        document.HeldAdditionInfo.Add(new HeldAddition { PeerItemId = held, MappingKey = "r0123456789abcdef", Title = "Sintel\u202e\n" + new string('x', 400), Year = 2010 });
        document.HeldAdditionInfo.Add(new HeldAddition { PeerItemId = held, MappingKey = "r0123456789abcdef", Title = "The same again" }); // a duplicate
        document.HeldAdditionInfo.Add(new HeldAddition { PeerItemId = new string('b', 32), MappingKey = "m", Title = "No longer held" });
        document.HeldAdditionInfo.Add(new HeldAddition { PeerItemId = "not-hex", MappingKey = "m", Title = "Bad id" });
        document.HeldAdditionInfo.Add(new HeldAddition { PeerItemId = held, MappingKey = "../evil", Title = "Bad key" });
        document.HeldAdditionInfo.Add(new HeldAddition { PeerItemId = held, MappingKey = "m2", Title = "Odd year", Year = 99999 });
        for (var i = 0; i < CatalogDocument.MaxEntries + TvLimits.MaxSeriesPerPeer + 100; i++)
        {
            var id = i.ToString("x32", System.Globalization.CultureInfo.InvariantCulture);
            document.HeldAdditions.Add(id);
            document.HeldAdditionInfo.Add(new HeldAddition { PeerItemId = id, MappingKey = "bulk", Title = "T" });
        }

        catalogs.Save(document);
        var loaded = catalogs.Load(StateSamples.PeerKeyA).Document.HeldAdditionInfo;

        var first = loaded[0];
        Assert.Equal(held, first.PeerItemId);
        Assert.StartsWith("Sintel", first.Title, StringComparison.Ordinal);
        Assert.DoesNotContain('\u202e', first.Title);
        Assert.DoesNotContain('\n', first.Title);
        Assert.True(first.Title.Length <= HeldAddition.TitleMax);
        Assert.Equal(2010, first.Year);
        Assert.Single(loaded, h => h.PeerItemId == held && h.MappingKey == "r0123456789abcdef");
        Assert.DoesNotContain(loaded, h => h.Title is "No longer held" or "Bad id" or "Bad key");
        Assert.Null(loaded.Single(h => h.MappingKey == "m2").Year);
        Assert.Equal(CatalogDocument.MaxEntries + TvLimits.MaxSeriesPerPeer, loaded.Count);
    }
}
