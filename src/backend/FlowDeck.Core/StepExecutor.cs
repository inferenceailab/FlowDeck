namespace FlowDeck.Core;

/// <summary>
/// Lifecycle state of a workflow instance.
/// </summary>
/// <remarks>
/// Serialised by name, not ordinal. The attribute lives on the type rather than
/// in host configuration so every serialiser agrees - a client that forgot to
/// configure a converter would otherwise read <c>2</c> and have to guess.
///
/// <para>
/// Names are also stable in a way ordinals are not: inserting a status in the
/// middle of this enum would silently change the meaning of every value already
/// transmitted or stored.
/// </para>
/// </remarks>
[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter))]
public enum InstanceStatus
{
    /// <summary>Actively executing, or ready to continue.</summary>
    Running = 0,

    /// <summary>Parked at a step, awaiting an external event or timer.</summary>
    Suspended = 1,

    /// <summary>Every step completed.</summary>
    Completed = 2,

    /// <summary>Halted by an unhandled step failure.</summary>
    Failed = 3,

    /// <summary>Stopped deliberately by an operator.</summary>
    Cancelled = 4,

    /// <summary>
    /// A step failed and every compensating action succeeded.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="Failed"/> because an operator triaging a list
    /// needs to tell "broke, needs a human" from "broke, cleaned itself up".
    /// Folding them together makes a failure list least useful exactly when it
    /// is being scanned under pressure (ADR-0021).
    /// </remarks>
    Compensated = 5,

    /// <summary>
    /// A step failed and at least one compensating action also failed.
    /// </summary>
    /// <remarks>
    /// The instance is partly rolled back and the engine cannot say how partly
    /// - only history can. This is the status that always needs a human.
    /// </remarks>
    CompensationFailed = 6,
}

/// <summary>
/// Whether a single step execution succeeded.
/// </summary>
/// <remarks>Serialised by name — see <see cref="InstanceStatus"/>.</remarks>
[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter))]
public enum StepStatus
{
    Success = 0,
    Failed = 1,
}

/// <summary>
/// The outcome of executing one step.
/// </summary>
/// <param name="StepName">Step this result describes.</param>
/// <param name="Status">Whether the step body completed without throwing.</param>
/// <param name="Outcome">
/// What the step asked the engine to do. Meaningless when
/// <paramref name="Status"/> is <see cref="StepStatus.Failed"/>.
/// </param>
/// <param name="Error">The exception thrown, when the step failed.</param>
public sealed record StepExecutionResult(
    string StepName,
    StepStatus Status,
    Outcome Outcome,
    Exception? Error = null)
{
    /// <summary>
    /// Whether the engine should move past this step. Only a successful step
    /// that returned <see cref="FlowDeck.Core.Outcome.Next"/> advances.
    /// </summary>
    public bool ShouldAdvance =>
        this.Status == StepStatus.Success && this.Outcome == Outcome.Next;

    /// <summary>
    /// The status the instance takes on as a result of this step.
    /// </summary>
    /// <remarks>
    /// <see cref="InstanceStatus.Running"/> here means "ready to continue",
    /// not "already finished" - whether the workflow as a whole is complete
    /// depends on there being a next step, which this type cannot see.
    /// </remarks>
    public InstanceStatus ResultingInstanceStatus => this.Status switch
    {
        StepStatus.Failed => InstanceStatus.Failed,
        _ when this.Outcome == Outcome.Suspend => InstanceStatus.Suspended,
        _ => InstanceStatus.Running,
    };
}

/// <summary>
/// Executes a single step and translates its result, or its exception, into a
/// <see cref="StepExecutionResult"/>.
/// </summary>
/// <remarks>
/// This is the trust boundary between the engine and workflow author code.
/// Everything a step can do wrong is converted into data here so that the
/// execution loop above never has to catch.
/// </remarks>
public static class StepExecutor
{
    public static async ValueTask<StepExecutionResult> ExecuteAsync(
        IStep step,
        IStepContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(step);
        ArgumentNullException.ThrowIfNull(context);

        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var outcome = await step.ExecuteAsync(context, cancellationToken).ConfigureAwait(false);

            return new StepExecutionResult(context.StepName, StepStatus.Success, outcome);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Cancellation is the engine shutting down, not the step failing.
            // Recording it as a failure would mark healthy instances broken on
            // every deployment, so it propagates untouched.
            throw;
        }
        catch (Exception ex)
        {
            return new StepExecutionResult(context.StepName, StepStatus.Failed, Outcome.Next, ex);
        }
    }
}
