using System;
using System.Collections.Generic;
using System.Threading;

namespace Jellyfin.Plugin.Shoal.LocalRemoval;

/// <summary>
/// 2.10.4 K2: the core side of "delete like Jellyfin" (docs/plans/atlas-delete-like-jellyfin-2.10.4.md rules B, E, F, G and the outcome table).
/// Separate from <see cref="IRemovalLibrary"/> so the legacy fakes do not change. Every refusal is a <see cref="LocalDeleteRefusedException"/>.
/// </summary>
internal interface ILocalDeleteLibrary
{
    /// <summary>The deletable movies for one friend (discovery without the 2.10.3 holding-folder rule), at most 100.</summary>
    /// <param name="peerKey">The friend.</param>
    /// <returns>The candidates.</returns>
    IReadOnlyList<RemovalTarget> Candidates(string peerKey);

    /// <summary>Rule B: re-runs discovery and returns the candidate whose local and friend item ids match (the generation is not compared).</summary>
    /// <param name="peerKey">The friend.</param>
    /// <param name="localItemId">The ticked local movie.</param>
    /// <param name="peerItemId">The friend's copy it was matched with.</param>
    /// <returns>The fresh target.</returns>
    /// <exception cref="LocalDeleteRefusedException"><c>libraryBusy</c> during a scan, else <c>candidateChanged</c>.</exception>
    RemovalTarget Resolve(string peerKey, Guid localItemId, Guid peerItemId);

    /// <summary>Rule D (first half): the watch state of every local user for the movie.</summary>
    /// <param name="itemId">The local movie.</param>
    /// <returns>The capture.</returns>
    IReadOnlyList<RemovalUserState> Capture(Guid itemId);

    /// <summary>Rule D: refuses unless every local user who can see the movie is admitted to the friend's copy.</summary>
    /// <param name="target">The target.</param>
    /// <param name="history">The capture.</param>
    void VerifyAccess(RemovalTarget target, IReadOnlyList<RemovalUserState> history);

    /// <summary>Rule A: the starting admin is still an enabled local administrator and not a share account.</summary>
    /// <param name="admin">The admin user id.</param>
    /// <exception cref="InvalidOperationException"><c>administratorRequired</c>.</exception>
    void VerifyAdministrator(Guid admin);

    /// <summary>Rules E, F and G against the hashed file, without deleting anything (also re-run inside <see cref="Delete"/>).</summary>
    /// <param name="target">The fresh target.</param>
    /// <param name="admin">The admin user id.</param>
    /// <param name="hashed">The local snapshot taken by <see cref="PortableFileProbe"/> in this item run.</param>
    /// <returns>The paths core will delete (internal; never in a reply).</returns>
    IReadOnlyList<string> Inspect(RemovalTarget target, Guid admin, RemovalFileSnapshot hashed);

    /// <summary>Holds sync, re-checks rules A, E, F and G, asks core to delete the item and its files, and reads the true outcome from disk and the index.</summary>
    /// <param name="target">The fresh target.</param>
    /// <param name="admin">The admin user id.</param>
    /// <param name="hashed">The local snapshot taken in this item run.</param>
    /// <param name="allowed">The switch and batch state, read under the hold immediately before core is called (W23-1); false refuses with <c>disabled</c>.</param>
    /// <param name="cancellationToken">Checked before the call only; the call itself is never cancelled halfway.</param>
    /// <returns><c>done</c>, <c>refused</c>+<c>deleteFailed</c> or <c>notConfirmed</c>. Refusals before the call throw.</returns>
    LocalDeleteOutcome Delete(RemovalTarget target, Guid admin, RemovalFileSnapshot hashed, Func<bool> allowed, CancellationToken cancellationToken);

    /// <summary>The friend's copy (Shoal stub) for the target once core indexed it, else null.</summary>
    /// <param name="target">The deleted target.</param>
    /// <returns>The stub item id, or null.</returns>
    Guid? FindSharedFor(RemovalTarget target);

    /// <summary>Writes the captured watch state to the friend's copy.</summary>
    /// <param name="target">The deleted target.</param>
    /// <param name="itemId">The stub item.</param>
    /// <param name="history">The capture taken before the delete.</param>
    void Handoff(RemovalTarget target, Guid itemId, IReadOnlyList<RemovalUserState> history);
}

/// <summary>The end state of one delete call.</summary>
/// <param name="State"><see cref="LocalDeleteItemState.Done"/>, <see cref="LocalDeleteItemState.Refused"/> or <see cref="LocalDeleteItemState.NotConfirmed"/>.</param>
/// <param name="Reason">Null for done; <c>deleteFailed</c> or <c>notConfirmed</c> otherwise.</param>
internal sealed record LocalDeleteOutcome(LocalDeleteItemState State, LocalDeleteReason? Reason)
{
    /// <summary>Gets the deleted outcome.</summary>
    public static LocalDeleteOutcome Done { get; } = new(LocalDeleteItemState.Done, null);

    /// <summary>Gets the outcome for a delete core refused with the movie file intact and still indexed.</summary>
    public static LocalDeleteOutcome Failed { get; } = new(LocalDeleteItemState.Refused, LocalDeleteReason.DeleteFailed);

    /// <summary>Gets the outcome that needs a look by hand (stops the batch).</summary>
    public static LocalDeleteOutcome Unconfirmed { get; } = new(LocalDeleteItemState.NotConfirmed, LocalDeleteReason.NotConfirmed);
}

/// <summary>A refusal with one of the Table 2 reason words; nothing was deleted.</summary>
internal sealed class LocalDeleteRefusedException : InvalidOperationException
{
    /// <summary>Initializes a new instance of the <see cref="LocalDeleteRefusedException"/> class.</summary>
    /// <param name="reason">The reason.</param>
    public LocalDeleteRefusedException(LocalDeleteReason reason)
        : base(reason.ToString())
    {
        Reason = reason;
    }

    /// <summary>Initializes a new instance of the <see cref="LocalDeleteRefusedException"/> class (removalChanged).</summary>
    public LocalDeleteRefusedException()
        : this(LocalDeleteReason.RemovalChanged)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="LocalDeleteRefusedException"/> class (removalChanged).</summary>
    /// <param name="message">Ignored text; the reason is fixed.</param>
    public LocalDeleteRefusedException(string message)
        : base(message)
    {
        Reason = LocalDeleteReason.RemovalChanged;
    }

    /// <summary>Initializes a new instance of the <see cref="LocalDeleteRefusedException"/> class (removalChanged).</summary>
    /// <param name="message">Ignored text; the reason is fixed.</param>
    /// <param name="innerException">The cause.</param>
    public LocalDeleteRefusedException(string message, Exception innerException)
        : base(message, innerException)
    {
        Reason = LocalDeleteReason.RemovalChanged;
    }

    /// <summary>Gets the reason.</summary>
    public LocalDeleteReason Reason { get; }
}
