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
