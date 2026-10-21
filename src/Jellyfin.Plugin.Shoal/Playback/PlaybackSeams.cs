using System;
using System.Linq;
using Jellyfin.Data.Queries;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Devices;
using MediaBrowser.Model.Dlna;

namespace Jellyfin.Plugin.Shoal.Playback;

/// <summary>
/// The profile a device registered through <c>Sessions/Capabilities</c>, for a PlaybackInfo call that carries no profile (DESIGN 6.2 step 2).
/// </summary>
public interface IDeviceCapabilities
{
    /// <summary>
    /// Looks the registered profile up, but only when the device belongs to the caller (v2.3, W4-2): core stores capabilities by device id alone, so
    /// a user who knows another user's device id must not borrow that profile.
    /// </summary>
    /// <param name="userId">The caller.</param>
    /// <param name="deviceId">The caller's device id.</param>
    /// <returns>The profile, or null when the device is not the caller's or registered none.</returns>
    DeviceProfile? ProfileFor(Guid userId, string deviceId);
}

/// <summary>
/// The server-wide bitrate limits that cap what a Peer is asked for.
/// </summary>
public interface ISystemLimits
{
    /// <summary>
    /// Reads the limits now.
    /// </summary>
    /// <returns>The limits.</returns>
    BitrateLimits Current();

    /// <summary>
    /// Reads the limits for a request that goes to one peer: the same as <see cref="Current"/> plus that peer's link budget, when there is one.
    /// </summary>
    /// <param name="peerKey">The peer.</param>
    /// <returns>The limits.</returns>
    BitrateLimits ForPeer(string peerKey) => Current();
}

/// <summary>
/// Tells a user that a Peer is offline (DESIGN 5.7). K20 replaces the empty default with the display message.
/// </summary>
public interface IOfflineNotice
{
    /// <summary>
    /// A play request failed because the Peer is offline.
    /// </summary>
    /// <param name="userId">The user who asked.</param>
    /// <param name="peerKey">The peer.</param>
    /// <param name="peerName">The peer's display name.</param>
    void PeerOffline(Guid userId, string peerKey, string peerName);

    /// <summary>
    /// A play request was refused because the title is over the connection's quality limit and the Peer offered no conversion (PLAN v2.9 K51): the
    /// user reads <see cref="QualityCap.RefusalText"/>. The default shows nothing.
    /// </summary>
    /// <param name="userId">The user who asked.</param>
    /// <param name="peerKey">The peer.</param>
    void QualityRefused(Guid userId, string peerKey)
    {
    }

    /// <summary>
    /// A play request was refused because a limit was reached (PLAN 2.9.2 K11): the user reads <see cref="BusyNotice.Text"/> for the reason. The error code the
    /// client gets is unchanged (RateLimitExceeded). The default shows nothing.
    /// </summary>
    /// <param name="userId">The user who asked.</param>
    /// <param name="peerKey">The peer.</param>
    /// <param name="peerName">The name Home's users read for the peer (its alias, if any).</param>
    /// <param name="reason">Which limit was reached.</param>
    /// <param name="playing">How many titles the user plays now (read for <see cref="BusyReason.UserStreams"/> only).</param>
    void Busy(Guid userId, string peerKey, string peerName, BusyReason reason, int playing = 0)
    {
    }
}

/// <summary>
/// <see cref="IDeviceCapabilities"/> from core's device manager.
/// </summary>
public sealed class JellyfinDeviceCapabilities : IDeviceCapabilities
{
    private readonly IDeviceManager _devices;

    /// <summary>
    /// Initializes a new instance of the <see cref="JellyfinDeviceCapabilities"/> class.
    /// </summary>
    /// <param name="devices">The device manager.</param>
    public JellyfinDeviceCapabilities(IDeviceManager devices) => _devices = devices;

    /// <inheritdoc />
    public DeviceProfile? ProfileFor(Guid userId, string deviceId)
    {
        if (string.IsNullOrEmpty(deviceId))
        {
            return null;
        }

        var owned = _devices.GetDevices(new DeviceQuery { DeviceId = deviceId }).Items.Any(d => d.UserId == userId);
        return owned ? _devices.GetCapabilities(deviceId)?.DeviceProfile : null;
    }
}

/// <summary>
/// <see cref="ISystemLimits"/> from the server configuration. The link budget stays empty until phase 4 measures the link.
/// </summary>
public sealed class JellyfinSystemLimits : ISystemLimits
{
    private readonly IServerConfigurationManager _configuration;

    /// <summary>
    /// Initializes a new instance of the <see cref="JellyfinSystemLimits"/> class.
    /// </summary>
    /// <param name="configuration">The server configuration.</param>
    public JellyfinSystemLimits(IServerConfigurationManager configuration) => _configuration = configuration;

    /// <inheritdoc />
    public BitrateLimits Current() => new(_configuration.Configuration.RemoteClientBitrateLimit, null);
}

/// <summary>
/// The default notice: nothing is shown.
/// </summary>
public sealed class NoOfflineNotice : IOfflineNotice
{
    /// <inheritdoc />
    public void PeerOffline(Guid userId, string peerKey, string peerName)
    {
    }
}
