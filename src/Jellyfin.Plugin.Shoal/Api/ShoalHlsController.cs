using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.Authorization;
using Jellyfin.Plugin.Shoal.Playback;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Shoal.Api;

/// <summary>
/// The HLS proxy endpoints (DESIGN 6.4). The path names a grant, never an item: the Peer request is built from the grant and a few validated
/// integers. Core authenticates the caller; <see cref="HlsProxyHandler"/> runs CallerContext steps 1 to 4 on every request and checks that the
/// grant belongs to the caller (another user's grant is a 404).
/// </summary>
[Authorize]
[Route("Shoal/Hls/{grantId}")]
public sealed class ShoalHlsController : ControllerBase
{
    private readonly CallerContext _caller;
    private readonly HlsProxyHandler _handler;

    /// <summary>
    /// Initializes a new instance of the <see cref="ShoalHlsController"/> class.
    /// </summary>
    /// <param name="caller">The request's authorization.</param>
    /// <param name="handler">The proxy.</param>
    public ShoalHlsController(CallerContext caller, HlsProxyHandler handler)
    {
        _caller = caller;
        _handler = handler;
    }

    /// <summary>
    /// The master playlist.
    /// </summary>
    /// <param name="grantId">The grant id.</param>
    /// <param name="cancellationToken">The request's abort token.</param>
    /// <returns>The rebuilt playlist.</returns>
    [HttpGet("master.m3u8")]
    [HttpHead("master.m3u8")]
    public Task<IActionResult> Master(string grantId, CancellationToken cancellationToken) =>
        _handler.HandleAsync(_caller, HttpContext, new HlsRequest(HlsRoute.Master, grantId, Head: HttpMethods.IsHead(Request.Method)), cancellationToken);

    /// <summary>
    /// The media playlist.
    /// </summary>
    /// <param name="grantId">The grant id.</param>
    /// <param name="cancellationToken">The request's abort token.</param>
    /// <returns>The rebuilt playlist.</returns>
    [HttpGet("main.m3u8")]
    [HttpHead("main.m3u8")]
    public Task<IActionResult> Main(string grantId, CancellationToken cancellationToken) =>
        _handler.HandleAsync(_caller, HttpContext, new HlsRequest(HlsRoute.Main, grantId, Head: HttpMethods.IsHead(Request.Method)), cancellationToken);

    /// <summary>
    /// A segment (or the initialization segment, number -1).
    /// </summary>
    /// <param name="grantId">The grant id.</param>
    /// <param name="segmentId">The segment number, -1 to 999999.</param>
    /// <param name="ext">The extension, ts or mp4.</param>
    /// <param name="cancellationToken">The request's abort token.</param>
    /// <returns>The segment.</returns>
    [HttpGet("hls1/main/{segmentId:int:range(-1,999999)}.{ext:regex(^(ts|mp4)$)}")]
    [HttpHead("hls1/main/{segmentId:int:range(-1,999999)}.{ext:regex(^(ts|mp4)$)}")]
    public Task<IActionResult> Segment(string grantId, int segmentId, string ext, CancellationToken cancellationToken) =>
        _handler.HandleAsync(_caller, HttpContext, new HlsRequest(HlsRoute.Segment, grantId, segmentId, ext, Head: HttpMethods.IsHead(Request.Method)), cancellationToken);

    /// <summary>
    /// The playlist of one subtitle stream.
    /// </summary>
    /// <param name="grantId">The grant id.</param>
    /// <param name="index">The stream index, 0 to 127.</param>
    /// <param name="cancellationToken">The request's abort token.</param>
    /// <returns>The rebuilt playlist.</returns>
    [HttpGet("subs/{index:int:range(0,127)}/subtitles.m3u8")]
    [HttpHead("subs/{index:int:range(0,127)}/subtitles.m3u8")]
    public Task<IActionResult> SubtitlePlaylist(string grantId, int index, CancellationToken cancellationToken) =>
        _handler.HandleAsync(_caller, HttpContext, new HlsRequest(HlsRoute.SubtitlePlaylist, grantId, SubtitleIndex: index, Head: HttpMethods.IsHead(Request.Method)), cancellationToken);

    /// <summary>
    /// One WebVTT segment of a subtitle stream.
    /// </summary>
    /// <param name="grantId">The grant id.</param>
    /// <param name="index">The stream index, 0 to 127.</param>
    /// <param name="cancellationToken">The request's abort token.</param>
    /// <returns>The rebuilt WebVTT.</returns>
    [HttpGet("subs/{index:int:range(0,127)}/stream.vtt")]
    [HttpHead("subs/{index:int:range(0,127)}/stream.vtt")]
    public Task<IActionResult> SubtitleSegment(string grantId, int index, CancellationToken cancellationToken) =>
        _handler.HandleAsync(_caller, HttpContext, new HlsRequest(HlsRoute.SubtitleSegment, grantId, SubtitleIndex: index, Head: HttpMethods.IsHead(Request.Method)), cancellationToken);
}
