using System;
using System.Collections.Generic;
using MediaBrowser.Model.Dto;

namespace Jellyfin.Plugin.Shoal.Sync;

/// <summary>
/// Chooses which of a peer item's validated media sources Shoal keeps (DESIGN v2.6.4, K28). <see cref="MetadataSanitizer"/> calls it after its own
/// per-source checks; the default keeps what the sanitizer found, which is v1 behavior.
/// </summary>
public interface ISourceSanitizer
{
    /// <summary>Gets how many of the peer's sources are examined (8 in v1; 64 when alternate versions are on).</summary>
    int MaxExamined => 8;

    /// <summary>
    /// Refines the list of validated sources.
    /// </summary>
    /// <param name="dto">The peer's item (hostile input; read only what the implementation validates).</param>
    /// <param name="sources">The sources that passed the sanitizer's checks (at most 8).</param>
    /// <returns>The sources to keep, in order; the first is the primary.</returns>
    IReadOnlyList<SanitizedSource> Refine(BaseItemDto dto, IReadOnlyList<SanitizedSource> sources);
}

/// <summary>
/// Reads the trickplay levels of one peer source (DESIGN v2.6.5, K30). The default reads none, which is v1 behavior.
/// </summary>
public interface ITrickplaySanitizer
{
    /// <summary>
    /// Reads and validates the trickplay levels of one source.
    /// </summary>
    /// <param name="dto">The peer's item (hostile input).</param>
    /// <param name="peerSourceId">The peer's media source id.</param>
    /// <returns>The valid levels, at most four.</returns>
    IReadOnlyList<SanitizedTrickplay> Read(BaseItemDto dto, Guid peerSourceId);
}

/// <summary>
/// The default <see cref="ISourceSanitizer"/>: every validated source is kept as found.
/// </summary>
public sealed class KeepSources : ISourceSanitizer
{
    /// <summary>Gets the only instance.</summary>
    public static KeepSources Instance { get; } = new();

    /// <inheritdoc />
    public IReadOnlyList<SanitizedSource> Refine(BaseItemDto dto, IReadOnlyList<SanitizedSource> sources) => sources;
}

/// <summary>
/// The default <see cref="ITrickplaySanitizer"/>: no levels.
/// </summary>
public sealed class NoTrickplay : ITrickplaySanitizer
{
    /// <summary>Gets the only instance.</summary>
    public static NoTrickplay Instance { get; } = new();

    /// <inheritdoc />
    public IReadOnlyList<SanitizedTrickplay> Read(BaseItemDto dto, Guid peerSourceId) => [];
}
