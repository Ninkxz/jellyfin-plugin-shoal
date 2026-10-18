using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Jellyfin.Plugin.Shoal.Storage;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Shoal.Sync;

/// <summary>
/// Reads and writes <c>&lt;StubRoot&gt;/groups.json</c> (PLAN v2.9 K42): atomically, mode 0600, never through a link, and validated again on every
/// read. Past <see cref="GroupsDocument.ShardThresholdBytes"/> the groups are split by a hash of the group key into <c>groups.json</c> (shard 0,
/// which names the shard count and is written last, so it is the commit point) and <c>groups.&lt;n&gt;.json</c>. The file is derived data: a
/// damaged one reads as empty (with a warning) and the next syncs fill it again from the catalogs.
/// </summary>
public sealed class GroupStore : IGroupStore
{
    /// <summary>The file name of shard 0 below the stub root.</summary>
    public const string FileName = "groups.json";

    /// <summary>The most groups that are kept (a group needs two servers that carry the title, so this is far above any real library).</summary>
    public const int MaxGroups = 200_000;

    /// <summary>The most shards.</summary>
    public const int MaxShards = 64;

    private const long MaxReadBytes = 64L * 1024 * 1024;

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        MaxDepth = 16,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        AllowTrailingCommas = false,
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) },
    };

    private readonly Func<string?> _stubRoot;
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="GroupStore"/> class.
    /// </summary>
    /// <param name="stubRoot">Reads the validated stub root (empty or null: there is no file and nothing is written).</param>
    /// <param name="logger">The logger.</param>
    public GroupStore(Func<string?> stubRoot, ILogger logger)
    {
        _stubRoot = stubRoot;
        _logger = logger;
    }

    /// <summary>Gets or sets the size past which the file is sharded (tests lower it).</summary>
    internal long ShardThreshold { get; set; } = GroupsDocument.ShardThresholdBytes;

    /// <summary>
    /// Whether a key is a group, copy or stub key: 8 to 32 lower-case letters and digits.
    /// </summary>
    /// <param name="key">The key.</param>
    /// <returns>True when it is well formed.</returns>
    public static bool IsKey(string? key) => key is { Length: >= 8 and <= 32 } && key.All(static c => c is (>= '0' and <= '9') or (>= 'a' and <= 'z'));

    /// <summary>
    /// Checks one group and repairs what can be repaired: members of a bad shape or past the cap are dropped, copies of a bad shape are dropped.
    /// </summary>
    /// <param name="group">The group as read.</param>
    /// <param name="retired">Collects the extra copies that leave the group (retired: their folders are deleted from that record), or null.</param>
    /// <returns>True when the group is still usable.</returns>
    public static bool Validate(TitleGroup group, ICollection<GroupCopy>? retired = null)
    {
        if (group is null || !IsKey(group.GroupKey) || !Enum.IsDefined(group.Kind))
        {
            return false;
        }

        group.Members = [.. (group.Members ?? []).Where(static m => m is not null && IsPeerKey(m.PeerKey) && IsHex32(m.PeerItemId))
            .GroupBy(static m => m.PeerKey, StringComparer.Ordinal).Select(static g => g.First()).Take(GroupsDocument.MaxMembers)];
        foreach (var member in group.Members)
        {
            member.Sources = [.. (member.Sources ?? []).Where(IsHex32).Distinct(StringComparer.Ordinal).Take(CatalogSource.MaxPerTitle * 16)];
            member.LibKey = IsLibKey(member.LibKey) ? member.LibKey : string.Empty;
        }

        group.Copies = [.. (group.Copies ?? []).Where(IsValidCopy).GroupBy(static c => c.StubKey, StringComparer.Ordinal).Select(static g => g.First()).Take(64)];
        foreach (var copy in group.Copies)
        {
            CleanAltTags(copy);
        }

        // An extra copy only exists next to the owner's own stub, and at most one copy per library: a second extra copy in a library is retired (its folder is
        // still deleted from the record), never silently forgotten.
        foreach (var crowded in group.Copies.Where(static c => c.LibKey.Length > 0).GroupBy(static c => c.LibKey, StringComparer.Ordinal).Where(static g => g.Count() > 1).ToList())
        {
            var keep = crowded.FirstOrDefault(static c => !c.Extra) ?? crowded.First();
            foreach (var extra in crowded.Where(c => !ReferenceEquals(c, keep) && c.Extra).ToList())
            {
                group.Copies.Remove(extra);
                retired?.Add(extra);
            }
        }

        if (!group.Copies.Any(static c => !c.Extra) || group.Copies.Where(static c => c.LibKey.Length > 0).GroupBy(static c => c.LibKey, StringComparer.Ordinal).Any(static g => g.Count() > 1))
        {
            foreach (var extra in group.Copies.Where(static c => c.Extra))
            {
                retired?.Add(extra);
            }

            return false;
        }

        if (group.RankCache is { } cache)
        {
            cache.Order = [.. (cache.Order ?? []).Where(IsPeerKey).Distinct(StringComparer.Ordinal).Take(GroupsDocument.MaxMembers)];
        }

        // The owner of the metadata is always a member, and a group has two or more servers; otherwise it goes, and its extra copies are retired.
        if (!group.Members.Any(m => string.Equals(m.PeerKey, group.MetaOwner, StringComparison.Ordinal)) || group.Members.Count < 2)
        {
            foreach (var extra in group.Copies.Where(static c => c.Extra))
            {
                retired?.Add(extra);
            }

            return false;
        }

        return group.Copies.Count >= 1;
    }

    /// <inheritdoc />
    public GroupsDocument Load()
    {
        var root = Root();
        if (root is null)
        {
            return new GroupsDocument();
        }

        var first = Read(Path.Combine(root, FileName), root);
        if (first is null)
        {
            return new GroupsDocument();
        }

        var count = Math.Clamp(first.ShardCount, 1, MaxShards);
        var groups = new List<TitleGroup>(first.Groups);
        for (var shard = 1; shard < count; shard++)
        {
            var part = Read(ShardPath(root, shard), root);
            if (part is null || part.Shard != shard)
            {
                _logger.LogWarning("A shard of the Shoal title groups is missing or damaged; the groups are rebuilt by the next syncs");
                return new GroupsDocument();
            }

            groups.AddRange(part.Groups);
        }

        var valid = new List<TitleGroup>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var copies = new HashSet<string>(StringComparer.Ordinal);
        var dropped = 0;
        var leaving = new List<GroupCopy>();
        foreach (var group in groups)
        {
            // A group key, and a copy's stub key, belong to exactly one group.
            if (valid.Count >= MaxGroups || !Validate(group, leaving) || !seen.Add(group.GroupKey) || group.Copies.Any(c => !copies.Add(c.StubKey)))
            {
                dropped++;
                continue;
            }

            valid.Add(group);
        }

        if (dropped > 0)
        {
            _logger.LogWarning("The Shoal title groups dropped {Count} groups that failed validation", dropped);
        }

        // Retired extra copies: well formed, extra, and named once (their folders are deleted from this record).
        var retired = (first.Retired ?? []).Concat(leaving).Where(IsValidCopy).Where(static c => c.Extra).GroupBy(static c => c.StubKey, StringComparer.Ordinal).Select(static g => g.First()).Take(MaxGroups).ToList();
        return new GroupsDocument { Groups = valid, Retired = retired };
    }

    /// <inheritdoc />
    public void Save(GroupsDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var root = Root();
        if (root is null)
        {
            return;
        }

        var target = Path.Combine(root, FileName);
        if (PathSanitizer.HasLinkBelow(root, target))
        {
            _logger.LogWarning("The Shoal title groups were not written: the file is a link");
            return;
        }

        var whole = new GroupsDocument { Groups = document.Groups, ShardCount = 1, Retired = document.Retired ?? [] };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(whole, Options);
        var count = 1;
        if (bytes.LongLength > ShardThreshold)
        {
            count = (int)Math.Clamp((bytes.LongLength / Math.Max(1, ShardThreshold)) + 2, 2, MaxShards);
            var parts = Enumerable.Range(0, count).Select(static _ => new List<TitleGroup>()).ToArray();
            foreach (var group in document.Groups)
            {
                parts[ShardOf(group.GroupKey, count)].Add(group);
            }

            for (var shard = 1; shard < count; shard++)
            {
                var path = ShardPath(root, shard);
                if (PathSanitizer.HasLinkBelow(root, path))
                {
                    _logger.LogWarning("The Shoal title groups were not written: a shard is a link");
                    return;
                }

                AtomicFile.Write(path, JsonSerializer.SerializeToUtf8Bytes(new GroupsDocument { Shard = shard, ShardCount = count, Groups = parts[shard] }, Options));
            }

            bytes = JsonSerializer.SerializeToUtf8Bytes(new GroupsDocument { Shard = 0, ShardCount = count, Groups = parts[0], Retired = document.Retired ?? [] }, Options);
        }

        AtomicFile.Write(target, bytes); // shard 0 last: it names the shard count, so a crash before it leaves the previous set whole
        for (var stale = count == 1 ? 1 : count; stale < MaxShards; stale++)
        {
            var path = ShardPath(root, stale);
            if (File.Exists(path) && !PathSanitizer.HasLinkBelow(root, path))
            {
                File.Delete(path);
            }
        }
    }

    /// <summary>
    /// The shard of a group.
    /// </summary>
    /// <param name="groupKey">The group key.</param>
    /// <param name="count">The shard count.</param>
    /// <returns>0 to <paramref name="count"/> - 1.</returns>
    internal static int ShardOf(string groupKey, int count)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(groupKey));
        return (int)(BitConverter.ToUInt32(hash, 0) % (uint)count);
    }

    private static string ShardPath(string root, int shard) => Path.Combine(root, "groups." + shard.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".json");

    private static bool IsPeerKey(string? key) => key is { Length: >= 8 and <= 64 } && key.All(static c => c is (>= '0' and <= '9') or (>= 'a' and <= 'z') or '-');

    private static bool IsHex32(string? id) => id is { Length: 32 } && id.All(static c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'));

    private static bool IsLibKey(string? key) => key is { Length: 16 } && key.All(static c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'));

    private static bool IsSafeFolderRel(string? folder) => folder is not null && (folder.Length == 0 || CatalogStore.IsSafeFolder(folder, 1));

    // A copy: a valid stub key and folder; an extra copy always names its (v2.9) library and a folder, since its folder is created and deleted from that record.
    private static bool IsValidCopy(GroupCopy? c) =>
        c is not null && IsKey(c.StubKey) && (c.LibKey.Length == 0 || IsLibKey(c.LibKey)) && IsSafeFolderRel(c.FolderRel) && (!c.Extra || (c.LibKey.Length > 0 && c.FolderRel.Length > 0));

    private static void CleanAltTags(GroupCopy copy) =>
        copy.AltTags = [.. (copy.AltTags ?? []).Where(static t => t is { Length: 18 } && t.StartsWith("shoal-alt-", StringComparison.Ordinal) && t[10..].All(static c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'))).Distinct(StringComparer.Ordinal).Take(64)];

    private string? Root()
    {
        var root = _stubRoot();
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            return null;
        }

        return Path.GetFullPath(root);
    }

    private GroupsDocument? Read(string path, string root)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                return null;
            }

            if (info.Length > MaxReadBytes || PathSanitizer.HasLinkBelow(root, path))
            {
                _logger.LogWarning("The Shoal title groups file is too large or a link and was not read");
                return null;
            }

            var document = JsonSerializer.Deserialize<GroupsDocument>(File.ReadAllBytes(path), Options);
            if (document is null || document.Schema != GroupsDocument.CurrentSchema)
            {
                _logger.LogWarning("The Shoal title groups file has an unknown schema and was not used");
                return null;
            }

            document.Groups ??= [];
            return document;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            _logger.LogWarning("The Shoal title groups file could not be read ({Error}); the next syncs rebuild it", ex.GetType().Name);
            return null;
        }
    }
}
