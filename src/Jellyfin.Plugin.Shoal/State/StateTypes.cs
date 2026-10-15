using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Shoal.State;

/// <summary>
/// Whether the state could be loaded (DESIGN 3.7).
/// </summary>
public enum StateStatus
{
    /// <summary>The state was loaded (or is new) and can be changed.</summary>
    Ready,

    /// <summary>
    /// No state file could be used. Every peer counts as disabled, so federated titles fail fast, and nothing is written
    /// until the admin chooses "Restore from file" or "Start over". Shoal never writes a fresh state over unreadable files.
    /// </summary>
    Recovery
}

/// <summary>
/// Where the loaded state came from.
/// </summary>
public enum StateSource
{
    /// <summary>No state file existed: this is a new install. Nothing has been written yet.</summary>
    Fresh,

    /// <summary><c>state.json</c> itself.</summary>
    Primary,

    /// <summary><c>state.json.bak</c>, because the primary was missing or unreadable.</summary>
    Backup,

    /// <summary>A daily copy in <c>backups/</c>, restored by the admin because neither the primary nor the <c>.bak</c> could be used.</summary>
    Daily
}

/// <summary>
/// Why the state is in recovery mode.
/// </summary>
public enum RecoveryReason
{
    /// <summary>Not in recovery mode.</summary>
    None,

    /// <summary>No state file could be read, or none passed validation.</summary>
    Unreadable,

    /// <summary><c>state.json</c> was written by a newer Shoal. It is never downgraded.</summary>
    NewerSchema,

    /// <summary>The data folder could not be created or read.</summary>
    StorageUnavailable
}

/// <summary>
/// One file that could hold the state (the primary, its <c>.bak</c> or a daily copy).
/// </summary>
/// <param name="FileName">The file name below the Shoal data folder (for example <c>backups/state-20261014.json</c>).</param>
/// <param name="Exists">Whether the file exists.</param>
/// <param name="Usable">Whether it parsed and passed validation.</param>
/// <param name="Problem">Why it is not usable (a short fixed text, never file content), or null.</param>
/// <param name="Generation">The generation counter inside the file, when it is usable.</param>
/// <param name="WrittenUtc">When the file says it was written, when it is usable.</param>
public sealed record StateCandidate(string FileName, bool Exists, bool Usable, string? Problem, long? Generation, DateTime? WrittenUtc);

/// <summary>
/// What happened when the state was loaded, for the admin UI and the log.
/// </summary>
public sealed class StateLoadInfo
{
    /// <summary>Gets the status.</summary>
    public StateStatus Status { get; init; }

    /// <summary>Gets where the state came from.</summary>
    public StateSource Source { get; init; }

    /// <summary>Gets the reason for recovery mode, or <see cref="RecoveryReason.None"/>.</summary>
    public RecoveryReason Recovery { get; init; }

    /// <summary>
    /// Gets the file the state was restored from when it was not the primary ("Shoal restored its settings from ..."),
    /// otherwise null.
    /// </summary>
    public string? RestoredFrom { get; init; }

    /// <summary>Gets the name a corrupt primary was moved to (it is kept byte for byte), or null.</summary>
    public string? QuarantinedAs { get; init; }

    /// <summary>Gets the candidates that were examined, in order of preference.</summary>
    public IReadOnlyList<StateCandidate> Candidates { get; init; } = [];

    /// <summary>
    /// Gets a value indicating whether the admin must review access: the state came from a copy (<c>.bak</c> or a daily copy), which can
    /// be older than the last change. Every library is then back in "Only selected users" mode and every share is paused until the
    /// admin has looked (Warden N-9, T-REG-5).
    /// </summary>
    public bool AccessReviewRequired { get; init; }

    /// <summary>
    /// Gets when the settings were restored from a copy, or null. Shown with the restore banner (DESIGN 3.7, 9.3).
    /// </summary>
    public DateTime? RestoredUtc { get; init; }

    /// <summary>Gets notes about values that were adjusted during validation (no secrets, no file content).</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];

    /// <summary>
    /// Returns a copy of this information with the access review confirmed.
    /// </summary>
    /// <returns>The copy.</returns>
    internal StateLoadInfo WithReviewConfirmed() => new()
    {
        Status = Status,
        Source = Source,
        Recovery = Recovery,
        RestoredFrom = RestoredFrom,
        RestoredUtc = RestoredUtc,
        QuarantinedAs = QuarantinedAs,
        Candidates = Candidates,
        AccessReviewRequired = false,
        Notes = Notes
    };
}

/// <summary>
/// Raised after a state change was written.
/// </summary>
public sealed class StateChangedEventArgs : EventArgs
{
    /// <summary>
    /// Initializes a new instance of the <see cref="StateChangedEventArgs"/> class.
    /// </summary>
    /// <param name="state">The new state.</param>
    public StateChangedEventArgs(ShoalState state) => State = state;

    /// <summary>Gets the new state (read-only by convention).</summary>
    public ShoalState State { get; }
}

/// <summary>
/// Thrown when a write is attempted in recovery mode.
/// </summary>
public sealed class StateRecoveryException : InvalidOperationException
{
    private const string DefaultMessage = "Shoal is in recovery mode; restore a state file or start over before changing settings.";

    /// <summary>
    /// Initializes a new instance of the <see cref="StateRecoveryException"/> class.
    /// </summary>
    public StateRecoveryException()
        : base(DefaultMessage)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="StateRecoveryException"/> class.
    /// </summary>
    /// <param name="message">The message.</param>
    public StateRecoveryException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="StateRecoveryException"/> class.
    /// </summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The inner exception.</param>
    public StateRecoveryException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="StateRecoveryException"/> class.
    /// </summary>
    /// <param name="reason">Why the state is in recovery mode.</param>
    public StateRecoveryException(RecoveryReason reason)
        : base(DefaultMessage)
    {
        Reason = reason;
    }

    /// <summary>Gets the reason for recovery mode.</summary>
    public RecoveryReason Reason { get; }
}
