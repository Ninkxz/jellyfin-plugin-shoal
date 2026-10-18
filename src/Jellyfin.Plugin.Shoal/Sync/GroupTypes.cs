using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.Shoal.Sync;

// PLAN v2.9 K41/K42: the shapes and the store interface of <StubRoot>/groups.json (global title groups). GroupStore reads and writes it, GroupPlanner
// fills it after each movie sync, GroupBook caches it. Admin-only: it holds peer item ids. Every string is a sanitized value, never raw peer text.
// A group exists only while two or more servers carry the title; a title on one server has no group and behaves as before.

/// <summary>
/// What a group is.
/// </summary>
public enum GroupKind
{
    /// <summary>A movie.</summary>
    Movie,

    /// <summary>A series (its episodes are matched by season and number below it).</summary>
    Series,
}

/// <summary>
/// Reads and writes <c>groups.json</c> (K42/K43 implement it: atomic writes, confinement below the stub root, sharding past
/// <see cref="GroupsDocument.ShardThresholdBytes"/>).
/// </summary>
public interface IGroupStore
{
    /// <summary>
    /// Loads every group.
    /// </summary>
    /// <returns>The groups; an empty document when there is no file yet.</returns>
    GroupsDocument Load();

    /// <summary>
    /// Writes the groups atomically.
    /// </summary>
    /// <param name="document">The groups.</param>
    void Save(GroupsDocument document);
}

/// <summary>
/// <c>&lt;StubRoot&gt;/groups.json</c>, or one shard of it once it grows past 15 MB (sharded by a hash of the group key).
/// </summary>
public sealed class GroupsDocument
{
    /// <summary>The schema this build writes.</summary>
    public const int CurrentSchema = 1;

    /// <summary>The most servers in one group.</summary>
    public const int MaxMembers = 4;

    /// <summary>The size past which the file is split into shards.</summary>
    public const long ShardThresholdBytes = 15L * 1024 * 1024;

    /// <summary>Gets or sets the schema number.</summary>
    public int Schema { get; set; } = CurrentSchema;

    /// <summary>Gets or sets the shard number, or 0 for the single unsharded file.</summary>
    public int Shard { get; set; }

    /// <summary>Gets or sets how many shards there are (1 for a single file); shard 0 carries it and is written last (K42).</summary>
    public int ShardCount { get; set; } = 1;

    /// <summary>Gets or sets the groups.</summary>
    public List<TitleGroup> Groups { get; set; } = [];

    /// <summary>
    /// Gets or sets the extra copies whose group ended or whose library no member feeds any more (K44/K45): their folders are deleted by the copy writer, and only
    /// then do they leave this list, so a copy's folder is always recorded somewhere until it is gone. Kept in shard 0.
    /// </summary>
    public List<GroupCopy> Retired { get; set; } = [];
}

/// <summary>
/// One title as Home shows it once: the servers that have it (members) and the stub copies written for it, one per library a member feeds.
/// </summary>
public sealed class TitleGroup
{
    /// <summary>Gets or sets the random key of the group (16 lower-case letters and digits).</summary>
    public string GroupKey { get; set; } = string.Empty;

    /// <summary>Gets or sets what the group is.</summary>
    public GroupKind Kind { get; set; }

    /// <summary>Gets or sets the servers that have the title (at most <see cref="GroupsDocument.MaxMembers"/>).</summary>
    public List<GroupMember> Members { get; set; } = [];

    /// <summary>Gets or sets the stub copies, one per library.</summary>
    public List<GroupCopy> Copies { get; set; } = [];

    /// <summary>Gets or sets the peer key of the member whose metadata the NFO is written from.</summary>
    public string MetaOwner { get; set; } = string.Empty;

    /// <summary>Gets or sets the last computed member order, or null when it was never computed.</summary>
    public GroupRankCache? RankCache { get; set; }
}

/// <summary>
/// One server that has the title.
/// </summary>
public sealed class GroupMember
{
    /// <summary>Gets or sets the connection.</summary>
    public string PeerKey { get; set; } = string.Empty;

    /// <summary>Gets or sets the item id on that server (32 hex characters).</summary>
    public string PeerItemId { get; set; } = string.Empty;

    /// <summary>Gets or sets the media source ids of that server's versions; when the group has two or more members they are hidden sources.</summary>
    public List<string> Sources { get; set; } = [];

    /// <summary>Gets or sets the library the connection feeds for this type.</summary>
    public string LibKey { get; set; } = string.Empty;
}

/// <summary>
/// One stub copy of the title, in one library.
/// </summary>
public sealed class GroupCopy
{
    /// <summary>Gets or sets the library.</summary>
    public string LibKey { get; set; } = string.Empty;

    /// <summary>Gets or sets the stub key the sentinel names (it points at the group copy, not at a server).</summary>
    public string StubKey { get; set; } = string.Empty;

    /// <summary>Gets or sets the folder of the copy relative to the library folder.</summary>
    public string FolderRel { get; set; } = string.Empty;

    /// <summary>Gets or sets the <c>shoal-alt-&lt;libKey8&gt;</c> tags: one for each higher-ordered library that also holds a copy.</summary>
    public List<string> AltTags { get; set; } = [];

    /// <summary>
    /// Gets or sets a value indicating whether this is an extra copy (K44/K45): a stub the copy writer made in another library a member feeds, not the owner's own
    /// catalog stub. It is recorded here before its folder is written, so it can always be removed again.
    /// </summary>
    public bool Extra { get; set; }

    /// <summary>Gets or sets a value indicating whether the extra copy's folder was written (false: planned, its users' tags go first).</summary>
    public bool Written { get; set; }
}

/// <summary>
/// The member order last computed for a group (tier, limit fit, score), so a play does not recompute it.
/// </summary>
public sealed class GroupRankCache
{
    /// <summary>Gets or sets the peer keys, best first.</summary>
    public List<string> Order { get; set; } = [];

    /// <summary>Gets or sets when the order was computed (UTC).</summary>
    public DateTime ComputedUtc { get; set; }
}
