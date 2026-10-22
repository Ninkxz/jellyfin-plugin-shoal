using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.Shoal.Sync;

/// <summary>
/// The plain-words label that opens the overview of every friend's title (PLAN v2.9, owner decision 4): the first line of the movie, series and
/// episode <c>&lt;plot&gt;</c>, never a season's. Plain text only (Moonfin strips markup); the apostrophe is the ASCII one, U+0027.
/// 2.9.2 K4: the label names the friend ("From Hops's server. ..."); <see cref="Generic"/> is the v1 text, kept for a title with no known friend.
/// </summary>
public static partial class FriendLabel
{
    /// <summary>The v1 label text, used when no friend's name is known.</summary>
    public const string Generic = "From a friend's server. May buffer or be unavailable sometimes. If it won't play, try again later.";

    /// <summary>The sentences every label ends with.</summary>
    public const string Tail = "May buffer or be unavailable sometimes. If it won't play, try again later.";

    /// <summary>The longest friend's name a label carries (the offline card's cap).</summary>
    public const int NameMax = 40;

    /// <summary>
    /// The version of the label. A catalog entry whose NFO was written with a lower version is rewritten once (a bump of this constant forces one
    /// NFO rewrite for every movie, series and episode). 2: the label names the friend (2.9.2 K4). 3: the NFO's only studio is the friend (K5).
    /// </summary>
    public const int Version = 3;

    /// <summary>What every friend studio starts with (2.9.2 K5).</summary>
    public const string StudioPrefix = "Shared by ";

    /// <summary>
    /// The label for the given friends, in play order: one names it, two name both, three or more name two and count the rest; none (or only
    /// names that clean to nothing) gives <see cref="Generic"/>.
    /// </summary>
    /// <param name="names">The friends' shown names; cleaned here (<see cref="MetadataSanitizer.DisplayName"/>, at most 40 characters).</param>
    /// <returns>The label.</returns>
    public static string Text(IReadOnlyList<string>? names)
    {
        var clean = (names ?? []).Select(Clean).Where(static n => n.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        if (clean.Any(NamesAServer))
        {
            // 2.9.4: a name that already ends in "server" is not given a second one ("From A's Server and B's server.").
            static string Owner(string name) => NamesAServer(name) ? name : name + "'s server";
            return clean.Count switch
            {
                1 => "From " + Owner(clean[0]) + ". " + Tail,
                2 => "From " + Owner(clean[0]) + " and " + Owner(clean[1]) + ". " + Tail,
                _ => "From " + Owner(clean[0]) + ", " + Owner(clean[1]) + " and " + (clean.Count - 2).ToString(CultureInfo.InvariantCulture) + " more servers. " + Tail,
            };
        }

        return clean.Count switch
        {
            0 => Generic,
            1 => "From " + clean[0] + "'s server. " + Tail,
            2 => "From " + clean[0] + "'s and " + clean[1] + "'s servers. " + Tail,
            _ => "From " + clean[0] + "'s, " + clean[1] + "'s and " + (clean.Count - 2).ToString(CultureInfo.InvariantCulture) + " more servers. " + Tail,
        };
    }

    /// <summary>
    /// Removes every leading label: the v1 text or any "From ... server(s). May buffer ..." one, and the whitespace around them.
    /// </summary>
    /// <param name="overview">The overview; null or empty is allowed.</param>
    /// <returns>What follows the labels.</returns>
    public static string Strip(string? overview)
    {
        var rest = (overview ?? string.Empty).TrimStart();
        while (true)
        {
            if (rest.StartsWith(Generic, StringComparison.Ordinal))
            {
                rest = rest[Generic.Length..].TrimStart();
                continue;
            }

            var match = LeadingLabel().Match(rest);
            if (!match.Success)
            {
                return rest;
            }

            rest = rest[match.Length..].TrimStart();
        }
    }

    /// <summary>
    /// Puts the label in front of an overview. Leading labels (see <see cref="Strip"/>) are removed first, so applying it twice gives the same text
    /// as applying it once, and a friend rename replaces the old label.
    /// </summary>
    /// <param name="overview">The overview, already cleaned by <see cref="PeerText.Clean"/>; null or empty is allowed.</param>
    /// <param name="names">The friends to name (see <see cref="Text"/>); null or empty gives the generic label.</param>
    /// <returns>The label, a blank line and the rest; the label alone when nothing else is left.</returns>
    public static string Apply(string? overview, IReadOnlyList<string>? names = null)
    {
        var label = Text(names);
        var rest = Strip(overview);
        return rest.Length == 0 ? label : label + "\n\n" + rest;
    }

    /// <summary>
    /// The studios of a friend's title (2.9.2 K5, Warden decision W1): one "Shared by {name}" per friend, in the given order, cleaned like the label. The
    /// peer's own studios are never written, so the Studios filter stays one entry per friend. Written whatever the library's access (it says what the
    /// <c>Shoal: &lt;name&gt;</c> tag says).
    /// </summary>
    /// <param name="names">The friends' shown names; null, empty or names that clean to nothing give no studio.</param>
    /// <returns>The studio names.</returns>
    public static IReadOnlyList<string> Studios(IReadOnlyList<string>? names) =>
        [.. (names ?? []).Select(Clean).Where(static n => n.Length > 0).Distinct(StringComparer.Ordinal).Select(static n => StudioPrefix + n)];

    private static string Clean(string? name)
    {
        var clean = MetadataSanitizer.FriendName(name); // 2.9.3 K6: no link reaches a plot or a studio
        return clean.Length > NameMax ? clean[..NameMax].TrimEnd() : clean;
    }

    private static bool NamesAServer(string name) => name.EndsWith("server", StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"^From .{1,200}?(?i:servers?)\. May buffer or be unavailable sometimes\. If it won't play, try again later\.", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex LeadingLabel();
}
