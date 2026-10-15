using System;
using System.Net;

namespace Jellyfin.Plugin.Shoal.Peers;

/// <summary>
/// Why a call to a peer failed (DESIGN 6.10). Callers map these to their own outcome; none of them carries peer text.
/// </summary>
public enum PeerFailureKind
{
    /// <summary>The circuit breaker is open: nothing was sent (DESIGN 6.10).</summary>
    CircuitOpen,

    /// <summary>Connecting, TLS or the transport failed (refused, reset, DNS, bad certificate, oversized headers).</summary>
    Unreachable,

    /// <summary>A deadline passed: connect, headers, an idle read or the total.</summary>
    Timeout,

    /// <summary>The body is larger than the cap (declared or counted).</summary>
    TooLarge,

    /// <summary>The answer is not acceptable: wrong media type, invalid UTF-8 or JSON, too deep, shorter than declared, no declared length where one is required.</summary>
    BadResponse,

    /// <summary>The peer answered with a redirect. It is never followed (DESIGN 6.10).</summary>
    Redirect,

    /// <summary>The address is not acceptable for the peer's network type (DESIGN 8.3 T11); nothing was sent.</summary>
    AddressRefused,

    /// <summary>The identity or token check is older than the trust window (DESIGN 4.4); nothing was sent.</summary>
    TrustLapsed,

    /// <summary>The peer is stopped (identity mismatch, TLS key changed, token refused); nothing was sent until the admin acts.</summary>
    Blocked
}

/// <summary>
/// A call to a peer failed. The message is a fixed text built from <see cref="Kind"/> and <see cref="StatusCode"/> only: it never
/// contains the URL, a header, a token or peer-supplied text, and no inner exception is chained (their messages carry URLs), so
/// it is safe to log with <c>ex.Message</c>.
/// </summary>
public sealed class PeerCallException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PeerCallException"/> class.
    /// </summary>
    public PeerCallException()
        : this(PeerFailureKind.Unreachable)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="PeerCallException"/> class.
    /// </summary>
    /// <param name="message">The message.</param>
    public PeerCallException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="PeerCallException"/> class.
    /// </summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The inner exception (not used by Shoal itself; it would carry peer URLs).</param>
    public PeerCallException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="PeerCallException"/> class.
    /// </summary>
    /// <param name="kind">What went wrong.</param>
    /// <param name="statusCode">The HTTP status, when the peer answered.</param>
    public PeerCallException(PeerFailureKind kind, HttpStatusCode? statusCode = null)
        : base(MessageFor(kind, statusCode))
    {
        Kind = kind;
        StatusCode = statusCode;
    }

    /// <summary>Gets what went wrong.</summary>
    public PeerFailureKind Kind { get; }

    /// <summary>Gets the HTTP status when the peer answered (for example a redirect), otherwise null.</summary>
    public HttpStatusCode? StatusCode { get; }

    private static string MessageFor(PeerFailureKind kind, HttpStatusCode? status)
    {
        var text = kind switch
        {
            PeerFailureKind.CircuitOpen => "The peer is unavailable (circuit open); nothing was sent.",
            PeerFailureKind.Unreachable => "The peer could not be reached.",
            PeerFailureKind.Timeout => "The peer did not answer in time.",
            PeerFailureKind.TooLarge => "The peer's answer is larger than allowed.",
            PeerFailureKind.BadResponse => "The peer's answer is not acceptable.",
            PeerFailureKind.Redirect => "The peer answered with a redirect, which is never followed.",
            PeerFailureKind.AddressRefused => "The peer address is not acceptable; nothing was sent.",
            PeerFailureKind.TrustLapsed => "The peer has not been verified recently; nothing was sent.",
            PeerFailureKind.Blocked => "The peer is stopped until the admin acts; nothing was sent.",
            _ => "The peer call failed."
        };
        return status is null ? text : text + " (HTTP " + ((int)status.Value).ToString(System.Globalization.CultureInfo.InvariantCulture) + ")";
    }
}
