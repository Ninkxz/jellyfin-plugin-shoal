using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.Authorization;
using Jellyfin.Plugin.Shoal.Http;
using Jellyfin.Plugin.Shoal.Playback;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Shoal.Api;

/// <summary>
/// <c>GET Shoal/Subtitles/{localItemId}/{index}/Stream.{fmt}</c> (DESIGN 6.1, 6.6a): the <c>DeliveryUrl</c> Shoal puts on an external text subtitle. The
/// same <see cref="ISubtitleHandler"/> serves the intercepted core routes. CallerContext runs for the item before anything else.
/// </summary>
[Authorize]
[Route("Shoal/Subtitles")]
public sealed class ShoalSubtitlesController : ControllerBase
{
    private const string Template = "Shoal/Subtitles/{localItemId}/{index}/Stream.{fmt}";

    private readonly CallerContext _caller;
    private readonly ShoalIndex _index;
    private readonly ISubtitleHandler _handler;

    /// <summary>
    /// Initializes a new instance of the <see cref="ShoalSubtitlesController"/> class.
    /// </summary>
    /// <param name="caller">The request's authorization.</param>
    /// <param name="index">The federated item index.</param>
    /// <param name="handler">The subtitle handler.</param>
    public ShoalSubtitlesController(CallerContext caller, ShoalIndex index, ISubtitleHandler handler)
    {
        _caller = caller;
        _index = index;
        _handler = handler;
    }

    /// <summary>
    /// A subtitle file.
    /// </summary>
    /// <param name="localItemId">The local item id.</param>
    /// <param name="index">The stream index.</param>
    /// <param name="fmt">The format.</param>
    /// <param name="cancellationToken">The request's abort token.</param>
    /// <returns>The rebuilt subtitle.</returns>
    [HttpGet("{localItemId:guid}/{index:int:range(0,127)}/Stream.{fmt}")]
    public async Task<IActionResult> Stream(Guid localItemId, int index, string fmt, CancellationToken cancellationToken)
    {
        var decision = await _caller.AuthorizeAsync(HttpContext, localItemId, cancellationToken).ConfigureAwait(false);
        if (decision.Caller is not { } caller)
        {
            return new ShoalResult(decision.Status);
        }

        var stub = _index.Get(localItemId);
        if (stub is null)
        {
            return new ShoalResult(StatusCodes.Status404NotFound);
        }

        return await _handler.HandleAsync(new FederatedRequest(HttpContext, localItemId, stub, caller, Template), cancellationToken).ConfigureAwait(false);
    }
}
