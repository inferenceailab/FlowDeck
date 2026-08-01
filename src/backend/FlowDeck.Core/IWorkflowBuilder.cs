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

    /// <summary>
    /// Branches that leave this step, or empty for a plain sequential step.
    /// </summary>
    /// <remarks>
    /// Two shapes share one list, distinguished by
    /// <see cref="BranchDeclaration.IsParallel"/>:
    ///
    /// <list type="bullet">
    /// <item><b>A choice</b> — at most one branch is taken, selected either by
    /// the step returning its name or by a condition over workflow data.</item>
    /// <item><b>A fork</b> — every branch is taken, concurrently.</item>
    /// </list>
    ///
    /// <para>
    /// Mixing the two on one step is rejected at declaration time: "take one of
    /// these, and also all of those" has no sensible meaning.
    /// </para>
    /// </remarks>
    public IReadOnlyList<BranchDeclaration> Branches { get; init; } = [];
}

/// <summary>
/// One branch leaving a step.
/// </summary>
/// <param name="Name">
/// Identifies the branch. For a choice this is what the step returns to select
/// it; for a fork it is a label, surfaced in history and the visual view.
/// </param>
/// <param name="Steps">
/// The branch body, in declaration order. Never empty — a branch that leads
/// nowhere is a definition mistake rather than a shape.
/// </param>
public sealed record BranchDeclaration(string Name, IReadOnlyList<StepDeclaration> Steps)
{
    /// <summary>
    /// Selects this branch from workflow data, or <see langword="null"/> when
    /// the step selects it by name.
    /// </summary>
    /// <remarks>
    /// A predicate is declarative, so the visual view can show <i>why</i> an
    /// edge was taken rather than drawing an unexplained fork. A step-decided
    /// branch keeps the decision beside the logic that made it (ADR-0024).
    /// </remarks>
    public Func<IWorkflowData, bool>? Condition { get; init; }

    /// <summary>
    /// Whether this branch is one arm of a fork, in which case every arm runs.
    /// </summary>
    public bool IsParallel { get; init; }
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

    /// <summary>
    /// Declares a branch the step just added may select by name.
    /// </summary>
    /// <remarks>
    /// Attaches <b>backwards</b>, to the most recently declared step — the same
    /// rule as <see cref="WithCompensation"/>, and for the same reason: a
    /// branch belongs to the decision that selects it.
    ///
    /// <para>
    /// The join is implicit. When the branch finishes, execution continues with
    /// whatever was declared after the branching step, so there is no separate
    /// join to declare and no way to declare one that does not converge.
    /// </para>
    ///
    /// <para>
    /// If the step selects no branch, execution simply continues past the fork.
    /// A choice with no matching case is an ordinary shape, and failing would
    /// make every branch set implicitly require a catch-all (ADR-0024).
    /// </para>
    /// </remarks>
    IWorkflowBuilder Branch(string name, Action<IWorkflowBuilder> build);

    /// <summary>
    /// Declares a branch selected by a condition over workflow data.
    /// </summary>
    /// <remarks>
    /// Evaluated when execution reaches the branching step. Conditions are
    /// tested in declaration order and the first match wins, so an author reads
    /// them the way they read an <c>if / else if</c> chain.
    /// </remarks>
    IWorkflowBuilder BranchWhen(string name, Func<IWorkflowData, bool> condition, Action<IWorkflowBuilder> build);

    /// <summary>
    /// Declares branches that all run, concurrently.
    /// </summary>
    /// <remarks>
    /// Unlike <see cref="Branch"/>, every arm is taken. Arms execute genuinely
    /// concurrently (ADR-0024), so anything they share — workflow data above
    /// all — is shared across threads.
    ///
    /// <para>
    /// The join is implicit and waits for <b>every</b> arm. If any arm fails,
    /// the instance fails once the others have finished, and compensation
    /// unwinds what completed.
    /// </para>
    /// </remarks>
    IWorkflowBuilder Fork(params Action<IWorkflowBuilder>[] branches);
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
internal sealed class WorkflowBuilder(string definitionId, HashSet<string>? sharedNames = null) : IWorkflowBuilder
{
    private readonly List<StepDeclaration> steps = [];

    /// <summary>
    /// Step names already taken, shared with every nested branch builder.
    /// </summary>
    /// <remarks>
    /// Uniqueness is graph-wide, not per branch. Two steps called "charge" on
    /// different branches would make execution history ambiguous in exactly the
    /// way duplicate names in a sequence already do — and worse, because with a
    /// fork both can appear in the same run.
    /// </remarks>
    private readonly HashSet<string> names = sharedNames ?? new HashSet<string>(StringComparer.Ordinal);

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

    public IWorkflowBuilder Branch(string name, Action<IWorkflowBuilder> build) =>
        this.AddBranch(name, build, condition: null, parallel: false);

    public IWorkflowBuilder BranchWhen(string name, Func<IWorkflowData, bool> condition, Action<IWorkflowBuilder> build)
    {
        ArgumentNullException.ThrowIfNull(condition);

        return this.AddBranch(name, build, condition, parallel: false);
    }

    public IWorkflowBuilder Fork(params Action<IWorkflowBuilder>[] branches)
    {
        ArgumentNullException.ThrowIfNull(branches);

        if (branches.Length < 2)
        {
            // A fork of one is a sequence written confusingly, and a fork of
            // none is nothing at all. Rejecting both keeps the shape honest -
            // and a reader of the graph can trust that a fork really forks.
            throw new InvalidWorkflowDefinitionException(
                definitionId, "a fork must declare at least two branches");
        }

        for (var i = 0; i < branches.Length; i++)
        {
            this.AddBranch($"branch-{i + 1}", branches[i], condition: null, parallel: true);
        }

        return this;
    }

    private IWorkflowBuilder AddBranch(
        string name,
        Action<IWorkflowBuilder> build,
        Func<IWorkflowData, bool>? condition,
        bool parallel)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(build);

        if (this.steps.Count == 0)
        {
            // Attaching it to the next step instead would be a forward reading
            // this API deliberately does not have, and the author would find
            // out at execution rather than at definition time.
            throw new InvalidWorkflowDefinitionException(
                definitionId, "a branch was declared before any step");
        }

        var parent = this.steps[^1];

        if (parent.Branches.Any(branch => string.Equals(branch.Name, name, StringComparison.Ordinal)))
        {
            throw new InvalidWorkflowDefinitionException(
                definitionId, $"step '{parent.Name}' declares more than one branch named '{name}'");
        }

        // "Take one of these, and also all of those" has no sensible meaning.
        if (parent.Branches.Count > 0 && parent.Branches[0].IsParallel != parallel)
        {
            throw new InvalidWorkflowDefinitionException(
                definitionId,
                $"step '{parent.Name}' mixes a choice with a fork; a step branches one way or the other");
        }

        // Nested builder over the same name set, so uniqueness is graph-wide.
        var nested = new WorkflowBuilder(definitionId, this.names);
        build(nested);

        if (nested.steps.Count == 0)
        {
            throw new InvalidWorkflowDefinitionException(
                definitionId, $"branch '{name}' on step '{parent.Name}' declares no steps");
        }

        var branch = new BranchDeclaration(name, [.. nested.steps])
        {
            Condition = condition,
            IsParallel = parallel,
        };

        this.steps[^1] = parent with { Branches = [.. parent.Branches, branch] };

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
