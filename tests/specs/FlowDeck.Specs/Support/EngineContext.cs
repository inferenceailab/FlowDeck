using FlowDeck.Core;
using FlowDeck.Core.Persistence;

namespace FlowDeck.Specs.Support;

/// <summary>
/// The world a scenario builds up and then asserts against.
/// </summary>
/// <remarks>
/// Reqnroll creates one per scenario and injects it into every step class that
/// asks for it, so state crosses Given/When/Then without static fields. Static
/// state would leak between scenarios in the same class, and the failure would
/// look like flakiness rather than a shared-state bug.
///
/// <para>
/// Definitions are held as <b>declarations</b> and turned into a registry only
/// when a When step needs one. Scenarios routinely describe one workflow across
/// two sentences - "a definition declaring steps A, B and C" then "step B
/// throws an exception" - so a later Given has to amend an earlier one.
/// <see cref="WorkflowRegistry"/> deliberately has no replace, and widening the
/// production API to suit a test would be the wrong way round.
/// </para>
/// </remarks>
public sealed class EngineContext
{
    private readonly Dictionary<string, Declaration> declarations = new(StringComparer.Ordinal);

    /// <summary>Persisted state, so restart scenarios have something to restart from.</summary>
    public InMemoryWorkflowStore Store { get; } = new();

    /// <summary>What executed, in order. The subject of most Then steps.</summary>
    public List<string> Log { get; } = [];

    /// <summary>Values steps captured out of workflow data or input.</summary>
    public Dictionary<string, object?> Captured { get; } = new(StringComparer.Ordinal);

    /// <summary>The instance a When step started or resumed.</summary>
    public WorkflowInstance? Instance { get; set; }

    /// <summary>
    /// The exception a When step produced, or null if it succeeded.
    /// </summary>
    /// <remarks>
    /// Captured rather than allowed to escape: "Then the call fails with X" is
    /// an assertion about the exception, and a step that let it propagate would
    /// fail the scenario before reaching the Then that describes it.
    /// </remarks>
    public Exception? Error { get; set; }

    /// <summary>The definition a scenario declared first, for steps that say "the definition".</summary>
    public Declaration Only => this.declarations.Values.First();

    /// <summary>Declares a workflow, replacing any declaration with the same id.</summary>
    public void Declare(string id, int version, Action<IWorkflowBuilder> build) =>
        this.declarations[id] = new Declaration(id, version, build, InputType: null);

    /// <summary>Declares a workflow taking typed input.</summary>
    public void DeclareWithInput<TInput>(string id, int version, Action<IWorkflowBuilder> build) =>
        this.declarations[id] = new Declaration(id, version, build, typeof(TInput));

    /// <summary>Whether anything has been declared under this id.</summary>
    public bool IsDeclared(string id) => this.declarations.ContainsKey(id);

    /// <summary>Builds a registry holding everything declared so far.</summary>
    public WorkflowRegistry BuildRegistry()
    {
        var registry = new WorkflowRegistry();

        foreach (var declaration in this.declarations.Values)
        {
            registry.Register(declaration.ToDefinition());
        }

        return registry;
    }

    /// <summary>What the engine said while it ran, for the M8 scenarios.</summary>
    /// <remarks>
    /// Attached to every engine this context builds rather than only to the
    /// observability scenarios' one. An entry is emitted on the same paths every
    /// other scenario exercises, so recording it always means a change that
    /// starts logging workflow data fails somewhere rather than nowhere.
    /// </remarks>
    public RecordingLogger Logger { get; } = new();

    /// <summary>Builds an engine over this scenario's declarations and store.</summary>
    public WorkflowEngine Engine(TimeProvider? clock = null) =>
        new(this.BuildRegistry(), clock, this.Store, logger: new RecordingLogger<WorkflowEngine>(this.Logger));

    /// <summary>Builds an engine that was given no logger at all.</summary>
    /// <remarks>
    /// The case an embedder gets by default. Observability is something a host
    /// switches on, so an engine without a logger has to run rather than throw
    /// (ADR-0025 decision 1).
    /// </remarks>
    public WorkflowEngine UnloggedEngine() => new(this.BuildRegistry(), store: this.Store);

    /// <summary>
    /// Builds an engine over a fresh registry and the same store — a restart.
    /// </summary>
    /// <remarks>
    /// Identical to <see cref="Engine"/> today, and named separately on purpose:
    /// a restart scenario is asserting that nothing survives except what was
    /// persisted, and a reader should be able to see that is what the step did.
    /// </remarks>
    public WorkflowEngine RestartedHost() =>
        new(this.BuildRegistry(), store: this.Store, logger: new RecordingLogger<WorkflowEngine>(this.Logger));

    /// <summary>Runs an action, keeping any exception for a later Then.</summary>
    public async Task CapturingErrorAsync(Func<Task> action)
    {
        ArgumentNullException.ThrowIfNull(action);

        try
        {
            await action().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            this.Error = ex;
        }
    }

    /// <summary>Runs an action, keeping any exception for a later Then.</summary>
    public void CapturingError(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);

        try
        {
            action();
        }
        catch (Exception ex)
        {
            this.Error = ex;
        }
    }

    /// <summary>A workflow a scenario described, not yet registered.</summary>
    public sealed record Declaration(
        string Id,
        int Version,
        Action<IWorkflowBuilder> Build,
        Type? InputType)
    {
        public IWorkflowDefinition ToDefinition() => this.InputType is null
            ? new SpecWorkflow(this.Id, this.Version, this.Build)
            : (IWorkflowDefinition)Activator.CreateInstance(
                typeof(SpecWorkflow<>).MakeGenericType(this.InputType),
                this.Id,
                this.Version,
                this.Build)!;
    }
}
