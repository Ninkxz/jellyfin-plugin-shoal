using System;
using System.Linq;
using System.Text.Json;
using Jellyfin.Plugin.Shoal.Api.Admin;
using Jellyfin.Plugin.Shoal.LocalRemoval;
using Xunit;

namespace Jellyfin.Plugin.Shoal.Tests;

/// <summary>
/// 2.10.4 C0: the golden files of "delete like Jellyfin" (tests/contracts/local-delete.*.json) rendered from the real DTO records through
/// AdminJson, so the backend (K3/K4) and the UI (U1-U3) build against the same bytes. K4 adds the route-level checks.
/// </summary>
public sealed class LocalDeleteContractTests
{
    private const string A = "a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1";
    private const string B = "b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2";
    private const string C = "c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3";
    private const string D = "d4d4d4d4d4d4d4d4d4d4d4d4d4d4d4d4";
    private const string E = "e5e5e5e5e5e5e5e5e5e5e5e5e5e5e5e5";
    private const string BatchKey = "f6f6f6f6f6f6f6f6f6f6f6f6f6f6f6f6";
    private static readonly DateTimeOffset Started = new(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void StateListsCandidatesLegacyJobsAndTheActiveBatch()
    {
        var state = new LocalDeleteStateDto(
            true,
            true,
            [new(A, "Sintel", 2010, 199749), new(B, "Big Buck Bunny", null, 276134947)],
            [new(C, "Tears of Steel", "replacementReady", 185765954, true, null), new(D, "Elephants Dream", "purgeOutcomeUnknown", 0, false, "recoveryRequired")],
            BatchKey);
        Match("local-delete.state", state);
    }

    [Fact]
    public void OffHasNoCandidatesAndANullBatch()
    {
        var json = Match("local-delete.off", new LocalDeleteStateDto(false, true, [], [], null));
        Assert.Equal(JsonValueKind.Null, json.GetProperty("activeBatchId").ValueKind);
    }

    [Fact]
    public void StartRepliesWithEveryItemQueued()
    {
        var json = Match("local-delete.start", new LocalDeleteStartDto(BatchKey, LocalDeleteBatchState.Running, [Item(A, "Sintel", LocalDeleteItemState.Queued), Item(B, "Big Buck Bunny", LocalDeleteItemState.Queued)]));
        Assert.Equal("running", json.GetProperty("state").GetString());
        Assert.All(json.GetProperty("items").EnumerateArray(), i => Assert.Equal("queued", i.GetProperty("state").GetString()));
    }

    [Fact]
    public void RunningShowsMixedItemStates()
    {
        var items = new[]
        {
            Item(A, "Sintel", LocalDeleteItemState.Done),
            Item(B, "Big Buck Bunny", LocalDeleteItemState.Refused) with { Reason = LocalDeleteReason.ContentNotEquivalent },
            Item(C, "Tears of Steel", LocalDeleteItemState.Checking),
            Item(D, "Elephants Dream", LocalDeleteItemState.Queued),
        };
        var json = Match("local-delete.running", Batch(LocalDeleteBatchState.Running, null, false, items));
        Assert.Equal(JsonValueKind.Null, json.GetProperty("finishedUtc").ValueKind);
        Assert.False(json.GetProperty("stopping").GetBoolean());
        Assert.Equal("contentNotEquivalent", json.GetProperty("items")[1].GetProperty("reason").GetString());
        Assert.Equal(JsonValueKind.Null, json.GetProperty("items")[0].GetProperty("shared").ValueKind);
    }

    [Fact]
    public void SettlingHasEndedItemsAndNoFinishTime()
    {
        var items = new[] { Item(A, "Sintel", LocalDeleteItemState.Done), Item(B, "Big Buck Bunny", LocalDeleteItemState.Done) };
        Assert.Equal("settling", Match("local-delete.settling", Batch(LocalDeleteBatchState.Settling, null, false, items)).GetProperty("state").GetString());
    }

    [Fact]
    public void FinishedCoversEveryEndState()
    {
        // notConfirmed stops the batch (plan: stop-the-batch reasons), so this batch ends "stopped" and the item after it is notAttempted.
        var items = new[]
        {
            Item(A, "Sintel", LocalDeleteItemState.Done) with { Shared = LocalDeleteShared.Present, History = LocalDeleteHistory.Carried },
            Item(B, "Big Buck Bunny", LocalDeleteItemState.Done) with { Shared = LocalDeleteShared.Pending, History = LocalDeleteHistory.NotCarried },
            Item(C, "Tears of Steel", LocalDeleteItemState.Refused) with { Reason = LocalDeleteReason.FolderNotDedicated },
            Item(D, "Elephants Dream", LocalDeleteItemState.NotConfirmed) with { Reason = LocalDeleteReason.NotConfirmed },
            Item(E, "Cosmos Laundromat", LocalDeleteItemState.NotAttempted) with { Reason = LocalDeleteReason.NotConfirmed },
        };
        var json = Match("local-delete.finished", Batch(LocalDeleteBatchState.Stopped, Started.AddMinutes(9), false, items));
        var counts = json.GetProperty("counts");
        Assert.Equal(2, counts.GetProperty("done").GetInt32());
        Assert.Equal(1, counts.GetProperty("refused").GetInt32());
        Assert.Equal(1, counts.GetProperty("notConfirmed").GetInt32());
        Assert.Equal(1, counts.GetProperty("notAttempted").GetInt32());
        Assert.Equal("pending", json.GetProperty("items")[1].GetProperty("shared").GetString());
        Assert.Equal("notCarried", json.GetProperty("items")[1].GetProperty("history").GetString());
    }

    [Fact]
    public void StopRepliesStoppingUntilTheCurrentItemEnds()
    {
        var items = new[] { Item(A, "Sintel", LocalDeleteItemState.Deleting), Item(B, "Big Buck Bunny", LocalDeleteItemState.Queued) };
        Assert.True(Match("local-delete.stopping", Batch(LocalDeleteBatchState.Running, null, true, items)).GetProperty("stopping").GetBoolean());
    }

    [Fact]
    public void CurrentWithoutABatchIsNotFound()
    {
        var rendered = AdminTestSupport.Render(AdminReply.Error(404, "notFound"));
        Assert.Equal(404, rendered.Status);
        ContractGolden.Match("local-delete.current-none", rendered.Text);
    }

    [Fact]
    public void EveryReasonWordIsCamelCaseAndUnique()
    {
        var words = Enum.GetValues<LocalDeleteReason>().Select(r => JsonSerializer.Serialize(r, AdminJson.Options).Trim('"')).ToArray();
        Assert.Equal(16, words.Length);
        Assert.Equal(words.Length, words.Distinct(StringComparer.Ordinal).Count());
        Assert.All(words, w => Assert.Matches("^[a-z][A-Za-z]+$", w));
    }

    [Fact]
    public void NoReplyNamesAPathTokenOrPeerItem()
    {
        var names = new[] { typeof(LocalDeleteStateDto), typeof(LocalDeleteCandidateDto), typeof(LocalDeleteLegacyJobDto), typeof(LocalDeleteStartDto), typeof(LocalDeleteBatchDto), typeof(LocalDeleteItemDto), typeof(LocalDeleteCountsDto) }
            .SelectMany(t => t.GetProperties()).Select(p => p.Name.ToLowerInvariant()).ToArray();
        Assert.DoesNotContain(names, n => n.Contains("path", StringComparison.Ordinal) || n.Contains("token", StringComparison.Ordinal) || n.Contains("secret", StringComparison.Ordinal)
            || (n.Contains("key", StringComparison.Ordinal) && n != "itemkey") || n.Contains("peer", StringComparison.Ordinal) || n.Contains("sha256", StringComparison.Ordinal) || n.Contains("digest", StringComparison.Ordinal));
    }

    [Fact]
    public void EveryGoldenFileHasNoPathOrToken()
    {
        foreach (var file in System.IO.Directory.GetFiles(ContractGolden.Folder, "local-delete.*.json"))
        {
            var text = System.IO.File.ReadAllText(file);
            Assert.DoesNotContain("/", text.Replace("\\/", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
            Assert.DoesNotContain("\\\\", text, StringComparison.Ordinal);
            Assert.DoesNotContain("token", text, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static LocalDeleteItemDto Item(string key, string title, LocalDeleteItemState state) => new(key, title, 2010, 199749, state);

    private static LocalDeleteBatchDto Batch(LocalDeleteBatchState state, DateTimeOffset? finished, bool stopping, LocalDeleteItemDto[] items)
    {
        int Count(LocalDeleteItemState s) => items.Count(i => i.State == s);
        var counts = new LocalDeleteCountsDto(Count(LocalDeleteItemState.Done), Count(LocalDeleteItemState.Refused), Count(LocalDeleteItemState.NotConfirmed), Count(LocalDeleteItemState.NotAttempted), items.Count(i => i.State is LocalDeleteItemState.Queued or LocalDeleteItemState.Checking or LocalDeleteItemState.Deleting));
        return new LocalDeleteBatchDto(BatchKey, state, Started, finished, stopping, counts, items);
    }

    private static JsonElement Match(string name, object body)
    {
        var rendered = AdminTestSupport.Render(AdminReply.Json(200, body));
        ContractGolden.Match(name, rendered.Text);
        return rendered.Json;
    }
}
