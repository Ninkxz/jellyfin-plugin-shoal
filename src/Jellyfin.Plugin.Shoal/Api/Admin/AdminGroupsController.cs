using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.State;
using Jellyfin.Plugin.Shoal.Sync;
using Jellyfin.Plugin.Shoal.Visibility;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Shoal.Api.Admin;

/// <summary>
/// The migration of existing duplicates into title groups (PLAN v2.9 K55): a preview, and the run. Admin only.
/// </summary>
[ApiController]
[Authorize(Policy = Policies.RequiresElevation)]
[Route("Shoal/Admin")]
public class AdminGroupsController : ControllerBase
{
    private readonly IStateStore _state;
    private readonly GroupMigration _migration;
    private readonly LayoutJobService? _jobs;

    /// <summary>
    /// Initializes a new instance of the <see cref="AdminGroupsController"/> class.
    /// </summary>
    /// <param name="state">The state store.</param>
    /// <param name="migration">The migration.</param>
    /// <param name="jobs">The layout move job (no migration while one waits), or null.</param>
    public AdminGroupsController(IStateStore state, GroupMigration migration, LayoutJobService? jobs = null)
    {
        _state = state;
        _migration = migration;
        _jobs = jobs;
    }

    /// <summary>
    /// What the migration would do: <c>{ duplicateTitles, newGroups, extraCopies, ran: false }</c>. Nothing is written.
    /// </summary>
    /// <returns>200 or 409 (<c>recovery</c>).</returns>
    [HttpGet("Groups/Migration")]
    [Produces("application/json")]
    public IActionResult Preview() =>
        _state.Status != StateStatus.Ready ? AdminReply.Error(StatusCodes.Status409Conflict, "recovery") : AdminReply.Json(StatusCodes.Status200OK, _migration.Preview());

    /// <summary>
    /// Runs it: groups, then the users' alternate-copy tags, then the extra copies, then a targeted scan (no title shows twice in between).
    /// </summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>200 with the report, or 409 (<c>recovery</c>, <c>busy</c> while a library move waits or a layout change runs, <c>syncing</c> during a sync).</returns>
    [HttpPost("Groups/Migration")]
    [Produces("application/json")]
    public async Task<IActionResult> Run(CancellationToken cancellationToken)
    {
        if (_state.Status != StateStatus.Ready)
        {
            return AdminReply.Error(StatusCodes.Status409Conflict, "recovery");
        }

        if (_jobs?.Unfinished == true)
        {
            return AdminReply.Error(StatusCodes.Status409Conflict, "busy");
        }

        var report = await _migration.RunAsync(cancellationToken).ConfigureAwait(false);
        return report.Refused is { } word ? AdminReply.Error(StatusCodes.Status409Conflict, word) : AdminReply.Json(StatusCodes.Status200OK, report);
    }
}
