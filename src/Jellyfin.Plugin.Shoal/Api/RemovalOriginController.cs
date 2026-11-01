using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.Authorization;
using Jellyfin.Plugin.Shoal.Http;
using Jellyfin.Plugin.Shoal.LocalRemoval;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Shoal.Api;

/// <summary>Scoped read-only proof of one currently shared physical movie.</summary>
[ApiController]
[Authorize]
[Route("Shoal/Share/RemovalOrigin")]
public sealed class RemovalOriginController : ControllerBase
{
    private readonly RemovalOriginProof _proof;

    /// <summary>Initializes a new instance of the <see cref="RemovalOriginController"/> class.</summary>
    /// <param name="proof">The singleton scoped proof service.</param>
    public RemovalOriginController(RemovalOriginProof proof) => _proof = proof;

    /// <summary>Hashes one admitted movie without exposing its path or accepting one.</summary>
    /// <param name="itemId">The current peer movie item.</param>
    /// <param name="cancellationToken">The requesting client's cancellation.</param>
    /// <returns>A scoped proof or a fixed refusal.</returns>
    [HttpGet("{itemId:guid}")]
    [Produces("application/json")]
    public async Task<IActionResult> Get(Guid itemId, CancellationToken cancellationToken)
    {
        var principal = HttpContext.User;
        if (principal.Identity?.IsAuthenticated != true || string.Equals(principal.FindFirst(CallerContext.IsApiKeyClaim)?.Value, "true", StringComparison.OrdinalIgnoreCase)
            || !Guid.TryParse(principal.FindFirst(CallerContext.UserIdClaim)?.Value, out var userId)
            || principal.FindFirst(CallerContext.TokenClaim)?.Value is not { Length: > 0 } token)
        {
            return new ShoalResult(404, new { });
        }

        var result = await _proof.ReadAsync(userId, token, itemId, cancellationToken).ConfigureAwait(false);
        return result.Status switch
        {
            200 => new ShoalResult(200, result.Proof),
            429 => new ShoalResult(429, new { error = "rateLimited" }, result.RetryAfterSeconds),
            409 => new ShoalResult(409, new { error = "sourceChanged" }),
            _ => new ShoalResult(404, new { }),
        };
    }
}
