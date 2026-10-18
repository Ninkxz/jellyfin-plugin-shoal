using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Shoal.State;

/// <summary>
/// Where a Shoal library's titles show (PLAN v2.9, Library layouts).
/// </summary>
public enum LibraryPlacement
{
    /// <summary>Its own Jellyfin library; access through <c>EnabledFolders</c>.</summary>
    Shoal,

    /// <summary>Added into an existing main library; access through the <c>shoal-lib-&lt;libKey8&gt;</c> NFO tag in <c>BlockedTags</c>.</summary>
    Main,
}

/// <summary>
/// The layout preset the admin chose.
/// </summary>
public enum LayoutPreset
{
    /// <summary>Every friend's titles in one library per type (the default of a new install).</summary>
    AllMixed,

    /// <summary>One library per friend and type (the default of an install upgraded from schema 2: that is what it already has).</summary>
    PerFriend,

    /// <summary>Friends' titles go into the admin's own main movie and TV libraries (one placement-Main library per type; owner decision, v2.9).</summary>
    CombineIntoMine,

    /// <summary>The admin arranged the libraries by hand.</summary>
    Custom,
}

/// <summary>
/// The priority tier of a connection (PLAN v2.9, Global dedupe and priority).
/// </summary>
public enum PriorityTier
{
    /// <summary>Always prefer this server when it is online.</summary>
    Prefer,

    /// <summary>Automatic by connection quality (the default).</summary>
    Normal,

    /// <summary>Used only when no other server with the title is online.</summary>
    Backup,
}

/// <summary>
/// One Shoal library (<c>state.json</c> schema 3, <c>home.libraries[]</c>). Each connection feeds at most one library per type; a library migrated
/// from schema 2 (a <em>legacy</em> library) is bound to the one mapping it came from and keeps that mapping's folder through <see cref="PathRel"/>.
/// </summary>
public sealed class ShoalLibrary
{
    /// <summary>The prefix of <see cref="PathRel"/> for a library created by v2.9 or later: <c>libs/&lt;libKey&gt;</c>.</summary>
    public const string LibsFolder = "libs";

    /// <summary>Gets or sets the random key of the library (16 lower-case hex characters). The first 8 name its tags.</summary>
    public string LibKey { get; set; } = string.Empty;

    /// <summary>Gets or sets the name the admin and the users see (text only).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the type: <c>movies</c> or <c>tvshows</c>.</summary>
    public string Type { get; set; } = "movies";

    /// <summary>Gets or sets where the titles show.</summary>
    public LibraryPlacement Placement { get; set; } = LibraryPlacement.Shoal;

    /// <summary>Gets or sets the id of the Jellyfin library (its own, or the main library it is added into), or null when not known yet.</summary>
    public Guid? JellyfinLibraryId { get; set; }

    /// <summary>Gets or sets the connections (peer keys) whose titles this library holds.</summary>
    public List<string> Feeds { get; set; } = [];

    /// <summary>Gets or sets who may open the library.</summary>
    public LibraryAccess Access { get; set; } = new();

    /// <summary>
    /// Gets or sets the library's folder below the stub root: <c>libs/&lt;libKey&gt;</c>, or for a legacy library <c>&lt;peerKey&gt;/&lt;subfolder&gt;</c>, the folder it
    /// already had (an upgrade moves no file).
    /// </summary>
    public string PathRel { get; set; } = string.Empty;

    /// <summary>Gets a value indicating whether this library was migrated from a schema 2 mapping (its folder is <c>&lt;peerKey&gt;/&lt;subfolder&gt;</c>).</summary>
    [JsonIgnore]
    public bool IsLegacy => !PathRel.StartsWith(LibsFolder + "/", StringComparison.Ordinal);
}

/// <summary>
/// The library layout settings (<c>home.layout</c>).
/// </summary>
public sealed class LibraryLayout
{
    /// <summary>Gets or sets the preset; a new install starts with <see cref="LayoutPreset.AllMixed"/>.</summary>
    public LayoutPreset Default { get; set; } = LayoutPreset.AllMixed;

    /// <summary>Gets or sets the library a new connection feeds, by type (<c>movies</c>, <c>tvshows</c>) to library key; a type without one gets a new library.</summary>
    public Dictionary<string, string> DefaultTargets { get; set; } = [];
}

/// <summary>
/// The automatic ranking of a connection (PLAN v2.9 K54 fills it in).
/// </summary>
public sealed class AutoRank
{
    /// <summary>Gets or sets the score (the speed-test median, in Mbps).</summary>
    public double Score { get; set; }

    /// <summary>Gets or sets since when the connection holds its current rank (UTC), or null.</summary>
    public DateTime? Since { get; set; }

    /// <summary>Gets or sets the connection that is challenging this one (a peer key), or null.</summary>
    public string? PendingChallenger { get; set; }

    /// <summary>Gets or sets how many evaluations in a row the challenger has won.</summary>
    public int Streak { get; set; }

    /// <summary>Gets or sets the place of the connection in the automatic order, 0 first (K54); the order the next evaluation starts from.</summary>
    public int Position { get; set; }

    /// <summary>Gets or sets when the challenger first won (UTC), or null; the second win must come at least 12 hours later (K54).</summary>
    public DateTime? ChallengeUtc { get; set; }

    /// <summary>Gets or sets since when the connection is demoted for having been offline over 30 minutes (UTC), or null; cleared after 2 hours online (K54).</summary>
    public DateTime? DemotedUtc { get; set; }
}
