using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller;

namespace Jellyfin.Plugin.Shoal.Api.Admin;

/// <summary>What a call to Moonbase's admin messages route gave.</summary>
public enum MoonbaseOutcome
{
    /// <summary>It worked.</summary>
    Ok,

    /// <summary>Moonbase is not installed (the route answers 404).</summary>
    NotInstalled,

    /// <summary>The admin's session was refused (401 or 403).</summary>
    Refused,

    /// <summary>Anything else (unreachable, a bad answer).</summary>
    Failed,
}

/// <summary>
/// Moonbase's admin messages route (<c>/Moonfin/Admin/Messages</c>, Pilot 13 S1-S4): list, and post (a post with an existing id replaces it in place).
/// </summary>
public interface IMoonbaseClient
{
    /// <summary>
    /// Lists the messages.
    /// </summary>
    /// <param name="callerToken">The admin's own token from the request that asked (never stored, never logged).</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The outcome and the messages.</returns>
    Task<(MoonbaseOutcome Outcome, IReadOnlyList<MoonbaseMessage> Items)> ListAsync(string callerToken, CancellationToken cancellationToken);

    /// <summary>
    /// Posts a message.
    /// </summary>
    /// <param name="callerToken">The admin's own token from the request that asked.</param>
    /// <param name="message">The message.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The outcome and the id Moonbase gave the message.</returns>
    Task<(MoonbaseOutcome Outcome, Guid? Id)> PostAsync(string callerToken, MoonbasePost message, CancellationToken cancellationToken);
}

/// <summary>One inbox message as Moonbase lists it (only what Shoal reads).</summary>
/// <param name="Id">Its id.</param>
/// <param name="Title">Its title.</param>
/// <param name="TargetUserIds">The accounts a "users" message reaches.</param>
public sealed record MoonbaseMessage(Guid Id, string Title, IReadOnlyList<Guid> TargetUserIds);

/// <summary>A message to post.</summary>
/// <param name="Id">The id of the message it replaces, or null for a new one.</param>
/// <param name="Title">The title.</param>
/// <param name="Body">The body (markdown; Moonfin renders it).</param>
/// <param name="TargetUserIds">The accounts it reaches (audience "users" reaches only these).</param>
public sealed record MoonbasePost(Guid? Id, string Title, string Body, IReadOnlyList<Guid> TargetUserIds);

/// <summary>
/// The <see cref="IMoonbaseClient"/> of the plugin: a loopback call to this server with the admin's own session (no API key is made, stored or printed).
/// </summary>
public sealed class LoopbackMoonbaseClient : IMoonbaseClient, IDisposable
{
    private const long MaxBytes = 1024 * 1024;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true, MaxDepth = 16, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, Converters = { new AnyGuidConverter() } };

    private readonly IServerApplicationHost _host;
    private readonly HttpClient _http = new(new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false, UseCookies = false }) { Timeout = TimeSpan.FromSeconds(15), MaxResponseContentBufferSize = 4 * 1024 * 1024 };

    /// <summary>
    /// Initializes a new instance of the <see cref="LoopbackMoonbaseClient"/> class.
    /// </summary>
    /// <param name="host">The server host (its loopback address).</param>
    public LoopbackMoonbaseClient(IServerApplicationHost host) => _host = host;

    /// <inheritdoc />
    public async Task<(MoonbaseOutcome Outcome, IReadOnlyList<MoonbaseMessage> Items)> ListAsync(string callerToken, CancellationToken cancellationToken)
    {
        using var request = Request(HttpMethod.Get, callerToken, null);
        var (outcome, body) = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (outcome != MoonbaseOutcome.Ok)
        {
            return (outcome, []);
        }

        return ParseList(body);
    }

    /// <inheritdoc />
    public async Task<(MoonbaseOutcome Outcome, Guid? Id)> PostAsync(string callerToken, MoonbasePost message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        var payload = PostPayload(message);
        using var request = Request(HttpMethod.Post, callerToken, payload);
        var (outcome, body) = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (outcome != MoonbaseOutcome.Ok)
        {
            return (outcome, null);
        }

        return (MoonbaseOutcome.Ok, ParsePostId(body, message.Id));
    }

    /// <inheritdoc />
    public void Dispose() => _http.Dispose();

    /// <summary>
    /// The body of a post. Pilot 15: Moonbase 2.4.0 keeps message ids as strings, writes new ones as 32 hex digits without hyphens and replaces in place only on an
    /// ordinal (case-insensitive) string match, so a replace must send the id in that same form; the hyphenated form never matched and made a second message.
    /// </summary>
    /// <param name="message">The message.</param>
    /// <returns>The JSON body.</returns>
    internal static byte[] PostPayload(MoonbasePost message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var id = message.Id is { } existing && existing != Guid.Empty ? existing.ToString("N") : null;
        return JsonSerializer.SerializeToUtf8Bytes(new PostBody(id, message.Title, message.Body, "users", [.. message.TargetUserIds.Select(static t => t.ToString("N"))]), Json);
    }

    /// <summary>
    /// Reads Moonbase's list answer. Pilot 14: Moonbase 2.4.0 writes ids as 32 hex digits without hyphens; both forms are read, and an item with an id that is
    /// neither is skipped instead of failing the list.
    /// </summary>
    /// <param name="body">The answer.</param>
    /// <returns>The outcome and the messages.</returns>
    internal static (MoonbaseOutcome Outcome, IReadOnlyList<MoonbaseMessage> Items) ParseList(byte[] body)
    {
        try
        {
            var list = JsonSerializer.Deserialize<ListBody>(body, Json);
            return (MoonbaseOutcome.Ok, [.. (list?.Items ?? []).Where(static i => i is not null && i.Id != Guid.Empty).Take(1000).Select(static i => new MoonbaseMessage(i.Id, i.Title ?? string.Empty, [.. (i.TargetUserIds ?? []).Where(static t => t != Guid.Empty)]))]);
        }
        catch (JsonException)
        {
            return (MoonbaseOutcome.Failed, []);
        }
    }

    /// <summary>
    /// Reads the id of Moonbase's post answer (<c>{ success, item { Id } }</c>), in either Guid form.
    /// </summary>
    /// <param name="body">The answer.</param>
    /// <param name="sent">The id that was sent (a replace), or null.</param>
    /// <returns>The message id.</returns>
    internal static Guid? ParsePostId(byte[] body, Guid? sent)
    {
        try
        {
            var answer = JsonSerializer.Deserialize<PostAnswer>(body, Json);
            return answer?.Item?.Id is { } id && id != Guid.Empty ? id : sent;
        }
        catch (JsonException)
        {
            return sent;
        }
    }

    private HttpRequestMessage Request(HttpMethod method, string token, byte[]? payload)
    {
        var baseUrl = _host.GetLocalApiUrl("127.0.0.1", "http", null).TrimEnd('/');
        var request = new HttpRequestMessage(method, baseUrl + "/Moonfin/Admin/Messages");
        request.Headers.TryAddWithoutValidation("Authorization", "MediaBrowser Token=\"" + token + "\"");
        if (payload is not null)
        {
            request.Content = new ByteArrayContent(payload);
            request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        }

        return request;
    }

    private async Task<(MoonbaseOutcome Outcome, byte[] Body)> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            switch (response.StatusCode)
            {
                case HttpStatusCode.NotFound:
                    return (MoonbaseOutcome.NotInstalled, []);
                case HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden:
                    return (MoonbaseOutcome.Refused, []);
                case not HttpStatusCode.OK:
                    return (MoonbaseOutcome.Failed, []);
            }

            if (response.Content.Headers.ContentLength > MaxBytes)
            {
                return (MoonbaseOutcome.Failed, []);
            }

#pragma warning disable RS0030 // This server's own plugin on loopback, not a peer: the body is read here under a 1 MB bound.
            var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
#pragma warning restore RS0030
            await using var streamScope = stream.ConfigureAwait(false);
            using var buffer = new System.IO.MemoryStream();
            var chunk = new byte[16 * 1024];
            int read;
            while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > MaxBytes)
                {
                    return (MoonbaseOutcome.Failed, []);
                }

                await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }

            return (MoonbaseOutcome.Ok, buffer.ToArray());
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException or System.IO.IOException)
        {
            return (MoonbaseOutcome.Failed, []);
        }
    }

    private sealed class ListBody
    {
        public List<ListItem>? Items { get; set; }
    }

    private sealed class ListItem
    {
        public Guid Id { get; set; }

        public string? Title { get; set; }

        public List<Guid>? TargetUserIds { get; set; }
    }

    private sealed class PostAnswer
    {
        public ListItem? Item { get; set; }
    }

    private sealed record PostBody(string? Id, string Title, string Body, string Audience, List<string> TargetUserIds);
}

/// <summary>
/// Reads a Guid written in any form Guid.TryParse accepts (Moonbase 2.4.0 writes 32 hex digits without hyphens); an unreadable value reads as the empty Guid. Writes
/// the usual hyphenated form.
/// </summary>
internal sealed class AnyGuidConverter : JsonConverter<Guid>
{
    /// <inheritdoc />
    public override Guid Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.String && Guid.TryParse(reader.GetString(), out var id) ? id : Guid.Empty;

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, Guid value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStringValue(value.ToString("D"));
    }
}
