using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using Jellyfin.Plugin.Shoal.State;

namespace Jellyfin.Plugin.Shoal.Authorization;

/// <summary>
/// The playback error code a PlaybackInfo answer carries when CallerContext refuses (DESIGN 8.5 step 7).
/// </summary>
public enum PlaybackDenial
{
    /// <summary>No code: the caller sees a plain status.</summary>
    None,

    /// <summary><c>NotAllowed</c>: the caller may not play (recovery mode, <c>EnableMediaPlayback</c> off).</summary>
    NotAllowed
}

/// <summary>
/// A user, as CallerContext needs to see one. Built from Jellyfin's <c>User</c> by <see cref="ICallerUsers"/> so the rules can be tested
/// without a server.
/// </summary>
/// <param name="Id">The user id.</param>
/// <param name="IsDisabled">Whether the account is disabled.</param>
/// <param name="IsRestricted">Whether the account has a parental limit, blocks unrated items, allows only some tags or has an access schedule (DESIGN 5.6).</param>
/// <param name="CanPlay"><c>EnableMediaPlayback</c>.</param>
/// <param name="CanTranscodeVideo"><c>EnableVideoPlaybackTranscoding</c>.</param>
/// <param name="CanRemux"><c>EnablePlaybackRemuxing</c>.</param>
/// <param name="ForceRemoteSourceTranscoding"><c>ForceRemoteSourceTranscoding</c>.</param>
/// <param name="RemoteClientBitrateLimit">The user's limit for remote clients, or null.</param>
public sealed record CallerUser(
    Guid Id,
    bool IsDisabled,
    bool IsRestricted,
    bool CanPlay,
    bool CanTranscodeVideo,
    bool CanRemux,
    bool ForceRemoteSourceTranscoding,
    int? RemoteClientBitrateLimit);

/// <summary>
/// The library of a federated item (which peer, which mapping).
/// </summary>
/// <param name="PeerKey">The peer key.</param>
/// <param name="MappingKey">The mapping key of the library.</param>
/// <param name="LibKey">The library of v2.9 or later whose folder holds the item, or empty for a legacy per-friend folder (K44).</param>
/// <param name="IsCopy">Whether the item is an extra copy of a grouped title in the library <paramref name="LibKey"/> (K44/K45); the peer and mapping then name the owner.</param>
public sealed record FederatedItem(string PeerKey, string MappingKey, string LibKey = "", bool IsCopy = false);

/// <summary>
/// Who a federated request is made by, after every check (DESIGN 8.5 step 8). The token is the caller's own, used only to echo it into the
/// URLs that go back to that caller.
/// </summary>
public sealed class Caller
{
    /// <summary>
    /// Initializes a new instance of the <see cref="Caller"/> class.
    /// </summary>
    /// <param name="user">The checked user.</param>
    /// <param name="token">The caller's own token (the <c>Jellyfin-Token</c> claim).</param>
    /// <param name="deviceId">The device id, capped (see <see cref="DeviceKey.Cap"/>).</param>
    /// <param name="isLocalNetwork">Whether the request came from the local network.</param>
    internal Caller(CallerUser user, string token, string deviceId, bool isLocalNetwork)
    {
        User = user;
        Token = token;
        DeviceId = deviceId;
        IsLocalNetwork = isLocalNetwork;
    }

    /// <summary>Gets the checked user.</summary>
    public CallerUser User { get; }

    /// <summary>Gets the user id.</summary>
    public Guid UserId => User.Id;

    /// <summary>Gets the policy flags that shape the forwarded PlaybackInfo (DESIGN 6.2.1).</summary>
    public CallerUser PolicySnapshot => User;

    /// <summary>Gets the caller's own token, used only to echo it into URLs that go back to this caller. Never logged.</summary>
    public string Token { get; }

    /// <summary>Gets the device id: client-chosen and capped; a convenience key within one user, never an identity (DESIGN 8.5 step 8).</summary>
    public string DeviceId { get; }

    /// <summary>Gets a value indicating whether the request came from the local network.</summary>
    public bool IsLocalNetwork { get; }

    /// <inheritdoc />
    public override string ToString() => "Caller [token hidden]";
}

/// <summary>
/// The outcome of CallerContext: a <see cref="Caller"/>, or a refusal (status and optional playback error code). A refusal never carries text.
/// </summary>
public sealed class CallerDecision
{
    private CallerDecision(Caller? caller, int status, PlaybackDenial code, int step)
    {
        Caller = caller;
        Status = status;
        PlaybackError = code;
        DeniedAtStep = step;
    }

    /// <summary>Gets the caller when the request is allowed, otherwise null.</summary>
    public Caller? Caller { get; }

    /// <summary>Gets a value indicating whether the request is allowed.</summary>
    public bool Allowed => Caller is not null;

    /// <summary>Gets the HTTP status of a refusal (401, 403 or 404), or 200 when allowed.</summary>
    public int Status { get; }

    /// <summary>Gets the playback error code to use when the refused route is PlaybackInfo.</summary>
    public PlaybackDenial PlaybackError { get; }

    /// <summary>Gets the step (1 to 7) that refused, for logs and tests; 0 when allowed.</summary>
    public int DeniedAtStep { get; }

    /// <summary>Creates an allowing decision.</summary>
    /// <param name="caller">The checked caller.</param>
    /// <returns>The decision.</returns>
    internal static CallerDecision Allow(Caller caller) => new(caller, 200, PlaybackDenial.None, 0);

    /// <summary>Creates a refusal.</summary>
    /// <param name="status">The HTTP status.</param>
    /// <param name="step">The step that refused.</param>
    /// <param name="code">The playback error code for PlaybackInfo.</param>
    /// <returns>The decision.</returns>
    internal static CallerDecision Deny(int status, int step, PlaybackDenial code = PlaybackDenial.None) => new(null, status, code, step);
}

/// <summary>
/// The access rule of DESIGN 5.6, as one pure function. CallerContext step 6 and the visibility service both use it, so playback and browsing
/// follow the same rule.
/// </summary>
public static class AccessPolicy
{
    /// <summary>
    /// Whether <paramref name="user"/> may use the library with <paramref name="access"/>.
    /// </summary>
    /// <param name="user">The user.</param>
    /// <param name="access">The library's access mode and selection.</param>
    /// <param name="shareUserIds">The Shoal share users of this server; they are never admitted, in either mode (Warden N-2).</param>
    /// <returns>True when the user is admitted.</returns>
    public static bool Admitted(CallerUser user, LibraryAccess access, IReadOnlySet<Guid> shareUserIds)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(access);
        ArgumentNullException.ThrowIfNull(shareUserIds);
        if (shareUserIds.Contains(user.Id))
        {
            return false;
        }

        return access.Mode == AccessMode.Everyone
            ? !user.IsRestricted || access.IncludeRestricted
            : access.SelectedUserIds.Contains(user.Id);
    }
}

/// <summary>
/// The key a client-chosen device id is reduced to (DESIGN 3.5, 8.5 step 8).
/// </summary>
public static class DeviceKey
{
    private const int MaxChars = 128;

    /// <summary>
    /// Caps a device id: up to 128 printable ASCII characters are kept as they are; anything longer, or anything else, is replaced by a
    /// hash, so a hostile id cannot grow a dictionary key or smuggle control characters into a log.
    /// </summary>
    /// <param name="deviceId">The id from the request, or null.</param>
    /// <returns>A key that is at most 128 printable ASCII characters.</returns>
    public static string Cap(string? deviceId)
    {
        if (string.IsNullOrEmpty(deviceId))
        {
            return "unknown";
        }

        if (deviceId.Length <= MaxChars && IsPrintableAscii(deviceId))
        {
            return deviceId;
        }

        return "h-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(deviceId)))[..32];
    }

    private static bool IsPrintableAscii(string value)
    {
        foreach (var c in value)
        {
            if (c is < ' ' or > '~')
            {
                return false;
            }
        }

        return true;
    }
}
