namespace FlowDeck.Core;

/// <summary>
/// What a step tells the engine to do once it returns.
/// </summary>
public enum Outcome
{
    /// <summary>
    /// The step finished its work. Advance to the next step.
    /// </summary>
    Next = 0,

    /// <summary>
    /// The step has not finished. Suspend the instance here; it will be resumed
    /// later, typically by an external event or a timer.
    /// </summary>
    /// <remarks>
    /// The instance stays positioned on this step, so resuming re-enters it
    /// rather than skipping ahead.
    /// </remarks>
    Suspend = 1,
}

/// <summary>
/// The engine-supplied view a step has of the instance executing it.
/// </summary>
/// <remarks>
/// Deliberately narrow for now. Workflow data access arrives with issue #5;
/// adding it here before there are tests for it would be speculative.
/// </remarks>
public interface IStepContext
{
    /// <summary>Identifier of the executing instance.</summary>
    Guid InstanceId { get; }

    /// <summary>Name of the step currently executing.</summary>
    string StepName { get; }

    /// <summary>
    /// State shared by the steps of this instance, and only this instance.
    /// </summary>
    IWorkflowData Data { get; }

    /// <summary>
    /// The input this instance was started with, or <see langword="null"/> if
    /// the definition takes none. Read it with
    /// <see cref="StepContextExtensions.GetInput{TInput}"/>.
    /// </summary>
    object? Input => null;
}

/// <summary>
/// A unit of work within a workflow.
/// </summary>
/// <remarks>
/// Implementations are business code and are treated as untrusted by the
/// engine: a thrown exception is captured as a failed result rather than
/// being allowed to unwind the execution loop.
/// </remarks>
public interface IStep
{
    /// <summary>
    /// Performs this step's work.
    /// </summary>
    /// <returns>
    /// <see cref="Outcome.Next"/> to advance, <see cref="Outcome.Suspend"/> to
    /// suspend at this step.
    /// </returns>
    ValueTask<Outcome> ExecuteAsync(IStepContext context, CancellationToken cancellationToken = default);
}

/// <summary>
/// Minimal <see cref="IStepContext"/> implementation.
/// </summary>
public sealed record StepContext(
    Guid InstanceId,
    string StepName,
    IWorkflowData Data,
    object? Input = null) : IStepContext
{
    /// <summary>
    /// Convenience for tests and callers that do not exercise workflow data.
    /// </summary>
    public StepContext(Guid instanceId, string stepName)
        : this(instanceId, stepName, new WorkflowData())
    {
    }
}
