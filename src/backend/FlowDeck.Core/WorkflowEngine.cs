using FlowDeck.Core.Persistence;

namespace FlowDeck.Core;

/// <summary>
/// Executes workflow instances, checkpointing progress after every step.
/// </summary>
/// <remarks>
/// Follows ADR-0013: the instance record is the authoritative checkpoint. State
/// is written after every step, so at most one step of progress can be lost.
///
/// <para>
/// Steps are recompiled from the registry rather than cached across calls, so
/// an instance can be resumed by any engine holding the same definitions -
/// including one in a process that did not start it. That is what makes #14
/// possible.
/// </para>
/// </remarks>
public sealed class WorkflowEngine
{
    private readonly WorkflowRegistry registry;
    private readonly TimeProvider timeProvider;
    private readonly IWorkflowStore store;
    private readonly Random? random;

    public WorkflowEngine(
        WorkflowRegistry registry,
        TimeProvider? timeProvider = null,
        IWorkflowStore? store = null,
        Random? random = null)
    {
        ArgumentNullException.ThrowIfNull(registry);

        this.registry = registry;
        // Injectable so #8 can assert on timestamps without sleeping.
        this.timeProvider = timeProvider ?? TimeProvider.System;

        // Defaults to in-memory so tests and samples need no database. #17
        // substitutes the EF Core provider without changing this class.
        this.store = store ?? new InMemoryWorkflowStore();

        // Injectable so a jitter test can pin the delay. Randomness that
        // cannot be pinned makes a backoff test either flaky or vacuous.
        this.random = random;
    }

    /// <summary>
    /// Starts a new instance and runs it until it completes, suspends or fails.
    /// </summary>
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

        // Validated before anything is created, so a mismatched start cannot
        // leave a half-built instance behind.
        ValidateInput(definition, input);

        var steps = Compile(definition);

        var instance = new WorkflowInstance(
            Guid.NewGuid(), definition.Id, definition.Version, this.timeProvider.GetUtcNow());

        var data = new WorkflowData();

        // Written before execution starts, so an instance that suspends or
        // fails partway is still queryable. Recording it afterwards would hide
        // exactly the instances an operator needs to find.
        await this.store.CreateAsync(instance.ToRecord(data, input), cancellationToken).ConfigureAwait(false);
        instance.Revision = 1;

        await this.RunAsync(instance, steps, data, input, cancellationToken).ConfigureAwait(false);

        return instance;
    }

    /// <summary>
    /// Continues a suspended instance from the step it stopped at.
    /// </summary>
    /// <exception cref="InstanceNotFoundException">No such instance.</exception>
    /// <exception cref="InvalidStateTransitionException">Not suspended.</exception>
    public async Task<WorkflowInstance> ResumeAsync(
        Guid instanceId,
        CancellationToken cancellationToken = default)
    {
        var record = await this.store.FindAsync(instanceId, cancellationToken).ConfigureAwait(false)
            ?? throw new InstanceNotFoundException(instanceId);

        if (record.Status != InstanceStatus.Suspended)
        {
            throw new InvalidStateTransitionException(instanceId, record.Status, InstanceStatus.Running);
        }

        var definition = this.registry.Get(record.DefinitionId, record.DefinitionVersion);
        var steps = Compile(definition);

        var instance = WorkflowInstance.FromRecord(record);
        instance.Status = InstanceStatus.Running;

        var data = new WorkflowData(record.Data);

        await this.RunAsync(instance, steps, data, record.Input, cancellationToken).ConfigureAwait(false);

        return instance;
    }

    /// <summary>Retrieves an instance by id.</summary>
    /// <exception cref="InstanceNotFoundException">No such instance.</exception>
    public async Task<WorkflowInstance> GetInstanceAsync(
        Guid instanceId,
        CancellationToken cancellationToken = default)
    {
        var record = await this.store.FindAsync(instanceId, cancellationToken).ConfigureAwait(false)
            ?? throw new InstanceNotFoundException(instanceId);

        return WorkflowInstance.FromRecord(record);
    }

    /// <summary>Retrieves an instance by id, or null if unknown.</summary>
    public async Task<WorkflowInstance?> FindInstanceAsync(
        Guid instanceId,
        CancellationToken cancellationToken = default)
    {
        var record = await this.store.FindAsync(instanceId, cancellationToken).ConfigureAwait(false);

        return record is null ? null : WorkflowInstance.FromRecord(record);
    }

    /// <summary>Lists instances, most recently created first.</summary>
    public async Task<IReadOnlyList<WorkflowInstance>> ListInstancesAsync(
        InstanceFilter? filter = null,
        CancellationToken cancellationToken = default)
    {
        var records = await this.store
            .ListAsync(filter ?? new InstanceFilter(), cancellationToken)
            .ConfigureAwait(false);

        return [.. records.Select(WorkflowInstance.FromRecord)];
    }

    /// <summary>
    /// Counts instances matching a filter, ignoring its paging.
    /// </summary>
    /// <remarks>
    /// A caller rendering "page 3 of 12" needs the total alongside the page. A
    /// count that respected <c>Skip</c> and <c>Take</c> would always equal the
    /// page size and tell them nothing.
    /// </remarks>
    public Task<int> CountInstancesAsync(
        InstanceFilter? filter = null,
        CancellationToken cancellationToken = default) =>
        this.store.CountAsync(filter ?? new InstanceFilter(), cancellationToken);

    /// <summary>
    /// Reads an instance's execution history, in execution order.
    /// </summary>
    /// <remarks>
    /// Append-only and never rewritten: one entry per step execution, including
    /// failures and suspensions. Empty for an unknown instance rather than an
    /// error - history that has been purged (#20) is not an exceptional case.
    /// </remarks>
    public Task<IReadOnlyList<StepHistoryEntry>> GetHistoryAsync(
        Guid instanceId,
        CancellationToken cancellationToken = default) =>
        this.store.GetHistoryAsync(instanceId, cancellationToken);

    /// <summary>Stops an instance permanently.</summary>
    /// <exception cref="InstanceNotFoundException">No such instance.</exception>
    /// <exception cref="InvalidStateTransitionException">Already terminal.</exception>
    public async Task<WorkflowInstance> CancelAsync(
        Guid instanceId,
        CancellationToken cancellationToken = default)
    {
        var record = await this.store.FindAsync(instanceId, cancellationToken).ConfigureAwait(false)
            ?? throw new InstanceNotFoundException(instanceId);

        var instance = WorkflowInstance.FromRecord(record);

        if (instance.IsTerminal)
        {
            throw new InvalidStateTransitionException(instanceId, instance.Status, InstanceStatus.Cancelled);
        }

        instance.Status = InstanceStatus.Cancelled;
        instance.CompletedAt = this.timeProvider.GetUtcNow();

        // CurrentStepName is left intact so an operator can still see where the
        // instance stopped.
        var saved = await this.store
            .SaveAsync(instance.ToRecord(new WorkflowData(record.Data), record.Input), [], cancellationToken)
            .ConfigureAwait(false);

        instance.Revision = saved.Revision;

        return instance;
    }

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

    private static IReadOnlyList<StepDeclaration> Compile(IWorkflowDefinition definition)
    {
        var builder = new WorkflowBuilder(definition.Id);
        definition.Build(builder);
        return builder.Build();
    }

    /// <summary>
    /// Drives an instance forward, checkpointing after every step.
    /// </summary>
    private async Task RunAsync(
        WorkflowInstance instance,
        IReadOnlyList<StepDeclaration> steps,
        IWorkflowData data,
        object? input,
        CancellationToken cancellationToken)
    {
        while (instance.CurrentStepIndex < steps.Count)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var step = steps[instance.CurrentStepIndex];
            instance.CurrentStepName = step.Name;

            var startedAt = this.timeProvider.GetUtcNow();

            var context = new StepContext(instance.Id, step.Name, data, input);
            var result = await StepExecutor
                .ExecuteAsync(step.Factory(), context, cancellationToken)
                .ConfigureAwait(false);

            // Recorded for every execution, including failures and suspensions.
            // History that only covered successes would be silent about exactly
            // the runs an operator opens it to investigate.
            var entry = new StepHistoryEntry
            {
                InstanceId = instance.Id,
                Sequence = 0, // assigned by the store
                StepName = step.Name,
                StartedAt = startedAt,
                CompletedAt = this.timeProvider.GetUtcNow(),
                Status = result.Status,

                // StepAttempts counts attempts already finished, so the one
                // just executed is the next number. Read before the increment
                // below, which is why this is +1 rather than the field itself.
                Attempt = instance.StepAttempts + 1,
                ErrorType = result.Error?.GetType().Name,
                ErrorMessage = result.Error?.Message,
            };

            if (result.Status == StepStatus.Failed)
            {
                instance.StepAttempts++;

                if (step.RetryPolicy.AllowsAnotherAttempt(instance.StepAttempts))
                {
                    // Checkpointed before waiting, so the attempt count is
                    // durable. An in-memory counter would reset on restart and
                    // a host recycling during an outage would retry forever.
                    await this.CheckpointAsync(instance, data, input, [entry], cancellationToken)
                        .ConfigureAwait(false);

                    var delay = step.RetryPolicy.DelayBefore(instance.StepAttempts + 1, this.random);

                    if (delay > TimeSpan.Zero)
                    {
                        // Blocks the caller. The engine is synchronous, so
                        // there is nowhere else for the wait to live yet -
                        // releasing the worker is a scheduling question that
                        // overlaps #39, and ADR-0020 leaves it open.
                        await Task.Delay(delay, this.timeProvider, cancellationToken).ConfigureAwait(false);
                    }

                    continue;
                }

                instance.Status = InstanceStatus.Failed;
                instance.Error = result.Error;
                instance.ErrorType = result.Error?.GetType().Name;
                instance.ErrorMessage = result.Error?.Message;
                instance.FailedStepName = result.StepName;
                instance.CompletedAt = this.timeProvider.GetUtcNow();

                await this.CheckpointAsync(instance, data, input, [entry], cancellationToken).ConfigureAwait(false);
                return;
            }

            // Reset on success: advancing past a step means the next arrival at
            // any step starts fresh, rather than inheriting a count from work
            // that has already succeeded.
            instance.StepAttempts = 0;

            if (!result.ShouldAdvance)
            {
                // The step asked to be resumed later. Stay positioned on it so
                // resuming re-enters the same step rather than skipping it.
                instance.Status = InstanceStatus.Suspended;

                await this.CheckpointAsync(instance, data, input, [entry], cancellationToken).ConfigureAwait(false);
                return;
            }

            instance.CurrentStepIndex++;

            // Checkpointed after advancing, so recovery never re-runs a step
            // that already completed - the property NFR-1 rests on.
            await this.CheckpointAsync(instance, data, input, [entry], cancellationToken).ConfigureAwait(false);
        }

        instance.CurrentStepName = null;
        instance.Status = InstanceStatus.Completed;
        instance.CompletedAt = this.timeProvider.GetUtcNow();

        await this.CheckpointAsync(instance, data, input, [], cancellationToken).ConfigureAwait(false);
    }

    private async Task CheckpointAsync(
        WorkflowInstance instance,
        IWorkflowData data,
        object? input,
        IReadOnlyList<StepHistoryEntry> history,
        CancellationToken cancellationToken)
    {
        // State and history go together: ADR-0013 requires the store to write
        // them atomically, so a crash cannot leave one without the other.
        var saved = await this.store
            .SaveAsync(instance.ToRecord(data, input), history, cancellationToken)
            .ConfigureAwait(false);

        instance.Revision = saved.Revision;
    }
}
