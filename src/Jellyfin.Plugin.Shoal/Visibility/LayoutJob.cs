using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.Shoal.State;
using Jellyfin.Plugin.Shoal.Storage;
using Jellyfin.Plugin.Shoal.Sync;
using Microsoft.Extensions.Logging;
using LayoutLibrary = Jellyfin.Plugin.Shoal.State.ShoalLibrary;

namespace Jellyfin.Plugin.Shoal.Visibility;

/// <summary>
/// How far a layout job has come.
/// </summary>
public enum LayoutJobPhase
{
    /// <summary>The job is planned and written; nothing on disk has changed yet.</summary>
    Planned,

    /// <summary>The moves are running, one connection at a time.</summary>
    Running,

    /// <summary>Every move is done; empty libraries are being removed.</summary>
    Cleanup,

    /// <summary>The job finished.</summary>
    Done,

    /// <summary>The job stopped on an error; it is tried again at the next start (every step can be repeated).</summary>
    Failed,

    /// <summary>The admin gave up an unfinished job (Warden 10 M2): it is never resumed, and new layout changes and syncs may run again.</summary>
    Abandoned,
}

/// <summary>
/// What one step of a move does to one title folder.
/// </summary>
public enum LayoutOpKind
{
    /// <summary>The connection's title folder moves into the target library (a sole-member folder): the folder is renamed, nothing is fetched again.</summary>
    Rename,

    /// <summary>The title folder is a group copy another member still needs in the old library: only this connection's folder goes, and its title is added again in
    /// the target library at its next sync.</summary>
    Delete,
}

/// <summary>
/// The journal of the running (or last) layout job, <c>&lt;data&gt;/layout-job.json</c> (PLAN v2.9 K44). It holds keys and folder names only, never a secret. It is
/// written before anything changes and after each batch of steps; every step can be repeated, so a job resumes from its last checkpoint after a crash.
/// </summary>
public sealed class LayoutJournal
{
    /// <summary>The schema this build writes.</summary>
    public const int CurrentSchema = 1;

    /// <summary>Gets or sets the schema.</summary>
    public int Schema { get; set; } = CurrentSchema;

    /// <summary>Gets or sets the job's random key.</summary>
    public string JobId { get; set; } = string.Empty;

    /// <summary>Gets or sets what started the job (<c>move</c> or <c>preset</c>).</summary>
    public string Kind { get; set; } = "move";

    /// <summary>Gets or sets the phase.</summary>
    public LayoutJobPhase Phase { get; set; }

    /// <summary>Gets or sets when the job started (UTC).</summary>
    public DateTime StartedUtc { get; set; }

    /// <summary>Gets or sets when the job ended (UTC), or null.</summary>
    public DateTime? EndedUtc { get; set; }

    /// <summary>Gets or sets the admin who started it.</summary>
    public Guid AdminUserId { get; set; }

    /// <summary>Gets or sets the moves, run one at a time in this order.</summary>
    public List<LayoutMove> Moves { get; set; } = [];

    /// <summary>Gets or sets the libraries the job may remove at the end when they are left empty (the sources of its moves).</summary>
    public List<string> RemoveIfEmpty { get; set; } = [];

    /// <summary>Gets or sets the libraries the job created.</summary>
    public List<string> Created { get; set; } = [];

    /// <summary>Gets or sets the accounts that could open a source library but cannot open the target (the banner names them).</summary>
    public List<Guid> LostAccess { get; set; } = [];

    /// <summary>Gets or sets the accounts that can open a moved connection's titles in the target and could not before (Warden 10 M1: reported only).</summary>
    public List<Guid>? GainedAccess { get; set; } = [];

    /// <summary>Gets or sets those of them that are restricted profiles.</summary>
    public List<Guid>? GainedRestricted { get; set; } = [];

    /// <summary>Gets or sets a short fixed word for the error that stopped the job, or null.</summary>
    public string? Error { get; set; }
}

/// <summary>
/// One connection moving its titles of one type from its current library (or libraries) into a target library.
/// </summary>
public sealed class LayoutMove
{
    /// <summary>Gets or sets the connection.</summary>
    public string PeerKey { get; set; } = string.Empty;

    /// <summary>Gets or sets the type: <c>movies</c> or <c>tvshows</c>.</summary>
    public string Type { get; set; } = "movies";

    /// <summary>Gets or sets the target library.</summary>
    public string ToLibKey { get; set; } = string.Empty;

    /// <summary>Gets or sets the libraries the titles come from, with their recorded folders.</summary>
    public List<LayoutSource> Sources { get; set; } = [];

    /// <summary>Gets or sets a value indicating whether the state already says the connection feeds the target.</summary>
    public bool StateApplied { get; set; }

    /// <summary>Gets or sets the steps.</summary>
    public List<LayoutOp> Ops { get; set; } = [];

    /// <summary>Gets or sets the first step that may not have run yet (the checkpoint).</summary>
    public int NextOp { get; set; }

    /// <summary>Gets or sets a value indicating whether the catalog and the groups were brought up to date.</summary>
    public bool CatalogDone { get; set; }

    /// <summary>Gets or sets a value indicating whether the move is finished (targeted scans reported).</summary>
    public bool Done { get; set; }
}

/// <summary>
/// A library a move takes titles from.
/// </summary>
public sealed class LayoutSource
{
    /// <summary>Gets or sets the library key.</summary>
    public string LibKey { get; set; } = string.Empty;

    /// <summary>Gets or sets its folder below the stub root, as recorded when the job was planned.</summary>
    public string PathRel { get; set; } = string.Empty;
}

/// <summary>
/// One step of a move.
/// </summary>
public sealed class LayoutOp
{
    /// <summary>Gets or sets what the step does.</summary>
    public LayoutOpKind Kind { get; set; }

    /// <summary>Gets or sets the catalog entry's stub key (the step runs only while the connection's catalog still records the folder under it).</summary>
    public string StubKey { get; set; } = string.Empty;

    /// <summary>Gets or sets the entry's mapping.</summary>
    public string MappingKey { get; set; } = string.Empty;

    /// <summary>Gets or sets the source library.</summary>
    public string FromLibKey { get; set; } = string.Empty;

    /// <summary>Gets or sets the title folder in the source library.</summary>
    public string FromTop { get; set; } = string.Empty;

    /// <summary>Gets or sets the title folder in the target library (a rename), or empty.</summary>
    public string ToTop { get; set; } = string.Empty;
}

/// <summary>
/// Reads and writes the layout journal: atomic, owner-only, read with a size cap, and checked on every read (keys, folder names, schema). A damaged journal is
/// kept aside and never acted on.
/// </summary>
internal sealed partial class LayoutJournalStore
{
    private const string FileName = "layout-job.json";
    private const long MaxBytes = 64L * 1024 * 1024;

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        MaxDepth = 16,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        AllowTrailingCommas = false,
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) },
    };

    private readonly ShoalPaths _paths;
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="LayoutJournalStore"/> class.
    /// </summary>
    /// <param name="paths">The data folder.</param>
    /// <param name="logger">The logger.</param>
    public LayoutJournalStore(ShoalPaths paths, ILogger logger)
    {
        _paths = paths;
        _logger = logger;
    }

    private string FilePath => Path.Combine(_paths.Root, FileName);

    /// <summary>
    /// Whether a journal is well formed: known schema, valid keys, every folder one plain segment, every source a recorded library folder.
    /// </summary>
    /// <param name="journal">The journal as read.</param>
    /// <returns>True when it may be acted on.</returns>
    public static bool IsValid(LayoutJournal? journal)
    {
        if (journal is null || journal.Schema != LayoutJournal.CurrentSchema || !Enum.IsDefined(journal.Phase) || journal.Moves is null)
        {
            return false;
        }

        foreach (var move in journal.Moves)
        {
            if (move is null || !ShoalPaths.IsValidPeerKey(move.PeerKey) || move.Type is not ("movies" or "tvshows") || !LibKeyRegex().IsMatch(move.ToLibKey ?? string.Empty)
                || move.Sources is null || move.Ops is null || move.NextOp < 0 || move.NextOp > move.Ops.Count || move.Ops.Count > CatalogDocument.MaxEntries)
            {
                return false;
            }

            if (move.Sources.Any(static s => s is null || !LibKeyRegex().IsMatch(s.LibKey ?? string.Empty) || !IsSourceFolder(s)))
            {
                return false;
            }

            foreach (var op in move.Ops)
            {
                if (op is null || !Enum.IsDefined(op.Kind) || !GroupStore.IsKey(op.StubKey) || !move.Sources.Any(s => s.LibKey == op.FromLibKey)
                    || !IsSegment(op.FromTop) || (op.Kind == LayoutOpKind.Rename && !IsSegment(op.ToTop)))
                {
                    return false;
                }
            }
        }

        return (journal.RemoveIfEmpty ?? []).All(static k => LibKeyRegex().IsMatch(k)) && (journal.Created ?? []).All(static k => LibKeyRegex().IsMatch(k));
    }

    /// <summary>
    /// Reads the journal.
    /// </summary>
    /// <returns>The journal, or null when there is none or it is not usable (it is then never acted on).</returns>
    public LayoutJournal? Load()
    {
        try
        {
            var path = FilePath;
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                return null;
            }

            if (info.Length > MaxBytes || info.LinkTarget is not null)
            {
                _logger.LogWarning("The Shoal layout journal is not usable and was ignored");
                return null;
            }

            var journal = JsonSerializer.Deserialize<LayoutJournal>(File.ReadAllBytes(path), Options);
            if (!IsValid(journal))
            {
                _logger.LogWarning("The Shoal layout journal is damaged; it was ignored (no file was changed because of it)");
                return null;
            }

            return journal;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            _logger.LogWarning("The Shoal layout journal could not be read ({Error})", ex.GetType().Name);
            return null;
        }
    }

    /// <summary>
    /// Writes the journal atomically.
    /// </summary>
    /// <param name="journal">The journal.</param>
    public void Save(LayoutJournal journal)
    {
        SecureDirectory.Ensure(_paths.Root);
        AtomicFile.Write(FilePath, JsonSerializer.SerializeToUtf8Bytes(journal, Options));
    }

    private static bool IsSegment(string? name) =>
        name is { Length: > 0 and <= 400 } && name is not ("." or "..") && name.IndexOfAny(['/', '\\', '\0']) < 0 && !name.Any(char.IsControl);

    private static bool IsSourceFolder(LayoutSource source)
    {
        var parts = (source.PathRel ?? string.Empty).Split('/');
        if (parts.Length != 2)
        {
            return false;
        }

        return string.Equals(parts[0], LayoutLibrary.LibsFolder, StringComparison.Ordinal)
            ? string.Equals(parts[1], source.LibKey, StringComparison.Ordinal)
            : ShoalPaths.IsValidPeerKey(parts[0]) && IsSegment(parts[1]);
    }

    [GeneratedRegex("^[0-9a-f]{16}$")]
    private static partial Regex LibKeyRegex();
}

/// <summary>
/// Rewrites the library tag in the NFOs of a title folder that moved into another library (K44): every <c>shoal-lib-</c> tag goes, the target's goes in. Only
/// Shoal's own NFOs below the folder are touched, never through a link.
/// </summary>
internal static partial class NfoRetag
{
    private const long MaxNfoBytes = 4L * 1024 * 1024;

    /// <summary>
    /// Sets the library tag of every NFO below a folder.
    /// </summary>
    /// <param name="stubRoot">The stub root.</param>
    /// <param name="folder">The title folder.</param>
    /// <param name="libKey">The library the folder is in now, or null to leave the library tag alone (a legacy folder has none).</param>
    /// <param name="altTags">The copy's exact <c>shoal-alt-</c> tags, or null to leave them alone.</param>
    /// <returns>How many NFOs were rewritten.</returns>
    public static int Apply(string stubRoot, string folder, string? libKey, IReadOnlyList<string>? altTags = null)
    {
        if (!Directory.Exists(folder) || PathSanitizer.HasLinkBelow(stubRoot, folder))
        {
            return 0;
        }

        var lines = new List<string>();
        if (libKey is not null)
        {
            lines.Add("<tag>" + LibraryFolders.LibTag(libKey) + "</tag>");
        }

        lines.AddRange((altTags ?? []).Select(static t => "<tag>" + t + "</tag>"));
        var wanted = string.Concat(lines.Select(static l => "  " + l + "\n"));
        var rewritten = 0;
        var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, MaxRecursionDepth = 4 };
        foreach (var nfo in Directory.EnumerateFiles(folder, "*.nfo", options))
        {
            var info = new FileInfo(nfo);
            if (info.Length > MaxNfoBytes || info.LinkTarget is not null)
            {
                continue;
            }

            var text = File.ReadAllText(nfo);
            var stripped = libKey is null ? text : LibTagLine().Replace(text, string.Empty);
            stripped = altTags is null ? stripped : AltTagLine().Replace(stripped, string.Empty);
            var close = ClosingTag().Matches(stripped).LastOrDefault();
            if (close is null)
            {
                continue;
            }

            var next = stripped[..close.Index] + wanted + stripped[close.Index..];
            if (string.Equals(next, text, StringComparison.Ordinal))
            {
                continue;
            }

            AtomicFile.Write(nfo, System.Text.Encoding.UTF8.GetBytes(next));
            rewritten++;
        }

        return rewritten;
    }

    /// <summary>
    /// Takes the genres and studios out of every NFO below a folder (Warden 11 L3: a copy in a library not visible to everyone). Never through a link.
    /// </summary>
    /// <param name="stubRoot">The stub root.</param>
    /// <param name="folder">The title folder.</param>
    /// <returns>How many NFOs were rewritten.</returns>
    public static int StripFacets(string stubRoot, string folder)
    {
        if (!Directory.Exists(folder) || PathSanitizer.HasLinkBelow(stubRoot, folder))
        {
            return 0;
        }

        var rewritten = 0;
        var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, MaxRecursionDepth = 4 };
        foreach (var nfo in Directory.EnumerateFiles(folder, "*.nfo", options))
        {
            var info = new FileInfo(nfo);
            if (info.Length > MaxNfoBytes || info.LinkTarget is not null)
            {
                continue;
            }

            var text = File.ReadAllText(nfo);
            var next = FacetLine().Replace(text, string.Empty);
            if (!string.Equals(next, text, StringComparison.Ordinal))
            {
                AtomicFile.Write(nfo, System.Text.Encoding.UTF8.GetBytes(next));
                rewritten++;
            }
        }

        return rewritten;
    }

    [GeneratedRegex(@"^[ \t]*<(genre|studio)>[^<]*</\1>\r?\n", RegexOptions.Multiline)]
    private static partial Regex FacetLine();

    [GeneratedRegex(@"^[ \t]*<tag>shoal-lib-[0-9a-f]{8}</tag>\r?\n", RegexOptions.Multiline)]
    private static partial Regex LibTagLine();

    [GeneratedRegex(@"^[ \t]*<tag>shoal-alt-[0-9a-f]{8}</tag>\r?\n", RegexOptions.Multiline)]
    private static partial Regex AltTagLine();

    [GeneratedRegex(@"</(movie|tvshow|season|episodedetails)>")]
    private static partial Regex ClosingTag();
}
