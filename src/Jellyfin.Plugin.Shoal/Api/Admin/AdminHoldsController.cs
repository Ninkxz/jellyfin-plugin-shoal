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
/// What the guards of a TV library hold back, one row per series (DESIGN v2.7.2, "Release works by series group"). The page offers "Release" on a row, which is
/// <c>POST Peers/{h}/Holds/{kind}/Release?series=&lt;key&gt;</c>. The key is Home's own random key; no peer id is in the answer.
/// </summary>
[ApiController]
[Authorize(Policy = Policies.RequiresElevation)]
[Route("Shoal/Admin")]
public class AdminHoldsController : ControllerBase
{
    private readonly IStateStore _state;
    private readonly CatalogStore _catalogs;

    /// <summary>
    /// Initializes a new instance of the <see cref="AdminHoldsController"/> class.
    /// </summary>
    /// <param name="state">The state store.</param>
    /// <param name="catalogs">The catalogs.</param>
    public AdminHoldsController(IStateStore state, CatalogStore catalogs)
    {
        _state = state;
        _catalogs = catalogs;
    }

    /// <summary>
    /// Lists the held removals and additions of a server by series.
    /// </summary>
    /// <param name="handle">The server's handle.</param>
    /// <returns>The groups, 404 or 409.</returns>
    [HttpGet("Peers/{handle}/Holds")]
    [Produces("application/json")]
    public IActionResult Holds(string handle)
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
        if (load.Unreadable)
        {
            return AdminReply.Json(StatusCodes.Status200OK, new HoldsDto([], []));
        }

        var series = load.Document.Entries.Where(static e => e.Kind == EntryKind.Series).ToDictionary(static e => e.StubKey, StringComparer.Ordinal);
        var removals = load.Document.Entries
            .Where(static e => e.Kind == EntryKind.Episode && e.State == EntryState.HeldRemoval && e.ParentKey is not null)
            .GroupBy(static e => e.ParentKey!, StringComparer.Ordinal)
            .Select(g => new HoldGroupDto(g.Key, series.TryGetValue(g.Key, out var owner) ? owner.Title : string.Empty, series.TryGetValue(g.Key, out var dated) ? dated.Year : null, g.Count()))
            .OrderBy(static g => g.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var additions = load.Document.HeldSeries
            .Select(static h => new HoldGroupDto(h.Key, h.Title, h.Year, h.Episodes))
            .OrderBy(static g => g.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return AdminReply.Json(StatusCodes.Status200OK, new HoldsDto(removals, additions));
    }
}

/// <summary>
/// The held groups of a server.
/// </summary>
/// <param name="Removals">Series with episodes whose removal is held.</param>
/// <param name="Additions">Series with episodes whose addition is held.</param>
internal sealed record HoldsDto(IReadOnlyList<HoldGroupDto> Removals, IReadOnlyList<HoldGroupDto> Additions);

/// <summary>
/// One held series.
/// </summary>
/// <param name="Series">Home's key of the group (the <c>series</c> value of Release).</param>
/// <param name="Title">The sanitized title, or empty.</param>
/// <param name="Year">The year, or null.</param>
/// <param name="Episodes">How many episodes are held.</param>
internal sealed record HoldGroupDto(string Series, string Title, int? Year, int Episodes);
