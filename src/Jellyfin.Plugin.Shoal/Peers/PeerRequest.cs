using System;
using System.Net.Http;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.Shoal.Peers;

/// <summary>
/// One request to a peer (DESIGN 6.11). It can only carry what the table in 6.11 allows: a method, a path and query built by Shoal
/// from validated values, an optional JSON body, an optional single byte range and the choice between the JSON and the media
/// <c>Accept</c> header. Nothing from a client request has a way in.
/// </summary>
public sealed partial class PeerRequest
{
    private const int MaxPathAndQueryChars = 2048;

    private readonly byte[]? _body;

    private PeerRequest(HttpMethod method, string pathAndQuery, bool authenticated, bool acceptJson, byte[]? jsonBody, string? range, bool isProbe, TrustRequirement? trust = null, bool usesBreaker = true)
    {
        Method = method;
        PathAndQuery = ValidatePath(pathAndQuery);
        Authenticated = authenticated;
        AcceptJson = acceptJson;
        _body = jsonBody;
        JsonBody = jsonBody is null ? default(ReadOnlyMemory<byte>?) : new ReadOnlyMemory<byte>(jsonBody);
        Range = range is null ? null : ValidateRange(range);
        IsProbe = isProbe;
        Trust = trust ?? (authenticated ? TrustRequirement.PinFresh : TrustRequirement.None);
        UsesBreaker = usesBreaker;
    }

    /// <summary>Gets the method.</summary>
    public HttpMethod Method { get; }

    /// <summary>Gets the path and query, beginning with '/', printable ASCII only.</summary>
    public string PathAndQuery { get; }

    /// <summary>Gets a value indicating whether the <c>Token</c> is sent. False only for the identity check and pairing steps before a token exists.</summary>
    public bool Authenticated { get; }

    /// <summary>Gets a value indicating whether <c>Accept</c> is <c>application/json</c> (an API call) rather than <c>*/*</c> (media).</summary>
    public bool AcceptJson { get; }

    /// <summary>Gets the JSON body of a POST, or null.</summary>
    public ReadOnlyMemory<byte>? JsonBody { get; }

    /// <summary>Gets the single byte range of a static stream request (<c>bytes=x-y</c>), or null.</summary>
    public string? Range { get; }

    /// <summary>Gets a value indicating whether this is the health check's <c>GET /System/Ping</c>, the only request allowed through an open circuit.</summary>
    public bool IsProbe { get; }

    /// <summary>
    /// Gets what the trust window must show before this request is sent (DESIGN 4.4). Token-bearing requests default to
    /// <see cref="TrustRequirement.PinFresh"/>; only the calls that refresh the window use <see cref="TrustRequirement.None"/>.
    /// </summary>
    public TrustRequirement Trust { get; }

    /// <summary>Gets a value indicating whether the circuit breaker applies to this request (true unless <see cref="WithoutBreaker"/> was used).</summary>
    public bool UsesBreaker { get; }

    /// <summary>Creates an authenticated JSON GET.</summary>
    /// <param name="pathAndQuery">The path and query, built from validated values.</param>
    /// <returns>The request.</returns>
    public static PeerRequest Get(string pathAndQuery) => new(HttpMethod.Get, pathAndQuery, authenticated: true, acceptJson: true, null, null, isProbe: false);

    /// <summary>Creates a JSON GET without a token (<c>System/Info/Public</c>, <c>QuickConnect/*</c> before the token exists).</summary>
    /// <param name="pathAndQuery">The path and query, built from validated values.</param>
    /// <returns>The request.</returns>
    public static PeerRequest GetAnonymous(string pathAndQuery) => new(HttpMethod.Get, pathAndQuery, authenticated: false, acceptJson: true, null, null, isProbe: false);

    /// <summary>Creates the health probe, <c>GET /System/Ping</c>, which may pass an open circuit when it is due.</summary>
    /// <returns>The request.</returns>
    public static PeerRequest Probe() => new(HttpMethod.Get, "/System/Ping", authenticated: false, acceptJson: false, null, null, isProbe: true);

    /// <summary>Creates a media GET (<c>Accept: */*</c>), authenticated.</summary>
    /// <param name="pathAndQuery">The path and query, built from validated values.</param>
    /// <param name="range">A single <c>bytes=x-y</c> range (static stream only), or null.</param>
    /// <returns>The request.</returns>
    public static PeerRequest GetMedia(string pathAndQuery, string? range = null) => new(HttpMethod.Get, pathAndQuery, authenticated: true, acceptJson: false, null, range, isProbe: false);

    /// <summary>Creates a media HEAD, authenticated.</summary>
    /// <param name="pathAndQuery">The path and query, built from validated values.</param>
    /// <returns>The request.</returns>
    public static PeerRequest HeadMedia(string pathAndQuery) => new(HttpMethod.Head, pathAndQuery, authenticated: true, acceptJson: false, null, null, isProbe: false);

    /// <summary>Creates a POST with a JSON body (or none).</summary>
    /// <param name="pathAndQuery">The path and query, built from validated values.</param>
    /// <param name="jsonBody">The body, already serialized by Shoal, or null.</param>
    /// <param name="authenticated">False for the pre-token steps of a pairing.</param>
    /// <returns>The request.</returns>
    public static PeerRequest Post(string pathAndQuery, byte[]? jsonBody, bool authenticated = true) => new(HttpMethod.Post, pathAndQuery, authenticated, acceptJson: true, jsonBody, null, isProbe: false);

    /// <summary>Creates an authenticated DELETE.</summary>
    /// <param name="pathAndQuery">The path and query, built from validated values.</param>
    /// <returns>The request.</returns>
    public static PeerRequest Delete(string pathAndQuery) => new(HttpMethod.Delete, pathAndQuery, authenticated: true, acceptJson: true, null, null, isProbe: false);

    /// <summary>
    /// Returns a copy of this request with another trust requirement.
    /// </summary>
    /// <param name="trust">The requirement: <see cref="TrustRequirement.None"/> for the calls that refresh the window, <see cref="TrustRequirement.PinAndAuthFresh"/> for static proxying.</param>
    /// <returns>The new request.</returns>
    public PeerRequest WithTrust(TrustRequirement trust) => new(Method, PathAndQuery, Authenticated, AcceptJson, _body, Range, IsProbe, trust, UsesBreaker);

    /// <summary>
    /// Returns a copy that neither asks the circuit breaker nor counts towards it. Pairing uses this: its polls are expected to time out now and
    /// then, and a peer that is not paired yet has no health to protect (DESIGN 4.2a counts its own failures).
    /// </summary>
    /// <returns>The new request.</returns>
    public PeerRequest WithoutBreaker() => new(Method, PathAndQuery, Authenticated, AcceptJson, _body, Range, IsProbe, Trust, usesBreaker: false);

    private static string ValidatePath(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length is 0 or > MaxPathAndQueryChars || value[0] != '/' || (value.Length > 1 && value[1] == '/'))
        {
            throw new ArgumentException("A peer path must be a single-slash rooted path of at most 2048 characters.", nameof(value));
        }

        foreach (var c in value)
        {
            // Printable ASCII without space, backslash and fragment: nothing that changes the host, ends the line or smuggles a header.
            if (c is < '!' or > '~' or '\\' or '#')
            {
                throw new ArgumentException("A peer path may contain only printable ASCII without space, backslash or '#'.", nameof(value));
            }
        }

        return value;
    }

    private static string ValidateRange(string value) =>
        SingleRange().IsMatch(value) ? value : throw new ArgumentException("Only one bytes range is allowed.", nameof(value));

    [GeneratedRegex(@"\Abytes=(?:[0-9]{1,18}-[0-9]{0,18}|-[0-9]{1,18})\z", RegexOptions.CultureInvariant)]
    private static partial Regex SingleRange();
}
