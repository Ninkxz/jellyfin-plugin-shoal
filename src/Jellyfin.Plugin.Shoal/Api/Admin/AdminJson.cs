using System;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.Http;
using Microsoft.AspNetCore.Http;

namespace Jellyfin.Plugin.Shoal.Api.Admin;

/// <summary>
/// The JSON the admin page speaks (docs/ui-spec.md 13): camelCase, enums as their names, nothing for null. Serialized here, not by
/// the MVC output formatter, so the shapes do not change with the server's JSON settings.
/// </summary>
internal static class AdminJson
{
    /// <summary>Gets the options used for every admin response and request body.</summary>
    public static JsonSerializerOptions Options { get; } = Create();

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNameCaseInsensitive = true,
            WriteIndented = false
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}

/// <summary>
/// What the admin controllers answer with. Every answer is a <see cref="ShoalResult"/>, so it carries the headers and the type of
/// DESIGN 8.6 (nothing here writes a header or a content type itself).
/// </summary>
internal static class AdminReply
{
    /// <summary>A JSON answer.</summary>
    /// <param name="status">The status code.</param>
    /// <param name="body">The body.</param>
    /// <returns>The result.</returns>
    public static ShoalResult Json(int status, object body) => new(status, body);

    /// <summary>An error is a status and one fixed word. Never a message, never anything a peer sent (docs/ui-spec.md 11).</summary>
    /// <param name="status">The status code.</param>
    /// <param name="kind">The fixed word.</param>
    /// <returns>The result.</returns>
    public static ShoalResult Error(int status, string kind) => new(status, new { error = kind });

    /// <summary>A 429 with <c>Retry-After</c>.</summary>
    /// <param name="retryAfter">How long to wait.</param>
    /// <returns>The result.</returns>
    public static ShoalResult TooMany(TimeSpan retryAfter) =>
        new(StatusCodes.Status429TooManyRequests, new { error = "rateLimited" }, Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds)));

    /// <summary>
    /// Reads a JSON request body. A missing body, a body that is not JSON and a body of the wrong content type all give null, which the
    /// caller answers with 400 (never a 500).
    /// </summary>
    /// <typeparam name="T">The body type.</typeparam>
    /// <param name="request">The request.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The body, or null.</returns>
    public static async Task<T?> ReadAsync<T>(HttpRequest request, CancellationToken cancellationToken)
        where T : class
    {
        try
        {
            return await request.ReadFromJsonAsync<T>(AdminJson.Options, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }
}
