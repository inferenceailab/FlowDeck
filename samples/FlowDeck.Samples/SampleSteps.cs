using FlowDeck.Core;

namespace FlowDeck.Samples;

/// <summary>
/// The steps the sample workflows are built from.
/// </summary>
/// <remarks>
/// Every one of these is a simulation - nothing here reserves stock or charges
/// a card. What they do carry is the <i>shape</i> of real work: they write what
/// they did into workflow data, their compensations undo exactly that, and the
/// one that fails does so for a reason a retry can plausibly fix.
///
/// <para>
/// Deliberately deterministic. A sample that fails at random produces a
/// dashboard nobody can reason about, and a bug report nobody can reproduce.
/// </para>
/// </remarks>
internal static class SampleSteps
{
    /// <summary>Records that it ran, and advances.</summary>
    /// <remarks>
    /// The data it writes is what makes a compensation visible: an undo that
    /// removes a key you can see is a rollback, and an undo that removes
    /// nothing is a comment.
    /// </remarks>
    internal sealed class Records(string key, object? value = null) : IStep
    {
        public ValueTask<Outcome> ExecuteAsync(IStepContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);

            context.Data.Set(key, value ?? $"{context.StepName} at step time");

            return ValueTask.FromResult(Outcome.Next);
        }
    }

    /// <summary>Removes what the step it compensates wrote.</summary>
    internal sealed class Undoes(string key) : IStep
    {
        public ValueTask<Outcome> ExecuteAsync(IStepContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);

            context.Data.Set<string?>(key, null);
            context.Data.Set($"{key}.undone", true);

            return ValueTask.FromResult(Outcome.Next);
        }
    }

    /// <summary>
    /// Fails its first attempt and succeeds on the second.
    /// </summary>
    /// <remarks>
    /// The attempt count lives in workflow data rather than in a field, so it
    /// survives the step being reconstructed between attempts and is scoped to
    /// one instance. A static counter would make two concurrent instances
    /// interfere - which is exactly the bug a sample should not teach.
    /// </remarks>
    internal sealed class FailsOnce(string key) : IStep
    {
        public ValueTask<Outcome> ExecuteAsync(IStepContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);

            var attempt = context.Data.TryGet<int>($"{key}.attempts", out var previous) ? previous + 1 : 1;
            context.Data.Set($"{key}.attempts", attempt);

            if (attempt == 1)
            {
                throw new InvalidOperationException(
                    "The payment gateway timed out. This is the transient failure the retry policy exists for.");
            }

            context.Data.Set(key, $"authorised on attempt {attempt}");

            return ValueTask.FromResult(Outcome.Next);
        }
    }

    /// <summary>Always fails, so the compensation path is reachable.</summary>
    internal sealed class Fails(string because) : IStep
    {
        public ValueTask<Outcome> ExecuteAsync(IStepContext context, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException(because);
    }

    /// <summary>
    /// Suspends the first time it runs, and proceeds when resumed.
    /// </summary>
    /// <remarks>
    /// Re-entered on resume rather than skipped, so it has to be able to tell
    /// "I am waiting" from "what I was waiting for has happened" - and the flag
    /// in workflow data is that distinction. A step that only ever suspended
    /// would suspend again on every resume, which is faithful to an approval
    /// that never arrives and useless as something to click.
    ///
    /// <para>
    /// Treating the resume itself as the approval is the simulation here. Real
    /// work would carry the decision in from outside; FlowDeck has no endpoint
    /// for writing workflow data, by decision, so nothing about this shortcut
    /// is a pattern to copy.
    /// </para>
    /// </remarks>
    internal sealed class WaitsFor(string what) : IStep
    {
        public ValueTask<Outcome> ExecuteAsync(IStepContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);

            if (context.Data.Contains($"{what}.waiting-since"))
            {
                context.Data.Set($"{what}.received", true);

                return ValueTask.FromResult(Outcome.Next);
            }

            context.Data.Set($"{what}.waiting-since", "the first time this step ran");

            return ValueTask.FromResult(Outcome.Suspend);
        }
    }

    /// <summary>Takes long enough to be worth watching.</summary>
    /// <remarks>
    /// One second, not thirty. Long enough that an instance is observably
    /// <c>Running</c> and <c>flowdeck_instances_executing</c> is non-zero if you
    /// scrape while it goes; short enough that nobody waits on a sample.
    /// </remarks>
    internal sealed class Takes(TimeSpan duration, string key) : IStep
    {
        public async ValueTask<Outcome> ExecuteAsync(
            IStepContext context,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);

            await Task.Delay(duration, cancellationToken).ConfigureAwait(false);

            context.Data.Set(key, $"took {duration.TotalSeconds:0.#}s");

            return Outcome.Next;
        }
    }
}
