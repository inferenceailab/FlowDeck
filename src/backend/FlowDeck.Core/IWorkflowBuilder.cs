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
