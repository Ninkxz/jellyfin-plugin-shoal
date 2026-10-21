using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.Http;
using Jellyfin.Plugin.Shoal.Pairing;
using Jellyfin.Plugin.Shoal.Peers;
using Jellyfin.Plugin.Shoal.Security;
using Jellyfin.Plugin.Shoal.State;
using Jellyfin.Plugin.Shoal.Storage;
using Jellyfin.Plugin.Shoal.Sync;
using Jellyfin.Plugin.Shoal.Text;
using Jellyfin.Plugin.Shoal.Visibility;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Shoal.Api.Admin;

/// <summary>
/// What the admin changes about one server on its detail page (DESIGN 9.6): trusting a new certificate, its settings and its removal.
/// </summary>
[ApiController]
[Authorize(Policy = Policies.RequiresElevation)]
[Route("Shoal/Admin")]
public class AdminPeerSettingsController : ControllerBase
{
    private readonly IStateStore _state;
    private readonly IPeerEndpoints _peers;
    private readonly PeerClient _client;
    private readonly PeerRegistry _registry;
    private readonly PeerAddressService _addresses;
    private readonly ISecretStore _secrets;
    private readonly ShoalLibraryService _libraries;
    private readonly ShoalPaths _paths;
    private readonly IPairingGate? _gate;
    private readonly ISyncActivity? _sync;
    private readonly CatalogStore? _catalogs;
    private readonly StubEnricher? _enricher;
    private readonly ILogger<AdminPeerSettingsController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="AdminPeerSettingsController"/> class.
    /// </summary>
    /// <param name="state">The state store.</param>
    /// <param name="peers">The peers with their credentials.</param>
    /// <param name="client">The peer client (reads the key a server shows now).</param>
    /// <param name="registry">The runtime table of peers.</param>
    /// <param name="addresses">Changes a server address (a new host drops the token first).</param>
    /// <param name="secrets">The secret store.</param>
    /// <param name="libraries">Removes the Jellyfin libraries Shoal made.</param>
    /// <param name="paths">The data folder layout.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="gate">Tells whether a pairing is live for a server, or null.</param>
    /// <param name="sync">Tells whether a catalog sync of a server is running, or null.</param>
    /// <param name="catalogs">The catalogs (to find a removed server's title folders in shared libraries), or null.</param>
    /// <param name="enricher">Relabels the database items at once after an alias rename (2.9.2 K7), or null.</param>
    public AdminPeerSettingsController(IStateStore state, IPeerEndpoints peers, PeerClient client, PeerRegistry registry, PeerAddressService addresses, ISecretStore secrets, ShoalLibraryService libraries, ShoalPaths paths, ILogger<AdminPeerSettingsController> logger, IPairingGate? gate = null, ISyncActivity? sync = null, CatalogStore? catalogs = null, StubEnricher? enricher = null)
    {
        _sync = sync;
        _catalogs = catalogs;
        _enricher = enricher;
        _libraries = libraries;
        _paths = paths;
        _addresses = addresses;
        _secrets = secrets;
        _gate = gate;
        _state = state;
        _peers = peers;
        _client = client;
        _registry = registry;
        _logger = logger;
    }

    /// <summary>
    /// Gets the database relabel an alias rename started (2.9.2 K7), or null; it runs after the reply, so tests await it here.
    /// </summary>
    internal Task? Relabelling { get; private set; }

    /// <summary>
    /// Shows the key a server presents right now, so the admin can compare its fingerprint with the friend before trusting it (Warden 11 H1).
    /// Read-only: it changes nothing and sends nothing but an unauthenticated HEAD request. The answer is
    /// <c>{ pin, fingerprint, chainValid, pinnedFingerprint?, changed? }</c>: <c>pin</c> is the SHA-256 of the public key in base64 (what
    /// <c>expectedPin</c> must carry), <c>fingerprint</c> is the same hash as 32 colon-separated hex pairs for people to read out,
    /// <c>chainValid</c> says whether the certificate also passes the normal chain and host name checks, and <c>pinnedFingerprint</c> and
    /// <c>changed</c> are there when the server is pinned already.
    /// </summary>
    /// <param name="handle">The server handle.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>200, 400 (<c>notHttps</c>), 404, 409 (<c>recovery</c>) or 502 (<c>unreachable</c>).</returns>
    [HttpGet("Peers/{handle}/Certificate")]
    [Produces("application/json")]
    public async Task<IActionResult> Certificate(string handle, CancellationToken cancellationToken)
    {
        if (_state.Status != StateStatus.Ready)
        {
            return AdminReply.Error(StatusCodes.Status409Conflict, "recovery");
        }

        var peer = _state.Current.Home.Peers.FirstOrDefault(p => string.Equals(p.PeerKey, handle, StringComparison.Ordinal));
        if (peer is null || !Uri.TryCreate(peer.BaseUrl, UriKind.Absolute, out var uri))
        {
            return AdminReply.Error(StatusCodes.Status404NotFound, "notFound");
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal))
        {
            return AdminReply.Error(StatusCodes.Status400BadRequest, "notHttps");
        }

        var shown = await _client.ReadPresentedKeyAsync(PeerEndpoint.WithoutToken(handle, peer.DisplayName, uri, string.Empty, null).Configured(peer.Network, peer.ProxyUrl, peer.LanCleartextAck), cancellationToken).ConfigureAwait(false);
        if (shown is null)
        {
            return AdminReply.Error(StatusCodes.Status502BadGateway, "unreachable");
        }

        return AdminReply.Json(
            StatusCodes.Status200OK,
            new CertificateDto
            {
                Pin = shown.Pin,
                Fingerprint = TlsPin.Fingerprint(shown.Pin),
                ChainValid = shown.ChainValid,
                PinnedFingerprint = TlsPin.IsValid(peer.TlsSpkiPin) ? TlsPin.Fingerprint(peer.TlsSpkiPin!) : null,
                Changed = TlsPin.IsValid(peer.TlsSpkiPin) ? !string.Equals(peer.TlsSpkiPin, shown.Pin, StringComparison.Ordinal) : null,
            });
    }

    /// <summary>
    /// "Trust the new certificate" (DESIGN 4.4): the server's TLS key no longer matches the pinned one, and the admin says that is expected.
    /// The admin has seen the fingerprint (<see cref="Certificate"/>) and compared it with the friend, so the body carries it:
    /// <c>{ expectedPin, acceptUntrusted? }</c>. Shoal reads the key again and pins it only when it is the one that was shown (409
    /// <c>changedAgain</c> otherwise, with the new key). A Public server whose certificate does not pass the normal chain checks is refused
    /// (409 <c>untrusted</c>) unless <c>acceptUntrusted</c> is true. Nothing is sent but an unauthenticated HEAD request.
    /// </summary>
    /// <param name="handle">The server handle.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>200, 400 (<c>expectedPin</c>), 404, 409 (<c>recovery</c>, <c>notPinned</c>, <c>notChanged</c>, <c>changedAgain</c> or <c>untrusted</c>) or 502 (<c>unreachable</c>).</returns>
    [HttpPost("Peers/{handle}/TrustCertificate")]
    [Produces("application/json")]
    public async Task<IActionResult> TrustCertificate(string handle, CancellationToken cancellationToken)
    {
        if (_state.Status != StateStatus.Ready)
        {
            return AdminReply.Error(StatusCodes.Status409Conflict, "recovery");
        }

        var peer = _state.Current.Home.Peers.FirstOrDefault(p => string.Equals(p.PeerKey, handle, StringComparison.Ordinal));
        var endpoint = peer is null ? null : _peers.Find(handle);
        if (peer is null || endpoint is null)
        {
            return AdminReply.Error(StatusCodes.Status404NotFound, "notFound");
        }

        if (string.IsNullOrEmpty(peer.TlsSpkiPin))
        {
            return AdminReply.Error(StatusCodes.Status409Conflict, "notPinned");
        }

        if (_registry.StatusOf(handle) != PeerStatus.TlsKeyChanged)
        {
            return AdminReply.Error(StatusCodes.Status409Conflict, "notChanged");
        }

        var body = await AdminReply.ReadAsync<TrustCertificateRequest>(Request, cancellationToken).ConfigureAwait(false);
        if (body is null || !TlsPin.IsValid(body.ExpectedPin))
        {
            return AdminReply.Error(StatusCodes.Status400BadRequest, "expectedPin");
        }

        var shown = await _client.ReadPresentedKeyAsync(endpoint, cancellationToken).ConfigureAwait(false);
        if (shown is null)
        {
            return AdminReply.Error(StatusCodes.Status502BadGateway, "unreachable");
        }

        if (!string.Equals(shown.Pin, body.ExpectedPin, StringComparison.Ordinal))
        {
            return KeyRefused("changedAgain", shown);
        }

        if (peer.Network == PeerNetwork.Public && !shown.ChainValid && body.AcceptUntrusted != true)
        {
            return KeyRefused("untrusted", shown);
        }

        // The address may have changed while the key was being read: the key belongs to the address it was read from (Warden 11 M3).
        var applied = false;
        await _state.UpdateAsync(
            s =>
            {
                var target = s.Home.Peers.FirstOrDefault(p => string.Equals(p.PeerKey, handle, StringComparison.Ordinal));
                if (target is not null && !string.IsNullOrEmpty(target.TlsSpkiPin) && string.Equals(target.BaseUrl, peer.BaseUrl, StringComparison.Ordinal) && string.Equals(target.ProxyUrl, peer.ProxyUrl, StringComparison.Ordinal))
                {
                    target.TlsSpkiPin = shown.Pin;
                    applied = true;
                }
            },
            cancellationToken).ConfigureAwait(false);
        if (!applied)
        {
            return KeyRefused("changedAgain", shown);
        }

        _registry.BeginRetrust(handle); // token-bearing calls stay blocked until the next health check has confirmed the server id on the new key
        _logger.LogWarning("Admin trusted the new TLS key of peer {Peer} (chain valid: {Chain})", Redact.Id(handle), shown.ChainValid);
        return AdminReply.Json(StatusCodes.Status200OK, new { trusted = true });
    }

    /// <summary>
    /// Saves what the Server detail page edits (DESIGN 9.6): the stream limit, the priority, the address, the proxy and the certificate pin. A
    /// new <b>host</b> needs the server name typed back (<c>confirmName</c>) and drops the stored token first, so the server must be paired
    /// again (DESIGN 4.4, Warden N-8). The address, the network type and the proxy cannot change while a pairing session lives.
    /// </summary>
    /// <param name="handle">The server handle.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>200 with <c>needsPairing</c>, 400 (<c>invalid</c> with <c>detail</c>, <c>confirmName</c>, <c>expectedPin</c> or <c>pinNeedsHttps</c>), 404, 409 (<c>recovery</c>, <c>pairingActive</c>, <c>secrets</c>, <c>changedAgain</c> or <c>untrusted</c>) or 502 (the key could not be read).</returns>
    [HttpPut("Peers/{handle}")]
    [Produces("application/json")]
    public async Task<IActionResult> Update(string handle, CancellationToken cancellationToken)
    {
        if (_state.Status != StateStatus.Ready)
        {
            return AdminReply.Error(StatusCodes.Status409Conflict, "recovery");
        }

        var peer = _state.Current.Home.Peers.FirstOrDefault(p => string.Equals(p.PeerKey, handle, StringComparison.Ordinal));
        if (peer is null)
        {
            return AdminReply.Error(StatusCodes.Status404NotFound, "notFound");
        }

        var body = await AdminReply.ReadAsync<UpdatePeerRequest>(Request, cancellationToken).ConfigureAwait(false);
        if (body is null || body.MaxConcurrentStreams is < 1 or > 16 || body.Priority is < 1 or > 1000 || (body.Alias is not null && TextClean.Display(body.Alias, 40).Length != body.Alias.Trim().Length))
        {
            return AdminReply.Error(StatusCodes.Status400BadRequest, "invalid");
        }

        var shownBefore = peer.ShownName();
        var url = string.IsNullOrWhiteSpace(body.Url) ? peer.BaseUrl : body.Url.Trim();
        var proxy = body.ProxyUrl.ValueKind switch
        {
            JsonValueKind.String => string.IsNullOrWhiteSpace(body.ProxyUrl.GetString()) ? null : body.ProxyUrl.GetString()!.Trim(),
            JsonValueKind.Null => null,
            _ => peer.ProxyUrl,
        };
        var check = AddressPolicy.Validate(url, peer.Network, peer.LanCleartextAck, proxy);
        if (!check.Ok)
        {
            return new ShoalResult(StatusCodes.Status400BadRequest, new { error = "invalid", detail = check.Problem.ToString() });
        }

        var normalized = check.Address!.BaseUri.AbsoluteUri.TrimEnd('/');
        var urlChanged = !string.Equals(normalized, peer.BaseUrl, StringComparison.OrdinalIgnoreCase);
        var proxyChanged = !string.Equals(proxy ?? string.Empty, peer.ProxyUrl ?? string.Empty, StringComparison.Ordinal);
        if ((urlChanged || proxyChanged) && _gate?.IsLive(handle) == true)
        {
            return AdminReply.Error(StatusCodes.Status409Conflict, "pairingActive");
        }

        var hostChanged = urlChanged && !PeerAddressService.SameHost(peer.BaseUrl, normalized);
        if (hostChanged && !string.Equals(body.ConfirmName, peer.DisplayName, StringComparison.Ordinal))
        {
            return AdminReply.Error(StatusCodes.Status400BadRequest, "confirmName");
        }

        if (hostChanged && _secrets.Status != SecretStoreStatus.Ready)
        {
            return AdminReply.Error(StatusCodes.Status409Conflict, "secrets");
        }

        // The pin: a new host loses the old one, "pin" reads the key the server shows now, "don't pin" clears it.
        var pin = hostChanged ? null : peer.TlsSpkiPin;
        if (body.TlsPinned == false)
        {
            pin = null;
        }
        else if (body.TlsPinned == true && string.IsNullOrEmpty(pin))
        {
            if (!string.Equals(check.Address.BaseUri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal))
            {
                return AdminReply.Error(StatusCodes.Status400BadRequest, "pinNeedsHttps");
            }

            // The same flow as Trust: the admin saw this key (GET Certificate) and compared it with the friend; a Public server with a chain that is not valid needs a second yes.
            if (!TlsPin.IsValid(body.ExpectedPin))
            {
                return AdminReply.Error(StatusCodes.Status400BadRequest, "expectedPin");
            }

            var endpoint = PeerEndpoint.WithoutToken(handle, peer.DisplayName, check.Address.BaseUri, string.Empty, null).Configured(peer.Network, proxy, peer.LanCleartextAck);
            var shown = await _client.ReadPresentedKeyAsync(endpoint, cancellationToken).ConfigureAwait(false);
            if (shown is null)
            {
                return AdminReply.Error(StatusCodes.Status502BadGateway, "unreachable");
            }

            if (!string.Equals(shown.Pin, body.ExpectedPin, StringComparison.Ordinal))
            {
                return KeyRefused("changedAgain", shown);
            }

            if (peer.Network == PeerNetwork.Public && !shown.ChainValid && body.AcceptUntrusted != true)
            {
                return KeyRefused("untrusted", shown);
            }

            pin = shown.Pin;
        }

        // A new host: the session on the old one is ended first, while the token still exists (Warden 11 L5).
        bool? signedOut = null;
        if (hostChanged)
        {
            signedOut = await SignOutAsync(EndpointForSignOut(peer), cancellationToken).ConfigureAwait(false);
        }

        // One write for everything (Warden 11 L4). A new host drops the token before it; the pin is the one decided above.
        void Apply(PeerState target)
        {
            if (body.MaxConcurrentStreams is { } streams)
            {
                target.MaxConcurrentStreams = streams;
            }

            if (body.Priority is { } priority)
            {
                target.Priority = priority;
            }

            if (body.Alias is not null)
            {
                target.Alias = TextClean.Display(body.Alias, 40); // empty clears it: the display name is used again
            }

            target.MaxConcurrentTranscodes = Math.Min(target.MaxConcurrentTranscodes, target.MaxConcurrentStreams);
            target.ProxyUrl = proxy;
            target.TlsSpkiPin = pin;
        }

        var stored = peer.TlsSpkiPin;
        if (urlChanged || proxyChanged)
        {
            try
            {
                await _addresses.ChangeAddressAsync(handle, normalized, proxy, useProxy: true, Apply, cancellationToken).ConfigureAwait(false);
            }
            catch (PeerAddressChangeException ex)
            {
                return ex.Problem switch
                {
                    PeerAddressChangeProblem.UnknownPeer => AdminReply.Error(StatusCodes.Status404NotFound, "notFound"),
                    PeerAddressChangeProblem.PairingActive => AdminReply.Error(StatusCodes.Status409Conflict, "pairingActive"),
                    PeerAddressChangeProblem.SecretsUnreadable => AdminReply.Error(StatusCodes.Status409Conflict, "secrets"),
                    _ => new ShoalResult(StatusCodes.Status400BadRequest, new { error = "invalid", detail = ex.Detail.ToString() }),
                };
            }
        }
        else
        {
            await _state.UpdateAsync(s => Apply(s.Home.Peers.First(p => string.Equals(p.PeerKey, handle, StringComparison.Ordinal))), cancellationToken).ConfigureAwait(false);
        }

        var pinChanged = !string.Equals(pin ?? string.Empty, stored ?? string.Empty, StringComparison.Ordinal);
        if (pinChanged && !hostChanged && _registry.StatusOf(handle) == PeerStatus.TlsKeyChanged)
        {
            _registry.BeginRetrust(handle);
        }

        // 2.9.2 K7: a rename relabels the database items now (overview and the "Shared by" studios, also of grouped titles naming this friend);
        // the NFOs follow at the next sync (SyncRun sees the catalog's LabelName differ). It runs after the reply: thousands of items take a while.
        var shownAfter = _state.Current.Home.Peers.FirstOrDefault(p => string.Equals(p.PeerKey, handle, StringComparison.Ordinal))?.ShownName();
        if (_enricher is { } enricher && shownAfter is not null && !string.Equals(shownBefore, shownAfter, StringComparison.Ordinal))
        {
            Relabelling = Task.Run(() => RelabelAsync(enricher), CancellationToken.None);
        }

        _logger.LogInformation("Admin changed the settings of peer {Peer} (host changed {Host}, proxy changed {Proxy}, pin changed {Pin})", Redact.Id(handle), hostChanged, proxyChanged, pinChanged);
        return AdminReply.Json(StatusCodes.Status200OK, new { needsPairing = hostChanged, signedOut });
    }

    /// <summary>
    /// "Remove server" (DESIGN 9.6, ui-spec 8.3.8): signs out of the server (best effort), deletes its key, removes it from the settings and,
    /// with <c>deleteLibrary=true</c>, deletes its Jellyfin libraries, its stubs and its catalog. Without it the libraries, stubs and catalog stay
    /// where they are (the admin keeps the titles as they are; they stop playing). The server name must be typed back (<c>confirmName</c>).
    /// </summary>
    /// <param name="handle">The server handle.</param>
    /// <param name="deleteLibrary">Whether its libraries and files go too.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>200, 400 (<c>confirmName</c>), 404 or 409 (<c>recovery</c>, <c>pairingActive</c> or <c>syncing</c>: try again when the sync is done).</returns>
    [HttpDelete("Peers/{handle}")]
    [Produces("application/json")]
    public async Task<IActionResult> Remove(string handle, [FromQuery] bool deleteLibrary, CancellationToken cancellationToken)
    {
        if (_state.Status != StateStatus.Ready)
        {
            return AdminReply.Error(StatusCodes.Status409Conflict, "recovery");
        }

        var peer = _state.Current.Home.Peers.FirstOrDefault(p => string.Equals(p.PeerKey, handle, StringComparison.Ordinal));
        if (peer is null)
        {
            return AdminReply.Error(StatusCodes.Status404NotFound, "notFound");
        }

        var confirm = await AdminReply.ReadAsync<RemovePeerRequest>(Request, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(confirm?.ConfirmName, peer.DisplayName, StringComparison.Ordinal))
        {
            return AdminReply.Error(StatusCodes.Status400BadRequest, "confirmName");
        }

        // Removing a server ends its pairing: a live session is cancelled instead of refusing the removal.
        if (_gate?.IsLive(handle) == true)
        {
            try
            {
                await _gate.CancelAsync(handle, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // the pairing did not end within its wait: the removal says so instead of failing
            }

            if (_gate.IsLive(handle))
            {
                return AdminReply.Error(StatusCodes.Status409Conflict, "pairingActive");
            }
        }

        var wasEnabled = peer.Enabled;
        if (wasEnabled)
        {
            await _state.UpdateAsync(s => s.Home.Peers.First(p => string.Equals(p.PeerKey, handle, StringComparison.Ordinal)).Enabled = false, cancellationToken).ConfigureAwait(false);
        }

        // A running sync would recreate stubs and catalog files behind the removal. The server is disabled now, so no new one starts; the running one is told to
        // stop and given a few seconds. A server that is offline makes a sync wait on its calls, which this ends at the next checkpoint too.
        if (_sync?.IsSyncing(handle) == true)
        {
            await _sync.StopAsync(handle, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }

        if (_sync?.IsSyncing(handle) == true)
        {
            if (wasEnabled)
            {
                await _state.UpdateAsync(s => s.Home.Peers.First(p => string.Equals(p.PeerKey, handle, StringComparison.Ordinal)).Enabled = true, cancellationToken).ConfigureAwait(false);
            }

            return AdminReply.Error(StatusCodes.Status409Conflict, "syncing");
        }

        // Sign out first, while the key still exists; a server that cannot be reached keeps its session until its own idle rules end it.
        var signedOut = await SignOutAsync(EndpointForSignOut(peer), cancellationToken).ConfigureAwait(false);

        var stubRoot = _state.Current.StubRoot;
        var libraries = 0;
        var catalog = deleteLibrary && _catalogs is not null ? _catalogs.Load(handle) : null;
        foreach (var mapping in deleteLibrary ? peer.Libraries.ToList() : [])
        {
            // A shared library (K44) keeps its folder and the other connections' titles: only this server's recorded title folders go.
            if (LibraryFolders.FedBy(_state.Current, handle, mapping.CollectionType) is { } shared && LibraryFolders.PathOf(stubRoot, shared) is { } sharedFolder
                && catalog is { Unreadable: false })
            {
                LibraryTree.DeleteRecorded(stubRoot, sharedFolder, catalog.Document.Entries.Where(e => e.MappingKey == mapping.MappingKey), _logger);
            }

            if (await _libraries.RemoveAsync(handle, mapping.MappingKey, cancellationToken).ConfigureAwait(false))
            {
                libraries++;
            }

            DeleteFolder(Path.IsPathRooted(stubRoot) ? VisibilityService.MappingPath(stubRoot, handle, mapping.Subfolder) : null, stubRoot);
        }

        if (_secrets.Status == SecretStoreStatus.Ready)
        {
            await _secrets.RemoveAsync(handle, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            _logger.LogWarning("The key of a removed server could not be deleted because the secret store is not readable");
        }

        await _state.UpdateAsync(s => s.Home.Peers.RemoveAll(p => string.Equals(p.PeerKey, handle, StringComparison.Ordinal)), cancellationToken).ConfigureAwait(false);
        _registry.Forget(handle);
        _client.Forget(handle);
        if (deleteLibrary)
        {
            DeleteFolder(_paths.PeerDir(handle), _paths.Root);
        }

        _logger.LogWarning("Admin removed server {Peer}: {Libraries} libraries deleted, files {Files}", Redact.Id(handle), libraries, deleteLibrary ? "deleted" : "kept");
        return AdminReply.Json(StatusCodes.Status200OK, new { removed = true, librariesRemoved = libraries, signedOut });
    }

    private static ShoalResult KeyRefused(string word, PresentedKey shown) =>
        new(StatusCodes.Status409Conflict, new { error = word, pin = shown.Pin, fingerprint = TlsPin.Fingerprint(shown.Pin), chainValid = shown.ChainValid });

    // The endpoint with the token, also for a server that is paused or whose address the directory refuses: the session on it must end all the same.
    private async Task RelabelAsync(StubEnricher enricher)
    {
        try
        {
            var saved = await enricher.ApplyLabelsAsync(CancellationToken.None).ConfigureAwait(false);
            _logger.LogInformation("Relabelled {Count} items after a server rename", saved);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogWarning("The items could not be relabelled after a server rename ({Type}); the next library scan does it", ex.GetType().Name);
        }
    }

    private PeerEndpoint? EndpointForSignOut(PeerState peer)
    {
        if (_peers.Find(peer.PeerKey) is { } found)
        {
            return found;
        }

        return _secrets.TryGet(peer.PeerKey) is { } credential && Uri.TryCreate(peer.BaseUrl, UriKind.Absolute, out var uri)
            ? new PeerEndpoint(peer.PeerKey, peer.DisplayName, uri, credential.DeviceId, credential.Token).Configured(peer.Network, peer.ProxyUrl, peer.LanCleartextAck, peer.PinnedServerId, peer.TlsSpkiPin)
            : null;
    }

    // Best effort, as everywhere else Shoal signs out; the answer says whether it worked, so the page can tell the admin when it did not.
    private async Task<bool> SignOutAsync(PeerEndpoint? endpoint, CancellationToken cancellationToken)
    {
        if (endpoint is null)
        {
            return false;
        }

        try
        {
            using var response = await _client.SendAsync(endpoint, PeerRequest.Post("/Sessions/Logout", null).WithTrust(TrustRequirement.None), PeerCallProfile.SessionReport, cancellationToken).ConfigureAwait(false);
            return (int)response.StatusCode is >= 200 and < 300;
        }
        catch (PeerCallException)
        {
            return false;
        }
    }

    // Only Shoal's own folders, never through a link.
    private void DeleteFolder(string? folder, string root)
    {
        try
        {
            if (folder is not null && Directory.Exists(folder) && !PathSanitizer.HasLinkBelow(root, folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("A folder of a removed server could not be deleted ({Error})", ex.GetType().Name);
        }
    }
}

/// <summary>
/// What the Server detail page saves. A field that is absent is left as it is.
/// </summary>
internal sealed class UpdatePeerRequest
{
    /// <summary>Gets the most streams at once through this server.</summary>
    public int? MaxConcurrentStreams { get; init; }

    /// <summary>Gets the priority (lower wins).</summary>
    public int? Priority { get; init; }

    /// <summary>Gets the local name for the offline card and message: text sets it, an empty string clears it, absent keeps it.</summary>
    public string? Alias { get; init; }

    /// <summary>Gets the server address.</summary>
    public string? Url { get; init; }

    /// <summary>Gets the proxy: a string sets it, null or an empty string clears it, absent keeps it.</summary>
    public JsonElement ProxyUrl { get; init; }

    /// <summary>Gets a value indicating whether the certificate key is pinned: true pins the key shown now, false clears the pin, absent keeps it.</summary>
    public bool? TlsPinned { get; init; }

    /// <summary>Gets the server name typed back, needed for a new host.</summary>
    public string? ConfirmName { get; init; }

    /// <summary>Gets the pin of the key the admin was shown (<c>GET Certificate</c>); needed to pin an unpinned server.</summary>
    public string? ExpectedPin { get; init; }

    /// <summary>Gets a value indicating whether a certificate that fails the normal chain checks is accepted on a Public server.</summary>
    public bool? AcceptUntrusted { get; init; }
}

/// <summary>
/// The body of a removal: the server name typed back.
/// </summary>
internal sealed class RemovePeerRequest
{
    /// <summary>Gets the server name typed back.</summary>
    public string? ConfirmName { get; init; }
}

/// <summary>
/// The body of "Trust the new certificate": the key the admin was shown.
/// </summary>
internal sealed class TrustCertificateRequest
{
    /// <summary>Gets the pin of the key the admin saw (the <c>pin</c> of <c>GET Certificate</c>).</summary>
    public string? ExpectedPin { get; init; }

    /// <summary>Gets a value indicating whether a certificate that fails the normal chain checks is accepted on a Public server.</summary>
    public bool? AcceptUntrusted { get; init; }
}

/// <summary>
/// The key a server presents, for <c>GET Certificate</c>.
/// </summary>
internal sealed class CertificateDto
{
    /// <summary>Gets the SHA-256 of the public key in base64.</summary>
    public required string Pin { get; init; }

    /// <summary>Gets the same hash as colon-separated hex pairs.</summary>
    public required string Fingerprint { get; init; }

    /// <summary>Gets a value indicating whether the certificate also passes the normal chain and host name checks.</summary>
    public bool ChainValid { get; init; }

    /// <summary>Gets the fingerprint of the pinned key, when the server is pinned.</summary>
    public string? PinnedFingerprint { get; init; }

    /// <summary>Gets a value indicating whether the key differs from the pinned one, when the server is pinned.</summary>
    public bool? Changed { get; init; }
}
