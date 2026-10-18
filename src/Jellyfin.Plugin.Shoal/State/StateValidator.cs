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
    /// <summary>The most dismissed recommendations kept per connection (the newest).</summary>
    public const int MaxDismissed = 20;

    /// <summary>The highest throughput a dismissal may record, in Mbps.</summary>
    public const double MaxThroughputMbps = 100_000;

    /// <summary>The most libraries created by v2.9 or later (legacy libraries from an upgrade do not count).</summary>
    public const int MaxLibraries = 50;

    /// <summary>The most libraries of any kind, a bound on the size of the document.</summary>
    public const int MaxLibrariesTotal = 500;

    private const int MaxNameChars = 64;
    private const int MaxManagedTags = (2 * MaxLibrariesTotal) + 1;
    private const int MaxAliasChars = 40;
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

        var layoutFatal = ValidateLayout(state, notes);
        if (layoutFatal is not null)
        {
            return layoutFatal;
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
        peer.Alias = TextClean.Display(peer.Alias ?? string.Empty, MaxAliasChars);
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

        ValidateQuality(peer, notes);

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

    // PLAN v2.9 K41. A library key and folder name a place on disk that K44 creates and deletes in, so a malformed one refuses the file, like a
    // peer key or a subfolder. Everything else is repaired and noted, always toward showing less: an unknown feed is dropped, never invented.
    private static string? ValidateLayout(ShoalState state, ICollection<string> notes)
    {
        var home = state.Home;
        var peers = home.Peers.ToDictionary(static p => p.PeerKey, StringComparer.Ordinal);
        var libKeys = new HashSet<string>(StringComparer.Ordinal);
        var paths = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var fedTypes = new HashSet<(string Peer, string Type)>();
        var created = 0;
        if (home.Libraries.Count > MaxLibrariesTotal)
        {
            return "there are too many libraries";
        }

        // A legacy library whose server or mapping was removed (through the per-friend screens, which do not know about libraries yet) goes with it.
        var orphaned = home.Libraries.RemoveAll(l => l.IsLegacy && l.PathRel.Split('/') is [var peerKey, var sub]
            && ShoalPaths.IsValidPeerKey(peerKey) && SubfolderRegex().IsMatch(sub)
            && !(peers.TryGetValue(peerKey, out var owner) && owner.Libraries.Any(m => string.Equals(m.Subfolder, sub, StringComparison.Ordinal))));
        if (orphaned > 0)
        {
            notes.Add($"{orphaned} librar{(orphaned == 1 ? "y" : "ies")} of a removed server or library mapping {(orphaned == 1 ? "was" : "were")} removed from the layout.");
        }

        foreach (var library in home.Libraries)
        {
            if (!LibKeyRegex().IsMatch(library.LibKey))
            {
                return "a library key is not valid";
            }

            if (!libKeys.Add(library.LibKey))
            {
                return "a library key is used twice";
            }

            if (library.Type is not ("movies" or "tvshows"))
            {
                return "a library type is not valid";
            }

            string? legacyPeer = null;
            if (library.IsLegacy)
            {
                var parts = library.PathRel.Split('/');
                if (parts.Length != 2 || !peers.TryGetValue(parts[0], out var owner) || !SubfolderRegex().IsMatch(parts[1])
                    || !owner.Libraries.Any(m => string.Equals(m.Subfolder, parts[1], StringComparison.Ordinal)))
                {
                    return "a library folder is not valid";
                }

                legacyPeer = parts[0];
            }
            else
            {
                created++;
                if (!string.Equals(library.PathRel, ShoalLibrary.LibsFolder + "/" + library.LibKey, StringComparison.Ordinal))
                {
                    return "a library folder is not valid";
                }
            }

            if (!paths.Add(library.PathRel))
            {
                return "a library folder is used twice";
            }

            if (!Enum.IsDefined(library.Placement))
            {
                return "a library placement is not valid";
            }

            library.Name = Clean(library.Name, "library name", notes);
            library.Access.SelectedUserIds = library.Access.SelectedUserIds.Distinct().ToList();

            // Feeds: known peers only, once each; a legacy library is fed only by its own peer; a peer feeds at most one non-legacy library per type.
            var kept = new List<string>();
            foreach (var feed in library.Feeds.Distinct(StringComparer.Ordinal))
            {
                var ok = peers.ContainsKey(feed)
                    && (legacyPeer is null ? fedTypes.Add((feed, library.Type)) : string.Equals(feed, legacyPeer, StringComparison.Ordinal));
                if (ok)
                {
                    kept.Add(feed);
                }
            }

            if (kept.Count != library.Feeds.Count)
            {
                notes.Add($"A feed of library '{library.Name}' named a server that is gone or already feeds another library of that type, and was removed.");
            }

            library.Feeds = kept;
        }

        if (created > MaxLibraries)
        {
            return "there are too many libraries";
        }

        // Order: known keys once, in the admin's order; libraries it does not name yet go last.
        var order = home.LibraryOrder.Where(libKeys.Contains).Distinct(StringComparer.Ordinal).ToList();
        order.AddRange(home.Libraries.Select(static l => l.LibKey).Where(k => !order.Contains(k, StringComparer.Ordinal)));
        if (!order.SequenceEqual(home.LibraryOrder, StringComparer.Ordinal))
        {
            home.LibraryOrder = order;
            notes.Add("The library order was brought up to date.");
        }

        if (!Enum.IsDefined(home.Layout.Default))
        {
            home.Layout.Default = LayoutPreset.Custom;
        }

        var byKey = home.Libraries.ToDictionary(static l => l.LibKey, StringComparer.Ordinal);
        var targets = home.Layout.DefaultTargets
            .Where(t => t.Key is "movies" or "tvshows" && byKey.TryGetValue(t.Value, out var target) && target.Type == t.Key && !target.IsLegacy)
            .ToDictionary(static t => t.Key, static t => t.Value, StringComparer.Ordinal);
        if (targets.Count != home.Layout.DefaultTargets.Count)
        {
            notes.Add("A default library for new servers no longer exists and was cleared.");
        }

        home.Layout.DefaultTargets = targets;

        foreach (var peer in home.Peers)
        {
            if (!Enum.IsDefined(peer.PriorityTier))
            {
                peer.PriorityTier = PriorityTier.Normal;
            }

            var rank = peer.AutoRank;
            rank.Score = double.IsFinite(rank.Score) ? Math.Clamp(rank.Score, 0, MaxThroughputMbps) : 0;
            rank.Streak = Clamp(rank.Streak, 0, 1000, "ranking streak", notes);
            rank.Position = Clamp(rank.Position, 0, 10_000, "ranking position", notes);
            if (rank.PendingChallenger is { } challenger && (!peers.ContainsKey(challenger) || string.Equals(challenger, peer.PeerKey, StringComparison.Ordinal)))
            {
                rank.PendingChallenger = null;
                rank.Streak = 0;
                rank.ChallengeUtc = null;
            }
        }

        // K45: the record of the BlockedTags Shoal added holds Shoal's own tag kinds only, so removing what it recorded can never take an admin's tag away.
        var visibility = home.Visibility;
        visibility.ManagedBlockedTags ??= [];
        visibility.HiddenFriendTitles ??= [];
        foreach (var userId in visibility.ManagedBlockedTags.Keys.ToList())
        {
            var tags = (visibility.ManagedBlockedTags[userId] ?? []).Where(static t => t is not null && LibraryFolders.IsShoalTag(t) && t.Length <= 64).Distinct(StringComparer.Ordinal).Take(MaxManagedTags).ToList();
            if (userId == Guid.Empty || tags.Count == 0)
            {
                visibility.ManagedBlockedTags.Remove(userId);
                continue;
            }

            visibility.ManagedBlockedTags[userId] = tags;
        }

        visibility.HiddenFriendTitles = visibility.HiddenFriendTitles.Where(static id => id != Guid.Empty).Distinct().ToList();

        // K49: bookkeeping only; repaired, never fatal.
        home.Inbox ??= new InboxState();
        home.Inbox.TargetUserIds = (home.Inbox.TargetUserIds ?? []).Where(static id => id != Guid.Empty).Distinct().Take(10_000).ToList();
        if (home.Inbox.ExplainerMessageId == Guid.Empty)
        {
            home.Inbox.ExplainerMessageId = null;
        }

        return null;
    }

    // PLAN v2.9 K50. A limit only ever narrows what plays, so a bad value is repaired, never fatal: an unknown class cannot get here (the enum
    // reader refuses it), the Mbps is clamped, and an impossible dismissal is dropped.
    private static void ValidateQuality(PeerState peer, ICollection<string> notes)
    {
        peer.QualityLimit ??= new QualityLimit();
        var limit = peer.QualityLimit;
        if (!Enum.IsDefined(limit.MaxClass))
        {
            limit.MaxClass = QualityClass.Off;
            notes.Add($"The quality limit of '{peer.DisplayName}' was not valid and was switched off.");
        }

        if (!Enum.IsDefined(limit.Origin))
        {
            limit.Origin = QualityLimitOrigin.Manual;
        }

        if (limit.MaxMbps is { } mbps)
        {
            limit.MaxMbps = Clamp(mbps, QualityLimit.MinMbps, QualityLimit.MaxMbpsCap, "quality limit Mbps", notes);
        }

        peer.Dismissed ??= [];
        var before = peer.Dismissed.Count;
        peer.Dismissed = peer.Dismissed
            .Where(static d => d is not null && Enum.IsDefined(d.SuggestedClass) && d.SuggestedClass != QualityClass.Off && double.IsFinite(d.ThroughputMbps) && d.ThroughputMbps >= 0 && d.ThroughputMbps <= MaxThroughputMbps)
            .OrderBy(static d => d.DismissedUtc)
            .TakeLast(MaxDismissed)
            .ToList();
        if (peer.Dismissed.Count != before)
        {
            notes.Add($"Old or invalid dismissed recommendations of '{peer.DisplayName}' were removed.");
        }
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

    [GeneratedRegex(@"\A[0-9a-f]{16}\z", RegexOptions.CultureInvariant)]
    private static partial Regex LibKeyRegex();
}
