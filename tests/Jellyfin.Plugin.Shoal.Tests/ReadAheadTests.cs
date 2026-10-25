using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.Playback;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Shoal.Tests;

/// <summary>
/// Owner request, 2026-10-16: Home reads ahead of a slow friend's server so the client reads from Home's fast side. The static path keeps a few MB per stream, an HLS
/// stream the next few segments; both are bounded in bytes, per stream and globally, and nothing is read past the window.
/// </summary>
public sealed class ReadAheadTests
{
    // A source that hands out bytes in small steps and counts what was taken from it.
    private sealed class CountingSource(int total, Func<int, bool>? failAt = null) : Stream
    {
        private int _position;

        public int Taken => Volatile.Read(ref _position);

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => total;

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            if (failAt?.Invoke(_position) == true)
            {
                throw new InvalidOperationException("the link broke");
            }

            var count = Math.Min(Math.Min(buffer.Length, 16 * 1024), total - _position);
            for (var i = 0; i < count; i++)
            {
                buffer.Span[i] = (byte)((_position + i) % 251);
            }

            _position += count;
            return count;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static async Task<byte[]> ReadAllAsync(Stream stream)
    {
        using var all = new MemoryStream();
        await stream.CopyToAsync(all);
        return all.ToArray();
    }

    [Fact]
    public async Task TheStaticBuffer_ReadsAheadOfTheClient_ButNeverPastItsCapacity()
    {
        var source = new CountingSource(8 * 1024 * 1024);
        var budget = new ReadAheadBudget(10 * 1024 * 1024);
        await using var body = (ReadAheadBody)ReadAheadBody.Wrap(source, 512 * 1024, budget);

        await Task.Delay(300); // nobody reads: the buffer fills and the source is left alone

        Assert.InRange(source.Taken, 512 * 1024, (512 * 1024) + (80 * 1024));
        Assert.Equal(512 * 1024, budget.Used);
        var first = new byte[1000];
        Assert.Equal(1000, await body.ReadAsync(first));
        await Task.Delay(200);
        Assert.True(source.Taken < 1024 * 1024, "the source stays one buffer ahead, not further: " + source.Taken);
    }

    [Fact]
    public async Task TheStaticBuffer_DeliversEveryByteInOrder_AndGivesTheBudgetBack()
    {
        var source = new CountingSource(3 * 1024 * 1024);
        var budget = new ReadAheadBudget(10 * 1024 * 1024);

        await using (var body = ReadAheadBody.Wrap(source, 256 * 1024, budget))
        {
            var bytes = await ReadAllAsync(body);

            Assert.Equal(3 * 1024 * 1024, bytes.Length);
            Assert.True(Enumerable.Range(0, bytes.Length).All(i => bytes[i] == (byte)(i % 251)));
        }

        Assert.Equal(0, budget.Used);
    }

    [Fact]
    public async Task ABrokenLink_ReachesTheReaderAsTheSameError_SoTheResponseIsCutNotEndedCleanly()
    {
        var source = new CountingSource(2 * 1024 * 1024, position => position >= 100 * 1024);
        await using var body = ReadAheadBody.Wrap(source, 256 * 1024, new ReadAheadBudget(10 * 1024 * 1024));
        var buffer = new byte[64 * 1024];
        var got = 0;

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            int read;
            while ((read = await body.ReadAsync(buffer)) > 0)
            {
                got += read;
            }
        });

        Assert.Equal("the link broke", failure.Message);
        Assert.True(got <= 128 * 1024);
    }

    [Fact]
    public async Task WithTheBudgetSpent_AStreamIsNotBuffered_ItGoesStraightThrough()
    {
        var budget = new ReadAheadBudget(1024 * 1024);
        await using var first = ReadAheadBody.Wrap(new CountingSource(10), 768 * 1024, budget);
        var plain = new CountingSource(10);

        var second = ReadAheadBody.Wrap(plain, 768 * 1024, budget);

        Assert.IsType<ReadAheadBody>(first);
        Assert.Same(plain, second);
        Assert.Equal(768 * 1024, budget.Used);
    }

    [Fact]
    public async Task ClosingTheStream_StopsTheBackgroundRead()
    {
        var source = new CountingSource(64 * 1024 * 1024);
        var budget = new ReadAheadBudget(10 * 1024 * 1024);
        var body = ReadAheadBody.Wrap(source, 256 * 1024, budget);
        await Task.Delay(100);

        await body.DisposeAsync();
        var taken = source.Taken;
        await Task.Delay(150);

        Assert.Equal(taken, source.Taken);
        Assert.Equal(0, budget.Used);
    }

    // ---------------------------------------------------------------- the HLS window

    private sealed class Rig : IDisposable
    {
        private readonly object _gate = new();
        private int _running;

        public Rig(int window, Func<int, long>? size = null, bool alive = true)
        {
            Alive = alive;
            ReadAhead = new HlsReadAhead(() => window, _ => Alive, TimeProvider.System, NullLogger<HlsReadAhead>.Instance);
            Size = size ?? (_ => 1000);
        }

        public bool Alive { get; set; }

        public HlsReadAhead ReadAhead { get; }

        public Func<int, long> Size { get; }

        public List<int> Fetched { get; } = [];

        public int MostAtOnce { get; private set; }

        public TaskCompletionSource? Hold { get; set; }

        public Func<int, byte[]?>? Answer { get; set; }

        public async Task<byte[]?> FetchAsync(int id, ReadAheadReservation reservation, CancellationToken token)
        {
            lock (_gate)
            {
                Fetched.Add(id);
                _running++;
                MostAtOnce = Math.Max(MostAtOnce, _running);
            }

            try
            {
                if (!reservation.TryReserve(Size(id)))
                {
                    return null;
                }

                if (Hold is { } hold)
                {
                    await hold.Task.WaitAsync(token);
                }
                else
                {
                    await Task.Delay(15, token);
                }

                return Answer is null ? new byte[Size(id)] : Answer(id);
            }
            finally
            {
                lock (_gate)
                {
                    _running--;
                }
            }
        }

        public void Advance(string grant, int segment, int count = 50, string peer = "peer") => ReadAhead.Advance(grant, peer, segment, count, FetchAsync);

        public int[] Asked()
        {
            lock (_gate)
            {
                return [.. Fetched.Order()];
            }
        }

        public void Dispose() => ReadAhead.Dispose();
    }

    private static async Task SettleAsync(Func<bool> done)
    {
        for (var i = 0; i < 400 && !done(); i++)
        {
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task NothingIsReadForTheFirstRequest_ThenOnlyTheWindowAfterTheClient_OneAtATime()
    {
        using var rig = new Rig(3);

        rig.Advance("g", 0);
        await Task.Delay(100);
        Assert.Empty(rig.Asked()); // a first request only says where the client is

        rig.Advance("g", 1); // the next in order: now the window is read
        await SettleAsync(() => rig.ReadAhead.HeldBytes >= 3000);
        await Task.Delay(100);

        Assert.Equal([2, 3, 4], rig.Asked()); // never past the window
        Assert.Equal(1, rig.MostAtOnce); // one read at a time for the stream
        Assert.NotNull(rig.ReadAhead.Find("g", 3));
        Assert.Null(rig.ReadAhead.Find("g", 5));
        Assert.Equal(3000, rig.ReadAhead.HeldBytes);
    }

    [Fact]
    public async Task ASeek_PausesTheFilling_UntilTheNextRequestInOrder()
    {
        using var rig = new Rig(3);
        rig.Advance("g", 0);
        rig.Advance("g", 1);
        await SettleAsync(() => rig.ReadAhead.HeldBytes >= 3000);

        rig.Advance("g", 30); // a jump: the client seeked
        await Task.Delay(150);
        Assert.Equal([2, 3, 4], rig.Asked()); // nothing was read around the new position yet
        Assert.Null(rig.ReadAhead.Find("g", 3)); // and what the client is past was dropped

        rig.Advance("g", 31);
        await SettleAsync(() => rig.Asked().Contains(34));
        Assert.Equal([2, 3, 4, 32, 33, 34], rig.Asked());
    }

    [Fact]
    public async Task WhenTheClientMovesOn_WhatItIsPastIsDropped_AndTheWindowSlidesOnce()
    {
        using var rig = new Rig(3);
        rig.Advance("g", 0);
        rig.Advance("g", 1);
        await SettleAsync(() => rig.ReadAhead.HeldBytes >= 3000);

        rig.Advance("g", 2);
        await SettleAsync(() => rig.Asked().Contains(5));
        await Task.Delay(100);

        Assert.Null(rig.ReadAhead.Find("g", 1));
        Assert.NotNull(rig.ReadAhead.Find("g", 2));
        Assert.Equal([2, 3, 4, 5], rig.Asked()); // each segment was read once
        Assert.Equal(4000, rig.ReadAhead.HeldBytes);
    }

    [Fact]
    public async Task ThePlaylistsEnd_StopsTheWindow()
    {
        using var rig = new Rig(5);

        rig.Advance("g", 5, count: 8); // segments 0 to 7 exist
        rig.Advance("g", 6, count: 8);
        await SettleAsync(() => rig.ReadAhead.HeldBytes >= 1000);
        await Task.Delay(100);

        Assert.Equal([7], rig.Asked());
    }

    [Fact]
    public async Task ReleasingAStream_DropsEverything_AndALateAnswerCreatesNothing()
    {
        using var rig = new Rig(6) { Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        rig.Advance("g", 0);
        rig.Advance("g", 1);
        await SettleAsync(() => rig.Asked().Length > 0);
        Assert.True(rig.ReadAhead.HeldBytes > 0); // a read is on its way: its bytes are already counted

        rig.ReadAhead.Release("g");
        rig.Hold.SetResult(); // the late answer arrives after the release
        await Task.Delay(150);

        Assert.Equal(0, rig.ReadAhead.HeldBytes);
        Assert.Equal(0, rig.ReadAhead.Streams);
        Assert.Null(rig.ReadAhead.Find("g", 2));
        Assert.Single(rig.Asked()); // and nothing further was read
    }

    [Fact]
    public async Task ARequestThatComesAfterTheRelease_StartsNothing_WhenTheStreamHasNoSlotAnyMore()
    {
        using var rig = new Rig(3, alive: false);

        rig.Advance("g", 0);
        rig.Advance("g", 1);
        await Task.Delay(100);

        Assert.Equal(0, rig.ReadAhead.Streams); // no state, no read, for a stream that is not alive
        Assert.Empty(rig.Asked());
    }

    [Fact]
    public async Task AStreamIsHeldToItsByteLimit_AndNoFurther()
    {
        using var rig = new Rig(10, id => 20 * 1024 * 1024);

        rig.Advance("g", 0);
        rig.Advance("g", 1);
        await SettleAsync(() => rig.ReadAhead.HeldBytes >= 3 * 20L * 1024 * 1024);
        await Task.Delay(150);

        Assert.True(rig.ReadAhead.HeldBytes <= HlsReadAhead.PerStreamBytes, "held " + rig.ReadAhead.HeldBytes);
        Assert.Equal(60L * 1024 * 1024, rig.ReadAhead.HeldBytes); // three of 20 MB; a fourth would pass 64 MB
    }

    [Fact]
    public async Task EightSegmentsOf32MB_AreReadAtOnce_TheNinthIsRefused_BeforeAnyByteIsRead()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var results = new ConcurrentBag<bool>();
        using var ra = new HlsReadAhead(() => 2, _ => true, TimeProvider.System, NullLogger<HlsReadAhead>.Instance);
        for (var i = 0; i < 9; i++)
        {
            var id = "g" + i;
            ra.Advance(id, "p" + i, 0, 50, (n, reservation, token) => Task.FromResult<byte[]?>(null));
            ra.Advance(id, "p" + i, 1, 50, async (n, reservation, token) =>
            {
                var got = reservation.TryReserve(HlsReadAhead.MaxSegmentBytes);
                results.Add(got);
                if (!got)
                {
                    return null;
                }

                await gate.Task.WaitAsync(token);
                return new byte[10];
            });
        }

        await SettleAsync(() => results.Count >= 9);
        await Task.Delay(100);

        Assert.Equal(8, results.Count(static r => r));
        Assert.Equal(1, results.Count(static r => !r));
        Assert.Equal(HlsReadAhead.GlobalBytes, ra.HeldBytes); // reserved, not allocated: the whole budget, never more

        gate.SetResult(); // the 32 MB were only a ceiling: what the segments really are is held
        await SettleAsync(() => ra.HeldBytes <= 8 * 10 + 8 * 10);
        Assert.InRange(ra.HeldBytes, 80, 160);
    }

    [Fact]
    public async Task ASegmentThatIsShorterThanItsReservation_GivesTheRestBack()
    {
        using var rig = new Rig(2, id => HlsReadAhead.MaxSegmentBytes) { Answer = _ => new byte[500] };

        rig.Advance("g", 0);
        rig.Advance("g", 1);
        await SettleAsync(() => rig.ReadAhead.HeldBytes is > 0 and <= 1000);
        await Task.Delay(100);

        Assert.Equal(1000, rig.ReadAhead.HeldBytes); // two segments of 500, not two of 32 MB
    }

    [Fact]
    public async Task AFailedRead_GivesItsReservationBack_AndIsNotRetriedUntilTheClientMovesOn()
    {
        using var rig = new Rig(3) { Answer = id => id == 3 ? null : new byte[500] };

        rig.Advance("g", 0);
        rig.Advance("g", 1);
        await SettleAsync(() => rig.ReadAhead.Find("g", 4) is not null);
        await rig.ReadAhead.Find("g", 4)!; // final accounting follows completion, not the in-flight reservation

        Assert.Equal([2, 3, 4], rig.Asked()); // 3 failed once and was left alone
        Assert.Null(rig.ReadAhead.Find("g", 3)); // so the client's own request goes to the Peer
        Assert.NotNull(rig.ReadAhead.Find("g", 4));
        Assert.Equal(1000, rig.ReadAhead.HeldBytes); // 2 and 4; the failed one holds nothing
    }

    [Fact]
    public async Task ASegmentThatIsBeingRead_CanBeAwaitedByTheClient()
    {
        using var rig = new Rig(2);

        rig.Advance("g", 0);
        rig.Advance("g", 1);
        await SettleAsync(() => rig.ReadAhead.Find("g", 2) is not null);
        var held = rig.ReadAhead.Find("g", 2);

        Assert.NotNull(held);
        Assert.Equal(1000, (await held)!.Length);
    }

    [Fact]
    public async Task AStreamWhoseSlotIsGone_IsSweptAway()
    {
        using var rig = new Rig(2);
        rig.Advance("g", 0);
        rig.Advance("g", 1);
        await SettleAsync(() => rig.ReadAhead.HeldBytes >= 2000);
        Assert.Equal(2000, rig.ReadAhead.HeldBytes);

        rig.Alive = false;
        rig.ReadAhead.ReleaseWhereGone();

        Assert.Equal(0, rig.ReadAhead.HeldBytes);
        Assert.Equal(0, rig.ReadAhead.Streams);
    }

    [Fact]
    public async Task WhenAServerIsStopped_AllItsStreamsGo_AndOthersStay()
    {
        using var rig = new Rig(2);
        foreach (var (grant, peer) in new[] { ("a", "p1"), ("b", "p1"), ("c", "p2") })
        {
            rig.Advance(grant, 0, peer: peer);
            rig.Advance(grant, 1, peer: peer);
        }

        await SettleAsync(() => rig.ReadAhead.HeldBytes >= 6000);

        rig.ReadAhead.ReleasePeer("p1");

        Assert.Equal(1, rig.ReadAhead.Streams);
        Assert.Null(rig.ReadAhead.Find("a", 2));
        Assert.NotNull(rig.ReadAhead.Find("c", 2));
        Assert.Equal(2000, rig.ReadAhead.HeldBytes);
    }

    [Fact]
    public void TheBudget_NeverGoesPastItsLimit_AndGivesBack()
    {
        var budget = new ReadAheadBudget(100);

        Assert.True(budget.TryReserve(60));
        Assert.False(budget.TryReserve(41));
        Assert.True(budget.TryReserve(40));
        Assert.False(budget.TryReserve(1));
        budget.Release(100);
        Assert.Equal(0, budget.Used);
    }

    [Fact]
    public void TheWindowSetting_IsClampedToTwoThroughTwenty()
    {
        using var low = new HlsReadAhead(() => 0, _ => true, TimeProvider.System, NullLogger<HlsReadAhead>.Instance);
        using var high = new HlsReadAhead(() => 99, _ => true, TimeProvider.System, NullLogger<HlsReadAhead>.Instance);

        Assert.Equal(2, low.Window);
        Assert.Equal(20, high.Window);
    }
}
