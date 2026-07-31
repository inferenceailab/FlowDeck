namespace FlowDeck.Core;

/// <summary>
/// One declared step within a workflow definition.
/// </summary>
/// <param name="Name">
/// Identifies the step within its workflow. Surfaced in history, dashboards
/// and error messages, so it is part of the definition's public contract.
/// </param>
/// <param name="BodyFactory">
/// Produces the step body. A factory rather than a shared instance: bodies are
/// author-written classes that may hold per-execution state, and two instances
/// of the same workflow must never share it.
/// </param>
public sealed record WorkflowStep(string Name, Func<IStepBody> BodyFactory);

/// <summary>
/// Collects the steps a definition declares.
/// </summary>
public interface IWorkflowBuilder
{
    /// <summary>
    /// Appends a step. Declaration order is execution order.
    /// </summary>
    IWorkflowBuilder AddStep(string name, Func<IStepBody> bodyFactory);
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
    private readonly List<WorkflowStep> steps = [];
    private readonly HashSet<string> names = new(StringComparer.Ordinal);

    public IWorkflowBuilder AddStep(string name, Func<IStepBody> bodyFactory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(bodyFactory);

        // Duplicate names would make execution history ambiguous: two entries
        // called "validate" with no way to tell which ran when.
        if (!this.names.Add(name))
        {
            throw new InvalidWorkflowDefinitionException(
                definitionId, $"step name '{name}' is declared more than once");
        }

        this.steps.Add(new WorkflowStep(name, bodyFactory));
        return this;
    }

    /// <summary>
    /// The declared steps, in declaration order.
    /// </summary>
    /// <exception cref="InvalidWorkflowDefinitionException">
    /// No steps were declared. An empty workflow is a definition mistake, not a
    /// workflow that instantly completes.
    /// </exception>
    public IReadOnlyList<WorkflowStep> Build()
    {
        if (this.steps.Count == 0)
        {
            throw new InvalidWorkflowDefinitionException(definitionId, "it declares no steps");
        }

        return this.steps.ToArray();
    }
}
