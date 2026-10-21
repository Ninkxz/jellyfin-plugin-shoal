using System;
using System.IO;
using System.Linq;

namespace Jellyfin.Plugin.Shoal.Sync;

/// <summary>
/// What is wrong with a movie stub folder on disk (Pilot 14 LBL3): an NFO saver with "Replace all metadata" can write a <c>movie.nfo</c> that shadows Shoal's NFO,
/// drop the synopsis and the label, and delete the art. The next sync heals what it finds: the NFO is rewritten, the art fetched again, the stray file removed.
/// </summary>
[Flags]
public enum StubDamage
{
    /// <summary>Nothing.</summary>
    None = 0,

    /// <summary>Shoal's NFO is gone.</summary>
    NfoMissing = 1,

    /// <summary>Shoal's NFO no longer carries the friend label (it was rewritten by someone else).</summary>
    LabelMissing = 2,

    /// <summary>A <c>movie.nfo</c> written by a saver shadows Shoal's NFO.</summary>
    StrayNfo = 4,

    /// <summary>The poster Shoal stored is gone.</summary>
    PosterMissing = 8,

    /// <summary>The backdrop Shoal stored is gone.</summary>
    BackdropMissing = 16,

    /// <summary>Shoal's NFO lost a library tag (shoal-lib-*) that hides the title from accounts its library does not admit (Warden 13 L6).</summary>
    TagMissing = 32,
}

/// <summary>
/// Finds <see cref="StubDamage"/> in a movie stub folder: a few file checks, no reads but the NFO's.
/// </summary>
public static class StubInspector
{
    private const long MaxNfoBytes = 4L * 1024 * 1024;

    /// <summary>
    /// What is wrong with the folder of an active movie stub.
    /// </summary>
    /// <param name="libraryFolder">The library folder.</param>
    /// <param name="entry">The entry (its stub path names the folder; its image tags say which art Shoal stored).</param>
    /// <param name="tags">The library tags its NFO must carry (shoal-lib-*), or null.</param>
    /// <returns>The damage; None when the entry has no stub or its folder is gone (the scan and sync handle that).</returns>
    public static StubDamage Of(string libraryFolder, CatalogEntry entry, System.Collections.Generic.IReadOnlyList<string>? tags = null)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.Kind != EntryKind.Movie || entry.StubRelPath is not { } rel || string.IsNullOrEmpty(libraryFolder))
        {
            return StubDamage.None;
        }

        var top = rel.Split('/')[0];
        var folder = Path.Combine(libraryFolder, top);
        try
        {
            if (!Directory.Exists(folder))
            {
                return StubDamage.None;
            }

            var damage = StubDamage.None;
            var nfo = new FileInfo(Path.Combine(folder, top + ".nfo"));
            if (!nfo.Exists)
            {
                damage |= StubDamage.NfoMissing;
            }
            else if (nfo.LinkTarget is null && nfo.Length <= MaxNfoBytes)
            {
                var text = File.ReadAllText(nfo.FullName);
                if (!HasLabel(text))
                {
                    damage |= StubDamage.LabelMissing;
                }

                if (tags is not null && tags.Any(tag => !text.Contains("<tag>" + System.Security.SecurityElement.Escape(tag) + "</tag>", StringComparison.Ordinal)))
                {
                    damage |= StubDamage.TagMissing;
                }
            }

            if (!string.Equals(top, "movie", StringComparison.OrdinalIgnoreCase) && File.Exists(Path.Combine(folder, "movie.nfo")))
            {
                damage |= StubDamage.StrayNfo;
            }

            if (entry.ImageTags.ContainsKey("Primary") && !File.Exists(Path.Combine(folder, "poster.jpg")) && !File.Exists(Path.Combine(folder, "poster.png")))
            {
                damage |= StubDamage.PosterMissing;
            }

            if (entry.ImageTags.ContainsKey("Backdrop") && !File.Exists(Path.Combine(folder, "backdrop.jpg")) && !File.Exists(Path.Combine(folder, "backdrop.png")))
            {
                damage |= StubDamage.BackdropMissing;
            }

            return damage;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return StubDamage.None;
        }
    }

    // The label as the NFO writer wrote it, its apostrophe plain or escaped. 2.9.2 K4: any friend's name, so the shared tail is what is looked for.
    private static bool HasLabel(string nfo) =>
        nfo.Contains(FriendLabel.Tail, StringComparison.Ordinal) || nfo.Contains(FriendLabel.Tail.Replace("'", "&apos;", StringComparison.Ordinal), StringComparison.Ordinal) || nfo.Contains(FriendLabel.Tail.Replace("'", "&#39;", StringComparison.Ordinal), StringComparison.Ordinal);
}
