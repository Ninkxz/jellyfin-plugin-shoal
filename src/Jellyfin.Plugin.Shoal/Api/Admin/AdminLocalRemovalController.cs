using System;
using System.Linq;
using Jellyfin.Plugin.Shoal.LocalRemoval;
using Jellyfin.Plugin.Shoal.State;
using Jellyfin.Plugin.Shoal.Sync;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Shoal.Api.Admin;

/// <summary>Read-only discovery of cached local movie overlaps. Never removes files.</summary>
[ApiController]
[Authorize(Policy = Policies.RequiresElevation)]
[Route("Shoal/Admin")]
public sealed class AdminLocalRemovalController : ControllerBase
{
    private readonly IStateStore _state;
    private readonly CatalogStore _catalogs;

    /// <summary>Initializes a new instance of the <see cref="AdminLocalRemovalController"/> class.</summary>
    /// <param name="state">Current local topology.</param>
    /// <param name="catalogs">Validated cached catalogs.</param>
    public AdminLocalRemovalController(IStateStore state, CatalogStore catalogs)
    {
        _state = state;
        _catalogs = catalogs;
    }

    /// <summary>Lists possible movie overlaps; every row remains unverified and blocked.</summary>
    /// <param name="handle">The configured friend handle.</param>
    /// <param name="offset">A nonnegative row offset.</param>
    /// <param name="limit">A page size between one and one hundred.</param>
    /// <returns>A read-only preview or a fail-closed error.</returns>
    [HttpGet("Peers/{handle}/LocalRemovalPreview")]
    [Produces("application/json")]
    public IActionResult Preview(string handle, [FromQuery] int offset = 0, [FromQuery] int limit = 50)
    {
        if (offset < 0 || limit is < 1 or > 100)
        {
            return AdminReply.Error(StatusCodes.Status400BadRequest, "invalidRequest");
        }

        if (_state.Status != StateStatus.Ready)
        {
            return AdminReply.Error(StatusCodes.Status409Conflict, "recovery");
        }

        var peer = _state.Current.Home.Peers.FirstOrDefault(p => string.Equals(p.PeerKey, handle, StringComparison.Ordinal));
        if (peer is null)
        {
            return AdminReply.Error(StatusCodes.Status404NotFound, "notFound");
        }

        if (!peer.Enabled || peer.Pairing?.Method != PairingMethod.Invite)
        {
            return AdminReply.Json(StatusCodes.Status200OK, LocalRemovalPreview.Build(new CatalogDocument(), !peer.Enabled ? "disabled" : "needsPairing", offset, limit));
        }

        var load = _catalogs.LoadForPreview(handle);
        if (load.Unreadable || load.Dropped > 0 || load.Document.LoadedWithLoss)
        {
            return AdminReply.Error(StatusCodes.Status409Conflict, "recovery");
        }

        return AdminReply.Json(StatusCodes.Status200OK, LocalRemovalPreview.Build(load.Document, load.Document.Report is null ? "neverSynced" : "ready", offset, limit));
    }
}
