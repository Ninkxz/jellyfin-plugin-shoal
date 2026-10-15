using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Jellyfin.Plugin.Shoal.Tests;

/// <summary>No libraries and no accounts, for tests that do not look at them.</summary>
internal sealed class NoLibraryView : Jellyfin.Plugin.Shoal.Api.Admin.ILibraryView
{
    public object Build(Guid? currentUserId) => new { };

    public (System.Collections.Generic.IReadOnlyList<object> Libraries, System.Collections.Generic.IReadOnlyList<object> Users) ForPeer(string peerKey, Guid? currentUserId) => ([], []);
}

/// <summary>
/// Runs an admin action's result through the real <see cref="ShoalResult"/> pipeline, so a test sees what a client sees: the status, the
/// headers of DESIGN 8.6 and the serialized body.
/// </summary>
internal static class AdminTestSupport
{
    /// <summary>What a client would receive.</summary>
    /// <param name="Status">The status code.</param>
    /// <param name="Headers">The response headers.</param>
    /// <param name="Text">The body text (empty for no body).</param>
    internal sealed record Rendered(int Status, IHeaderDictionary Headers, string Text)
    {
        /// <summary>Gets the body as JSON.</summary>
        public JsonElement Json => JsonDocument.Parse(Text).RootElement;
    }

    /// <summary>Executes the result against a fresh response.</summary>
    /// <param name="result">The action result.</param>
    /// <returns>What the client would receive.</returns>
    public static Rendered Render(IActionResult result)
    {
        var shoal = Assert.IsType<ShoalResult>(result);
        var context = new DefaultHttpContext();
        using var body = new MemoryStream();
        context.Response.Body = body;
        shoal.ExecuteResultAsync(new ActionContext { HttpContext = context }).GetAwaiter().GetResult();
        return new Rendered(context.Response.StatusCode, context.Response.Headers, Encoding.UTF8.GetString(body.ToArray()));
    }

    /// <summary>Renders and checks the status and the headers every admin answer must have.</summary>
    /// <param name="result">The action result.</param>
    /// <param name="expectedStatus">The expected status.</param>
    /// <returns>The body as JSON (an empty object for no body).</returns>
    public static JsonElement Body(IActionResult result, int expectedStatus = 200)
    {
        var rendered = Render(result);
        Assert.Equal(expectedStatus, rendered.Status);
        Assert.Equal("nosniff", rendered.Headers["X-Content-Type-Options"].ToString());
        Assert.Equal("no-referrer", rendered.Headers["Referrer-Policy"].ToString());
        Assert.Equal("no-store", rendered.Headers["Cache-Control"].ToString());
        if (rendered.Text.Length == 0)
        {
            return JsonDocument.Parse("{}").RootElement;
        }

        Assert.StartsWith("application/json", rendered.Headers["Content-Type"].ToString(), StringComparison.Ordinal);
        return rendered.Json;
    }

    /// <summary>The text of a rendered result (for the scans that look for secrets).</summary>
    /// <param name="result">The action result.</param>
    /// <returns>The body text.</returns>
    public static string Text(IActionResult result) => Render(result).Text;
}
