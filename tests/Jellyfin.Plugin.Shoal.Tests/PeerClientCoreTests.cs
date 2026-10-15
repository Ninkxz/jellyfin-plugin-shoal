using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.Peers;
using Xunit;

namespace Jellyfin.Plugin.Shoal.Tests;

/// <summary>
/// K3 unit tests for the pieces of the peer client that need no server: the bounded read stream (F-RS-04/05/06 behavior), the
/// circuit breaker, the header builder and the request validation.
/// </summary>
[Collection("MemoryMeasurement")]
public sealed class PeerClientCoreTests
{
    private static BoundedReadStream Bounded(Stream inner, long max, double idleSeconds = 5, long? declared = null, CancellationToken caller = default, TimeSpan? total = null) =>
        new(inner, max, TimeSpan.FromSeconds(idleSeconds), declared, new CallBudget(total, caller));

    private static async Task<long> DrainAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        var buffer = new byte[16 * 1024];
        long total = 0;
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken)) > 0)
        {
            total += read;
        }

        return total;
    }

    [Fact]
    public async Task ABodyOfExactlyTheCap_Passes_AndOneByteMoreFails()
    {
        await using (var exact = Bounded(new MemoryStream(new byte[1000]), 1000))
        {
            Assert.Equal(1000, await DrainAsync(exact));
        }

        await using var over = Bounded(new MemoryStream(new byte[1001]), 1000);
        var failure = await Assert.ThrowsAsync<PeerCallException>(() => DrainAsync(over));
        Assert.Equal(PeerFailureKind.TooLarge, failure.Kind);
    }

    [Fact]
    public async Task AnEndlessBody_IsCutAtTheCap_AndNeverReadBeyondOneByteOver()
    {
        var endless = new EndlessStream();
        await using var bounded = Bounded(endless, 1024 * 1024);

        var failure = await Assert.ThrowsAsync<PeerCallException>(() => DrainAsync(bounded));

        Assert.Equal(PeerFailureKind.TooLarge, failure.Kind);
        Assert.True(endless.BytesServed <= (1024 * 1024) + 1, "must stop right after the cap, served " + endless.BytesServed);
    }

    [Fact]
    public async Task AnEndlessBody_KeepsMemoryFlat()
    {
        var endless = new EndlessStream();
        await using var bounded = Bounded(endless, 200L * 1024 * 1024);
        GC.Collect();
        var before = GC.GetTotalMemory(forceFullCollection: true);

        await Assert.ThrowsAsync<PeerCallException>(() => DrainAsync(bounded));

        GC.Collect();
        var growth = GC.GetTotalMemory(forceFullCollection: true) - before;
        Assert.True(growth < 8 * 1024 * 1024, "memory grew by " + growth);
        Assert.True(endless.BytesServed > 100L * 1024 * 1024, "the test must really have streamed past 100 MB");
    }

    [Fact]
    public async Task ABodyShorterThanDeclared_IsAnError_AndALongerOneToo()
    {
        await using var shorter = Bounded(new MemoryStream(new byte[10]), 1000, declared: 100);
        var failure = await Assert.ThrowsAsync<PeerCallException>(() => DrainAsync(shorter));
        Assert.Equal(PeerFailureKind.BadResponse, failure.Kind);

        await using var longer = Bounded(new MemoryStream(new byte[100]), 1000, declared: 10);
        failure = await Assert.ThrowsAsync<PeerCallException>(() => DrainAsync(longer));
        Assert.Equal(PeerFailureKind.BadResponse, failure.Kind);

        await using var exact = Bounded(new MemoryStream(new byte[100]), 1000, declared: 100);
        Assert.Equal(100, await DrainAsync(exact));
    }

    [Fact]
    public async Task AReadThatWaitsLongerThanTheIdleTimeout_IsATimeout()
    {
        await using var bounded = Bounded(new StalledStream(), 1024, idleSeconds: 0.3);

        var failure = await Assert.ThrowsAsync<PeerCallException>(() => DrainAsync(bounded));

        Assert.Equal(PeerFailureKind.Timeout, failure.Kind);
    }

    [Fact]
    public async Task ATricklingBody_IsStoppedByTheTotalDeadline_EvenThoughEveryReadIsInTime()
    {
        await using var bounded = Bounded(new TrickleStream(TimeSpan.FromMilliseconds(50)), 1024 * 1024, idleSeconds: 5, total: TimeSpan.FromMilliseconds(500));

        var failure = await Assert.ThrowsAsync<PeerCallException>(() => DrainAsync(bounded));

        Assert.Equal(PeerFailureKind.Timeout, failure.Kind);
    }

    [Fact]
    public async Task ACallerCancellation_StaysACancellation_NotATimeout()
    {
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await using var bounded = Bounded(new StalledStream(), 1024, idleSeconds: 30, caller: cancel.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DrainAsync(bounded));
    }

    [Fact]
    public async Task FailuresAreReportedToTheCallbackBeforeTheyAreThrown()
    {
        var seen = new List<PeerFailureKind>();
        await using var bounded = new BoundedReadStream(new EndlessStream(), 100, TimeSpan.FromSeconds(5), null, new CallBudget(null, CancellationToken.None), e => seen.Add(e.Kind));

        await Assert.ThrowsAsync<PeerCallException>(() => DrainAsync(bounded));

        Assert.Equal([PeerFailureKind.TooLarge], seen);
    }

    [Fact]
    public async Task SynchronousReads_AreNotSupported()
    {
        await using var bounded = Bounded(new MemoryStream(new byte[4]), 10);

        Assert.Throws<NotSupportedException>(() => bounded.Read(new byte[4], 0, 4));
        Assert.Throws<NotSupportedException>(() => bounded.Read(new byte[4].AsSpan()));
        Assert.False(bounded.CanSeek);
        Assert.False(bounded.CanWrite);
    }

    [Fact]
    public void Breaker_OpensAfterThreeFailuresInARow_AndASuccessInBetweenResetsTheCount()
    {
        var breaker = new CircuitBreaker(new ManualTimeProvider());

        breaker.RecordFailure();
        breaker.RecordFailure();
        breaker.RecordSuccess();
        breaker.RecordFailure();
        breaker.RecordFailure();
        Assert.False(breaker.IsOpen);
        Assert.True(breaker.TryAcquire(isProbe: false));

        breaker.RecordFailure();

        Assert.True(breaker.IsOpen);
        Assert.False(breaker.TryAcquire(isProbe: false));
        Assert.False(breaker.TryAcquire(isProbe: true), "the first probe is due after 30 s");
    }

    [Fact]
    public void Breaker_ProbesAt30_60_120_240_AndThenEvery300Seconds()
    {
        var time = new ManualTimeProvider();
        var breaker = new CircuitBreaker(time);
        for (var i = 0; i < 3; i++)
        {
            breaker.RecordFailure();
        }

        foreach (var expected in new[] { 30, 60, 120, 240, 300, 300 })
        {
            var due = breaker.NextProbeUtc!.Value;
            Assert.Equal(expected, (due - time.GetUtcNow()).TotalSeconds);
            time.Advance(TimeSpan.FromSeconds(expected - 1));
            Assert.False(breaker.TryAcquire(isProbe: true), "not due yet");
            time.Advance(TimeSpan.FromSeconds(1));
            Assert.False(breaker.TryAcquire(isProbe: false), "only the probe goes through");
            Assert.True(breaker.TryAcquire(isProbe: true));
            Assert.False(breaker.TryAcquire(isProbe: true), "one probe at a time");
            breaker.RecordFailure();
        }
    }

    [Fact]
    public void Breaker_ASuccessfulProbeClosesIt_AndAReleasedProbeIsNotCounted()
    {
        var time = new ManualTimeProvider();
        var breaker = new CircuitBreaker(time);
        for (var i = 0; i < 3; i++)
        {
            breaker.RecordFailure();
        }

        time.Advance(TimeSpan.FromSeconds(30));
        Assert.True(breaker.TryAcquire(isProbe: true));
        breaker.ReleaseProbe();
        Assert.True(breaker.TryAcquire(isProbe: true));
        breaker.RecordSuccess();

        Assert.False(breaker.IsOpen);
        Assert.Equal(0, breaker.ConsecutiveFailures);
        Assert.True(breaker.TryAcquire(isProbe: false));
        Assert.Null(breaker.NextProbeUtc);
    }

    [Theory]
    [InlineData("abcXYZ019-._~", "abcXYZ019-._~")]
    [InlineData("a b", "a%20b")]
    [InlineData("a+b", "a%2Bb")]
    [InlineData("100%", "100%25")]
    [InlineData("\"x\", y", "%22x%22%2C%20y")]
    [InlineData("café", "caf%C3%A9")]
    [InlineData("☃", "%E2%98%83")]
    [InlineData("a/b?c=d&e", "a%2Fb%3Fc%3Dd%26e")]
    public void HeaderValues_ArePercentEncoded(string value, string expected)
    {
        Assert.Equal(expected, PeerAuthorization.Encode(value));
        Assert.Equal(value, WebUtility.UrlDecode(expected));
    }

    [Fact]
    public void TheAuthorizationHeader_IsExactlyAsDesigned()
    {
        var header = PeerAuthorization.Build("Shoal: Home (K7QM)", "shoal-1a2b3c4d5e6f-9a8b7c6d", "0.1.0.0", "0123456789abcdef0123456789abcdef");

        Assert.Equal(
            "MediaBrowser Client=\"Shoal\", Device=\"Shoal%3A%20Home%20%28K7QM%29\", DeviceId=\"shoal-1a2b3c4d5e6f-9a8b7c6d\", Version=\"0.1.0.0\", Token=\"0123456789abcdef0123456789abcdef\"",
            header);
        Assert.DoesNotContain("Token=", PeerAuthorization.Build("d", "i", "v", null), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Home", "Home")]
    [InlineData("A+B 100% \"x\", y", "A+B 100% x y")]
    [InlineData("Ho\\me\r\n‮", "Home")]
    [InlineData("", "Jellyfin")]
    [InlineData(null, "Jellyfin")]
    [InlineData("\"\",,\\\\", "Jellyfin")]
    public void ServerNames_AreSanitizedBeforeEncoding(string? input, string expected)
    {
        Assert.Equal(expected, PeerAuthorization.SanitizeServerName(input));
    }

    [Fact]
    public void ServerNames_AreCutAt64Characters()
    {
        Assert.Equal(64, PeerAuthorization.SanitizeServerName(new string('n', 300)).Length);
        Assert.Equal("Shoal: Home", PeerAuthorization.DeviceName("Home", null));
        Assert.Equal("Shoal: Home (K7QM)", PeerAuthorization.DeviceName("Home", "K7QM"));
    }

    [Theory]
    [InlineData("/System/Info/Public")]
    [InlineData("/Items?ParentId=abc&Recursive=true")]
    [InlineData("/Videos/abc/hls1/main/7.mp4?runtimeTicks=1&x=%2F")]
    public void GoodPaths_AreAccepted(string path)
    {
        Assert.Equal(path, PeerRequest.Get(path).PathAndQuery);
    }

    [Theory]
    [InlineData("")]
    [InlineData("System/Info")]
    [InlineData("//evil.example/x")]
    [InlineData("http://evil.example/x")]
    [InlineData("/a b")]
    [InlineData("/a\r\nHost: evil")]
    [InlineData("/a\\b")]
    [InlineData("/a#frag")]
    [InlineData("/café")]
    [InlineData("/a\tb")]
    public void BadPaths_AreRefused(string path)
    {
        Assert.Throws<ArgumentException>(() => PeerRequest.Get(path));
    }

    [Fact]
    public void ARequestCanOnlyCarryOneWellFormedRange()
    {
        Assert.Equal("bytes=0-", PeerRequest.GetMedia("/x", "bytes=0-").Range);
        Assert.Equal("bytes=10-20", PeerRequest.GetMedia("/x", "bytes=10-20").Range);
        Assert.Equal("bytes=-500", PeerRequest.GetMedia("/x", "bytes=-500").Range);
        foreach (var bad in new[] { "bytes=0-1,5-9", "items=0-1", "bytes=a-b", "bytes=0-1\r\nX: y", "bytes=", "bytes=-", "BYTES=0-1", "bytes=1234567890123456789-" })
        {
            Assert.Throws<ArgumentException>(() => PeerRequest.GetMedia("/x", bad));
        }
    }

    [Fact]
    public void TheProbeIsAnUnauthenticatedPingAndNothingElse()
    {
        var probe = PeerRequest.Probe();

        Assert.True(probe.IsProbe);
        Assert.False(probe.Authenticated);
        Assert.Equal("/System/Ping", probe.PathAndQuery);
        Assert.False(PeerRequest.Get("/System/Ping").IsProbe);
    }

    [Fact]
    public void TheProfiles_FollowTheDesignTable()
    {
        Assert.Equal(TimeSpan.FromSeconds(8), PeerCallProfile.Info.Total);
        Assert.Equal(64 * 1024, PeerCallProfile.Info.MaxBytes);
        Assert.Equal(TimeSpan.FromSeconds(3), PeerCallProfile.QuickConnect.Total);
        Assert.Equal(8 * 1024 * 1024, PeerCallProfile.Items.MaxBytes);
        Assert.Equal(TimeSpan.FromSeconds(12), PeerCallProfile.PlaybackInfo.Total);
        Assert.Null(PeerCallProfile.StaticStream.Total);
        Assert.True(PeerCallProfile.StaticStream.RequireDeclaredLength);
        Assert.Equal(TimeSpan.FromSeconds(60), PeerCallProfile.HlsSegment.Headers);
        Assert.Equal(256L * 1024 * 1024, PeerCallProfile.HlsSegment.MaxBytes);
        Assert.Equal(2 * 1024 * 1024, PeerCallProfile.PosterImage.MaxBytes);
        Assert.Equal(4 * 1024 * 1024, PeerCallProfile.BackdropImage.MaxBytes);
        Assert.Equal(64L * 1024 * 1024, PeerCallProfile.BitrateTest.MaxBytes);
    }

    [Fact]
    public void ExceptionMessages_AreFixedTexts()
    {
        foreach (var kind in Enum.GetValues<PeerFailureKind>())
        {
            var message = new PeerCallException(kind, HttpStatusCode.Found).Message;
            Assert.DoesNotContain("http", message.Replace("HTTP 302", string.Empty, StringComparison.Ordinal), StringComparison.OrdinalIgnoreCase);
            Assert.Contains("HTTP 302", message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TheBannedSymbolsFile_BansTheUnboundedReads()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "src", "Jellyfin.Plugin.Shoal", "BannedSymbols.txt")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        var banned = File.ReadAllText(Path.Combine(directory.FullName, "src", "Jellyfin.Plugin.Shoal", "BannedSymbols.txt"));
        foreach (var symbol in new[]
        {
            "HttpContent.ReadAsStringAsync()", "HttpContent.ReadAsByteArrayAsync()", "HttpContent.ReadAsStreamAsync()",
            "HttpContent.ReadAsStreamAsync(System.Threading.CancellationToken)", "HttpClient.GetStringAsync(System.String)",
            "HttpClient.GetStreamAsync(System.Uri)", "HttpContentJsonExtensions", "IHttpClientFactory", "System.Random"
        })
        {
            Assert.Contains(symbol, banned, StringComparison.Ordinal);
        }
    }

    private sealed class EndlessStream : Stream
    {
        public long BytesServed { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            buffer.Span.Fill(0x41);
            BytesServed += buffer.Length;
            return new ValueTask<int>(buffer.Length);
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class StalledStream : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    // One byte per interval, forever.
    private sealed class TrickleStream : Stream
    {
        private readonly TimeSpan _interval;

        public TrickleStream(TimeSpan interval) => _interval = interval;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(_interval, cancellationToken);
            buffer.Span[0] = 0x42;
            return 1;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

/// <summary>
/// Tests that measure the process's memory run alone: any other test allocating at the same time would be counted as growth.
/// </summary>
[CollectionDefinition("MemoryMeasurement", DisableParallelization = true)]
public sealed class MemoryMeasurementCollection
{
}
