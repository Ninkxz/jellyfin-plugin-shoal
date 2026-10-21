using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.Sync;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Shoal.Tests;

/// <summary>
/// Warden 10 L1: the label guard queues updates per item (no duplicates), in a bounded queue, saves one at a time, and ignores the update its own save raises.
/// </summary>
public sealed class FriendLabelGuardTests
{
    [Fact]
    public async Task ManyUpdatesOfOneItem_AreOneSave_AndTheGuardsOwnSaveIsNotQueuedAgain()
    {
        var saves = new List<Guid>();
        var echoes = new List<bool>();
        FriendLabelGuard? guard = null;
        guard = new FriendLabelGuard(null!, null!, (id, _) =>
        {
            saves.Add(id);
            echoes.Add(guard!.Enqueue(id)); // the save raises ItemUpdated for the same item
            return Task.FromResult(true);
        }, NullLogger.Instance);
        var item = Guid.NewGuid();

        Assert.True(guard.Enqueue(item));
        for (var i = 0; i < 50; i++)
        {
            Assert.False(guard.Enqueue(item));
        }

        await guard.DrainAsync();

        Assert.Equal([item], saves);
        Assert.Equal([false], echoes);
        Assert.Equal(0, guard.Waiting);
        Assert.True(guard.Enqueue(item)); // a later update counts again
    }

    [Fact]
    public async Task Refresh_ReplacesStudios_PutBack()
    {
        var (rig, paths, index, items, enricher) = await FriendLabelTests.EnricherRigAsync();
        await using (rig)
        {
            using (index)
            {
                var stub = Guid.NewGuid();
                var path = System.IO.Path.Combine(rig.LibraryFolder, "M (2000)", "M (2000).strm");
                items.Items[stub] = (path, "A film.", ["Shoal"]);
                paths.Items[stub] = path;
                await index.RebuildAsync();
                Assert.Equal(1, await enricher.ApplyLabelsAsync());
                Assert.Equal(["Shared by Hops"], items.Studios[stub]);

                // A "replace all" refresh with a provider on: the provider's studios replace the friend's.
                items.Studios[stub] = ["Blender Foundation"];
                var guard = new FriendLabelGuard(null!, index, enricher.ReapplyLabelAsync, NullLogger.Instance);
                Assert.True(guard.Enqueue(stub));
                await guard.DrainAsync();

                Assert.Equal(["Shared by Hops"], items.Studios[stub]);
                Assert.Equal(2, items.Saves);
            }
        }
    }

    [Fact]
    public async Task TheQueueIsBounded_AndAFullQueueDropsTheUpdate()
    {
        var saves = 0;
        var guard = new FriendLabelGuard(null!, null!, (_, _) =>
        {
            Interlocked.Increment(ref saves);
            return Task.FromResult(true);
        }, NullLogger.Instance);

        var queued = Enumerable.Range(0, FriendLabelGuard.MaxQueued + 100).Count(_ => guard.Enqueue(Guid.NewGuid()));
        await guard.DrainAsync();

        Assert.Equal(FriendLabelGuard.MaxQueued, queued);
        Assert.Equal(FriendLabelGuard.MaxQueued, saves);
        Assert.Equal(0, guard.Waiting);
    }
}
