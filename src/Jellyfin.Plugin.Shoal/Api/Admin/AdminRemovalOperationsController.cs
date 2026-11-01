using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.Authorization;
using Jellyfin.Plugin.Shoal.Http;
using Jellyfin.Plugin.Shoal.LocalRemoval;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Shoal.Api.Admin;

/// <summary>
/// Restore of originals held by 2.10.2/2.10.3 recoverable removal (Table 2 row 8). 2.10.4 K4b: the move (Plans, Confirm), the retained-original
/// deletion (PurgePlans, Confirm) and the PermanentDeletionEnabled switch are removed and answer 404; their coordinator code is dormant.
/// State, Status and Enabled are in <see cref="AdminLocalDeleteController"/>.
/// </summary>
[ApiController]
[Authorize(Policy = Policies.RequiresElevation)]
[Route("Shoal/Admin")]
[RequestSizeLimit(1024)]
public sealed class AdminRemovalOperationsController : ControllerBase
{
    private static readonly HashSet<string> Codes = new(StringComparer.Ordinal)
    {
        "unsupportedPlatform", "busy", "historyChanged", "historyConflict", "historyUserChanged", "removalChanged", "restoredItemNotIndexed", "recoveryRequired", "notFound", "administratorRequired",
    };

    private readonly RemovalCoordinator _coordinator;

    /// <summary>Initializes a new instance of the <see cref="AdminRemovalOperationsController"/> class.</summary>
    /// <param name="coordinator">The singleton recoverable operation service.</param>
    public AdminRemovalOperationsController(RemovalCoordinator coordinator) => _coordinator = coordinator;

    /// <summary>Restores retained bytes without consulting the friend's availability (works with the switch off).</summary>
    /// <param name="jobId">The opaque durable recovery handle.</param>
    /// <param name="cancellationToken">Cancellation before recovery acceptance.</param>
    /// <returns>The recovered or explicitly held operation.</returns>
    [HttpPost("LocalRemoval/Jobs/{jobId}/Restore")]
    public Task<IActionResult> Restore(string jobId, CancellationToken cancellationToken) => Opaque(jobId)
        ? RunAsync(() =>
        {
            _ = Admin();
            return _coordinator.RestoreAsync(jobId, cancellationToken);
        }) : Task.FromResult<IActionResult>(AdminReply.Error(400, "invalidRequest"));

    private static bool Opaque(string? value) => value is { Length: 32 } && value.All(static c => c is >= '0' and <= '9' or >= 'a' and <= 'f') && Guid.TryParseExact(value, "N", out var id) && id != Guid.Empty;

    private static async Task<IActionResult> RunAsync(Func<Task<object>> action)
    {
        try
        {
            return AdminReply.Json(200, await action().ConfigureAwait(false));
        }
        catch (InvalidOperationException ex)
        {
            var code = Codes.Contains(ex.Message) ? ex.Message : "removalUnavailable";
            return code switch
            {
                "notFound" => AdminReply.Error(404, code),
                "administratorRequired" => AdminReply.Error(403, code),
                _ => AdminReply.Error(409, code),
            };
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return AdminReply.Error(409, "recoveryRequired");
        }
    }

    private Guid Admin()
    {
        if (HttpContext.User.Identity?.IsAuthenticated != true || string.Equals(HttpContext.User.FindFirst(CallerContext.IsApiKeyClaim)?.Value, "true", StringComparison.OrdinalIgnoreCase)
            || !Guid.TryParse(HttpContext.User.FindFirst(CallerContext.UserIdClaim)?.Value, out var id) || id == Guid.Empty)
        {
            throw new InvalidOperationException("administratorRequired");
        }

        return id;
    }
}

/// <summary>Only an explicit enablement value is accepted.</summary>
public sealed class RemovalEnabledRequest
{
    /// <summary>Gets the requested toggle, absent means invalid.</summary>
    public bool? Enabled { get; init; }
}
