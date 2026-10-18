using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace Jellyfin.Plugin.Shoal.Visibility;

/// <summary>
/// One gate for every account policy write Shoal makes (Warden 10 M4): library lists, hidden tags and the sharing account's policy. Each write reads the policy
/// inside the gate, so two Shoal writes never undo each other. A write never takes the gate twice.
/// </summary>
public static class PolicyGate
{
    /// <summary>Gets the gate.</summary>
    public static SemaphoreSlim Gate { get; } = new(1, 1);

    /// <summary>
    /// A tag list with some tags taken away and others added (each once, at the end, in the order given).
    /// </summary>
    /// <param name="current">The list now.</param>
    /// <param name="remove">The tags to take away.</param>
    /// <param name="add">The tags to add.</param>
    /// <returns>The new list.</returns>
    public static IReadOnlyList<string> Next(IReadOnlyList<string> current, IReadOnlyCollection<string> remove, IReadOnlyCollection<string> add)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(remove);
        ArgumentNullException.ThrowIfNull(add);
        var next = current.Where(t => !remove.Contains(t, StringComparer.Ordinal)).ToList();
        foreach (var tag in add.Where(t => !remove.Contains(t, StringComparer.Ordinal)))
        {
            if (!next.Contains(tag, StringComparer.Ordinal))
            {
                next.Add(tag);
            }
        }

        return next;
    }

    /// <summary>
    /// Whether a list as read back after a write has every added tag and none of the removed ones (the post-write check).
    /// </summary>
    /// <param name="after">The list read back.</param>
    /// <param name="remove">The tags taken away.</param>
    /// <param name="add">The tags added.</param>
    /// <returns>True when the write stuck.</returns>
    public static bool Holds(IReadOnlyList<string> after, IReadOnlyCollection<string> remove, IReadOnlyCollection<string> add)
    {
        ArgumentNullException.ThrowIfNull(after);
        return add.Where(t => !remove.Contains(t, StringComparer.Ordinal)).All(t => after.Contains(t, StringComparer.Ordinal)) && !remove.Any(t => after.Contains(t, StringComparer.Ordinal));
    }
}
