using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.Shoal.Authorization;
using Jellyfin.Plugin.Shoal.Playback;
using Jellyfin.Plugin.Shoal.Sharing;
using Jellyfin.Plugin.Shoal.State;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.Shoal.LocalRemoval;

/// <summary>Fresh, scoped, read-only physical-source evidence. Register as a singleton.</summary>
public sealed class RemovalOriginProof : IDisposable
{
    private readonly Func<Guid, string, Guid, RemovalOriginSource?> _read;
    private readonly Func<string, string, CancellationToken, Task<RemovalFileSnapshot>> _probe;
    private readonly RateLimiter _perShare;
    private readonly RateLimiter _global;
    private readonly SemaphoreSlim _worker = new(1, 1);
    private readonly TimeSpan _ceiling;

    /// <summary>Initializes a new instance of the <see cref="RemovalOriginProof"/> class.</summary>
    /// <param name="state">Current enabled shares.</param>
    /// <param name="users">Live sharing accounts, policy and token devices.</param>
    /// <param name="library">Core's indexed movies and collection folders.</param>
    /// <param name="accounts">Current core account permissions.</param>
    /// <param name="visible">Current user-specific item visibility.</param>
    /// <param name="federated">Authoritative Shoal classification.</param>
    /// <param name="sessions">Current playback sessions.</param>
    /// <param name="time">The rate-limit clock.</param>
    public RemovalOriginProof(IStateStore state, IShareUserSource users, ILibraryManager library, IUserManager accounts, IVisibleItems visible, IFederatedItems federated, ISessionManager sessions, TimeProvider time)
        : this((user, token, item) => ReadSource(state, users, library, accounts, visible, federated, sessions, user, token, item), new PortableFileProbe().ProbeAsync, time)
    {
        // 2.10.4 K1: the portable probe (any filesystem, incl. SMB/CIFS). The native RemovalFiles probe refused every network share.
    }

    /// <summary>Initializes a new instance of the <see cref="RemovalOriginProof"/> class for controlled source tests.</summary>
    /// <param name="read">Current admitted source lookup.</param>
    /// <param name="probe">Fresh file probing.</param>
    /// <param name="time">The rate-limit clock.</param>
    internal RemovalOriginProof(Func<Guid, string, Guid, RemovalOriginSource?> read, Func<string, string, CancellationToken, Task<RemovalFileSnapshot>> probe, TimeProvider time)
        : this(read, probe, time, FriendProofCeiling)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="RemovalOriginProof"/> class with a controlled outer deadline.</summary>
    /// <param name="read">Current admitted source lookup.</param>
    /// <param name="probe">Fresh file probing.</param>
    /// <param name="time">The rate-limit clock.</param>
    /// <param name="ceiling">The outer bound of one proof.</param>
    internal RemovalOriginProof(Func<Guid, string, Guid, RemovalOriginSource?> read, Func<string, string, CancellationToken, Task<RemovalFileSnapshot>> probe, TimeProvider time, TimeSpan ceiling)
    {
        _read = read;
        _probe = probe;
        _ceiling = ceiling;
        _perShare = new RateLimiter(time, perMinute: 6, burst: 3);
        _global = new RateLimiter(time, perMinute: 12, burst: 6);
    }

    /// <summary>Gets the outer bound of one friend-side proof: the scaled hashing deadline of the largest supported file (130 minutes for 100 GiB, RemovalPolicy.DeleteHashingMaximum).</summary>
    internal static TimeSpan FriendProofCeiling { get; } = RemovalPolicy.DeleteHashingMaximum;

    /// <inheritdoc />
    public void Dispose() => _worker.Dispose();

    /// <summary>Returns fresh evidence only while the caller and exact file remain admitted.</summary>
    /// <param name="userId">The core-authenticated sharing account.</param>
    /// <param name="token">The core-authenticated live token.</param>
    /// <param name="itemId">The current physical movie identifier.</param>
    /// <param name="cancellationToken">Client cancellation.</param>
    /// <returns>A fixed proof result, never exception text.</returns>
    internal async Task<RemovalOriginResult> ReadAsync(Guid userId, string token, Guid itemId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (userId == Guid.Empty || itemId == Guid.Empty || string.IsNullOrEmpty(token))
        {
            return new(404);
        }

        var source = ReadCurrent(userId, token, itemId);
        if (source is null)
        {
            return new(404);
        }

        if (!_perShare.TryTake(userId) || !_global.TryTake(Guid.Empty))
        {
            return new(429, RetryAfterSeconds: 10);
        }

        if (!await _worker.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return new(429, RetryAfterSeconds: 2);
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            // 2.10.4 K1: was a fixed 2 minutes, which capped provable files near 13 GB on gigabit SMB. The probe applies the size-scaled
            // deadline (5 min + 75 s per GiB) once it has the length; this outer bound is the largest of those (130 min), so the single
            // worker slot can never be held longer, and the rate limits above are unchanged.
            timeout.CancelAfter(_ceiling);
            var file = await _probe(source.Root, source.Path, timeout.Token).ConfigureAwait(false);
            var current = ReadCurrent(userId, token, itemId);
            if (current is null)
            {
                return new(404);
            }

            if (current.Generation != source.Generation || current.Path != source.Path || current.Root != source.Root
                || file.FullPath != source.Path || file.Root != source.Root || !RemovalPolicy.DeleteSizeAllowed(file.Length)
                || file.Sha256.Length != 64 || !file.Sha256.All(static c => c is >= '0' and <= '9' or >= 'a' and <= 'f') || string.IsNullOrEmpty(file.Identity))
            {
                return new(409);
            }

            var generation = Digest(source.Generation + "\n" + file.Identity + "\n" + file.Length.ToString(CultureInfo.InvariantCulture) + "\n" + file.Sha256);
            return new(200, new RemovalOriginDto(1, file.Sha256, file.Length, source.ProviderIds, source.RuntimeTicks, generation));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(409);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return new(409);
        }
        finally
        {
            _worker.Release();
        }
    }

    /// <summary>Rejects relayed, multipart, alternate and externally supplemented movie shapes.</summary>
    /// <param name="movie">The indexed movie.</param>
    /// <returns>Whether its shape can qualify for a single physical-file proof.</returns>
    internal static bool SupportsMovieShape(Movie movie) => !movie.IsShortcut && !movie.IsPlaceHolder && movie.VideoType == VideoType.VideoFile
        && movie.Path is { Length: > 0 } path && Path.IsPathFullyQualified(path) && !path.EndsWith(".strm", StringComparison.OrdinalIgnoreCase)
        && movie.PrimaryVersionId is null && !movie.HasLocalAlternateVersions && movie.LinkedAlternateVersions.Length == 0
        && movie.SubtitleFiles.Length == 0 && movie.AudioFiles.Length == 0;

    private static string Digest(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private RemovalOriginSource? ReadCurrent(Guid userId, string token, Guid itemId)
    {
        try
        {
            return _read(userId, token, itemId);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    private static RemovalOriginSource? ReadSource(IStateStore state, IShareUserSource users, ILibraryManager library, IUserManager accounts, IVisibleItems visible, IFederatedItems federated, ISessionManager sessions, Guid userId, string token, Guid itemId)
    {
        if (state.Status != StateStatus.Ready)
        {
            return null;
        }

        var snapshot = state.Current;
        var shares = snapshot.PeerRole.Shares.Where(s => s.ShareUserId == userId).Take(2).ToArray();
        var share = shares.Length == 1 ? shares[0] : null;
        var account = accounts.GetUserById(userId);
        if (share is null || !share.Enabled || account is null || account.HasPermission(PermissionKind.IsAdministrator)
            || !users.Exists(userId) || users.DeviceIdOfToken(userId, token) is not { } device
            || !share.ExpectedDeviceIds.Contains(device, StringComparer.Ordinal) || !users.PolicyMatches(userId, ShareService.PolicyOf(share, snapshot)))
        {
            return null;
        }

        var found = library.GetItemList(new InternalItemsQuery(account) { ItemIds = [itemId], Recursive = true, Limit = 2 });
        if (found.Count != 1 || found[0] is not Movie movie || !SupportsMovieShape(movie) || movie.GetAdditionalParts(account).Any()
            || federated.Find(itemId) is not null || !visible.IsVisible(userId, itemId)
            || sessions.Sessions.Any(s => s.NowPlayingItem?.Id == itemId))
        {
            return null;
        }

        var granted = share.LibraryIds.ToHashSet();
        var parents = library.GetCollectionFolders(movie).Select(static f => f.Id).Where(granted.Contains).ToHashSet();
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var path = Path.GetFullPath(movie.Path);
        var roots = library.GetVirtualFolders().Where(v => Guid.TryParse(v.ItemId, out var id) && parents.Contains(id))
            .SelectMany(static v => v.Locations).Select(static r => Path.TrimEndingDirectorySeparator(Path.GetFullPath(r)))
            .Where(r => path.StartsWith(Path.TrimEndingDirectorySeparator(r) + Path.DirectorySeparatorChar, comparison)).Distinct().ToArray();
        if (roots.Length != 1)
        {
            return null;
        }

        var ids = StrongIds(movie.ProviderIds);
        if (ids is null || ids.Count == 0 || movie.RunTimeTicks is not > 0)
        {
            return null;
        }

        var generation = Digest(share.ShareId + "\n" + device + "\n" + string.Join(',', share.LibraryIds.Order()) + "\n" + path + "\n"
            + movie.RunTimeTicks.Value.ToString(CultureInfo.InvariantCulture) + "\n" + string.Join(',', ids.OrderBy(static p => p.Key, StringComparer.OrdinalIgnoreCase).Select(static p => p.Key.ToLowerInvariant() + "=" + p.Value)));
        return new(roots[0], path, ids, movie.RunTimeTicks.Value, generation);
    }

    private static Dictionary<string, string>? StrongIds(IReadOnlyDictionary<string, string> providers)
    {
        var ids = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in new[] { "Tmdb", "Tvdb", "Imdb" })
        {
            var found = providers.Where(p => string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
            if (found.Length > 1)
            {
                return null;
            }

            if (found.Length == 0)
            {
                continue;
            }

            var value = found[0].Value.Trim().ToLowerInvariant();
            if (key == "Imdb" && value.Length is >= 3 and <= 12 && value.StartsWith("tt", StringComparison.Ordinal)
                && value.AsSpan(2).IndexOfAnyExceptInRange('0', '9') < 0)
            {
                ids[key] = value;
            }
            else if (key != "Imdb" && long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number > 0)
            {
                ids[key] = number.ToString(CultureInfo.InvariantCulture);
            }
            else
            {
                return null;
            }
        }

        return ids;
    }
}

/// <summary>Internal current source facts; paths never leave the proof service.</summary>
/// <param name="Root">The current granted physical library root.</param>
/// <param name="Path">The current indexed ordinary file.</param>
/// <param name="ProviderIds">Current canonical strong identifiers.</param>
/// <param name="RuntimeTicks">The current actual duration.</param>
/// <param name="Generation">An opaque digest of the current share and item facts.</param>
internal sealed record RemovalOriginSource(string Root, string Path, IReadOnlyDictionary<string, string> ProviderIds, long RuntimeTicks, string Generation);

/// <summary>Peer-only proof with no local path, token or user identifier.</summary>
/// <param name="V">The proof format version.</param>
/// <param name="ContentSha256">The full current file digest.</param>
/// <param name="Length">The actual file length.</param>
/// <param name="ProviderIds">Current canonical strong identifiers.</param>
/// <param name="RuntimeTicks">Current actual duration.</param>
/// <param name="Generation">The opaque share/item/file generation.</param>
internal sealed record RemovalOriginDto(int V, string ContentSha256, long Length, IReadOnlyDictionary<string, string> ProviderIds, long RuntimeTicks, string Generation);

/// <summary>A fixed proof outcome that never carries exception or path text.</summary>
/// <param name="Status">The fixed HTTP outcome.</param>
/// <param name="Proof">The admitted proof, if any.</param>
/// <param name="RetryAfterSeconds">The bounded retry delay.</param>
internal sealed record RemovalOriginResult(int Status, RemovalOriginDto? Proof = null, int RetryAfterSeconds = 0);
