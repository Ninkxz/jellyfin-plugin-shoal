using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.Authorization;
using Jellyfin.Plugin.Shoal.State;
using Jellyfin.Plugin.Shoal.Sync;
using LayoutLibrary = Jellyfin.Plugin.Shoal.State.ShoalLibrary;

namespace Jellyfin.Plugin.Shoal.Visibility;

/// <summary>
/// A library a preset would create.
/// </summary>
/// <param name="Name">Its name.</param>
/// <param name="Type"><c>movies</c> or <c>tvshows</c>.</param>
public sealed record PresetNewLibrary(string Name, string Type);

/// <summary>
/// A library a preset would remove (no connection is left in it).
/// </summary>
/// <param name="LibKey">The library.</param>
/// <param name="Name">Its name.</param>
public sealed record PresetRemovedLibrary(string LibKey, string Name);

/// <summary>
/// One connection a preset would move.
/// </summary>
/// <param name="PeerKey">The connection's handle.</param>
/// <param name="PeerName">Its name (text only).</param>
/// <param name="Type"><c>movies</c> or <c>tvshows</c>.</param>
/// <param name="From">The names of the libraries its titles are in now.</param>
/// <param name="To">The name of the library they go into.</param>
public sealed record PresetMove(string PeerKey, string PeerName, string Type, IReadOnlyList<string> From, string To);

/// <summary>
/// What applying a preset would do (<c>POST Shoal/Admin/Layouts/Preview</c>): nothing changes until Apply.
/// </summary>
/// <param name="Preset">The preset.</param>
/// <param name="Created">The libraries it creates.</param>
/// <param name="Removed">The libraries it removes.</param>
/// <param name="Moves">The connections it moves.</param>
/// <param name="TitlesCollapsing">How many titles that show in two or more libraries today would show in fewer.</param>
/// <param name="UsersLosingAccess">The accounts that can open a connection's titles today and could not afterwards (a combined library admits only what all its
/// sources admit).</param>
/// <param name="UsersGainingAccess">The accounts that can open a moved connection's titles afterwards and could not before (Warden 10 M1: reported, never blocked).</param>
/// <param name="RestrictedGainingAccess">Those of them that are restricted profiles.</param>
/// <param name="PlanHash">A hash of this plan; Apply must send it back and is refused when the plan it would run is not this one (Warden 10 L3).</param>
public sealed record PresetPreview(LayoutPreset Preset, IReadOnlyList<PresetNewLibrary> Created, IReadOnlyList<PresetRemovedLibrary> Removed, IReadOnlyList<PresetMove> Moves, int TitlesCollapsing, IReadOnlyList<Guid> UsersLosingAccess, IReadOnlyList<Guid> UsersGainingAccess, IReadOnlyList<Guid> RestrictedGainingAccess, string PlanHash)
{
    /// <summary>Gets the main library chosen or to choose per type (Combine into my libraries only; empty for the other presets).</summary>
    public IReadOnlyList<PresetTargetChoice> Targets { get; init; } = [];

    /// <summary>Gets a value indicating whether the admin must pick a main library for some type before Apply (Apply answers targetRequired until then).</summary>
    public bool NeedsTargets { get; init; }

    /// <summary>Gets the chosen main libraries whose NFO saver is on (names only): the admin should turn it off before friends' titles go in.</summary>
    public IReadOnlyList<string> TargetsSavingNfo { get; init; } = [];

    /// <summary>
    /// Gets the chosen main libraries whose NFO saver or internet metadata is on (Warden 13 L6; names only): friends' titles in them can be rewritten by the admin's
    /// metadata providers, and they stay hidden from accounts the library does not admit only through the shoal-lib tag Shoal puts back.
    /// </summary>
    public IReadOnlyList<string> TargetsRewritingTitles { get; init; } = [];

    /// <summary>Gets the libraries holding a Shoal folder beside other folders, which Shoal does not lock (Warden 12 L2; names only).</summary>
    public IReadOnlyList<string> ShoalLibrariesNotLocked { get; init; } = [];
}

/// <summary>
/// A main library the admin may pick for Combine into my libraries.
/// </summary>
/// <param name="Id">The Jellyfin library id.</param>
/// <param name="Name">Its name (text only).</param>
/// <param name="SavesNfo">Whether its NFO saver is on.</param>
public sealed record PresetTargetCandidate(Guid Id, string Name, bool SavesNfo);

/// <summary>
/// The main library of one type for Combine into my libraries.
/// </summary>
/// <param name="Type"><c>movies</c> or <c>tvshows</c>.</param>
/// <param name="Chosen">The library chosen (sent by the admin, or the only candidate), or null when the admin must pick one.</param>
/// <param name="Candidates">The libraries that may be picked: the admin's own libraries of the type, never a Shoal library or one holding only Shoal folders.</param>
/// <param name="Rejected">Whether the library the admin sent is not one of the candidates.</param>
public sealed record PresetTargetChoice(string Type, Guid? Chosen, IReadOnlyList<PresetTargetCandidate> Candidates, bool Rejected);

/// <summary>
/// The layout presets (PLAN v2.9 K44): All mixed (one library per type for every friend), One per friend (one library per friend and type), Custom (the admin
/// arranges by hand; nothing moves). Preview says what Apply would do; Apply starts the move job with the same plan and remembers the preset for new connections.
/// </summary>
public sealed class LayoutPresetService
{
    /// <summary>The name of the All mixed movie library.</summary>
    public const string MixedMovies = "Friends' movies";

    /// <summary>The name of the All mixed TV library.</summary>
    public const string MixedTv = "Friends' TV shows";

    private static readonly string[] Types = ["movies", "tvshows"];

    private readonly IStateStore _state;
    private readonly IAccountSource _accounts;
    private readonly LayoutJobService _jobs;
    private readonly GroupBook? _groups;
    private readonly ILibrarySource? _libraries;
    private readonly LibraryOptionsGuard? _options;

    /// <summary>
    /// Initializes a new instance of the <see cref="LayoutPresetService"/> class.
    /// </summary>
    /// <param name="state">The state store.</param>
    /// <param name="accounts">Jellyfin's accounts.</param>
    /// <param name="jobs">The move job.</param>
    /// <param name="groups">The title groups (titles collapsing), or null.</param>
    /// <param name="libraries">Jellyfin's libraries (the main libraries of Combine into my libraries), or null.</param>
    /// <param name="options">The options guard (the warnings in a preview), or null.</param>
    public LayoutPresetService(IStateStore state, IAccountSource accounts, LayoutJobService jobs, GroupBook? groups = null, ILibrarySource? libraries = null, LibraryOptionsGuard? options = null)
    {
        _state = state;
        _accounts = accounts;
        _jobs = jobs;
        _groups = groups;
        _libraries = libraries;
        _options = options;
    }

    /// <summary>
    /// The moves a preset needs in this state.
    /// </summary>
    /// <param name="state">The state.</param>
    /// <param name="preset">The preset.</param>
    /// <param name="mainTargets">Combine into my libraries: the main library of each type (a type without one moves nothing).</param>
    /// <returns>The moves (empty for Custom, or when the layout already is the preset).</returns>
    public static IReadOnlyList<MoveRequest> Plan(ShoalState state, LayoutPreset preset, IReadOnlyDictionary<string, LibraryInfo>? mainTargets = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        var moves = new List<MoveRequest>();
        if (preset == LayoutPreset.Custom)
        {
            return moves;
        }

        foreach (var type in Types)
        {
            var peers = state.Home.Peers.Where(p => p.Libraries.Any(m => m.CollectionType == type)).OrderBy(static p => p.PeerKey, StringComparer.Ordinal).ToList();
            if (peers.Count == 0)
            {
                continue;
            }

            if (preset == LayoutPreset.CombineIntoMine)
            {
                if (mainTargets?.GetValueOrDefault(type) is not { } main)
                {
                    continue; // the admin has not picked a main library of this type yet
                }

                // The placement-Main library already in that main library, else a new one; every connection of the type feeds it.
                var existing = MainLayoutLibrary(state, type, main);
                var create = new LibraryCreateRequest(CombinedName(state, type, main), type, LibraryPlacement.Main, main.Id);
                foreach (var peer in peers)
                {
                    if (existing is not null && LibraryFolders.FedBy(state, peer.PeerKey, type)?.LibKey == existing.LibKey)
                    {
                        continue;
                    }

                    moves.Add(new MoveRequest(peer.PeerKey, type, existing?.LibKey, existing is null ? create : null));
                }

                continue;
            }

            if (preset == LayoutPreset.AllMixed)
            {
                // The default library of the type when there is one, else one new library for all of them.
                var target = state.Home.Layout.DefaultTargets.TryGetValue(type, out var key) ? state.Home.Libraries.FirstOrDefault(l => l.LibKey == key && !l.IsLegacy && l.Type == type && l.Placement == LibraryPlacement.Shoal) : null; // never the admin's main library (Combine into my libraries)
                var create = new LibraryCreateRequest(type == "tvshows" ? MixedTv : MixedMovies, type, LibraryPlacement.Shoal);
                foreach (var peer in peers)
                {
                    if (target is not null && LibraryFolders.FedBy(state, peer.PeerKey, type)?.LibKey == target.LibKey)
                    {
                        continue;
                    }

                    moves.Add(new MoveRequest(peer.PeerKey, type, target?.LibKey, target is null ? create : null));
                }

                continue;
            }

            // One per friend: a connection that already has libraries of its own (legacy ones, or a shared library only it feeds) stays. Warden 13 L2: never one inside the admin's main library.
            foreach (var peer in peers)
            {
                var fed = LibraryFolders.FedBy(state, peer.PeerKey, type);
                if (fed is null || (fed.Feeds.Count == 1 && fed.Placement != LibraryPlacement.Main))
                {
                    continue;
                }

                // Warden 10 M3: the friend-chosen server name never reaches a path as it is.
                var name = MetadataSanitizer.DisplayName(LibraryNames.FromFriend(MetadataSanitizer.DisplayName(peer.ShownName()), "Friend") + (type == "tvshows" ? " TV shows" : " movies"));
                moves.Add(new MoveRequest(peer.PeerKey, type, null, new LibraryCreateRequest(name, type, LibraryPlacement.Shoal)));
            }
        }

        return moves;
    }

    /// <summary>
    /// Says what applying a preset would do, without changing anything.
    /// </summary>
    /// <param name="preset">The preset.</param>
    /// <param name="targets">Combine into my libraries: the main library the admin picked per type (<c>movies</c>, <c>tvshows</c>), or null.</param>
    /// <returns>The preview, or null in recovery mode.</returns>
    public PresetPreview? Preview(LayoutPreset preset, IReadOnlyDictionary<string, Guid>? targets = null)
    {
        if (_state.Status != StateStatus.Ready || !Enum.IsDefined(preset))
        {
            return null;
        }

        var state = _state.Current;
        var (mainTargets, choices) = preset == LayoutPreset.CombineIntoMine ? ResolveTargets(state, targets) : (new Dictionary<string, LibraryInfo>(), []);
        var plan = Plan(state, preset, mainTargets);
        var accounts = _accounts.List();
        var shareUsers = VisibilityService.ShareUserIds(state);
        var now = DateTime.UtcNow;
        var created = plan.Where(static m => m.NewLibrary is not null).Select(static m => m.NewLibrary!).Distinct().ToList();

        // Where each moved connection lands, and what its new library admits (the intersection of its sources for a new one).
        var afterAccess = new Dictionary<LibraryCreateRequest, LibraryAccess>();
        foreach (var request in created)
        {
            var sources = plan.Where(m => m.NewLibrary == request).SelectMany(m => SourcesOf(state, m.PeerKey, m.Type)).Select(l => LibraryFolders.AccessOf(state, l)).ToList();
            afterAccess[request] = LayoutJobService.Intersection(sources, accounts, shareUsers, now);
        }

        var moves = new List<PresetMove>();
        var losing = new HashSet<Guid>();
        var gaining = new List<AccountInfo>();
        var fromKeys = new HashSet<string>(StringComparer.Ordinal);
        var newTarget = new Dictionary<(string Peer, string Type), string>();
        foreach (var move in plan)
        {
            var peer = state.Home.Peers.First(p => p.PeerKey == move.PeerKey);
            var sources = SourcesOf(state, move.PeerKey, move.Type);
            var target = move.ToLibKey is { } key ? state.Home.Libraries.First(l => l.LibKey == key) : null;
            var access = target?.Access ?? afterAccess[move.NewLibrary!];
            moves.Add(new PresetMove(peer.PeerKey, MetadataSanitizer.DisplayName(peer.ShownName()), move.Type, [.. sources.Select(static l => l.Name)], target?.Name ?? move.NewLibrary!.Name));
            fromKeys.UnionWith(sources.Select(static l => l.LibKey));
            newTarget[(move.PeerKey, move.Type)] = move.ToLibKey ?? "new:" + move.NewLibrary!.Name;
            foreach (var account in accounts)
            {
                var user = account.ToCallerUser();
                if (sources.Any(l => AccessPolicy.Admitted(user, LibraryFolders.AccessOf(state, l), shareUsers)) && !AccessPolicy.Admitted(user, access, shareUsers))
                {
                    losing.Add(account.Id);
                }
            }

            gaining.AddRange(AccessGain.Gaining(accounts, access, [.. sources.Select(l => LibraryFolders.AccessOf(state, l))], shareUsers));
        }

        // A library goes when every connection that writes into it moves out.
        var removed = state.Home.Libraries
            .Where(l => fromKeys.Contains(l.LibKey) && LibraryFolders.ConnectionsOf(l).All(p => newTarget.ContainsKey((p, l.Type))))
            .Select(static l => new PresetRemovedLibrary(l.LibKey, l.Name))
            .ToList();

        var gain = AccessGainReport.Of(gaining);

        // Warden 13 M2: only a type that has candidates and none chosen waits for a pick; a type with no library of the admin's keeps its friends' titles where
        // they are (in their own Shoal library), and Apply goes ahead for the other type.
        var needs = choices.Any(static c => c.Chosen is null && c.Candidates.Count > 0);
        return new PresetPreview(preset, [.. created.Select(static c => new PresetNewLibrary(c.Name, c.Type))], removed, moves, Collapsing(state, newTarget), [.. losing], gain.Users, gain.RestrictedUsers, HashOf(preset, plan, removed, losing, gain, needs))
        {
            Targets = choices,
            NeedsTargets = needs,
            TargetsSavingNfo = [.. mainTargets.Values.Where(static t => t.SavesNfo).Select(static t => t.Name).Distinct(StringComparer.Ordinal)],
            TargetsRewritingTitles = [.. mainTargets.Values.Where(static t => t.SavesNfo || t.FetchesMetadata).Select(static t => t.Name).Distinct(StringComparer.Ordinal)],
            ShoalLibrariesNotLocked = _options?.ShoalLibrariesNotLocked() ?? [],
        };
    }

    /// <summary>
    /// Applies a preset: remembers it for new connections and starts the move job with the plan Preview showed.
    /// </summary>
    /// <param name="preset">The preset.</param>
    /// <param name="adminUserId">The admin.</param>
    /// <param name="planHash">The <see cref="PresetPreview.PlanHash"/> the admin approved (Warden 10 L3); a plan that differs now is refused with PlanChanged.</param>
    /// <param name="targets">Combine into my libraries: the main library the admin picked per type, as sent to Preview, or null.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Why the job did not start, or <see cref="LayoutJobError.None"/> (also when nothing has to move).</returns>
    public async Task<LayoutJobError> ApplyAsync(LayoutPreset preset, Guid adminUserId, string? planHash, IReadOnlyDictionary<string, Guid>? targets = null, CancellationToken cancellationToken = default)
    {
        if (_state.Status != StateStatus.Ready)
        {
            return LayoutJobError.RecoveryMode;
        }

        if (!Enum.IsDefined(preset) || string.IsNullOrEmpty(planHash))
        {
            return LayoutJobError.Invalid;
        }

        var preview = Preview(preset, targets);
        if (preview is { NeedsTargets: true })
        {
            return LayoutJobError.TargetRequired;
        }

        if (!string.Equals(preview?.PlanHash, planHash, StringComparison.Ordinal))
        {
            return LayoutJobError.PlanChanged; // the admin approved another plan: preview again
        }

        var (mainTargets, _) = preset == LayoutPreset.CombineIntoMine ? ResolveTargets(_state.Current, targets) : (new Dictionary<string, LibraryInfo>(), []);
        var plan = Plan(_state.Current, preset, mainTargets);
        var started = plan.Count == 0 ? LayoutJobError.None : await _jobs.StartAsync(plan, adminUserId, "preset", cancellationToken).ConfigureAwait(false);
        if (started != LayoutJobError.None)
        {
            return started;
        }

        await _state.UpdateAsync(
            s =>
            {
                s.Home.Layout.Default = preset;
                if (preset == LayoutPreset.AllMixed)
                {
                    // The mixed library of each type is where the next connection goes; the job created it already (planning creates before it returns).
                    foreach (var type in Types)
                    {
                        var name = type == "tvshows" ? MixedTv : MixedMovies;
                        var current = s.Home.Layout.DefaultTargets.TryGetValue(type, out var currentKey) ? s.Home.Libraries.FirstOrDefault(l => l.LibKey == currentKey) : null;
                        if (current is not { Placement: LibraryPlacement.Shoal } && s.Home.Libraries.FirstOrDefault(l => !l.IsLegacy && l.Type == type && l.Name == name && l.Placement == LibraryPlacement.Shoal) is { } mixed)
                        {
                            s.Home.Layout.DefaultTargets[type] = mixed.LibKey;
                        }
                    }
                }
                else if (preset == LayoutPreset.CombineIntoMine)
                {
                    // New connections join the admin's main library of the type too.
                    foreach (var (type, main) in mainTargets)
                    {
                        if (MainLayoutLibrary(s, type, main) is { } combined)
                        {
                            s.Home.Layout.DefaultTargets[type] = combined.LibKey;
                        }
                    }
                }
            },
            cancellationToken).ConfigureAwait(false);
        return LayoutJobError.None;
    }

    // The placement-Main library of the type whose folder is in that main library (by folder; the stored id as a fallback).
    private static LayoutLibrary? MainLayoutLibrary(ShoalState state, string type, LibraryInfo main) =>
        state.Home.Libraries.FirstOrDefault(l => !l.IsLegacy && l.Placement == LibraryPlacement.Main && l.Type == type
            && (LibraryFolders.PathOf(state.StubRoot, l) is { } path ? main.Locations.Any(location => LibraryFolders.SamePath(location, path)) || l.JellyfinLibraryId == main.Id : l.JellyfinLibraryId == main.Id));

    // "Friends in Movies"; a main library name that cannot be part of a library name gives a plain one. Unique among Shoal's libraries.
    private static string CombinedName(ShoalState state, string type, LibraryInfo main)
    {
        var name = MetadataSanitizer.DisplayName("Friends in " + MetadataSanitizer.DisplayName(main.Name));
        if (name.Length > 100 || !LibraryNames.IsSafe(name) || state.Home.Libraries.Any(l => string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            name = type == "tvshows" ? "Friends in my TV shows" : "Friends in my movies";
        }

        return name;
    }

    private static List<LayoutLibrary> SourcesOf(ShoalState state, string peerKey, string type)
    {
        if (LibraryFolders.FedBy(state, peerKey, type) is { } fed)
        {
            return [fed];
        }

        var peer = state.Home.Peers.First(p => p.PeerKey == peerKey);
        return [.. peer.Libraries.Where(m => m.CollectionType == type).Select(m => LibraryFolders.LegacyOf(state, peerKey, m)).OfType<LayoutLibrary>()];
    }

    // Combine into my libraries: per type with connections, the candidates and the one chosen (the admin's pick when it is a candidate, else the only candidate).
    private (Dictionary<string, LibraryInfo> Chosen, List<PresetTargetChoice> Choices) ResolveTargets(ShoalState state, IReadOnlyDictionary<string, Guid>? requested)
    {
        var all = _libraries?.List() ?? [];
        var chosen = new Dictionary<string, LibraryInfo>(StringComparer.Ordinal);
        var choices = new List<PresetTargetChoice>();
        foreach (var type in Types)
        {
            if (!state.Home.Peers.Any(p => p.Libraries.Any(m => m.CollectionType == type)))
            {
                continue;
            }

            var candidates = all.Where(l => LibraryLayoutService.IsMainCandidate(state, all, l, type)).OrderBy(static l => l.Name, StringComparer.OrdinalIgnoreCase).ToList();
            var rejected = false;
            LibraryInfo? pick = null;
            if (requested is not null && requested.TryGetValue(type, out var id))
            {
                pick = candidates.FirstOrDefault(c => c.Id == id);
                rejected = pick is null;
            }
            else if (candidates.Count == 1)
            {
                pick = candidates[0];
            }

            if (pick is not null)
            {
                chosen[type] = pick;
            }

            choices.Add(new PresetTargetChoice(type, pick?.Id, [.. candidates.Select(static c => new PresetTargetCandidate(c.Id, c.Name, c.SavesNfo))], rejected));
        }

        return (chosen, choices);
    }

    // A title "collapses" when its servers are spread over more libraries today than after the moves (it then shows in fewer places).
    private int Collapsing(ShoalState state, Dictionary<(string Peer, string Type), string> newTarget)
    {
        if (_groups is null)
        {
            return 0;
        }

        var count = 0;
        foreach (var group in _groups.All())
        {
            var type = group.Kind == GroupKind.Series ? "tvshows" : "movies";
            var before = group.Members.Select(static m => m.LibKey).Where(static k => k.Length > 0).Distinct(StringComparer.Ordinal).Count();
            var after = group.Members.Select(m => newTarget.TryGetValue((m.PeerKey, type), out var to) ? to : m.LibKey).Where(static k => k.Length > 0).Distinct(StringComparer.Ordinal).Count();
            if (before >= 2 && after < before)
            {
                count++;
            }
        }

        return count;
    }

    // Warden 10 L3: what the admin approved, as one value. Any change in the moves, the targets, the libraries removed or who loses or gains access changes it.
    private static string HashOf(LayoutPreset preset, IReadOnlyList<MoveRequest> plan, IReadOnlyList<PresetRemovedLibrary> removed, IEnumerable<Guid> losing, AccessGainReport gain, bool needsTargets)
    {
        var text = new System.Text.StringBuilder().Append(preset).Append('\n');
        if (needsTargets)
        {
            text.Append("needs-targets\n");
        }

        foreach (var move in plan)
        {
            text.Append(move.PeerKey).Append('|').Append(move.Type).Append('|').Append(move.ToLibKey ?? "-").Append('|').Append(move.NewLibrary?.Name ?? "-").Append('|').Append(move.NewLibrary?.Placement.ToString() ?? "-").Append('|').Append(move.NewLibrary?.MainLibraryId?.ToString("N") ?? "-").Append('\n');
        }

        text.Append("removed:").AppendJoin(',', removed.Select(static r => r.LibKey).Order(StringComparer.Ordinal)).Append('\n');
        text.Append("losing:").AppendJoin(',', losing.Order()).Append('\n');
        text.Append("gaining:").AppendJoin(',', gain.Users.Order()).Append('\n');
        return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text.ToString())))[..32];
    }
}
