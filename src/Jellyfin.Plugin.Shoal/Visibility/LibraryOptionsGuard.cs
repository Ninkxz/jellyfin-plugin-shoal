using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Jellyfin.Plugin.Shoal.State;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Shoal.Visibility;

/// <summary>
/// Keeps the libraries Shoal owns locked (Pilot 14 LBL3): an NFO saver turned on by hand plus "Replace all metadata" overwrites a stub's synopsis and deletes its
/// art. After every scan the locked options of every Shoal library (placement Shoal, and the per-friend libraries) are put back. A main library that holds Shoal
/// folders (placement Main) belongs to the admin: Shoal never changes it, but the admin status names it when its NFO saver is on. Warden 12: only a library with
/// one folder (Shoal's) is locked; a library that holds a Shoal folder beside others is named in the status instead, and a library whose options Jellyfin does
/// not keep is left alone after <see cref="MaxChangingPasses"/> changing passes in a row.
/// </summary>
public sealed class LibraryOptionsGuard
{
    /// <summary>The most passes in a row (each within a minute of the last) that may change one library; then Shoal stops for it and logs that once.</summary>
    public const int MaxChangingPasses = 3;

    private const long RunWindowMs = 60_000;

    private readonly IStateStore _state;
    private readonly ILibrarySource _libraries;
    private readonly ILogger _logger;
    private readonly Func<long> _ticks;
    private readonly Dictionary<string, (int Count, long LastTick)> _changes = new(StringComparer.Ordinal);
    private readonly HashSet<string> _stoppedLogged = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();
    private int _enforcing;

    /// <summary>
    /// Initializes a new instance of the <see cref="LibraryOptionsGuard"/> class.
    /// </summary>
    /// <param name="state">The state store.</param>
    /// <param name="libraries">Jellyfin's libraries.</param>
    /// <param name="logger">The logger.</param>
    public LibraryOptionsGuard(IStateStore state, ILibrarySource libraries, ILogger<LibraryOptionsGuard> logger)
        : this(state, libraries, (ILogger)logger)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="LibraryOptionsGuard"/> class (tests: any logger and clock).
    /// </summary>
    /// <param name="state">The state store.</param>
    /// <param name="libraries">Jellyfin's libraries.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="ticks">A millisecond clock, or null for the system's.</param>
    internal LibraryOptionsGuard(IStateStore state, ILibrarySource libraries, ILogger logger, Func<long>? ticks = null)
    {
        _state = state;
        _libraries = libraries;
        _logger = logger;
        _ticks = ticks ?? (static () => Environment.TickCount64);
    }

    /// <summary>Gets a value indicating whether a pass runs now (the options watch ignores the saves a pass makes itself).</summary>
    public bool Enforcing => Volatile.Read(ref _enforcing) > 0;

    /// <summary>
    /// Puts the locked options of every Shoal library back.
    /// </summary>
    /// <returns>How many libraries had to be locked again.</returns>
    public int Enforce()
    {
        if (_state.Status != StateStatus.Ready)
        {
            return 0;
        }

        Interlocked.Increment(ref _enforcing);
        try
        {
            lock (_gate)
            {
                return EnforceLocked();
            }
        }
        finally
        {
            Interlocked.Decrement(ref _enforcing);
        }
    }

    /// <summary>
    /// The main libraries holding Shoal folders (placement Main) whose NFO saver is on: the admin should turn it off, or a "Replace all metadata" can overwrite the
    /// friends' titles in them.
    /// </summary>
    /// <returns>Their names.</returns>
    public IReadOnlyList<string> MainLibrariesSavingNfo()
    {
        if (_state.Status != StateStatus.Ready)
        {
            return [];
        }

        // Pilot 15 R6-MAIN-WARN: the TARGET main library is the one whose folders hold the Shoal folder (its own options), the stored id only as a fallback.
        var state = _state.Current;
        var all = _libraries.List();
        return [.. state.Home.Libraries.Where(static l => !l.IsLegacy && l.Placement == LibraryPlacement.Main)
            .Select(l => Holding(state, l, all))
            .Where(static j => j is { SavesNfo: true })
            .Select(static j => j!.Name)
            .Distinct(StringComparer.Ordinal)];
    }

    /// <summary>
    /// The Jellyfin libraries that hold the folder of a placement-Shoal library beside other folders (Warden 12 L2): they may be the admin's own, so Shoal does not
    /// lock them; the admin status names them.
    /// </summary>
    /// <returns>Their names.</returns>
    public IReadOnlyList<string> ShoalLibrariesNotLocked()
    {
        if (_state.Status != StateStatus.Ready)
        {
            return [];
        }

        var state = _state.Current;
        var all = _libraries.List();
        return [.. state.Home.Libraries.Where(static l => !l.IsLegacy && l.Placement == LibraryPlacement.Shoal)
            .Select(l => Holding(state, l, all))
            .Where(static j => j is not null && j.Locations.Count != 1)
            .Select(static j => j!.Name)
            .Distinct(StringComparer.Ordinal)];
    }

    /// <summary>
    /// The placement-Main libraries no Jellyfin library holds any more (Warden 13 L5: the admin deleted the main library, or took Shoal's folder out of it): their
    /// titles show nowhere, and new connections that join them show nowhere either. Shoal's own names for them.
    /// </summary>
    /// <returns>Their names.</returns>
    public IReadOnlyList<string> MainLibrariesMissing()
    {
        if (_state.Status != StateStatus.Ready)
        {
            return [];
        }

        var state = _state.Current;
        var all = _libraries.List();
        return [.. state.Home.Libraries.Where(l => !l.IsLegacy && l.Placement == LibraryPlacement.Main && Holding(state, l, all) is null).Select(static l => l.Name)];
    }

    // The Jellyfin library that has the layout library's folder among its folders.
    private static LibraryInfo? Holding(ShoalState state, Jellyfin.Plugin.Shoal.State.ShoalLibrary library, IReadOnlyList<LibraryInfo> all)
    {
        var path = LibraryFolders.PathOf(state.StubRoot, library);
        return path is null ? null : all.FirstOrDefault(l => l.Locations.Any(location => LibraryFolders.SamePath(location, path)));
    }

    private int EnforceLocked()
    {
        var state = _state.Current;
        var all = _libraries.List();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var library in state.Home.Libraries.Where(static l => !l.IsLegacy && l.Placement == LibraryPlacement.Shoal))
        {
            // Pilot 15: by its folder; the stored id can be missing or stale after a create or a rename. Warden 12 L2: only a library with that one folder. Warden 13 L1: never by a stored id.
            if (Holding(state, library, all) is { Locations.Count: 1 } found)
            {
                names.Add(found.Name);
            }
        }

        foreach (var legacy in VisibilityService.ShoalLibraries(state, all))
        {
            if (all.FirstOrDefault(l => l.Id == legacy.Id) is { } found && found.Locations.Count == 1)
            {
                names.Add(found.Name); // a per-friend library of its own (one path): Shoal's
            }
        }

        var locked = 0;
        var now = _ticks();
        foreach (var name in names)
        {
            // Warden 12 L1: changing passes close together mean Jellyfin does not keep the locked options; stop for that library rather than loop.
            var count = _changes.TryGetValue(name, out var seen) && now - seen.LastTick < RunWindowMs ? seen.Count : 0;
            if (count >= MaxChangingPasses)
            {
                if (_stoppedLogged.Add(name))
                {
                    _logger.LogWarning("Shoal stopped locking the options of one of its libraries: Jellyfin did not keep them after {Count} tries", MaxChangingPasses);
                }

                continue;
            }

            try
            {
                if (_libraries.LockOptions(name))
                {
                    locked++;
                    _changes[name] = (count + 1, now);
                }
                else
                {
                    _changes.Remove(name);
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _logger.LogWarning("Shoal could not lock the options of one of its libraries ({Error})", ex.GetType().Name);
            }
        }

        if (locked > 0)
        {
            _logger.LogWarning("Shoal turned metadata savers and internet metadata off again for {Count} of its libraries (they would overwrite friends' titles)", locked);
        }

        return locked;
    }
}
