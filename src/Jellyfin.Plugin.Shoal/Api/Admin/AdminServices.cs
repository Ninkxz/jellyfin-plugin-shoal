using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.Shoal.Configuration;
using Jellyfin.Plugin.Shoal.State;
using Jellyfin.Plugin.Shoal.Storage;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.Shoal.Api.Admin;

/// <summary>
/// What is wrong with the folder for Shoal's library files (DESIGN 3.1, ui-spec 8.5 item 1).
/// </summary>
public enum StubRootCheck
{
    /// <summary>A local, writable folder outside every library.</summary>
    Ok,

    /// <summary>The folder is on a network share: a network hiccup could freeze the server.</summary>
    Network,

    /// <summary>The folder is inside one of the Jellyfin libraries.</summary>
    InsideLibrary,

    /// <summary>The folder cannot be created or written to.</summary>
    NotWritable
}

/// <summary>
/// Reads and writes the plugin's XML toggles for the admin page.
/// </summary>
public interface ISettingsAccessor
{
    /// <summary>Gets a copy of the current toggles (changing the copy changes nothing until <see cref="Save"/>).</summary>
    PluginConfiguration Current { get; }

    /// <summary>
    /// Saves toggles. The caller has already validated them.
    /// </summary>
    /// <param name="configuration">The new toggles.</param>
    void Save(PluginConfiguration configuration);
}

/// <summary>
/// Checks a folder for Shoal's library files.
/// </summary>
public interface IStubRootChecker
{
    /// <summary>
    /// Checks a folder.
    /// </summary>
    /// <param name="path">The folder.</param>
    /// <returns>The result.</returns>
    StubRootCheck Check(string path);

    /// <summary>
    /// Checks a folder, optionally without touching the disk (the read path must not create folders).
    /// </summary>
    /// <param name="path">The folder.</param>
    /// <param name="probeWrite">True to create the folder and prove that it can be written to.</param>
    /// <returns>The result.</returns>
    StubRootCheck Check(string path, bool probeWrite) => Check(path);
}

/// <summary>
/// What the Peer-side guard filter counted. Implemented by the guard filter (K23); until then nothing has been counted.
/// </summary>
public interface IGuardCounters
{
    /// <summary>Gets the requests that would have been blocked in the last 7 days.</summary>
    IReadOnlyList<GuardCounter> WouldHaveBeenBlocked { get; }

    /// <summary>Gets how many times the guard failed open.</summary>
    int GuardErrors { get; }

    /// <summary>Gets how many transcodes were refused over a friend's limit.</summary>
    int TranscodeCapHits { get; }

    /// <summary>Gets how many wrong invites were tried in the last hour.</summary>
    int InviteFailuresLastHour => 0;

    /// <summary>Gets a value indicating whether most requests seem to come from one address (a reverse proxy in front).</summary>
    bool ProxySuspected => false;
}

/// <summary>
/// What the startup reconciliation found (DESIGN 3.7), read-only.
/// </summary>
public interface IFindingsSource
{
    /// <summary>
    /// Compares the state with the secrets, the folders and the libraries.
    /// </summary>
    /// <returns>The findings.</returns>
    IReadOnlyList<ReconcileFinding> Compute();
}

/// <summary>
/// Persists toggles through the plugin instance (which clamps them again, see <see cref="SettingsValidator"/>).
/// </summary>
public sealed class PluginSettingsAccessor : ISettingsAccessor
{
    /// <inheritdoc />
    public PluginConfiguration Current => Copy(Plugin.Instance?.Configuration ?? new PluginConfiguration());

    /// <inheritdoc />
    public void Save(PluginConfiguration configuration)
    {
        var plugin = Plugin.Instance ?? throw new InvalidOperationException("The plugin is not loaded.");
        plugin.UpdateConfiguration(configuration);
    }

    /// <summary>
    /// Copies every toggle (the page edits a copy, never the live object).
    /// </summary>
    /// <param name="source">The source.</param>
    /// <returns>The copy.</returns>
    public static PluginConfiguration Copy(PluginConfiguration source) => new()
    {
        SchemaVersion = source.SchemaVersion,
        AutoFitQualityToLink = source.AutoFitQualityToLink,
        MaxStreamsPerUser = source.MaxStreamsPerUser,
        HlsBufferAhead = source.HlsBufferAhead,
        SyncIntervalHours = source.SyncIntervalHours,
        SpeedTestIntervalHours = source.SpeedTestIntervalHours,
        MassDeletionMinCount = source.MassDeletionMinCount,
        MassDeletionPercent = source.MassDeletionPercent,
        NewItemsMinCount = source.NewItemsMinCount,
        NewItemsPercent = source.NewItemsPercent,
        DiskQuotaGbPerPeer = source.DiskQuotaGbPerPeer,
        ArtQuality = source.ArtQuality,
        ParkOfflinePeers = source.ParkOfflinePeers,
        ParkAfterHours = source.ParkAfterHours,
        NotifyClientsWhenOffline = source.NotifyClientsWhenOffline,
        HlsSubtitlesEnabled = source.HlsSubtitlesEnabled,
        DetectableByFriends = source.DetectableByFriends,
        TailscaleSocket = source.TailscaleSocket,
        AlternateVersionsEnabled = source.AlternateVersionsEnabled,
        TrickplayEnabled = source.TrickplayEnabled,
        TrickplayCacheGb = source.TrickplayCacheGb,
        AttachmentsEnabled = source.AttachmentsEnabled,
        ReencodeImages = source.ReencodeImages,
        TvShowsEnabled = true, // always on (v2.9.1); kept in the answer for an older page
        TvEpisodeThumbs = source.TvEpisodeThumbs,
        OfflineCardEnabled = source.OfflineCardEnabled,
        RequestServiceName = source.RequestServiceName,
        RequestServiceUrl = source.RequestServiceUrl
    };
}

/// <summary>
/// The real check (DESIGN 3.1): the folder must not be inside, or contain, a library (also after following symlinks and reparse points),
/// must not itself be a link, must not be on a network or FUSE file system, and must be writable.
/// </summary>
public sealed class StubRootChecker : IStubRootChecker
{
    private static readonly string[] NetworkFileSystems = ["nfs", "nfs4", "cifs", "smb", "smb3", "smbfs", "afs", "ceph", "glusterfs", "9p"];

    private readonly Func<IReadOnlyList<string>> _libraryLocations;

    /// <summary>
    /// Initializes a new instance of the <see cref="StubRootChecker"/> class.
    /// </summary>
    /// <param name="libraries">The library manager.</param>
    public StubRootChecker(ILibraryManager libraries)
        : this(() => libraries.GetVirtualFolders().SelectMany(f => f.Locations ?? []).ToList())
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="StubRootChecker"/> class.
    /// </summary>
    /// <param name="libraryLocations">Where the libraries are.</param>
    internal StubRootChecker(Func<IReadOnlyList<string>> libraryLocations) => _libraryLocations = libraryLocations;

    /// <inheritdoc />
    public StubRootCheck Check(string path) => Check(path, probeWrite: true);

    /// <inheritdoc />
    public StubRootCheck Check(string path, bool probeWrite)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            return StubRootCheck.NotWritable;
        }

        string full;
        try
        {
            full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return StubRootCheck.NotWritable;
        }

        // The folder itself must not be a symlink or a reparse point: what it points at could be anywhere.
        if (IsLink(full))
        {
            return StubRootCheck.NotWritable;
        }

        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var resolved = ResolveLinks(full);
        foreach (var location in _libraryLocations())
        {
            if (string.IsNullOrWhiteSpace(location))
            {
                continue;
            }

            var library = Path.TrimEndingDirectorySeparator(location);
            var libraryResolved = ResolveLinks(library);
            foreach (var mine in new[] { full, resolved })
            {
                foreach (var theirs in new[] { library, libraryResolved })
                {
                    // Shoal's own libraries live inside the stub root (<root>/<peer key>/<folder>); they are what the root is for, not a conflict.
                    if (IsInside(mine, theirs, comparison) || (IsInside(theirs, mine, comparison) && !IsShoalOwn(theirs, mine)))
                    {
                        return StubRootCheck.InsideLibrary;
                    }
                }
            }
        }

        if (IsNetwork(resolved))
        {
            return StubRootCheck.Network;
        }

        return CanWrite(full, probeWrite) ? StubRootCheck.Ok : StubRootCheck.NotWritable;
    }

    /// <summary>
    /// Whether a library lies in a folder Shoal itself creates below the stub root: <c>root/&lt;peer key&gt;/...</c> (a per-friend library, now legacy) or
    /// <c>root/libs/&lt;library key&gt;/...</c> (a v2.9 library layout, Pilot 14: the libs tree was reported as "inside one of your libraries").
    /// </summary>
    /// <param name="library">The library's folder.</param>
    /// <param name="root">The stub root.</param>
    /// <returns>True for a folder Shoal made.</returns>
    internal static bool IsShoalOwn(string library, string root)
    {
        var relative = Path.GetRelativePath(root, library);
        var parts = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (parts[0] == ShoalLibrary.LibsFolder)
        {
            return parts.Length >= 2 && ShoalPaths.IsValidPeerKey(parts[1]);
        }

        return ShoalPaths.IsValidPeerKey(parts[0]);
    }

    /// <summary>Whether a file system type is one that must not hold Shoal's files (network, or any FUSE mount).</summary>
    /// <param name="fileSystemType">The type from the mount table, for example <c>nfs4</c> or <c>fuse.sshfs</c>.</param>
    /// <returns>True for network and FUSE types.</returns>
    internal static bool IsNetworkFileSystem(string fileSystemType) =>
        NetworkFileSystems.Contains(fileSystemType, StringComparer.Ordinal) || fileSystemType.StartsWith("fuse.", StringComparison.Ordinal);

    /// <summary>Whether <paramref name="folder"/> is <paramref name="other"/> or lies inside it.</summary>
    /// <param name="folder">The folder.</param>
    /// <param name="other">The possible parent.</param>
    /// <param name="comparison">How to compare.</param>
    /// <returns>True when inside.</returns>
    internal static bool IsInside(string folder, string other, StringComparison comparison)
    {
        if (string.IsNullOrWhiteSpace(other))
        {
            return false;
        }

        var root = Path.TrimEndingDirectorySeparator(other);
        return string.Equals(folder, root, comparison) || folder.StartsWith(root + Path.DirectorySeparatorChar, comparison);
    }

    private static bool IsLink(string path)
    {
        try
        {
            return new DirectoryInfo(path).LinkTarget is not null || (Directory.Exists(path) && new DirectoryInfo(path).Attributes.HasFlag(FileAttributes.ReparsePoint));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    // The real path: the first component that is a link is replaced by its final target, then the walk continues below it.
    private static string ResolveLinks(string full)
    {
        try
        {
            var current = Path.GetPathRoot(full) ?? string.Empty;
            var rest = full[current.Length..].Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var part in rest)
            {
                current = Path.Combine(current, part);
                var info = new DirectoryInfo(current);
                if (info.Exists && info.LinkTarget is not null && info.ResolveLinkTarget(returnFinalTarget: true) is { } target)
                {
                    current = Path.TrimEndingDirectorySeparator(target.FullName);
                }
            }

            return Path.TrimEndingDirectorySeparator(current);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return full;
        }
    }

    private static bool IsNetwork(string full)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                return full.StartsWith(@"\\", StringComparison.Ordinal) || new DriveInfo(Path.GetPathRoot(full)!).DriveType == DriveType.Network;
            }

            if (!File.Exists("/proc/mounts"))
            {
                return false;
            }

            string? best = null;
            var bestType = string.Empty;
            foreach (var line in File.ReadLines("/proc/mounts"))
            {
                var parts = line.Split(' ');
                if (parts.Length < 3)
                {
                    continue;
                }

                var mount = Path.TrimEndingDirectorySeparator(parts[1].Replace("\\040", " ", StringComparison.Ordinal));
                var matches = mount.Length == 0 || string.Equals(full, mount, StringComparison.Ordinal) || full.StartsWith(mount + "/", StringComparison.Ordinal);
                if (matches && (best is null || mount.Length >= best.Length))
                {
                    best = mount;
                    bestType = parts[2];
                }
            }

            return IsNetworkFileSystem(bestType);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    // probeWrite false is the read path (GET Settings): it must not create a directory or a file, so a folder that does not exist yet
    // counts as writable when its nearest existing parent exists. The write path (PUT Settings) creates the folder and proves it.
    private static bool CanWrite(string folder, bool probeWrite)
    {
        try
        {
            if (!probeWrite)
            {
                var existing = folder;
                while (!Directory.Exists(existing))
                {
                    var parent = Path.GetDirectoryName(existing);
                    if (string.IsNullOrEmpty(parent))
                    {
                        return false;
                    }

                    existing = parent;
                }

                return true;
            }

            Directory.CreateDirectory(folder);
            var probe = Path.Combine(folder, ".shoal-write-test-" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }
}

/// <summary>
/// One row of "Would have been blocked, last 7 days" (DESIGN 8.4).
/// </summary>
/// <param name="App">The client app (text from the request, never HTML).</param>
/// <param name="Network">thisServer, lan, tailnet or internet.</param>
/// <param name="Kind">stream, subtitle, attachment, hls or image.</param>
/// <param name="Count">How many.</param>
public sealed record GuardCounter(string App, string Network, string Kind, int Count);

/// <summary>
/// The placeholder until the guard filter exists: nothing counted.
/// </summary>
public sealed class NoGuardCounters : IGuardCounters
{
    /// <inheritdoc />
    public IReadOnlyList<GuardCounter> WouldHaveBeenBlocked => [];

    /// <inheritdoc />
    public int GuardErrors => 0;

    /// <inheritdoc />
    public int TranscodeCapHits => 0;
}

/// <summary>
/// Runs <see cref="ReconciliationService"/>.
/// </summary>
public sealed class ReconciliationFindings : IFindingsSource
{
    private readonly ReconciliationService _service;

    /// <summary>
    /// Initializes a new instance of the <see cref="ReconciliationFindings"/> class.
    /// </summary>
    /// <param name="service">The service.</param>
    public ReconciliationFindings(ReconciliationService service) => _service = service;

    /// <inheritdoc />
    public IReadOnlyList<ReconcileFinding> Compute() => _service.Compute().Findings;
}
