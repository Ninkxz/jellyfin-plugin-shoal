using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Shoal.Sync;

namespace Jellyfin.Plugin.Shoal.LocalRemoval;

/// <summary>Cached overlap discovery only. No result grants permission to remove media.</summary>
internal sealed class LocalRemovalPreview
{
    /// <summary>Gets a value indicating whether this is discovery only.</summary>
    public bool PreviewOnly { get; } = true;

    /// <summary>Gets a value indicating whether removal is available. Always false.</summary>
    public bool RemovalEnabled { get; }

    /// <summary>Gets space reclaimed by this preview. Always zero.</summary>
    public long ReclaimableBytes { get; }

    /// <summary>Gets why cached discovery is available or unavailable.</summary>
    public required string CatalogStatus { get; init; }

    /// <summary>Gets when the last completed sync computed these cached overlaps.</summary>
    public DateTimeOffset? ComputedUtc { get; init; }

    /// <summary>Gets the requested row offset.</summary>
    public int Offset { get; init; }

    /// <summary>Gets the bounded page size.</summary>
    public int Limit { get; init; }

    /// <summary>Gets cached movie overlap rows, not unique local files.</summary>
    public int Total { get; init; }

    /// <summary>Gets a value indicating whether more cached rows exist.</summary>
    public bool HasMore => Offset < Total && Items.Count < Total - Offset;

    /// <summary>Gets the bounded unverified overlap rows.</summary>
    public IReadOnlyList<LocalRemovalPreviewItem> Items { get; init; } = [];

    /// <summary>Builds a bounded page without touching local media or contacting friends.</summary>
    /// <param name="document">The validated cached catalog.</param>
    /// <param name="status">The availability code.</param>
    /// <param name="offset">The validated nonnegative offset.</param>
    /// <param name="limit">The validated page size.</param>
    /// <returns>A preview whose rows are all blocked.</returns>
    public static LocalRemovalPreview Build(CatalogDocument document, string status, int offset, int limit)
    {
        var rows = status == "ready"
            ? document.Entries.Where(static e => e.Kind == EntryKind.Movie && e.State == EntryState.DuplicateLocal).ToList()
            : [];
        return new LocalRemovalPreview
        {
            CatalogStatus = status,
            ComputedUtc = status == "ready" && document.Report is { } report
                ? new DateTimeOffset(DateTime.SpecifyKind(report.ComputedUtc, DateTimeKind.Utc)) : null,
            Offset = offset,
            Limit = limit,
            Total = rows.Count,
            Items = rows.Skip(offset).Take(limit).Select(static e => new LocalRemovalPreviewItem(e.Title, e.Year, Math.Max(0, e.LocalSizeBytes))).ToArray(),
        };
    }
}

/// <summary>An unverified cached movie overlap. No file or peer identifiers are exposed.</summary>
/// <param name="Title">The sanitized display title.</param>
/// <param name="Year">The production year, if known.</param>
/// <param name="LocalBytesEstimate">The cached local size estimate, not reclaimable space.</param>
internal sealed record LocalRemovalPreviewItem(string Title, int? Year, long LocalBytesEstimate)
{
    /// <summary>Gets a value indicating whether removal has been proven safe. Always false.</summary>
    public bool SafeToRemove { get; }

    /// <summary>Gets independent proofs this cached overlap cannot establish.</summary>
    public IReadOnlyList<string> Blockers { get; } = Array.AsReadOnly(new[]
    {
        "identityUnverified", "freshnessUnverified", "qualityUnverified",
        "originRetentionUnverified", "accessUnverified", "historyTransferUnverified",
    });
}
