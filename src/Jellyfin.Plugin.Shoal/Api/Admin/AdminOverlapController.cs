using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Shoal.State;
using Jellyfin.Plugin.Shoal.Sync;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Shoal.Api.Admin;

/// <summary>
/// The overlap and storage report of one server (DESIGN 5.8, 9.6): how many titles both servers have, how much of Home's storage that is,
/// and how many titles Home gains without storing them, in total and by quality. It reads the catalog the last complete sync wrote; it
/// asks nobody and it carries no peer-side id.
/// </summary>
[ApiController]
[Authorize(Policy = Policies.RequiresElevation)]
[Route("Shoal/Admin")]
public class AdminOverlapController : ControllerBase
{
    private readonly IStateStore _state;
    private readonly CatalogStore _catalogs;
    private readonly QualityAdmin? _quality;

    /// <summary>
    /// Initializes a new instance of the <see cref="AdminOverlapController"/> class.
    /// </summary>
    /// <param name="state">The state store.</param>
    /// <param name="catalogs">The catalogs.</param>
    /// <param name="quality">The backup counts (PLAN v2.9), or null.</param>
    public AdminOverlapController(IStateStore state, CatalogStore catalogs, QualityAdmin? quality = null)
    {
        _state = state;
        _catalogs = catalogs;
        _quality = quality;
    }

    /// <summary>
    /// The quality bucket of a title from its picture size: 4k, 1080p, 720p or sd.
    /// </summary>
    /// <param name="width">The width in pixels (0 when unknown).</param>
    /// <param name="height">The height in pixels (0 when unknown).</param>
    /// <returns>The bucket word.</returns>
    public static string QualityOf(int width, int height) => height >= 2000 || width >= 3200 ? "4k"
        : height >= 1000 || width >= 1800 ? "1080p"
        : height >= 700 || width >= 1200 ? "720p"
        : "sd";

    /// <summary>
    /// The overlap report of a server.
    /// </summary>
    /// <param name="handle">The server's handle.</param>
    /// <returns>The report (<c>computedUtc</c> is absent before the first complete sync), 404 or 409.</returns>
    [HttpGet("Peers/{handle}/Overlap")]
    [Produces("application/json")]
    public IActionResult Overlap(string handle)
    {
        if (_state.Status != StateStatus.Ready)
        {
            return AdminReply.Error(StatusCodes.Status409Conflict, "recovery");
        }

        if (!_state.Current.Home.Peers.Any(p => string.Equals(p.PeerKey, handle, StringComparison.Ordinal)))
        {
            return AdminReply.Error(StatusCodes.Status404NotFound, "notFound");
        }

        var load = _catalogs.Load(handle);
        var report = load.Document.Report;
        var backups = _quality?.Backups(handle);
        if (load.Unreadable || report is null)
        {
            return AdminReply.Json(StatusCodes.Status200OK, new OverlapDto { Backups = backups });
        }

        var both = load.Document.Entries.Where(static e => e.State == EntryState.DuplicateLocal && e.Kind is EntryKind.Movie or EntryKind.Episode).ToList();
        var gain = load.Document.Entries.Where(static e => e.State == EntryState.Active && e.Kind is EntryKind.Movie or EntryKind.Episode).ToList();
        var buckets = new[] { "4k", "1080p", "720p", "sd" };
        var byQuality = new List<OverlapQualityDto>();
        foreach (var bucket in buckets)
        {
            var b = both.Where(e => QualityOf(e.Width, e.Height) == bucket).ToList();
            var g = gain.Where(e => QualityOf(e.Width, e.Height) == bucket).ToList();
            byQuality.Add(new OverlapQualityDto(bucket, b.Count, b.Sum(static e => e.LocalSizeBytes), g.Count, g.Sum(static e => e.SizeBytes)));
        }

        return AdminReply.Json(
            StatusCodes.Status200OK,
            new OverlapDto
            {
                ComputedUtc = new DateTimeOffset(DateTime.SpecifyKind(report.ComputedUtc, DateTimeKind.Utc)),
                Both = new OverlapSideDto(report.OverlapCount, report.OverlapLocalBytes, null),
                Gain = new OverlapSideDto(report.GainCount, null, report.GainBytes),
                ByQuality = byQuality,
                Tv = load.Document.Entries.Any(static e => e.Kind != EntryKind.Movie)
                    ? new OverlapTvDto(report.SharedSeries, report.SharedSeriesEpisodes, report.GainSeries, report.GainEpisodes)
                    : null,
                Backups = backups,
            });
    }
}

/// <summary>
/// The overlap report as the admin page reads it.
/// </summary>
internal sealed class OverlapDto
{
    /// <summary>Gets when the last complete sync computed it; absent before the first one.</summary>
    public DateTimeOffset? ComputedUtc { get; init; }

    /// <summary>Gets the titles both servers have.</summary>
    public OverlapSideDto? Both { get; init; }

    /// <summary>Gets the titles Home gains without storing them.</summary>
    public OverlapSideDto? Gain { get; init; }

    /// <summary>Gets the same by quality.</summary>
    public IReadOnlyList<OverlapQualityDto>? ByQuality { get; init; }

    /// <summary>Gets the TV lines (absent when the server has no TV entries): "N shows you already have, K episodes of them", "M shows, E episodes gained".</summary>
    public OverlapTvDto? Tv { get; init; }

    /// <summary>Gets the backup counts (PLAN v2.9): titles another friend also has, how many this server plays first, how many it backs up; absent without groups.</summary>
    public BackupCountsDto? Backups { get; init; }
}

/// <summary>
/// One half of the report: a count and the bytes on one side.
/// </summary>
/// <param name="Count">How many titles.</param>
/// <param name="LocalBytes">Home's own copies, in bytes (the "both" half).</param>
/// <param name="PeerBytes">The server's copies, in bytes (the "gain" half).</param>
internal sealed record OverlapSideDto(int Count, long? LocalBytes, long? PeerBytes);

/// <summary>
/// The TV lines of the overlap report (DESIGN v2.7, decision 3): a series Home has hides the friend's whole copy, so the episodes it hides are counted apart.
/// </summary>
/// <param name="SharedSeries">Shows both servers have.</param>
/// <param name="SharedSeriesEpisodes">Episodes the friend has of those shows (not imported).</param>
/// <param name="GainSeries">Shows Home gains.</param>
/// <param name="GainEpisodes">Episodes Home gains (counted in the gain total too).</param>
internal sealed record OverlapTvDto(int SharedSeries, int SharedSeriesEpisodes, int GainSeries, int GainEpisodes);

/// <summary>
/// One row of the quality table.
/// </summary>
/// <param name="Q">4k, 1080p, 720p or sd.</param>
/// <param name="BothCount">Titles both servers have.</param>
/// <param name="LocalBytes">Home's own copies of those.</param>
/// <param name="GainCount">Titles only the server has.</param>
/// <param name="PeerBytes">The server's copies of those.</param>
internal sealed record OverlapQualityDto(string Q, int BothCount, long LocalBytes, int GainCount, long PeerBytes);
