namespace FlowDeck.Core;

/// <summary>
/// One declared step within a workflow definition.
/// </summary>
/// <param name="Name">
/// Identifies the step within its workflow. Surfaced in history, dashboards
/// and error messages, so it is part of the definition's public contract.
/// </param>
/// <param name="Factory">
/// Produces the step body. A factory rather than a shared instance: bodies are
/// author-written classes that may hold per-execution state, and two instances
/// of the same workflow must never share it.
/// </param>
public sealed record StepDeclaration(string Name, Func<IStep> Factory)
{
    /// <summary>
    /// How this step retries. Defaults to no retry — retry is opt-in
    /// (ADR-0020).
    /// </summary>
    public RetryPolicy RetryPolicy { get; init; } = RetryPolicy.None;

    /// <summary>
    /// Produces the action that undoes this step, or <see langword="null"/> if
    /// the step declares none.
    /// </summary>
    /// <remarks>
    /// Null rather than a do-nothing action: "nothing to undo" and "an undo
    /// that does nothing" are different, and rollback skips the first rather
    /// than recording a compensation that did not happen.
    ///
    /// <para>
    /// A factory for the same reason <see cref="Factory"/> is one — a
    /// compensating action is author code that may hold per-execution state,
    /// and two instances rolling back at once must not share it.
    /// </para>
    /// </remarks>
    public Func<IStep>? Compensation { get; init; }
}

/// <summary>
/// Collects the steps a definition declares.
/// </summary>
public interface IWorkflowBuilder
{
    /// <summary>
    /// Appends a step. Declaration order is execution order.
    /// </summary>
    /// <param name="retryPolicy">
    /// How this step retries, or <see langword="null"/> to use the workflow
    /// default. With neither, the step does not retry: retry is opt-in, because
    /// silently retrying a step an author believed ran once converts a visible
    /// failure into duplicated side effects (ADR-0020).
    /// </param>
    IWorkflowBuilder AddStep(string name, Func<IStep> factory, RetryPolicy? retryPolicy = null);

    /// <summary>
    /// Sets the retry policy for steps that do not declare their own.
    /// </summary>
    IWorkflowBuilder WithRetryPolicy(RetryPolicy policy);

    /// <summary>
    /// Declares how the step just added is undone if the workflow later fails.
    /// </summary>
    /// <remarks>
    /// Applies <b>backwards</b>, to the most recently declared step — unlike
    /// <see cref="WithRetryPolicy"/>, which sets a forward default. A retry
    /// policy is a sensible thing to apply broadly; an undo action is specific
    /// to the one thing it undoes, so a compensation default would be a
    /// category error (ADR-0021).
    ///
    /// <para>
    /// Declaring it is the whole opt-in: if a step has a compensating action
    /// and the workflow fails, the action runs. There is no second switch,
    /// because a workflow carrying declared-but-inert undo actions would look
    /// protected and not be.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidWorkflowDefinitionException">
    /// No step has been declared yet, so there is nothing to attach it to.
    /// </exception>
    IWorkflowBuilder WithCompensation(Func<IStep> compensation);
}

/// <summary>
/// Thrown when a definition declares something the engine cannot execute.
/// </summary>
public sealed class InvalidWorkflowDefinitionException(string definitionId, string reason)
    : FlowDeckException($"Workflow definition '{definitionId}' is invalid: {reason}")
{
    public string DefinitionId { get; } = definitionId;

    public string Reason { get; } = reason;
}

/// <summary>
/// Default <see cref="IWorkflowBuilder"/>. Not thread-safe: a builder is used
/// once, on one thread, while a definition is being compiled.
/// </summary>
internal sealed class WorkflowBuilder(string definitionId) : IWorkflowBuilder
{
    private readonly List<StepDeclaration> steps = [];
    private readonly HashSet<string> names = new(StringComparer.Ordinal);

    /// <summary>
    /// The workflow-level default, applied to steps that declare nothing.
    /// </summary>
    private RetryPolicy defaultRetryPolicy = RetryPolicy.None;

    public IWorkflowBuilder WithRetryPolicy(RetryPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);

        this.defaultRetryPolicy = policy;
        return this;
    }

    public IWorkflowBuilder WithCompensation(Func<IStep> compensation)
    {
        ArgumentNullException.ThrowIfNull(compensation);

        if (this.steps.Count == 0)
        {
            // Attaching it to the next step instead would be the forward
            // reading this API deliberately does not have, and the author would
            // find out at rollback rather than at compile time.
            throw new InvalidWorkflowDefinitionException(
                definitionId, "compensation was declared before any step");
        }

        // A record, so the last entry is replaced rather than mutated. Declaring
        // twice replaces the first, consistent with WithRetryPolicy - throwing
        // would be defensible, but being inconsistent with the sibling method on
        // the same builder would not.
        this.steps[^1] = this.steps[^1] with { Compensation = compensation };

        return this;
    }

    public IWorkflowBuilder AddStep(string name, Func<IStep> factory, RetryPolicy? retryPolicy = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(factory);

        // Duplicate names would make execution history ambiguous: two entries
        // called "validate" with no way to tell which ran when.
        if (!this.names.Add(name))
        {
            throw new InvalidWorkflowDefinitionException(
                definitionId, $"step name '{name}' is declared more than once");
        }

        // Resolved at declaration time, not execution time, so a step's policy
        // is whatever the default was when it was declared. Declaring the
        // default after some steps would otherwise apply it retroactively to
        // them, which reads as a bug at the call site.
        this.steps.Add(new StepDeclaration(name, factory)
        {
            RetryPolicy = retryPolicy ?? this.defaultRetryPolicy,
        });

        return this;
    }

    /// <summary>
    /// The declared steps, in declaration order.
    /// </summary>
    /// <exception cref="InvalidWorkflowDefinitionException">
    /// No steps were declared. An empty workflow is a definition mistake, not a
    /// workflow that instantly completes.
    /// </exception>
    public IReadOnlyList<StepDeclaration> Build()
    {
        if (this.steps.Count == 0)
        {
            throw new InvalidWorkflowDefinitionException(definitionId, "it declares no steps");
        }

        return this.steps.ToArray();
    }
}
