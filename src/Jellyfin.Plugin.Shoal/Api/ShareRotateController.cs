using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.Authorization;
using Jellyfin.Plugin.Shoal.Http;
using Jellyfin.Plugin.Shoal.Sharing;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Shoal.Api;

/// <summary>
/// <c>POST Shoal/Share/Rotate</c> (DESIGN v2.6.1, K25): Home asks this Peer for a new token for its sharing account. The route is
/// <c>[Authorize]</c> (core authenticates the old token); who the caller is comes from core's claims, never from the body or the DeviceId claim.
/// Every caller that is not an enabled share user holding the live token of an expected device gets the same <c>404 {}</c>.
/// </summary>
[ApiController]
[Authorize]
[Route("Shoal/Share")]
public sealed class ShareRotateController : ControllerBase
{
    private const int MaxBodyBytes = 64;

    private readonly ShareRotation _rotation;

    /// <summary>
    /// Initializes a new instance of the <see cref="ShareRotateController"/> class.
    /// </summary>
    /// <param name="rotation">The rotation service.</param>
    public ShareRotateController(ShareRotation rotation) => _rotation = rotation;

    /// <summary>
    /// Rotates the caller's token.
    /// </summary>
    /// <param name="cancellationToken">The request's abort token.</param>
    /// <returns>200 with the new token, 404 <c>{}</c>, 409 busy, 429 with Retry-After, or 500 rotationFailed.</returns>
    [HttpPost("Rotate")]
    [Produces("application/json")]
    public async Task<IActionResult> Rotate(CancellationToken cancellationToken)
    {
        var principal = HttpContext.User;
        var isApiKey = string.Equals(principal.FindFirst(CallerContext.IsApiKeyClaim)?.Value, "true", StringComparison.OrdinalIgnoreCase);
        if (isApiKey || !Guid.TryParse(principal.FindFirst(CallerContext.UserIdClaim)?.Value, out var userId) || principal.FindFirst(CallerContext.TokenClaim)?.Value is not { Length: > 0 } token
            || !await BodyIsOkAsync(cancellationToken).ConfigureAwait(false))
        {
            return Stranger();
        }

        var result = await _rotation.RotateAsync(userId, token, cancellationToken).ConfigureAwait(false);
        return result.Outcome switch
        {
            RotationOutcome.Ok => new ShoalResult(StatusCodes.Status200OK, new { v = 1, token = result.Token, deviceId = result.DeviceId, rotatedUtc = result.RotatedUtc }),
            RotationOutcome.Busy => new ShoalResult(StatusCodes.Status409Conflict, new { error = "busy" }),
            RotationOutcome.TooMany => new ShoalResult(StatusCodes.Status429TooManyRequests, new { error = "rateLimited" }, result.RetryAfterSeconds),
            RotationOutcome.Failed => new ShoalResult(StatusCodes.Status500InternalServerError, new { error = "rotationFailed" }),
            _ => Stranger(),
        };
    }

    private static ShoalResult Stranger() => new(StatusCodes.Status404NotFound, new { });

    // {"v":1}, application/json, at most 64 bytes. Anything else is the same 404 as a stranger gets.
    private async Task<bool> BodyIsOkAsync(CancellationToken cancellationToken)
    {
        if (!Request.HasJsonContentType() || Request.ContentLength is null or > MaxBodyBytes)
        {
            return false;
        }

        var buffer = new byte[MaxBodyBytes + 1];
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await Request.Body.ReadAsync(buffer.AsMemory(read), cancellationToken).ConfigureAwait(false);
            if (n == 0)
            {
                break;
            }

            read += n;
        }

        if (read is 0 or > MaxBodyBytes)
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(buffer.AsMemory(0, read));
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("v", out var version)
                && version.ValueKind == JsonValueKind.Number
                && version.TryGetInt32(out var number)
                && number == 1;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
