using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Jellyfin.Plugin.Shoal.Security;
using Xunit;

namespace Jellyfin.Plugin.Shoal.Tests;

/// <summary>
/// K2 and K2.1: the logging helpers (DESIGN 2 #35, 8.3 T4) and the scrubber that is the last line of defense (Warden M-2, M-3).
/// </summary>
public sealed class RedactTests
{
    private const string Token = "9f2c4d6e8a0b1c3d5e7f9a0b2c4d6e8f";
    private const string QuickConnectSecret = "0A1B2C3D4E5F60718293A4B5C6D7E8F90A1B2C3D4E5F60718293A4B5C6D7E8F9";

    public static TheoryData<string, string> Secrets => new()
    {
        { "MediaBrowser Client=\"Shoal\", Device=\"Shoal: Home (K7QM)\", DeviceId=\"shoal-1\", Version=\"0.1\", Token=\"" + Token + "\"", Token },
        { "GET http://hops:8096/QuickConnect/Connect?secret=" + QuickConnectSecret + " failed", QuickConnectSecret },
        { "{\"Secret\":\"" + QuickConnectSecret + "\"}", QuickConnectSecret },
        { "{\"AccessToken\":\"" + Token + "\",\"ServerId\":\"x\"}", Token },
        { "api_key=" + Token, Token },
        { "ApiKey=" + Token + "&static=true", Token },
        { "X-Emby-Token: " + Token, Token },
        { "the token was " + Token + " today", Token },
        { "secret " + QuickConnectSecret + " seen", QuickConnectSecret },
        { "invite shoal1-1a2b3c4d-K7QM-2XA4-9FZD-R3TB-W8NE-HC5J-6P was used", "K7QM-2XA4" },
        { "https://hops/Videos/abc/stream?static=true&ApiKey=" + Token + "&mediaSourceId=1", Token },

        // Warden M-3: every shape the first version missed.
        { "GET /x?ApiKey%3D" + Token, Token },
        { "redirect Token%3D%22" + Token + "%22 done", Token },
        { "failed with {\\\"Token\\\":\\\"hunter2value\\\"}", "hunter2value" },
        { "failed with {\\\"Token\\\":\\\"" + Token + "\\\"}", Token },
        { "Authorization: Bearer eyJhbGciOi.payloadvalue.signaturevalue", "payloadvalue" },
        { "Authorization: Basic dXNlcjpwYXNzd29yZA==", "dXNlcjpwYXNzd29yZA" },
        { "authToken=SECRETVALUEONE", "SECRETVALUEONE" },
        { "refresh_token=SECRETVALUETWO", "SECRETVALUETWO" },
        { "id_token: SECRETVALUETHREE", "SECRETVALUETHREE" },
        { "{\"id_token\":\"SECRETVALUEFOUR\"}", "SECRETVALUEFOUR" },
        { "device_" + Token + " signed out", Token },
        { "t" + Token + "x", Token },
        { "upper " + Token.ToUpperInvariant() + " case", Token.ToUpperInvariant() },
        { "{\"Code\":\"123456\"}", "123456" },
        { "QuickConnect code=123456 expired", "123456" },
        { "bare K7QM-2XA4-9FZD-R3TB-W8NE-HC5J-6P copy", "R3TB-W8NE" },
        { "{\"invite\":\"K7QM2XA49FZDR3TBW8NEHC5J6P\"}", "K7QM2XA49FZDR3TBW8NEHC5J6P" },
        { "homeNonce=SGVsbG9Ob25jZVZhbHVl", "SGVsbG9Ob25jZVZhbHVl" },
        { "{\"peerNonce\":\"cGVlck5vbmNlVmFsdWU\"}", "cGVlck5vbmNlVmFsdWU" },
        { "{\"homeNonceCommit\":\"Y29tbWl0bWVudHZhbHVl\"}", "Y29tbWl0bWVudHZhbHVl" },
        { "https://admin:hunter2pw@hops.example/jellyfin/System/Info", "hunter2pw" },
        { "password: \"hunter two three\"", "two three" },
        { "{\"Password\":\"correct horse battery\"}", "horse battery" },
        { "item d6b69020-0fc1-c631-3344-25f6f7db7d2e leaked", "d6b69020-0fc1-c631-3344-25f6f7db7d2e" },
        { "item d6b690200fc1c631334425f6f7db7d2e leaked", "d6b690200fc1c631334425f6f7db7d2e" },
    };

    public static TheoryData<string> Harmless => new()
    {
        "Peer Hops is Online after 3 polls (status 200)",
        "HTTP status code: 404 for GET /Items",
        "code: 404",
        "Retry-After: 30",
        "http://hops.tail1234.ts.net:8096/Items (500)",
        "tokens are never logged",
        "passwordless sign-in is off",
        "peer 8f3a2b1c answered in 120 ms",
        "version 1.2.3.4 on port 8096",
        "Shoal: Home (K7QM)",
        "The peer did not answer in time.",
        "invited a friend to the server",
        "no secrets here, just words and numbers 12345 678901",
        "a1b2c3 is only six hex",
    };

    [Fact]
    public void Peer_IsTheCleanedDisplayName()
    {
        Assert.Equal("Hops", Redact.Peer("Hops"));
        Assert.Equal("Hops", Redact.Peer("Ho‮ps\r\n"));
        Assert.Equal("(unnamed peer)", Redact.Peer(null));
        Assert.Equal("(unnamed peer)", Redact.Peer("\u0007‎"));
        Assert.Equal(64, Redact.Peer(new string('x', 500)).Length);
    }

    [Fact]
    public void Id_IsEightHexOfAHash_NeverTheIdItself()
    {
        var id = Guid.Parse("d6b69020-0fc1-c631-3344-25f6f7db7d2e");

        var redacted = Redact.Id(id);

        Assert.Equal(8, redacted.Length);
        Assert.Matches("^[0-9a-f]{8}$", redacted);
        Assert.Equal(redacted, Redact.Id(id.ToString("N")));
        Assert.NotEqual(redacted, Redact.Id(Guid.NewGuid()));
        Assert.DoesNotContain(redacted, id.ToString("N"), StringComparison.Ordinal);
        Assert.Equal("(none)", Redact.Id((string?)null));
        Assert.Equal("(none)", Redact.Id(string.Empty));
    }

    [Theory]
    [InlineData("http://hops.tail1234.ts.net:8096/Users/Me?api_key=SECRET#frag", "hops.tail1234.ts.net:8096")]
    [InlineData("https://user:pass@Example.COM/jellyfin/QuickConnect/Connect?secret=ABC", "example.com")]
    [InlineData("https://example.com:443/x", "example.com")]
    [InlineData("not a url", "(invalid address)")]
    [InlineData("", "(invalid address)")]
    [InlineData("/relative?token=1", "(invalid address)")]
    public void Host_KeepsOnlyTheHostAndPort(string url, string expected)
    {
        Assert.Equal(expected, Redact.Host(url));
    }

    [Fact]
    public void Error_IsTheTypeNameOnly()
    {
        Assert.Equal("InvalidOperationException", Redact.Error(new InvalidOperationException("Token=" + Token)));
        Assert.Equal("(none)", Redact.Error(null));
    }

    [Theory]
    [MemberData(nameof(Secrets))]
    public void Scrub_RemovesSecretShapedValues(string text, string secret)
    {
        var scrubbed = Redact.Scrub(text);

        Assert.DoesNotContain(secret, scrubbed, StringComparison.Ordinal);
        Assert.Contains(Redact.Mask, scrubbed, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Harmless))]
    public void Scrub_LeavesOrdinaryTextAlone(string text)
    {
        Assert.Equal(text, Redact.Scrub(text));
    }

    [Fact]
    public void Scrub_IsIdempotent_OverEveryExample()
    {
        var corpus = new List<string>(((IEnumerable<object[]>)Harmless).Select(row => (string)row[0]));
        corpus.AddRange(((IEnumerable<object[]>)Secrets).Select(row => (string)row[0]));
        corpus.Add(string.Join(' ', corpus));

        foreach (var text in corpus)
        {
            var once = Redact.Scrub(text);
            Assert.Equal(once, Redact.Scrub(once));
        }
    }

    [Fact]
    public void Scrub_KeepsASurroundingSentenceReadable()
    {
        Assert.Equal("login failed for Token=\"" + Redact.Mask + "\" on retry", Redact.Scrub("login failed for Token=\"" + Token + "\" on retry"));
        Assert.Equal(string.Empty, Redact.Scrub(null));
        Assert.Equal(string.Empty, Redact.Scrub(string.Empty));
    }

    [Theory]
    [InlineData("a.", 40_000)]
    [InlineData("a.", 100_000)]
    [InlineData("a", 200_000)]
    [InlineData("http://a", 30_000)]
    [InlineData("://", 40_000)]
    [InlineData("token=", 40_000)]
    [InlineData("\\\"", 40_000)]
    [InlineData("0123456789abcdef", 30_000)]
    [InlineData("%3D%22", 30_000)]
    [InlineData("password ", 30_000)]
    public void Scrub_IsLinearTime_OnHostileInput(string unit, int repeat)
    {
        var hostile = string.Concat(Enumerable.Repeat(unit, repeat));
        var stopwatch = Stopwatch.StartNew();

        var result = Redact.Scrub(hostile);

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1), "took " + stopwatch.Elapsed);
        Assert.True(result.Length <= Redact.MaxScrubChars + 3 + 200 * Redact.Mask.Length, "result length " + result.Length);
    }

    [Fact]
    public void Scrub_80KilobytesOfDotSeparatedLetters_FinishesWellUnderASecond()
    {
        var hostile = string.Concat(Enumerable.Repeat("a.", 40_000)); // 80 KB, a word boundary every other character
        var stopwatch = Stopwatch.StartNew();

        var result = Redact.Scrub(hostile);

        Assert.True(stopwatch.Elapsed < TimeSpan.FromMilliseconds(500), "took " + stopwatch.Elapsed);
        Assert.Equal(Redact.MaxScrubChars + 3, result.Length);
        Assert.EndsWith("...", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Scrub_CutsLongTextBeforeScanningIt_SoASecretPastTheLimitIsDroppedNotKept()
    {
        var text = new string('x', Redact.MaxScrubChars + 10) + " Token=" + Token;

        var result = Redact.Scrub(text);

        Assert.DoesNotContain(Token, result, StringComparison.Ordinal);
        Assert.True(result.Length <= Redact.MaxScrubChars + 3);
    }
}
