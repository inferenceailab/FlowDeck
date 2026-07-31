namespace FlowDeck.Core;

/// <summary>
/// Executes workflow instances.
/// </summary>
/// <remarks>
/// This implementation runs an instance to a stopping point synchronously on
/// the calling thread and keeps nothing. Durability arrives with #13, resume
/// with #14, and multi-node claiming with #39. Keeping it in-memory until
/// those stories land avoids inventing a persistence shape before there is a
/// test that constrains it.
/// </remarks>
public sealed class WorkflowEngine
{
    private readonly WorkflowRegistry registry;
    private readonly StepExecutor executor;
    private readonly TimeProvider timeProvider;

    public WorkflowEngine(WorkflowRegistry registry, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(registry);

        this.registry = registry;
        this.executor = new StepExecutor();

        // Injectable so #8 can assert on timestamps without sleeping.
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Starts a new instance of a definition and runs it until it completes,
    /// suspends or fails.
    /// </summary>
    /// <exception cref="DefinitionNotFoundException">No such definition.</exception>
    /// <exception cref="InvalidWorkflowDefinitionException">
    /// The definition declares no steps, or declares a duplicate step name.
    /// </exception>
    public Task<WorkflowInstance> StartAsync(
        string definitionId,
        int version,
        CancellationToken cancellationToken = default) =>
        this.StartAsync(definitionId, version, input: null, cancellationToken);

    /// <summary>
    /// Starts a new instance with input and runs it until it completes,
    /// suspends or fails.
    /// </summary>
    /// <exception cref="DefinitionNotFoundException">No such definition.</exception>
    /// <exception cref="InvalidInputTypeException">
    /// The input does not match what the definition declares.
    /// </exception>
    /// <exception cref="InvalidWorkflowDefinitionException">
    /// The definition declares no steps, or declares a duplicate step name.
    /// </exception>
    public async Task<WorkflowInstance> StartAsync(
        string definitionId,
        int version,
        object? input,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(definitionId);

        var definition = this.registry.Get(definitionId, version);

        // Validated before anything is constructed, so a mismatched start
        // cannot leave a half-built instance behind.
        ValidateInput(definition, input);

        var steps = Compile(definition);

        var instance = new WorkflowInstance(
            Guid.NewGuid(), definition.Id, definition.Version, this.timeProvider.GetUtcNow());

        // One store per instance. Constructed here rather than shared on the
        // engine so that concurrent instances of the same definition cannot
        // see each other's writes.
        var data = new WorkflowData();

        await this.RunAsync(instance, steps, data, input, cancellationToken).ConfigureAwait(false);

        return instance;
    }

    /// <summary>
    /// Checks the supplied input against what the definition declares.
    /// </summary>
    /// <remarks>
    /// Both directions are errors. A missing required input would surface as a
    /// null inside step code far from its cause, and an input handed to a
    /// definition that declares none would be silently discarded, leaving the
    /// author believing it was delivered.
    /// </remarks>
    private static void ValidateInput(IWorkflowDefinition definition, object? input)
    {
        var expected = definition.InputType;

        if (expected is null)
        {
            if (input is not null)
            {
                throw new InvalidInputTypeException(definition.Id, null, input.GetType());
            }

            return;
        }

        if (input is null)
        {
            throw new InvalidInputTypeException(definition.Id, expected, null);
        }

        if (!expected.IsInstanceOfType(input))
        {
            throw new InvalidInputTypeException(definition.Id, expected, input.GetType());
        }
    }

    /// <summary>
    /// Compiles a definition into its ordered step list.
    /// </summary>
    private static IReadOnlyList<WorkflowStep> Compile(IWorkflowDefinition definition)
    {
        var builder = new WorkflowBuilder(definition.Id);
        definition.Build(builder);
        return builder.Build();
    }

    /// <summary>
    /// Drives an instance forward until it completes, suspends or fails.
    /// </summary>
    private async Task RunAsync(
        WorkflowInstance instance,
        IReadOnlyList<WorkflowStep> steps,
        IWorkflowData data,
        object? input,
        CancellationToken cancellationToken)
    {
        while (instance.CurrentStepIndex < steps.Count)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var step = steps[instance.CurrentStepIndex];
            instance.CurrentStepName = step.Name;

            var context = new StepContext(instance.Id, step.Name, data, input);
            var result = await this.executor
                .ExecuteAsync(step.BodyFactory(), context, cancellationToken)
                .ConfigureAwait(false);

            if (result.Status == StepStatus.Failed)
            {
                instance.Status = InstanceStatus.Failed;
                instance.Error = result.Error;
                instance.FailedStepName = result.StepName;
                instance.CompletedAt = this.timeProvider.GetUtcNow();
                return;
            }

            if (!result.ShouldAdvance)
            {
                // The step asked to be resumed later. Stay positioned on it so
                // that resuming re-enters the same step rather than skipping it.
                instance.Status = InstanceStatus.Suspended;
                return;
            }

            instance.CurrentStepIndex++;
        }

        // Every step advanced, so there is nothing left to run.
        instance.CurrentStepName = null;
        instance.Status = InstanceStatus.Completed;
        instance.CompletedAt = this.timeProvider.GetUtcNow();
    }
}
