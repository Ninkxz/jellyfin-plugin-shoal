using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.Sharing;
using Jellyfin.Plugin.Shoal.State;
using Jellyfin.Plugin.Shoal.Visibility;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Shoal.Api.Admin;

/// <summary>
/// Deleting a leftover sharing account (Warden 08 M7). The Overview lists the accounts named <c>shoal-*</c> that no share uses; nothing is
/// ever deleted without this call, and the call re-checks that the account still is one of them.
/// </summary>
[ApiController]
[Authorize(Policy = Policies.RequiresElevation)]
[Route("Shoal/Admin")]
public class AdminOrphanAccountsController : ControllerBase
{
    private readonly IStateStore _state;
    private readonly IAccountSource _accounts;
    private readonly IShareUserSource _users;
    private readonly ILogger<AdminOrphanAccountsController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="AdminOrphanAccountsController"/> class.
    /// </summary>
    /// <param name="state">The state store.</param>
    /// <param name="accounts">The accounts.</param>
    /// <param name="users">Deletes accounts.</param>
    /// <param name="logger">The logger.</param>
    public AdminOrphanAccountsController(IStateStore state, IAccountSource accounts, IShareUserSource users, ILogger<AdminOrphanAccountsController> logger)
    {
        _state = state;
        _accounts = accounts;
        _users = users;
        _logger = logger;
    }

    /// <summary>
    /// Deletes one leftover sharing account.
    /// </summary>
    /// <param name="userId">The account.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>200, or 404 when the account is not (or no longer) a leftover, or 409 while the state is not trusted.</returns>
    [HttpDelete("OrphanAccounts/{userId}")]
    [Produces("application/json")]
    public async Task<IActionResult> Delete(string userId, CancellationToken cancellationToken)
    {
        if (_state.Status != StateStatus.Ready)
        {
            return AdminReply.Error(StatusCodes.Status409Conflict, "recovery");
        }

        if (!Guid.TryParse(userId, out var id) || OrphanAccounts.Find(_state, _accounts.List()).FirstOrDefault(a => a.Id == id) is not { } orphan)
        {
            return AdminReply.Error(StatusCodes.Status404NotFound, "notFound");
        }

        try
        {
            await _users.DeleteAsync(orphan.Id, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning("An admin asked to delete a leftover sharing account and it failed ({Error})", ex.GetType().Name);
            return AdminReply.Error(StatusCodes.Status500InternalServerError, "server");
        }

        _logger.LogWarning("Admin deleted a leftover sharing account ({Name})", orphan.Name);
        return AdminReply.Json(StatusCodes.Status200OK, new { });
    }
}
