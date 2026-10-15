using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.Shoal.Text;

namespace Jellyfin.Plugin.Shoal.Security;

/// <summary>
/// Helpers for log lines and error text (DESIGN 2 #35, 8.3 T4): peers appear by display name, ids as 8 hex characters of a hash,
/// hosts without path or query, exceptions by type. Tokens, invites, Quick Connect secrets and URLs with queries never appear.
/// <see cref="Scrub"/> is the safety net for text whose origin is not under our control (an exception message from a library). It
/// is not a reason to log secrets: the first rule is to not pass them in, and peer-supplied text is never logged at all (log fixed
/// sentences, a status code and <see cref="Error"/>).
/// </summary>
public static class Redact
{
    /// <summary>The text that replaces a removed secret.</summary>
    public const string Mask = "[redacted]";

    /// <summary>The text <see cref="Scrub"/> returns when it could not finish within its time limit (it never returns unscrubbed text).</summary>
    public const string Omitted = "[text omitted]";

    /// <summary>The longest text <see cref="Scrub"/> looks at; the rest is cut off.</summary>
    public const int MaxScrubChars = 4096;

    private const int MaxNameChars = 64;
    private const RegexOptions Options = RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.NonBacktracking;

    // All patterns run on the non-backtracking engine: linear time in the input length whatever the input is. Because that engine has no
    // look-around, "not preceded by a letter or digit" is written as a captured prefix that is put back.
    private const string KeyWords = "token|secret|api[_-]?key|passw(?:or)?d|pwd|invite|nonce|commitment|credential";
    private const string Separator = @"\s*(?:%3[da]|[=:])\s*";
    private const string ClosingQuote = @"(?:\\*""|%22|')?";

    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(100);

    // Everything after '?' of something that looks like a URL (the Quick Connect poll URL carries the secret in its query).
    private static readonly Regex QueryString = new(@"(://[^\s?""'<>]*\?)[^\s""'<>]*", Options, MatchTimeout);

    // //user:password@host
    private static readonly Regex UserInfo = new(@"(://)[^/@\s""'<>]+@", Options, MatchTimeout);

    // Token="abc", "AccessToken":"abc", {\"Token\":\"abc\"}, api_key=abc, refresh_token%3Dabc, X-Emby-Token: abc, homeNonce=abc ...
    // First the quoted values (they may contain spaces), then the unquoted ones.
    private static readonly Regex QuotedValue = new(
        @"(^|[^0-9a-z_-])([a-z0-9_-]*(?:" + KeyWords + @")[a-z0-9_-]*" + ClosingQuote + Separator + @"(?:\\*""|%22|'))[^""'\\]+",
        Options,
        MatchTimeout);

    private static readonly Regex PlainValue = new(
        @"(^|[^0-9a-z_-])([a-z0-9_-]*(?:" + KeyWords + @")[a-z0-9_-]*" + ClosingQuote + Separator + @")[^\s""'\\&,;}]+",
        Options,
        MatchTimeout);

    // Authorization: Bearer abc, Basic abc
    private static readonly Regex AuthScheme = new(@"(^|[^0-9a-z_-])((?:bearer|basic)\s+)[^\s""',;]+", Options, MatchTimeout);

    // The Quick Connect code: "Code":"123456", code=123456
    private static readonly Regex QuickConnectCode = new(
        @"(^|[^0-9a-z_-])(code" + ClosingQuote + Separator + @"(?:\\*""|%22|')?)[0-9]{6}\b",
        Options,
        MatchTimeout);

    // An invite with or without its "shoal1-<8 hex>-" prefix: 26 Crockford base32 characters in groups of four, hyphen or space separated.
    private static readonly Regex Invite = new(
        @"(?:shoal1-[0-9a-f]{8}-(?:[0-9a-hjkmnp-tv-z]{4}[- ]?){6}[0-9a-hjkmnp-tv-z]{2})|(?:\b(?:[0-9a-hjkmnp-tv-z]{4}-){6}[0-9a-hjkmnp-tv-z]{2}\b)",
        Options,
        MatchTimeout);

    // A GUID in its dashed form (item, user and server ids are bearer-equivalent for peer media).
    private static readonly Regex DashedGuid = new(@"[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}", Options, MatchTimeout);

    // Any run of 32 or more hex digits, whatever surrounds it: an access token, a Quick Connect secret, a GUID in its short form.
    private static readonly Regex LongHex = new(@"[0-9a-f]{32,}", Options, MatchTimeout);

    /// <summary>
    /// A peer by its display name (cleaned of control and direction characters, at most 64 characters).
    /// </summary>
    /// <param name="displayName">The display name.</param>
    /// <returns>Text that is safe to log, or <c>(unnamed peer)</c>.</returns>
    public static string Peer(string? displayName)
    {
        var clean = TextClean.Display(displayName, MaxNameChars);
        return clean.Length == 0 ? "(unnamed peer)" : clean;
    }

    /// <summary>
    /// An id (item, user, library, peer key, play session) as the first 8 hex characters of its SHA-256. Enough to correlate two log
    /// lines, not enough to use the id anywhere.
    /// </summary>
    /// <param name="id">The id in any text form, or null.</param>
    /// <returns>8 lower-case hex characters, or <c>(none)</c>.</returns>
    public static string Id(string? id)
    {
        if (string.IsNullOrEmpty(id))
        {
            return "(none)";
        }

        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(id), hash);
        return Convert.ToHexStringLower(hash[..4]);
    }

    /// <summary>
    /// An id (see <see cref="Id(string)"/>), taken in its 32-hex "N" form so the hash does not depend on how the id was written.
    /// </summary>
    /// <param name="id">The id.</param>
    /// <returns>8 lower-case hex characters.</returns>
    public static string Id(Guid id) => Id(id.ToString("N"));

    /// <summary>
    /// The host (and port) of a URL, and nothing else: no scheme, user info, path, query or fragment. A string that is not an
    /// absolute URL gives <c>(invalid address)</c>.
    /// </summary>
    /// <param name="url">The URL.</param>
    /// <returns>For example <c>hops.tail1234.ts.net:8096</c>.</returns>
    public static string Host(string? url)
    {
        if (string.IsNullOrEmpty(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host))
        {
            return "(invalid address)";
        }

        return uri.IsDefaultPort ? uri.Host : uri.Host + ":" + uri.Port.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// An exception as its type name only. Exception messages can carry URLs, headers or values, so they are never logged from
    /// outside code that knows its own messages are clean.
    /// </summary>
    /// <param name="exception">The exception.</param>
    /// <returns>The type name.</returns>
    public static string Error(Exception? exception) => exception is null ? "(none)" : exception.GetType().Name;

    /// <summary>
    /// Removes secret-shaped values from <paramref name="text"/>: values of keys such as <c>Token</c>, <c>ApiKey</c>, <c>secret</c>,
    /// <c>password</c>, <c>invite</c> and <c>nonce</c> (also in prefixed forms such as <c>refresh_token</c>, in JSON, in escaped JSON
    /// and with percent-encoded separators), <c>Bearer</c> and <c>Basic</c> values, whole query strings of URLs, user info in URLs,
    /// Quick Connect codes, invites (with or without their prefix), GUIDs and every run of 32 or more hex digits.
    /// <para>
    /// The text is cut to <see cref="MaxScrubChars"/> characters first and every pattern runs on the non-backtracking engine under a
    /// time limit, so hostile input cannot make this slow. If the limit is ever hit, <see cref="Omitted"/> is returned, never the
    /// unscrubbed text.
    /// </para>
    /// </summary>
    /// <param name="text">Text of unknown origin, or null.</param>
    /// <returns>The text with those values replaced by <see cref="Mask"/>.</returns>
    public static string Scrub(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var result = text.Length > MaxScrubChars ? text[..MaxScrubChars] + "..." : text;
        try
        {
            result = UserInfo.Replace(result, "$1" + Mask + "@");
            result = QueryString.Replace(result, "$1" + Mask);
            result = QuotedValue.Replace(result, "$1$2" + Mask);
            result = PlainValue.Replace(result, "$1$2" + Mask);
            result = AuthScheme.Replace(result, "$1$2" + Mask);
            result = QuickConnectCode.Replace(result, "$1$2" + Mask);
            result = Invite.Replace(result, Mask);
            result = DashedGuid.Replace(result, Mask);
            result = LongHex.Replace(result, Mask);
            return result;
        }
        catch (RegexMatchTimeoutException)
        {
            return Omitted;
        }
    }
}
