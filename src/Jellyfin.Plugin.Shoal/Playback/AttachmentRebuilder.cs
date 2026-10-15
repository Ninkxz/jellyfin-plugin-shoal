using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.Shoal.Playback;

/// <summary>
/// Rebuilds <c>MediaAttachments</c> from the Peer's source (DESIGN v2.6.6, K31). The Peer's list is hostile: nothing is copied. An entry is kept only if it
/// is a font (by a closed list of MIME types or file extensions), its index is 0 to 127, and at most 32 are kept; covers, images and everything else are
/// dropped. Each kept entry is built fresh: Home's own index, a sanitized file name, a MIME type from Home's closed table, and a <c>DeliveryUrl</c> that goes
/// back to Home (never to the Peer) with the caller's own token, as subtitle URLs do (jellyfin-web's ASS renderer fetches fonts without headers).
/// </summary>
public sealed partial class AttachmentRebuilder : IAttachmentRebuilder
{
    /// <summary>The most attachments kept.</summary>
    public const int MaxKept = 32;

    /// <summary>The highest attachment index kept.</summary>
    public const int MaxIndex = 127;

    private static readonly Dictionary<string, string> TypeByMime = new(StringComparer.OrdinalIgnoreCase)
    {
        ["font/ttf"] = "ttf",
        ["font/otf"] = "otf",
        ["font/sfnt"] = "ttf",
        ["font/collection"] = "collection",
        ["font/woff"] = "woff",
        ["font/woff2"] = "woff2",
        ["application/x-truetype-font"] = "ttf",
        ["application/x-font-ttf"] = "ttf",
        ["application/vnd.ms-opentype"] = "otf",
        ["application/font-sfnt"] = "ttf",
    };

    private static readonly Dictionary<string, string> TypeByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ttf"] = "ttf",
        ["otf"] = "otf",
        ["ttc"] = "collection",
        ["woff"] = "woff",
        ["woff2"] = "woff2",
    };

    private static readonly Dictionary<string, string> MimeByType = new(StringComparer.Ordinal)
    {
        ["ttf"] = "font/ttf",
        ["otf"] = "font/otf",
        ["collection"] = "font/collection",
        ["woff"] = "font/woff",
        ["woff2"] = "font/woff2",
    };

    private readonly Func<bool> _enabled;

    /// <summary>
    /// Initializes a new instance of the <see cref="AttachmentRebuilder"/> class.
    /// </summary>
    /// <param name="enabled">Reads the <c>AttachmentsEnabled</c> switch; off lists no attachments, as v1 does.</param>
    public AttachmentRebuilder(Func<bool> enabled) => _enabled = enabled;

    /// <inheritdoc />
    public AttachmentRebuild Rebuild(MediaSourceInfo peerSource, Guid localItemId, Guid localMediaSourceId, string callerToken)
    {
        ArgumentNullException.ThrowIfNull(peerSource);
        if (!_enabled() || peerSource.MediaAttachments is not { Count: > 0 } offered || string.IsNullOrEmpty(callerToken))
        {
            return new AttachmentRebuild([], []);
        }

        var kept = new List<MediaAttachment>();
        var seen = new HashSet<int>();
        foreach (var entry in offered)
        {
            if (kept.Count == MaxKept)
            {
                break;
            }

            if (entry is null || entry.Index is < 0 or > MaxIndex || !seen.Add(entry.Index) || FontType(entry) is not { } type)
            {
                continue;
            }

            kept.Add(new MediaAttachment
            {
                Index = entry.Index,
                FileName = SafeName(entry.FileName, entry.Index),
                MimeType = MimeByType[type],
                Codec = null,
                DeliveryUrl = string.Create(
                    CultureInfo.InvariantCulture,
                    $"/Videos/{localItemId:N}/{localMediaSourceId:N}/Attachments/{entry.Index}?ApiKey={Uri.EscapeDataString(callerToken)}"),
            });
        }

        return new AttachmentRebuild(kept, [.. kept.Select(static a => a.Index)]);
    }

    /// <summary>
    /// The font type Home serves for a Peer entry: from the declared MIME type, else from the file extension.
    /// </summary>
    /// <param name="entry">The Peer's entry.</param>
    /// <returns>One of <c>ttf</c>, <c>otf</c>, <c>collection</c>, <c>woff</c>, <c>woff2</c>, or null when the entry is not a font.</returns>
    internal static string? FontType(MediaAttachment entry)
    {
        if (entry.MimeType is { Length: > 0 and <= 128 } mime && TypeByMime.TryGetValue(mime.Split(';')[0].Trim(), out var byMime))
        {
            return byMime;
        }

        if (entry.FileName is { Length: > 0 and <= 512 } name)
        {
            var dot = name.LastIndexOf('.');
            if (dot >= 0 && TypeByExtension.TryGetValue(name[(dot + 1)..], out var byExtension))
            {
                return byExtension;
            }
        }

        return null;
    }

    private static string SafeName(string? name, int index)
    {
        if (name is { Length: > 0 and <= 512 })
        {
            var slash = name.LastIndexOfAny(['/', '\\']);
            var baseName = slash >= 0 ? name[(slash + 1)..] : name;
            if (NameRegex().IsMatch(baseName))
            {
                return baseName;
            }
        }

        return string.Create(CultureInfo.InvariantCulture, $"font{index}");
    }

    [GeneratedRegex(@"\A[A-Za-z0-9 ._()-]{1,64}\z", RegexOptions.CultureInvariant)]
    private static partial Regex NameRegex();
}
