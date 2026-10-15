using System;
using System.Threading;

namespace Jellyfin.Plugin.Shoal.Peers;

/// <summary>
/// The deadlines of one peer call (DESIGN 6.10): a token linked to the caller's cancellation (<c>RequestAborted</c>, the sync
/// budget) that also fires after the total time, plus the phase deadlines the caller derives from it. A cancellation of the caller
/// is told apart from a deadline: <see cref="Expired"/> is true only when the total time ran out.
/// </summary>
public sealed class CallBudget : IDisposable
{
    private readonly CancellationTokenSource _source;
    private readonly CancellationToken _caller;

    /// <summary>
    /// Initializes a new instance of the <see cref="CallBudget"/> class.
    /// </summary>
    /// <param name="total">The total time allowed for the whole call including the body, or null for none (static streams).</param>
    /// <param name="caller">The caller's cancellation (a client disconnect, the end of a sync).</param>
    public CallBudget(TimeSpan? total, CancellationToken caller)
    {
        _caller = caller;
        _source = CancellationTokenSource.CreateLinkedTokenSource(caller);
        if (total is { } limit)
        {
            _source.CancelAfter(limit);
        }
    }

    /// <summary>Gets the token that fires on the caller's cancellation or when the total time is over.</summary>
    public CancellationToken Token => _source.Token;

    /// <summary>Gets a value indicating whether the total time ran out (and the caller did not cancel).</summary>
    public bool Expired => _source.IsCancellationRequested && !_caller.IsCancellationRequested;

    /// <summary>Gets the caller's own cancellation.</summary>
    public CancellationToken Caller => _caller;

    /// <summary>
    /// Creates a token that fires when the budget does or after <paramref name="phase"/>, whichever comes first.
    /// </summary>
    /// <param name="phase">The phase deadline (headers, one idle read).</param>
    /// <param name="extra">An additional token to link (for example the one given to a single read).</param>
    /// <returns>A source the caller must dispose.</returns>
    public CancellationTokenSource StartPhase(TimeSpan phase, CancellationToken extra = default)
    {
        var phaseSource = extra.CanBeCanceled
            ? CancellationTokenSource.CreateLinkedTokenSource(_source.Token, extra)
            : CancellationTokenSource.CreateLinkedTokenSource(_source.Token);
        phaseSource.CancelAfter(phase);
        return phaseSource;
    }

    /// <inheritdoc />
    public void Dispose() => _source.Dispose();
}
