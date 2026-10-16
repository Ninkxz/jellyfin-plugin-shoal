using System.Net;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.Peers;

namespace Jellyfin.Plugin.Shoal.Pairing;

/// <summary>
/// What a ping found out.
/// </summary>
internal enum PingOutcome
{
    /// <summary>No Shoal answered (or anything else went wrong).</summary>
    No,

    /// <summary>Shoal answered.</summary>
    Yes,

    /// <summary>The other server's ping limit said "later": nothing is known about Shoal there.</summary>
    Throttled,
}

/// <summary>
/// Asks a probed server whether Shoal is installed there (<c>GET /Shoal/Ping</c>, anonymous), so the pairing page can pick a route (pairing v2, hasShoal).
/// The answer is a hint for the admin and nothing more: any failure, any other status, any other body and any other type means "no".
/// </summary>
internal static class ShoalPing
{
    /// <summary>
    /// Pings the server.
    /// </summary>
    /// <param name="client">The peer client.</param>
    /// <param name="endpoint">The probe endpoint (no token).</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns><see cref="PingOutcome.Yes"/> only for <c>200 {"shoal":true}</c>; <see cref="PingOutcome.Throttled"/> for a 429; otherwise no.</returns>
    public static async Task<PingOutcome> DetectAsync(PeerClient client, PeerEndpoint endpoint, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await client.SendAsync(endpoint, PeerRequest.GetAnonymous("/Shoal/Ping"), PeerCallProfile.Info, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                return PingOutcome.Throttled;
            }

            if (response.StatusCode != HttpStatusCode.OK)
            {
                return PingOutcome.No;
            }

            var reply = await response.ReadJsonAsync<PingWire>(cancellationToken).ConfigureAwait(false);
            return reply.Shoal is true ? PingOutcome.Yes : PingOutcome.No;
        }
        catch (PeerCallException)
        {
            return PingOutcome.No;
        }
    }
}

#pragma warning disable SA1600 // Wire member: the name is the protocol's.
internal sealed class PingWire
{
    [JsonPropertyName("shoal")]
    public bool? Shoal { get; set; }
}
