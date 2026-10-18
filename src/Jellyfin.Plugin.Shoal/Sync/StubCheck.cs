using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;

namespace Jellyfin.Plugin.Shoal.Sync;

/// <summary>
/// Bounds the stub damage checks of one sync (Warden 12 L6). A big library is sampled: about <see cref="MaxChecks"/> titles a sync, a different share each
/// sync, and no more than <see cref="MaxTime"/> of checking. A title that was healed <see cref="MaxHeals"/> times for the same damage and shows it again is
/// left alone (an NFO whose label form the check does not know would otherwise be rewritten every sync); its memory clears when a check finds it whole.
/// </summary>
internal sealed class StubCheck
{
    /// <summary>About how many titles one sync checks.</summary>
    public const int MaxChecks = 400;

    /// <summary>How many times the same damage of one title is healed before it is left alone (until the process restarts or the title is found whole).</summary>
    public const int MaxHeals = 2;

    /// <summary>The most time one sync spends on checks.</summary>
    public static readonly TimeSpan MaxTime = TimeSpan.FromSeconds(2);

    private const int MaxRemembered = 10_000;

    // Shared by every sync of this process: how often each title (peer + stub key) was healed for which damage.
    private static readonly ConcurrentDictionary<string, (StubDamage Damage, int Heals)> Healed = new(StringComparer.Ordinal);

    private readonly int _stride;
    private readonly long _rotation;
    private readonly Stopwatch _clock = new();
    private readonly TimeSpan _maxTime;

    /// <summary>
    /// Initializes a new instance of the <see cref="StubCheck"/> class.
    /// </summary>
    /// <param name="titles">How many titles the sync looks at.</param>
    /// <param name="rotation">A number that changes from sync to sync (which share is checked).</param>
    /// <param name="maxTime">The time budget, or null for <see cref="MaxTime"/>.</param>
    public StubCheck(int titles, long rotation, TimeSpan? maxTime = null)
    {
        _stride = Math.Max(1, (titles + MaxChecks - 1) / MaxChecks);
        _rotation = rotation;
        _maxTime = maxTime ?? MaxTime;
    }

    /// <summary>Gets how many checks ran.</summary>
    public int Checked { get; private set; }

    /// <summary>
    /// The damage of a title's stub folder, when this sync checks it; None when it is not in this sync's share, the time is spent, or it was healed often enough.
    /// </summary>
    /// <param name="peerKey">The connection.</param>
    /// <param name="libraryFolder">The library folder.</param>
    /// <param name="entry">The entry.</param>
    /// <param name="tags">The library tags its NFO must carry, or null.</param>
    /// <returns>The damage to heal.</returns>
    public StubDamage Sample(string peerKey, string libraryFolder, CatalogEntry entry, IReadOnlyList<string>? tags = null)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.Kind != EntryKind.Movie || entry.StubRelPath is null || (Hash(entry.StubKey) + _rotation) % _stride != 0 || _clock.Elapsed >= _maxTime)
        {
            return StubDamage.None;
        }

        _clock.Start();
        try
        {
            Checked++;
            return Remembered(peerKey, entry, StubInspector.Of(libraryFolder, entry, tags));
        }
        finally
        {
            _clock.Stop();
        }
    }

    /// <summary>
    /// The damage of a title's stub folder, always checked (the update of a title already chosen), with the heal memory applied.
    /// </summary>
    /// <param name="peerKey">The connection.</param>
    /// <param name="libraryFolder">The library folder.</param>
    /// <param name="entry">The entry.</param>
    /// <param name="tags">The library tags its NFO must carry, or null.</param>
    /// <returns>The damage to heal.</returns>
    public static StubDamage Full(string peerKey, string libraryFolder, CatalogEntry entry, IReadOnlyList<string>? tags = null)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return Remembered(peerKey, entry, StubInspector.Of(libraryFolder, entry, tags));
    }

    /// <summary>
    /// Notes that a title's damage was healed (its stub was rewritten).
    /// </summary>
    /// <param name="peerKey">The connection.</param>
    /// <param name="entry">The entry.</param>
    /// <param name="damage">The damage that was healed.</param>
    /// <returns>True when this was the last heal for that damage (the caller says once that the title is left alone from now on).</returns>
    public static bool NoteHealed(string peerKey, CatalogEntry entry, StubDamage damage)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (damage == StubDamage.None)
        {
            return false;
        }

        if (Healed.Count >= MaxRemembered)
        {
            Healed.Clear(); // a bound, not an exact memory: the worst case is two more heals per title
        }

        return Healed.AddOrUpdate(Key(peerKey, entry), (damage, 1), (_, old) => old.Damage == damage ? (damage, old.Heals + 1) : (damage, 1)).Heals == MaxHeals;
    }

    /// <summary>Forgets every heal (tests).</summary>
    internal static void Forget() => Healed.Clear();

    private static StubDamage Remembered(string peerKey, CatalogEntry entry, StubDamage damage)
    {
        var key = Key(peerKey, entry);
        if (damage == StubDamage.None)
        {
            Healed.TryRemove(key, out _);
            return damage;
        }

        return Healed.TryGetValue(key, out var seen) && seen.Damage == damage && seen.Heals >= MaxHeals ? StubDamage.None : damage;
    }

    private static string Key(string peerKey, CatalogEntry entry) => peerKey + "/" + entry.StubKey;

    // FNV-1a: stable across processes (string.GetHashCode is not).
    private static long Hash(string value)
    {
        uint hash = 2166136261;
        foreach (var c in value)
        {
            hash = (hash ^ c) * 16777619;
        }

        return hash;
    }
}
