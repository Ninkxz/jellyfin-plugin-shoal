using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.Configuration;
using Jellyfin.Plugin.Shoal.Security;
using Jellyfin.Plugin.Shoal.State;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Shoal.Api.Admin;

/// <summary>GET and PUT Shoal/Admin/Settings (ui-spec 8.5.1). Every field is optional in a PUT: what is missing stays as it is.</summary>
internal sealed record SettingsDto
{
    /// <summary>Gets the folder for Shoal's library files.</summary>
    public string? StubRoot { get; init; }

    /// <summary>Gets what is wrong with it: ok, network, insideLibrary or notWritable.</summary>
    public string? StubRootCheck { get; init; }

    /// <summary>Gets a value indicating whether the admin accepts a folder on a network share.</summary>
    public bool? StubRootNetworkAck { get; init; }

    /// <summary>Gets the sync interval.</summary>
    public int? SyncIntervalHours { get; init; }

    /// <summary>Gets the smallest number of removals held.</summary>
    public int? MassDeletionMinCount { get; init; }

    /// <summary>Gets the percentage of removals held.</summary>
    public int? MassDeletionPercent { get; init; }

    /// <summary>Gets the smallest number of additions held.</summary>
    public int? NewItemsMinCount { get; init; }

    /// <summary>Gets the percentage of additions held.</summary>
    public int? NewItemsPercent { get; init; }

    /// <summary>Gets the storage limit per server in GB.</summary>
    public int? DiskQuotaGbPerPeer { get; init; }

    /// <summary>Gets the picture quality.</summary>
    public ArtQuality? ArtQuality { get; init; }

    /// <summary>Gets a value indicating whether quality follows the link.</summary>
    public bool? AutoFitQualityToLink { get; init; }

    /// <summary>Gets the streams per person.</summary>
    public int? MaxStreamsPerUser { get; init; }

    /// <summary>Gets a value indicating whether HLS subtitles are offered.</summary>
    public bool? HlsSubtitlesEnabled { get; init; }

    /// <summary>Gets the speed test interval.</summary>
    public int? SpeedTestIntervalHours { get; init; }

    /// <summary>Gets a value indicating whether clients are told when a server is offline.</summary>
    public bool? NotifyClientsWhenOffline { get; init; }

    /// <summary>Gets a value indicating whether offline servers are parked.</summary>
    public bool? ParkOfflinePeers { get; init; }

    /// <summary>Gets the hours before parking.</summary>
    public int? ParkAfterHours { get; init; }

    /// <summary>Gets the Tailscale socket: auto, off or a path.</summary>
    public string? TailscaleSocket { get; init; }

    /// <summary>Gets a value indicating whether Tailscale was found (read-only).</summary>
    public bool? TailscaleDetected { get; init; }

    /// <summary>Gets where the key comes from (read-only).</summary>
    public string? KeySource { get; init; }

    /// <summary>Gets a value indicating whether a title with several versions shows them all (DESIGN v2.6.4).</summary>
    public bool? AlternateVersionsEnabled { get; init; }

    /// <summary>Gets a value indicating whether preview thumbnails are served (DESIGN v2.6.5).</summary>
    public bool? TrickplayEnabled { get; init; }

    /// <summary>Gets the most disk the preview thumbnails may use, in GB (1 to 20).</summary>
    public int? TrickplayCacheGb { get; init; }

    /// <summary>Gets the disk the preview thumbnails use now, in bytes (read-only).</summary>
    public long? TrickplayCacheBytes { get; init; }

    /// <summary>Gets a value indicating whether subtitle fonts are served (DESIGN v2.6.6).</summary>
    public bool? AttachmentsEnabled { get; init; }

    /// <summary>Gets a value indicating whether pictures are re-encoded by a separate process (DESIGN v2.6.7).</summary>
    public bool? ReencodeImages { get; init; }

    /// <summary>Gets the state of that process (read-only).</summary>
    public ImageSandboxDto? ImageSandbox { get; init; }

    /// <summary>Gets a value indicating whether the data drive cannot protect secret files (read-only).</summary>
    public bool? SecretsVolumeUnsafe { get; init; }
}

/// <summary>One row of the protection counters.</summary>
/// <param name="App">The app.</param>
/// <param name="Network">thisServer, lan, tailnet or internet.</param>
/// <param name="Kind">The kind of request.</param>
/// <param name="Count">How many.</param>
internal sealed record CounterDto(string App, string Network, string Kind, int Count);

/// <summary>GET and PUT Shoal/Admin/Hardening.</summary>
internal sealed record HardeningDto
{
    /// <summary>Gets the mode: LogOnly, Enforce or Off.</summary>
    public required HardeningMode Mode { get; init; }

    /// <summary>Gets the modes the page may offer.</summary>
    public IReadOnlyList<HardeningMode> Modes { get; init; } = [HardeningMode.LogOnly, HardeningMode.Enforce, HardeningMode.Off];

    /// <summary>Gets since when the mode is in effect.</summary>
    public DateTime? SinceUtc { get; init; }

    /// <summary>Gets what would have been blocked.</summary>
    public IReadOnlyList<CounterDto> Counters { get; init; } = [];

    /// <summary>Gets how many errors the guard let through.</summary>
    public int GuardErrors { get; init; }

    /// <summary>Gets how many pairing requests were slowed down.</summary>
    public int ShieldHits { get; init; }

    /// <summary>Gets how many transcodes were refused.</summary>
    public int TranscodeCapHits { get; init; }

    /// <summary>Gets the wrong-invite counter.</summary>
    public InviteFailuresDto? InviteFailures { get; init; }

    /// <summary>Gets a value indicating whether most requests seem to come from one address.</summary>
    public bool ProxySuspected { get; init; }
}

/// <summary>A copy of the state that can be restored.</summary>
/// <param name="File">The file name.</param>
/// <param name="WrittenUtc">When it was written.</param>
/// <param name="Kind">bak or daily.</param>
internal sealed record StateFileDto(string File, DateTime WrittenUtc, string Kind);

/// <summary>A problem the startup check found.</summary>
/// <param name="Kind">missingKey, missingFolder, libraryMissing, orphanLibrary, orphanSecret or orphanFolder.</param>
/// <param name="PeerHandle">The server's handle, when there is one.</param>
/// <param name="ServerName">The server's name (text only).</param>
/// <param name="Library">A library name (text only).</param>
internal sealed record FindingDto(string Kind, string? PeerHandle, string? ServerName, string? Library);

/// <summary>GET Shoal/Admin/State.</summary>
/// <param name="Mode">Normal or Recovery.</param>
/// <param name="Files">The copies that can be restored, newest first.</param>
/// <param name="LastBackupUtc">The newest copy.</param>
/// <param name="Findings">What the startup check found.</param>
/// <param name="BackupByCopy">True when the folder cannot make backups the usual way and Shoal copies the file instead.</param>
internal sealed record StateInfoDto(string Mode, IReadOnlyList<StateFileDto> Files, DateTime? LastBackupUtc, IReadOnlyList<FindingDto> Findings, bool BackupByCopy = false);

/// <summary>The typed name that destructive calls carry (ui-spec 13).</summary>
/// <param name="ConfirmName">What the admin typed.</param>
internal sealed record ConfirmRequest(string? ConfirmName);

/// <summary>POST Shoal/Admin/State/Restore.</summary>
/// <param name="File">One of the files listed by GET State.</param>
internal sealed record RestoreRequest(string? File);

/// <summary>PUT Shoal/Admin/Hardening.</summary>
/// <param name="Mode">The new mode.</param>
internal sealed record HardeningRequest(HardeningMode? Mode);

/// <summary>
/// The settings side of the admin API (DESIGN 9.6): the settings form, "Protect your shared videos", the state copies and recovery,
/// and the danger zone. Same route prefix and same admin policy as <see cref="AdminController"/>.
/// </summary>
[ApiController]
[Authorize(Policy = Policies.RequiresElevation)]
[Route("Shoal/Admin")]
public class AdminSettingsController : ControllerBase
{
    private readonly IStateStore _state;
    private readonly ISecretStore _secrets;
    private readonly ISettingsAccessor _settings;
    private readonly IStubRootChecker _stubRoot;
    private readonly IGuardCounters _guard;
    private readonly IFindingsSource _findings;
    private readonly TimeProvider _time;
    private readonly Jellyfin.Plugin.Shoal.Visibility.ShoalLibraryService? _libraries;
    private readonly Jellyfin.Plugin.Shoal.Sync.IImageSandboxStatus? _sandbox;
    private readonly Jellyfin.Plugin.Shoal.Playback.TrickplayCache? _trickplay;
    private readonly ILogger<AdminSettingsController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="AdminSettingsController"/> class.
    /// </summary>
    /// <param name="state">The state store.</param>
    /// <param name="secrets">The secret store.</param>
    /// <param name="settings">The plugin toggles.</param>
    /// <param name="stubRoot">The folder check.</param>
    /// <param name="guard">The guard counters.</param>
    /// <param name="findings">The startup check.</param>
    /// <param name="time">The clock.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="libraries">The Shoal library service ("Remove everything"), or null when it is not wired.</param>
    /// <param name="sandbox">The image safety check (asked to test itself when the switch turns on), or null.</param>
    /// <param name="trickplay">The preview-thumbnail cache (its size, and "Clear"), or null.</param>
    public AdminSettingsController(IStateStore state, ISecretStore secrets, ISettingsAccessor settings, IStubRootChecker stubRoot, IGuardCounters guard, IFindingsSource findings, TimeProvider time, ILogger<AdminSettingsController> logger, Jellyfin.Plugin.Shoal.Visibility.ShoalLibraryService? libraries = null, Jellyfin.Plugin.Shoal.Sync.IImageSandboxStatus? sandbox = null, Jellyfin.Plugin.Shoal.Playback.TrickplayCache? trickplay = null)
    {
        _sandbox = sandbox;
        _trickplay = trickplay;
        _state = state;
        _libraries = libraries;
        _secrets = secrets;
        _settings = settings;
        _stubRoot = stubRoot;
        _guard = guard;
        _findings = findings;
        _time = time;
        _logger = logger;
    }

    /// <summary>
    /// The settings form.
    /// </summary>
    /// <returns>The settings.</returns>
    [HttpGet("Settings")]
    [Produces("application/json")]
    public IActionResult GetSettings() => AdminReply.Json(StatusCodes.Status200OK, ReadSettings());

    /// <summary>
    /// Saves the settings form. Everything is validated first; nothing is saved when anything is wrong.
    /// </summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The saved settings, 400 with the reason, or 409 in recovery mode.</returns>
    [HttpPut("Settings")]
    public async Task<IActionResult> PutSettings(CancellationToken cancellationToken)
    {
        var body = await AdminReply.ReadAsync<SettingsDto>(Request, cancellationToken).ConfigureAwait(false);

        if (body is null)
        {
            return AdminReply.Error(StatusCodes.Status400BadRequest, "invalid");
        }

        if (_state.Status == StateStatus.Recovery)
        {
            return AdminReply.Error(StatusCodes.Status409Conflict, "recovery");
        }

        var config = _settings.Current;
        var reencodeWasOff = !config.ReencodeImages;
        config.SyncIntervalHours = body.SyncIntervalHours ?? config.SyncIntervalHours;
        config.MassDeletionMinCount = body.MassDeletionMinCount ?? config.MassDeletionMinCount;
        config.MassDeletionPercent = body.MassDeletionPercent ?? config.MassDeletionPercent;
        config.NewItemsMinCount = body.NewItemsMinCount ?? config.NewItemsMinCount;
        config.NewItemsPercent = body.NewItemsPercent ?? config.NewItemsPercent;
        config.DiskQuotaGbPerPeer = body.DiskQuotaGbPerPeer ?? config.DiskQuotaGbPerPeer;
        config.ArtQuality = body.ArtQuality ?? config.ArtQuality;
        config.AutoFitQualityToLink = body.AutoFitQualityToLink ?? config.AutoFitQualityToLink;
        config.MaxStreamsPerUser = body.MaxStreamsPerUser ?? config.MaxStreamsPerUser;
        config.HlsSubtitlesEnabled = body.HlsSubtitlesEnabled ?? config.HlsSubtitlesEnabled;
        config.SpeedTestIntervalHours = body.SpeedTestIntervalHours ?? config.SpeedTestIntervalHours;
        config.NotifyClientsWhenOffline = body.NotifyClientsWhenOffline ?? config.NotifyClientsWhenOffline;
        config.ParkOfflinePeers = body.ParkOfflinePeers ?? config.ParkOfflinePeers;
        config.ParkAfterHours = body.ParkAfterHours ?? config.ParkAfterHours;
        config.TailscaleSocket = body.TailscaleSocket ?? config.TailscaleSocket;
        config.AlternateVersionsEnabled = body.AlternateVersionsEnabled ?? config.AlternateVersionsEnabled;
        config.TrickplayEnabled = body.TrickplayEnabled ?? config.TrickplayEnabled;
        config.TrickplayCacheGb = body.TrickplayCacheGb ?? config.TrickplayCacheGb;
        config.AttachmentsEnabled = body.AttachmentsEnabled ?? config.AttachmentsEnabled;
        config.ReencodeImages = body.ReencodeImages ?? config.ReencodeImages;

        // The page validates too; a value outside its range is refused here, not silently clamped.
        var tooBig = config.DiskQuotaGbPerPeer > 50;
        if (SettingsValidator.Apply(PluginSettingsAccessor.Copy(config)).Count > 0 || tooBig)
        {
            return AdminReply.Error(StatusCodes.Status400BadRequest, "invalid");
        }

        string? newStubRoot = null;
        if (body.StubRoot is { } wanted && !string.Equals(wanted, _state.Current.StubRoot, StringComparison.Ordinal))
        {
            var check = _stubRoot.Check(wanted);
            if (check != StubRootCheck.Ok && !(check == StubRootCheck.Network && body.StubRootNetworkAck == true))
            {
                return AdminReply.Json(StatusCodes.Status400BadRequest, new { error = "stubRoot", detail = CheckName(check) });
            }

            newStubRoot = wanted;
        }

        try
        {
            if (newStubRoot is not null)
            {
                await _state.UpdateAsync(s => s.StubRoot = newStubRoot, cancellationToken).ConfigureAwait(false);
            }

            _settings.Save(config);

            // The switch just turned on: the sandbox tests itself again in the background (DESIGN v2.6.7).
            if (reencodeWasOff && config.ReencodeImages)
            {
                _sandbox?.RequestSelfTest();
            }
        }
        catch (StateRecoveryException)
        {
            return AdminReply.Error(StatusCodes.Status409Conflict, "recovery");
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "The settings could not be saved");
            return AdminReply.Error(StatusCodes.Status500InternalServerError, "server");
        }

        return AdminReply.Json(StatusCodes.Status200OK, ReadSettings());
    }

    /// <summary>
    /// Empties the preview-thumbnail cache ("Clear" in Settings). The thumbnails are fetched again when they are next asked for.
    /// </summary>
    /// <returns>200 with the bytes freed, or 404 when the cache is not wired.</returns>
    [HttpDelete("TrickplayCache")]
    [Produces("application/json")]
    public IActionResult ClearTrickplayCache()
    {
        if (_trickplay is null)
        {
            return AdminReply.Error(StatusCodes.Status404NotFound, "notFound");
        }

        var freed = _trickplay.Clear();
        _logger.LogInformation("Admin cleared the preview-thumbnail cache ({Bytes} bytes)", freed);
        return AdminReply.Json(StatusCodes.Status200OK, new { freedBytes = freed });
    }

    /// <summary>
    /// "Protect your shared videos": the mode and what the filter counted.
    /// </summary>
    /// <returns>The protection data.</returns>
    [HttpGet("Hardening")]
    [Produces("application/json")]
    public IActionResult GetHardening() => AdminReply.Json(StatusCodes.Status200OK, ReadHardening());

    /// <summary>
    /// Changes the protection mode. Saved at once (the page asks for confirmation first).
    /// </summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The protection data, 400 or 409.</returns>
    [HttpPut("Hardening")]
    public async Task<IActionResult> PutHardening(CancellationToken cancellationToken)
    {
        var body = await AdminReply.ReadAsync<HardeningRequest>(Request, cancellationToken).ConfigureAwait(false);

        if (body?.Mode is not { } mode || !Enum.IsDefined(mode))
        {
            return AdminReply.Error(StatusCodes.Status400BadRequest, "invalid");
        }

        try
        {
            await _state.UpdateAsync(
                s =>
                {
                    if (s.PeerRole.HardeningMode != mode)
                    {
                        s.PeerRole.HardeningMode = mode;
                        s.PeerRole.HardeningSinceUtc = _time.GetUtcNow().UtcDateTime;
                    }
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (StateRecoveryException)
        {
            return AdminReply.Error(StatusCodes.Status409Conflict, "recovery");
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "The protection mode could not be saved");
            return AdminReply.Error(StatusCodes.Status500InternalServerError, "server");
        }

        _logger.LogInformation("Admin set the stream protection mode to {Mode}", mode);
        return AdminReply.Json(StatusCodes.Status200OK, ReadHardening());
    }

    /// <summary>
    /// The copies of the state, and what the startup check found.
    /// </summary>
    /// <returns>The state information.</returns>
    [HttpGet("State")]
    [Produces("application/json")]
    public IActionResult GetState()
    {
        var files = _state.GetCandidates()
            .Where(c => c is { Exists: true, Usable: true, WrittenUtc: not null } && !string.Equals(c.FileName, "state.json", StringComparison.Ordinal))
            .Select(c => new StateFileDto(c.FileName, c.WrittenUtc!.Value, c.FileName.EndsWith(".bak", StringComparison.Ordinal) ? "bak" : "daily"))
            .OrderByDescending(f => f.WrittenUtc)
            .ToList();
        var findings = _state.Status == StateStatus.Recovery
            ? []
            : _findings.Compute().Select(Finding).Where(f => f is not null).Select(f => f!).ToList();
        return AdminReply.Json(
            StatusCodes.Status200OK,
            new StateInfoDto(_state.Status == StateStatus.Recovery ? "Recovery" : "Normal", files, files.Count > 0 ? files[0].WrittenUtc : null, findings));
    }

    /// <summary>
    /// "Restore a backup": makes one of the listed copies the state. The damaged file is kept next to it, never overwritten.
    /// </summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>200, or 400 when the file is not one of the listed copies.</returns>
    [HttpPost("State/Restore")]
    public async Task<IActionResult> Restore(CancellationToken cancellationToken)
    {
        var body = await AdminReply.ReadAsync<RestoreRequest>(Request, cancellationToken).ConfigureAwait(false);

        // Only a name from our own list goes to the store; whatever else was sent is not a file name.
        var allowed = _state.GetCandidates().Where(c => c.Exists && c.Usable).Select(c => c.FileName).ToHashSet(StringComparer.Ordinal);
        if (body?.File is not { } file || !allowed.Contains(file))
        {
            return AdminReply.Error(StatusCodes.Status400BadRequest, "invalid");
        }

        try
        {
            await _state.RestoreFromAsync(file, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "A state copy could not be restored");
            return AdminReply.Error(StatusCodes.Status400BadRequest, "invalid");
        }

        _logger.LogWarning("Admin restored Shoal's settings from {File}", file);
        return AdminReply.Json(StatusCodes.Status200OK, new { });
    }

    /// <summary>
    /// "Start over": the way out of recovery mode. Moves the damaged files aside (nothing is deleted) and starts with an empty state.
    /// </summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>200, 400 without the typed phrase, or 409 when the settings are not in recovery mode.</returns>
    [HttpPost("State/StartOver")]
    public async Task<IActionResult> StartOver(CancellationToken cancellationToken)
    {
        // The exit from recovery mode, nothing else: on a healthy server it would drop every share and the protection mode.
        if (_state.Status != StateStatus.Recovery)
        {
            return AdminReply.Error(StatusCodes.Status409Conflict, "notInRecovery");
        }

        if (!await ConfirmedAsync("start over", cancellationToken).ConfigureAwait(false))
        {
            return AdminReply.Error(StatusCodes.Status400BadRequest, "confirmName");
        }

        try
        {
            await _state.StartOverAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Shoal could not start over");
            return AdminReply.Error(StatusCodes.Status500InternalServerError, "server");
        }

        _logger.LogWarning("Admin started Shoal over with empty settings");
        return AdminReply.Json(StatusCodes.Status200OK, new { });
    }

    /// <summary>
    /// "Remove everything" (the clean way to uninstall): deletes the Shoal libraries first, then restores "all libraries" to the accounts Shoal
    /// managed (DESIGN 5.6). Peers, stubs and sharing accounts are not touched. It refuses (501) when the library service is not wired.
    /// </summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>400 without the typed phrase, 501 when not wired, else 200 with the counts.</returns>
    [HttpDelete("All")]
    public async Task<IActionResult> DeleteAll(CancellationToken cancellationToken)
    {
        if (!await ConfirmedAsync("delete everything", cancellationToken).ConfigureAwait(false))
        {
            return AdminReply.Error(StatusCodes.Status400BadRequest, "confirmName");
        }

        if (_libraries is null)
        {
            return AdminReply.Error(StatusCodes.Status501NotImplemented, "notImplemented");
        }

        // DESIGN 5.6 (W6 L-5): the Shoal libraries go first, then "all libraries" goes back to the accounts Shoal managed.
        var (removed, restored) = await _libraries.RemoveAllAsync(cancellationToken).ConfigureAwait(false);
        _logger.LogWarning("Admin removed everything: {Libraries} libraries removed, {Accounts} accounts restored", removed, restored);
        return AdminReply.Json(StatusCodes.Status200OK, new { librariesRemoved = removed, accountsRestored = restored });
    }

    private async Task<bool> ConfirmedAsync(string expected, CancellationToken cancellationToken)
    {
        var body = await AdminReply.ReadAsync<ConfirmRequest>(Request, cancellationToken).ConfigureAwait(false);

        return string.Equals(body?.ConfirmName, expected, StringComparison.Ordinal);
    }

    private SettingsDto ReadSettings()
    {
        var c = _settings.Current;
        var info = _secrets.Info;
        return new SettingsDto
        {
            StubRoot = _state.Current.StubRoot,
            StubRootCheck = string.IsNullOrEmpty(_state.Current.StubRoot) ? CheckName(StubRootCheck.NotWritable) : CheckName(_stubRoot.Check(_state.Current.StubRoot, probeWrite: false)),
            SyncIntervalHours = c.SyncIntervalHours,
            MassDeletionMinCount = c.MassDeletionMinCount,
            MassDeletionPercent = c.MassDeletionPercent,
            NewItemsMinCount = c.NewItemsMinCount,
            NewItemsPercent = c.NewItemsPercent,
            DiskQuotaGbPerPeer = c.DiskQuotaGbPerPeer,
            ArtQuality = c.ArtQuality,
            AutoFitQualityToLink = c.AutoFitQualityToLink,
            MaxStreamsPerUser = c.MaxStreamsPerUser,
            HlsSubtitlesEnabled = c.HlsSubtitlesEnabled,
            SpeedTestIntervalHours = c.SpeedTestIntervalHours,
            NotifyClientsWhenOffline = c.NotifyClientsWhenOffline,
            ParkOfflinePeers = c.ParkOfflinePeers,
            ParkAfterHours = c.ParkAfterHours,
            TailscaleSocket = c.TailscaleSocket,
            TailscaleDetected = false,
            AlternateVersionsEnabled = c.AlternateVersionsEnabled,
            TrickplayEnabled = c.TrickplayEnabled,
            TrickplayCacheGb = c.TrickplayCacheGb,
            TrickplayCacheBytes = _trickplay?.TotalBytes,
            AttachmentsEnabled = c.AttachmentsEnabled,
            ReencodeImages = c.ReencodeImages,
            ImageSandbox = _sandbox?.GetStatus() is { } sandbox ? new ImageSandboxDto(sandbox.State, sandbox.Limits, sandbox.Reason, sandbox.Rejected) : null,
            KeySource = info.KeySource switch
            {
                KeySource.Environment => "env",
                KeySource.Dpapi => "dpapi",
                KeySource.EnvironmentFile => "envFile",
                KeySource.KeyFile => "file",
                _ => null
            },
            SecretsVolumeUnsafe = info.VolumeWarning is not null
        };
    }

    private HardeningDto ReadHardening()
    {
        var role = _state.Current.PeerRole;
        return new HardeningDto
        {
            Mode = role.HardeningMode,
            SinceUtc = role.HardeningSinceUtc == default ? null : role.HardeningSinceUtc,
            Counters = _guard.WouldHaveBeenBlocked.Select(c => new CounterDto(c.App, c.Network, c.Kind, c.Count)).ToList(),
            GuardErrors = _guard.GuardErrors,
            ShieldHits = _guard.ShieldHits,
            TranscodeCapHits = _guard.TranscodeCapHits,
            InviteFailures = new InviteFailuresDto(_guard.InviteFailuresLastHour, _guard.InviteFailuresLastHour >= 5),
            ProxySuspected = _guard.ProxySuspected
        };
    }

    private FindingDto? Finding(ReconcileFinding f)
    {
        var peer = f.PeerKey is null ? null : _state.Current.Home.Peers.FirstOrDefault(p => string.Equals(p.PeerKey, f.PeerKey, StringComparison.Ordinal));
        return f.Kind switch
        {
            ReconcileKind.PeerWithoutSecret => new FindingDto("missingKey", peer?.PeerKey, peer?.DisplayName, null),
            ReconcileKind.PeerWithoutFolder => new FindingDto("missingFolder", peer?.PeerKey, peer?.DisplayName, null),
            ReconcileKind.LibraryMissing => new FindingDto("libraryMissing", peer?.PeerKey, peer?.DisplayName, f.Detail),
            ReconcileKind.LibraryWithoutPeer => new FindingDto("orphanLibrary", null, null, f.Detail),
            ReconcileKind.SecretWithoutPeer => new FindingDto("orphanSecret", null, null, null),
            ReconcileKind.FolderWithoutPeer => new FindingDto("orphanFolder", null, null, null),
            _ => null
        };
    }

    private static string CheckName(StubRootCheck check) => check switch
    {
        StubRootCheck.Ok => "ok",
        StubRootCheck.Network => "network",
        StubRootCheck.InsideLibrary => "insideLibrary",
        _ => "notWritable"
    };
}
