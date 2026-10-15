using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Queries;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Devices;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Users;

namespace Jellyfin.Plugin.Shoal.Sharing;

/// <summary>
/// Jellyfin's accounts, devices and sessions as the share service needs them. The production class talks to Jellyfin's managers; tests use a fake.
/// </summary>
public interface IShareUserSource
{
    /// <summary>Gets a value indicating whether the account exists.</summary>
    /// <param name="userId">The account.</param>
    /// <returns>True when it exists.</returns>
    bool Exists(Guid userId);

    /// <summary>Creates an account with a password nobody will ever see.</summary>
    /// <param name="userName">The account name.</param>
    /// <param name="password">The random password; it is set and then forgotten.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The new account's id.</returns>
    Task<Guid> CreateAsync(string userName, string password, CancellationToken cancellationToken);

    /// <summary>Writes the whole policy: the fixed switches (nothing the account does not need) and the given parts.</summary>
    /// <param name="userId">The account.</param>
    /// <param name="policy">The wanted parts.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>A task that completes when the policy is written.</returns>
    Task ApplyPolicyAsync(Guid userId, SharePolicy policy, CancellationToken cancellationToken);

    /// <summary>Reads the policy and says whether it still matches <paramref name="expected"/> in every switch (the drift check).</summary>
    /// <param name="userId">The account.</param>
    /// <param name="expected">What it should be.</param>
    /// <returns>True when it matches; false when something was edited (or the account is gone).</returns>
    bool PolicyMatches(Guid userId, SharePolicy expected);

    /// <summary>Lists the devices of the account.</summary>
    /// <param name="userId">The account.</param>
    /// <returns>The devices.</returns>
    IReadOnlyList<ShareDevice> Devices(Guid userId);

    /// <summary>Deletes a device and signs it out (its tokens stop working).</summary>
    /// <param name="userId">The account.</param>
    /// <param name="deviceId">The device.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>A task that completes when the device is gone.</returns>
    Task RemoveDeviceAsync(Guid userId, string deviceId, CancellationToken cancellationToken);

    /// <summary>Deletes the account (and with it every token it ever had).</summary>
    /// <param name="userId">The account.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>A task that completes when the account is gone.</returns>
    Task DeleteAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>Gives the account a session and an access token for one device, without a password (what Quick Connect does).</summary>
    /// <param name="userId">The account.</param>
    /// <param name="deviceId">Home's device id; the device lock must already allow it.</param>
    /// <param name="deviceName">The device name (text only).</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The access token; it is handed to Home once and never stored.</returns>
    Task<string> MintTokenAsync(Guid userId, string deviceId, string deviceName, CancellationToken cancellationToken);

    /// <summary>
    /// Finds the device of a user whose access token is the given one (DESIGN v2.6.1: the rotation caller proves it holds the live token of an expected device).
    /// </summary>
    /// <param name="userId">The sharing account.</param>
    /// <param name="accessToken">The caller's token.</param>
    /// <returns>The device id, or null when no device of the user has that token.</returns>
    string? DeviceIdOfToken(Guid userId, string accessToken);

    /// <summary>Counts what is playing for the account.</summary>
    /// <param name="userId">The account.</param>
    /// <returns>The activity.</returns>
    ShareActivity Activity(Guid userId);
}

/// <summary>A device of a sharing account, as the share screens show it.</summary>
/// <param name="DeviceId">The device id (never shown or returned by the admin API; the page gets <see cref="ShareDevices.Handle"/>).</param>
/// <param name="Name">The device name (text only).</param>
/// <param name="App">The app name.</param>
/// <param name="Version">The app version.</param>
/// <param name="FirstSeenUtc">When it first appeared.</param>
/// <param name="LastSeenUtc">Its last activity.</param>
public sealed record ShareDevice(string DeviceId, string Name, string App, string Version, DateTime FirstSeenUtc, DateTime LastSeenUtc);

/// <summary>What a sharing account is allowed to do (DESIGN 4.1b step 1). Everything not listed here is switched off.</summary>
/// <param name="Disabled">Whether the account is disabled (paused).</param>
/// <param name="Libraries">The only libraries the account sees.</param>
/// <param name="BitrateLimitBps">The most bits per second per stream, or null.</param>
/// <param name="AllowedDevices">The only devices that may sign in (the device lock): Home's own.</param>
public sealed record SharePolicy(bool Disabled, IReadOnlyList<Guid> Libraries, int? BitrateLimitBps, IReadOnlyList<string> AllowedDevices);

/// <summary>What is running for a sharing account right now.</summary>
/// <param name="Playing">Sessions that are playing.</param>
/// <param name="Transcoding">Of those, how many are transcoding.</param>
public sealed record ShareActivity(int Playing, int Transcoding);

/// <summary>Helpers for the device ids that must never leave the server.</summary>
public static class ShareDevices
{
    /// <summary>A short stable handle for a device id. The id itself is a peer-side value; the page only ever sees this.</summary>
    /// <param name="deviceId">The device id.</param>
    /// <returns>12 hex characters.</returns>
    public static string Handle(string deviceId) => Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("shoal-device-handle\n" + deviceId)))[..12];
}

/// <summary>
/// <see cref="IShareUserSource"/> over <c>IUserManager</c>, <c>IDeviceManager</c> and <c>ISessionManager</c>.
/// </summary>
public sealed class JellyfinShareUsers : IShareUserSource
{
    private readonly IUserManager _users;
    private readonly IDeviceManager _devices;
    private readonly ISessionManager _sessions;

    /// <summary>
    /// Initializes a new instance of the <see cref="JellyfinShareUsers"/> class.
    /// </summary>
    /// <param name="users">The user manager.</param>
    /// <param name="devices">The device manager.</param>
    /// <param name="sessions">The session manager.</param>
    public JellyfinShareUsers(IUserManager users, IDeviceManager devices, ISessionManager sessions)
    {
        _users = users;
        _devices = devices;
        _sessions = sessions;
    }

    /// <inheritdoc />
    public bool Exists(Guid userId) => _users.GetUserById(userId) is not null;

    /// <inheritdoc />
    public async Task<Guid> CreateAsync(string userName, string password, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var user = await _users.CreateUserAsync(userName).ConfigureAwait(false);
        try
        {
            // Core makes an account with every folder and device open: lock it (disabled, nothing allowed) before anything else (Warden M3).
            await ApplyPolicyAsync(user.Id, new SharePolicy(true, [], null, []), CancellationToken.None).ConfigureAwait(false);
            await _users.ChangePassword(user.Id, password).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // The half-made account is deleted and the original failure is rethrown.
        catch (Exception)
#pragma warning restore CA1031
        {
            await _users.DeleteUserAsync(user.Id).ConfigureAwait(false);
            throw;
        }

        return user.Id;
    }

    /// <inheritdoc />
    public async Task ApplyPolicyAsync(Guid userId, SharePolicy policy, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var user = _users.GetUserById(userId) ?? throw new InvalidOperationException("The sharing account no longer exists.");
        var dto = _users.GetUserDto(user).Policy ?? throw new InvalidOperationException("The account has no policy.");
        Fill(dto, policy);
        await _users.UpdatePolicyAsync(userId, dto).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public bool PolicyMatches(Guid userId, SharePolicy expected)
    {
        if (_users.GetUserById(userId) is not { } user)
        {
            return false;
        }

        var actual = _users.GetUserDto(user).Policy;
        if (actual is null)
        {
            return false;
        }

        var wanted = new UserPolicy();
        Fill(wanted, expected);
        return actual.IsAdministrator == wanted.IsAdministrator
            && actual.IsDisabled == wanted.IsDisabled
            && actual.EnableAllFolders == wanted.EnableAllFolders
            && SameSet(actual.EnabledFolders, wanted.EnabledFolders)
            && actual.EnableAllDevices == wanted.EnableAllDevices
            && SameSet(actual.EnabledDevices, wanted.EnabledDevices)
            && actual.SyncPlayAccess == wanted.SyncPlayAccess
            && actual.MaxActiveSessions == wanted.MaxActiveSessions
            && actual.LoginAttemptsBeforeLockout == wanted.LoginAttemptsBeforeLockout
            && actual.RemoteClientBitrateLimit == wanted.RemoteClientBitrateLimit
            && actual.EnableUserPreferenceAccess == wanted.EnableUserPreferenceAccess
            && actual.EnableMediaConversion == wanted.EnableMediaConversion
            && actual.EnableSyncTranscoding == wanted.EnableSyncTranscoding
            && actual.EnablePublicSharing == wanted.EnablePublicSharing
            && actual.EnableContentDeletion == wanted.EnableContentDeletion
            && actual.EnableContentDownloading == wanted.EnableContentDownloading
            && actual.EnableLiveTvAccess == wanted.EnableLiveTvAccess
            && actual.EnableLiveTvManagement == wanted.EnableLiveTvManagement
            && actual.EnableRemoteControlOfOtherUsers == wanted.EnableRemoteControlOfOtherUsers
            && actual.EnableSharedDeviceControl == wanted.EnableSharedDeviceControl
            && actual.EnableCollectionManagement == wanted.EnableCollectionManagement
            && actual.EnableSubtitleManagement == wanted.EnableSubtitleManagement
            && actual.EnableLyricManagement == wanted.EnableLyricManagement;
    }

    /// <inheritdoc />
    public IReadOnlyList<ShareDevice> Devices(Guid userId) => _devices.GetDevices(new DeviceQuery { UserId = userId }).Items
        .Select(d => new ShareDevice(d.DeviceId, d.DeviceName ?? string.Empty, d.AppName ?? string.Empty, d.AppVersion ?? string.Empty, d.DateCreated, d.DateLastActivity))
        .ToList();

    /// <inheritdoc />
    public async Task RemoveDeviceAsync(Guid userId, string deviceId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var device in _devices.GetDevices(new DeviceQuery { UserId = userId }).Items.Where(d => string.Equals(d.DeviceId, deviceId, StringComparison.Ordinal)).ToList())
        {
            if (!string.IsNullOrEmpty(device.AccessToken))
            {
                // The session goes first (it ends the live connection), then the device record and its tokens.
                await _sessions.Logout(device.AccessToken).ConfigureAwait(false);
            }

            await _devices.DeleteDevice(device).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public Task DeleteAsync(Guid userId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return _users.DeleteUserAsync(userId);
    }

    /// <inheritdoc />
    public async Task<string> MintTokenAsync(Guid userId, string deviceId, string deviceName, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var user = _users.GetUserById(userId) ?? throw new InvalidOperationException("The sharing account no longer exists.");
        var result = await _sessions.AuthenticateDirect(new AuthenticationRequest { UserId = user.Id, Username = user.Username, App = "Shoal", AppVersion = "1", DeviceId = deviceId, DeviceName = deviceName }).ConfigureAwait(false);
        return result.AccessToken ?? throw new InvalidOperationException("Jellyfin gave no access token.");
    }

    /// <inheritdoc />
    public string? DeviceIdOfToken(Guid userId, string accessToken) => string.IsNullOrEmpty(accessToken)
        ? null
        : _devices.GetDevices(new DeviceQuery { UserId = userId }).Items.FirstOrDefault(d => string.Equals(d.AccessToken, accessToken, StringComparison.Ordinal))?.DeviceId;

    /// <inheritdoc />
    public ShareActivity Activity(Guid userId)
    {
        var mine = _sessions.Sessions.Where(s => s.UserId == userId && s.NowPlayingItem is not null).ToList();
        return new ShareActivity(mine.Count, mine.Count(s => s.PlayState?.PlayMethod == MediaBrowser.Model.Session.PlayMethod.Transcode));
    }

    /// <summary>Writes the policy a sharing account must have onto a user policy (the account is hidden, locked to one device, and can do nothing else).</summary>
    /// <param name="dto">The user policy.</param>
    /// <param name="policy">The wanted policy.</param>
    internal static void Fill(UserPolicy dto, SharePolicy policy)
    {
        dto.IsAdministrator = false;
        dto.IsHidden = true;
        dto.IsDisabled = policy.Disabled;
        dto.EnableAllFolders = false;
        dto.EnabledFolders = [.. policy.Libraries];
        dto.EnableAllChannels = false;
        dto.EnabledChannels = [];
        dto.EnableAllDevices = false;
        dto.EnabledDevices = [.. policy.AllowedDevices];
        dto.EnableMediaPlayback = true;
        dto.EnableAudioPlaybackTranscoding = true;
        dto.EnableVideoPlaybackTranscoding = true;
        dto.EnablePlaybackRemuxing = true;
        dto.EnableContentDeletion = false;
        dto.EnableContentDeletionFromFolders = [];
        dto.EnableContentDownloading = false;
        dto.EnableLiveTvAccess = false;
        dto.EnableLiveTvManagement = false;
        dto.EnableRemoteControlOfOtherUsers = false;
        dto.EnableSharedDeviceControl = false;
        dto.EnableCollectionManagement = false;
        dto.EnableSubtitleManagement = false;
        dto.EnableLyricManagement = false;
        dto.EnableUserPreferenceAccess = false;
        dto.EnableMediaConversion = false;
        dto.EnableSyncTranscoding = false;
        dto.EnablePublicSharing = false;
        dto.SyncPlayAccess = Jellyfin.Database.Implementations.Enums.SyncPlayUserAccessType.None; // DESIGN 4.1 step 3: no SyncPlay groups, nothing to join
        dto.MaxActiveSessions = 1;
        dto.LoginAttemptsBeforeLockout = -1; // three bad passwords by name must not disable the account (Warden L6)
        dto.RemoteClientBitrateLimit = policy.BitrateLimitBps ?? 0;
    }

    private static bool SameSet<T>(IEnumerable<T>? a, IEnumerable<T>? b) => new HashSet<T>(a ?? []).SetEquals(b ?? []);
}
