using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.Http;
using Jellyfin.Plugin.Shoal.Sharing;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Shoal.Api;

/// <summary>The body of <c>POST /Shoal/Pair</c> (DESIGN 4.1b).</summary>
public sealed class PairBody
{
    /// <summary>Gets or sets the protocol version.</summary>
    [JsonPropertyName("v")]
    public int V { get; set; }

    /// <summary>Gets or sets the invite secret (26 Crockford characters).</summary>
    [JsonPropertyName("invite")]
    public string? Invite { get; set; }

    /// <summary>Gets or sets the commitment to Home's nonce.</summary>
    [JsonPropertyName("homeNonceCommit")]
    public string? HomeNonceCommit { get; set; }

    /// <summary>Gets or sets Home's nonce (absent on the first request).</summary>
    [JsonPropertyName("homeNonce")]
    public string? HomeNonce { get; set; }

    /// <summary>Gets or sets Home's server id.</summary>
    [JsonPropertyName("homeServerId")]
    public string? HomeServerId { get; set; }

    /// <summary>Gets or sets Home's name.</summary>
    [JsonPropertyName("homeName")]
    public string? HomeName { get; set; }

    /// <summary>Gets or sets the device id Home will use.</summary>
    [JsonPropertyName("homeDeviceId")]
    public string? HomeDeviceId { get; set; }
}

/// <summary>
/// The one anonymous endpoint of Shoal (DESIGN 4.1b "POST /Shoal/Pair"): Home redeems an invite here and polls for the answer. Every failure is
/// the same <c>404 {}</c>, with the same work done for every request, so nothing says whether an invite exists. A valid secret is never rate
/// limited; wrong ones are, per address and overall. The reflection test whitelists exactly this action.
/// </summary>
[ApiController]
[Authorize]
[Route("Shoal")]
public partial class PairController : ControllerBase
{
    private const int MaxBody = 1024;

    private static readonly JsonSerializerOptions BodyOptions = new() { MaxDepth = 4 };

    private readonly InviteBook _book;

    /// <summary>
    /// Initializes a new instance of the <see cref="PairController"/> class.
    /// </summary>
    /// <param name="book">The invites.</param>
    public PairController(InviteBook book) => _book = book;

    /// <summary>
    /// Redeems an invite or polls for the approval. Anonymous by design: the invite is the credential.
    /// </summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns><c>200 {v, state, peerServerId, peerNonce, token?}</c>, <c>429</c> for a failure when the caller used up its bucket, or the one <c>404 {}</c>.</returns>
    [HttpPost("Pair")]
    [AllowAnonymous]
    public async Task<IActionResult> Pair(CancellationToken cancellationToken)
    {
        var ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";

        // Always read the body (at most 1 KB), so the cost does not depend on whether the invite is real.
        var raw = new byte[MaxBody + 1];
        var read = 0;
        while (read < raw.Length)
        {
            var n = await Request.Body.ReadAsync(raw.AsMemory(read), cancellationToken).ConfigureAwait(false);
            if (n == 0)
            {
                break;
            }

            read += n;
        }

        PairAnswer answer = PairAnswer.Failure;
        if (read <= MaxBody && Parse(raw.AsSpan(0, read)) is { } request)
        {
            answer = _book.Redeem(request with { RemoteAddress = ip });
        }
        else
        {
            _book.Redeem(new PairRequestData("0", string.Empty, null, string.Empty, string.Empty, string.Empty, ip));
        }

        if (answer.Failed)
        {
            return _book.Throttled(ip)
                ? new ShoalResult(StatusCodes.Status429TooManyRequests, new { }, 1)
                : new ShoalResult(StatusCodes.Status404NotFound, new { });
        }

        return new ShoalResult(StatusCodes.Status200OK, new Reply(1, answer.State, answer.PeerServerId, answer.PeerNonce, answer.Token, answer.ExpiresInSeconds));
    }

    /// <summary>Reads and checks the request body strictly.</summary>
    /// <param name="json">The body.</param>
    /// <returns>The request, or null for anything that is not exactly a pairing request.</returns>
    internal static PairRequestData? Parse(ReadOnlySpan<byte> json)
    {
        PairBody? body;
        try
        {
            body = JsonSerializer.Deserialize<PairBody>(json, BodyOptions);
        }
        catch (JsonException)
        {
            return null;
        }

        if (body is not { V: 1 } || body.Invite is null || body.HomeNonceCommit is null || body.HomeServerId is null || body.HomeName is null || body.HomeDeviceId is null)
        {
            return null;
        }

        var secret = InviteCrypto.NormalizeCode(body.Invite);
        if (secret.Length != InviteCrypto.SecretLength
            || !HexId().IsMatch(body.HomeServerId)
            || !DeviceId().IsMatch(body.HomeDeviceId)
            || body.HomeName.Length is 0 or > 64
            || HasControl(body.HomeName)
            || !string.Equals(body.HomeDeviceId[6..18], body.HomeServerId[..12], StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        // The name is shown to the Peer admin: cleaned like every other text from outside.
        var name = Jellyfin.Plugin.Shoal.Text.TextClean.Display(body.HomeName, 64);
        return name.Length == 0 ? null : new PairRequestData(secret, body.HomeNonceCommit, body.HomeNonce, body.HomeServerId, name, body.HomeDeviceId, string.Empty);
    }

    private static bool HasControl(string text)
    {
        foreach (var c in text)
        {
            if (char.IsControl(c))
            {
                return true;
            }
        }

        return false;
    }

    [GeneratedRegex(@"\A[0-9a-fA-F]{32}\z")]
    private static partial Regex HexId();

    [GeneratedRegex(@"\Ashoal-[0-9a-f]{12}-[0-9a-f]{8}\z")]
    private static partial Regex DeviceId();

    private sealed record Reply(int V, string State, string? PeerServerId, string? PeerNonce, string? Token, int? ExpiresInSeconds);
}
