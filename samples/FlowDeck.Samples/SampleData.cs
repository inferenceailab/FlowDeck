using FlowDeck.Core;

namespace FlowDeck.Samples;

/// <summary>
/// Starts enough instances that the dashboard has something to show.
/// </summary>
/// <remarks>
/// Definitions alone leave an instance list that is empty and a set of actions
/// with nothing to act on - which looks like a broken deployment rather than a
/// clean one. This seeds one instance in each state the dashboard renders
/// differently, so every badge and every button has an example on first load.
///
/// <para>
/// The store defaults to in-memory, so this runs again on every restart and
/// accumulates nothing. Against a real database it would seed once per start
/// and pile up, which is one more reason it is Development-only.
/// </para>
/// </remarks>
public static class SampleData
{
    /// <summary>
    /// Runs one instance of each sample, leaving them in differing states.
    /// </summary>
    public static async Task SeedAsync(WorkflowEngine engine, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(engine);

        // Completed, with a retry in its history. The straight-line shape.
        await engine
            .StartAsync("order-fulfilment", 1, new OrderPlaced("ORD-1041", 128.40m), cancellationToken)
            .ConfigureAwait(false);

        // Completed, and forked. Started against version 2 while version 1 is
        // still registered, which is the case that makes versioning legible:
        // two runs of one id drawn as two different shapes.
        await engine
            .StartAsync("order-fulfilment", 2, new OrderPlaced("ORD-1042", 76.00m), cancellationToken)
            .ConfigureAwait(false);

        // Compensated. Reaches the third step, fails, and rolls the first two
        // back - so this one has a rollback to read rather than only an error.
        await engine.StartAsync("nightly-reconciliation", 1, cancellationToken).ConfigureAwait(false);

        // Suspended, and left that way. The one instance here that is waiting
        // on something, so Resume has a subject.
        await engine.StartAsync("document-review", 1, cancellationToken).ConfigureAwait(false);

        // The same review, already resumed - so a completed run with a taken
        // conditional branch is on screen before anyone clicks anything. Two
        // instances of one definition that ended differently is also the case
        // that shows a shape is drawn per definition and marked per run.
        var reviewed = await engine.StartAsync("document-review", 1, cancellationToken).ConfigureAwait(false);

        await engine.ResumeAsync(reviewed.Id, cancellationToken).ConfigureAwait(false);
    }
}
