using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.Shoal.Storage;
using Jellyfin.Plugin.Shoal.Text;

namespace Jellyfin.Plugin.Shoal.State;

/// <summary>
/// Checks a loaded or changed state and repairs what can be repaired safely (DESIGN 3.7).
/// <para>
/// Two outcomes. A <b>fatal</b> problem means the document cannot be trusted (a value that names a folder or a secret entry is
/// malformed, or identifiers collide): the candidate is refused. Everything else is repaired in place and noted: numbers are
/// clamped, text is cleaned, and a peer whose security settings are malformed is <b>disabled</b>, never silently loosened.
/// </para>
/// The validator never throws.
/// </summary>
internal static partial class StateValidator
{
    private const int MaxNameChars = 64;
    private const int MaxUrlChars = 2048;

    /// <summary>
    /// Validates and normalizes <paramref name="state"/> in place.
    /// </summary>
    /// <param name="state">The state.</param>
    /// <param name="notes">Receives one short note per adjustment (no secrets, no raw values).</param>
    /// <returns>A fixed description of the first fatal problem, or null when the state is usable.</returns>
    public static string? Validate(ShoalState state, ICollection<string> notes)
    {
        try
        {
            return ValidateCore(state, notes);
        }
        catch (Exception ex) when (ex is NullReferenceException or InvalidOperationException or ArgumentException)
        {
            return "state document is malformed";
        }
    }

    private static string? ValidateCore(ShoalState state, ICollection<string> notes)
    {
        if (state.Schema != ShoalState.CurrentSchema)
        {
            return "unsupported schema";
        }

        state.StubRoot = TextClean.Display(state.StubRoot, 1024);

        var peerKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var peer in state.Home.Peers)
        {
            if (!ShoalPaths.IsValidPeerKey(peer.PeerKey))
            {
                return "a peer key is not valid";
            }

            if (!peerKeys.Add(peer.PeerKey))
            {
                return "a peer key is used twice";
            }

            var fatal = ValidatePeer(peer, notes);
            if (fatal is not null)
            {
                return fatal;
            }
        }

        var shareIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var share in state.PeerRole.Shares)
        {
            if (!HandleRegex().IsMatch(share.ShareId))
            {
                return "a share id is not valid";
            }

            if (!shareIds.Add(share.ShareId))
            {
                return "a share id is used twice";
            }

            if (share.ExpectedDeviceIds.Any(id => !DeviceIdRegex().IsMatch(id)))
            {
                return "a device id is not valid";
            }

            share.FriendName = Clean(share.FriendName, "share friend name", notes);
            share.MaxConcurrentTranscodes = Clamp(share.MaxConcurrentTranscodes, 1, 16, "share transcode cap", notes);
            if (share.MaxBitrateMbps is { } mbps)
            {
                share.MaxBitrateMbps = Clamp(mbps, 1, 100_000, "share bitrate limit", notes);
            }

            share.LibraryIds = share.LibraryIds.Distinct().ToList();
            if (share.HomeServerIdPrefix.Length > 0 && !HomePrefixRegex().IsMatch(share.HomeServerIdPrefix))
            {
                share.HomeServerIdPrefix = string.Empty; // display only: a bad value is dropped, never fatal
                notes.Add("A share's Home server id prefix was not valid and was cleared.");
            }

            share.RotationCount = Clamp(share.RotationCount, 0, 1_000_000, "share rotation count", notes);
        }

        return null;
    }

    private static string? ValidatePeer(PeerState peer, ICollection<string> notes)
    {
        var name = TextClean.Display(peer.DisplayName, MaxNameChars);
        if (name.Length == 0)
        {
            name = "Server " + peer.PeerKey[..4];
            notes.Add("A peer had no name; it was named after its key.");
        }

        peer.DisplayName = name;
        peer.MaxConcurrentStreams = Clamp(peer.MaxConcurrentStreams, 1, 16, "peer stream limit", notes);
        peer.MaxConcurrentTranscodes = Math.Min(Clamp(peer.MaxConcurrentTranscodes, 1, 16, "peer conversion limit", notes), peer.MaxConcurrentStreams);
        peer.Priority = Clamp(peer.Priority, 0, 1000, "peer priority", notes);
        if (peer.RotationDays is not (0 or 30 or 60 or 90))
        {
            peer.RotationDays = 0;
            notes.Add($"The rotation schedule of '{peer.DisplayName}' was not valid and was switched off.");
        }

        // Malformed security settings disable the peer: never drop a pin or loosen a rule, and never refuse the whole file for it.
        var problem =
            (peer.BaseUrl.Length > MaxUrlChars || peer.BaseUrl.Any(char.IsControl)) ? "its address is not valid"
            : (peer.ProxyUrl is { } proxy && (proxy.Length > MaxUrlChars || proxy.Any(char.IsControl))) ? "its proxy address is not valid"
            : (peer.PinnedServerId is { } id && !ServerIdRegex().IsMatch(id)) ? "its pinned server id is not valid"
            : (peer.TlsSpkiPin is { } pin && !SpkiPinRegex().IsMatch(pin)) ? "its certificate pin is not valid"
            : null;
        if (problem is not null && peer.Enabled)
        {
            peer.Enabled = false;
            notes.Add($"Peer '{peer.DisplayName}' was disabled because {problem}.");
        }

        var mappingKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var library in peer.Libraries)
        {
            if (!HandleRegex().IsMatch(library.MappingKey))
            {
                return "a library mapping key is not valid";
            }

            if (!mappingKeys.Add(library.MappingKey))
            {
                return "a library mapping key is used twice";
            }

            // The subfolder becomes a folder name below the stub root, so it must be one plain path segment.
            if (!SubfolderRegex().IsMatch(library.Subfolder))
            {
                return "a library subfolder is not a plain folder name";
            }

            if (!CollectionTypeRegex().IsMatch(library.CollectionType))
            {
                return "a library collection type is not valid";
            }

            library.RemoteName = Clean(library.RemoteName, "library remote name", notes);
            library.LocalLibraryName = Clean(library.LocalLibraryName, "library name", notes);
            library.Access.SelectedUserIds = library.Access.SelectedUserIds.Distinct().ToList();
        }

        return null;
    }

    private static string Clean(string value, string what, ICollection<string> notes)
    {
        var cleaned = TextClean.Display(value, MaxNameChars * 2);
        if (!string.Equals(cleaned, value, StringComparison.Ordinal))
        {
            notes.Add($"A {what} was cleaned (control characters removed or text shortened).");
        }

        return cleaned;
    }

    private static int Clamp(int value, int min, int max, string what, ICollection<string> notes)
    {
        var clamped = Math.Clamp(value, min, max);
        if (clamped != value)
        {
            notes.Add($"The {what} was out of range ({min} to {max}) and was adjusted.");
        }

        return clamped;
    }

    [GeneratedRegex(@"\A[A-Za-z0-9_-]{1,64}\z", RegexOptions.CultureInvariant)]
    private static partial Regex HandleRegex();

    [GeneratedRegex(@"\A[A-Za-z0-9._-]{1,64}\z", RegexOptions.CultureInvariant)]
    private static partial Regex DeviceIdRegex();

    [GeneratedRegex(@"\A[0-9a-f]{32}\z", RegexOptions.CultureInvariant)]
    private static partial Regex ServerIdRegex();

    [GeneratedRegex(@"\A[0-9a-f]{12}\z", RegexOptions.CultureInvariant)]
    private static partial Regex HomePrefixRegex();

    [GeneratedRegex(@"\A[A-Za-z0-9+/]{43}=\z", RegexOptions.CultureInvariant)]
    private static partial Regex SpkiPinRegex();

    [GeneratedRegex(@"\A[A-Za-z0-9](?:[A-Za-z0-9 ._()-]{0,62}[A-Za-z0-9_()-])?\z", RegexOptions.CultureInvariant)]
    private static partial Regex SubfolderRegex();

    [GeneratedRegex(@"\A[a-z]{1,32}\z", RegexOptions.CultureInvariant)]
    private static partial Regex CollectionTypeRegex();
}
