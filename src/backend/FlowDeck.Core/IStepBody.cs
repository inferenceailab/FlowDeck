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
    /// The step has not finished. Persist the instance and suspend it; it will
    /// be resumed later, typically by an external event or a timer.
    /// </summary>
    Persist = 1,
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
}

/// <summary>
/// A unit of work within a workflow.
/// </summary>
/// <remarks>
/// Implementations are business code and are treated as untrusted by the
/// engine: a thrown exception is captured as a failed result rather than
/// being allowed to unwind the execution loop.
/// </remarks>
public interface IStepBody
{
    /// <summary>
    /// Performs this step's work.
    /// </summary>
    /// <returns>
    /// <see cref="Outcome.Next"/> to advance, <see cref="Outcome.Persist"/> to
    /// suspend at this step.
    /// </returns>
    ValueTask<Outcome> ExecuteAsync(IStepContext context, CancellationToken cancellationToken = default);
}

/// <summary>
/// Minimal <see cref="IStepContext"/> implementation.
/// </summary>
public sealed record StepContext(Guid InstanceId, string StepName) : IStepContext;
