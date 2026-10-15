using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.AspNetCore.Http;

namespace Jellyfin.Plugin.Shoal.Http;

/// <summary>
/// The only place Shoal looks at the raw query string of a client request (DESIGN 6.1, 8.8, W4-12, W4-14). Clients put their token in the query
/// (<c>ApiKey</c>), so the query string, the path and the full URL are never logged and never read directly; the analyzer bans
/// <c>HttpRequest.QueryString</c>, <c>Path</c>, <c>PathBase</c> and the <c>GetDisplayUrl</c> family everywhere else. Everything else reads
/// <c>Request.Query</c>, the same collection MVC's model binder sees (core re-parses a fully percent-encoded query into it).
/// </summary>
public static class ShoalRequest
{
    /// <summary>The longest query string that is accepted on a federated route.</summary>
    public const int MaxQueryChars = 8 * 1024;

    /// <summary>
    /// Whether the query string is longer than <see cref="MaxQueryChars"/>.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <returns>True when it is too long (answer 414).</returns>
    public static bool QueryTooLong(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
#pragma warning disable RS0030 // The one allowed reader of the raw query string: only its length is looked at.
        return request.QueryString.HasValue && request.QueryString.Value!.Length > MaxQueryChars;
#pragma warning restore RS0030
    }

    /// <summary>
    /// Reads every value of a query key, as MVC would see them: the key is matched without regard to case.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <param name="key">The key.</param>
    /// <returns>All values, in order (empty when the key is absent).</returns>
    public static IReadOnlyList<string> Values(HttpRequest request, string key)
    {
        ArgumentNullException.ThrowIfNull(request);
        return request.Query.TryGetValue(key, out var values) ? [.. values.Where(static v => v is not null).Select(static v => v!)] : [];
    }

    /// <summary>
    /// Parses a GUID the way the model binder does: <c>Guid.TryParse</c>, which accepts the N, D, B, P and X forms in any case with
    /// surrounding whitespace. Never <c>ParseExact</c>, which would let a form the binder accepts slip through to core.
    /// </summary>
    /// <param name="value">The text.</param>
    /// <param name="id">The id.</param>
    /// <returns>True when it parses.</returns>
    public static bool TryParseGuid(string? value, out Guid id) => Guid.TryParse(value, out id);
}
