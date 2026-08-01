using FlowDeck.Core.Persistence;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

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
    private readonly ILogger<WorkflowEngine> logger;

    public WorkflowEngine(
        WorkflowRegistry registry,
        TimeProvider? timeProvider = null,
        IWorkflowStore? store = null,
        Random? random = null,
        ILogger<WorkflowEngine>? logger = null)
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

        // Optional, and null means silent rather than broken. Every existing
        // test constructs an engine without one, and an engine that required a
        // logger would make observability a precondition of running a workflow
        // rather than a thing a host switches on (ADR-0025 decision 1).
        this.logger = logger ?? NullLogger<WorkflowEngine>.Instance;
    }

    /// <summary>
    /// Puts an instance's identity on every entry written inside the scope.
    /// </summary>
    /// <remarks>
    /// A scope rather than a field repeated at each call site, so that an entry
    /// added later cannot forget it and so a structured sink has something to
    /// group a run by (ADR-0025 decision 5).
    ///
    /// <para>
    /// Carries only identity. Everything here is engine-assigned or
    /// author-declared metadata; no workflow data reaches it, which is the
    /// boundary ADR-0025 decision 3 draws.
    /// </para>
    /// </remarks>
    private IDisposable? Scope(WorkflowInstance instance) =>
        this.logger.BeginScope(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["InstanceId"] = instance.Id,
            ["DefinitionId"] = instance.DefinitionId,
            ["DefinitionVersion"] = instance.DefinitionVersion,
        });

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

        using var scope = this.Scope(instance);

        // After the record exists, so an entry saying an instance started is
        // never about one an operator cannot then look up.
        this.logger.InstanceStarted(instance.DefinitionId, instance.DefinitionVersion);

        await this.RunAsync(instance, steps, data, input, resumeFrom: [], cancellationToken).ConfigureAwait(false);

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

        using var scope = this.Scope(instance);

        this.logger.InstanceResumed(instance.DefinitionId, instance.CurrentStepName);

        // The stored set, not the index. A forked instance is at several places
        // at once and the index names only the step that forked, so resuming
        // from it would re-run that step and every branch step already done
        // (#166).
        await this.RunAsync(instance, steps, data, record.Input, record.ActiveNodes, cancellationToken)
            .ConfigureAwait(false);

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

        using var scope = this.Scope(instance);

        // After the save, so the entry describes a cancellation that is durable
        // rather than one a concurrency failure was about to undo.
        this.logger.InstanceCancelled(instance.DefinitionId, instance.CurrentStepName);

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
    /// <remarks>
    /// The top-level sequence is walked by the same code that walks a branch;
    /// the only difference is that the top-level one also maintains
    /// <see cref="WorkflowInstance.CurrentStepIndex"/>, which describes a
    /// straight line and cannot describe a fork (ADR-0024).
    /// </remarks>
    private async Task RunAsync(
        WorkflowInstance instance,
        IReadOnlyList<StepDeclaration> steps,
        IWorkflowData data,
        object? input,
        IReadOnlyList<ActiveNode> resumeFrom,
        CancellationToken cancellationToken)
    {
        var run = new Run(this, instance, data, input);

        var progress = await run
            .SequenceAsync(steps, instance.CurrentStepIndex, branchPath: [], resumeFrom, cancellationToken)
            .ConfigureAwait(false);

        switch (progress)
        {
            case Progress.Suspended:
                // The step that suspended already checkpointed. Nothing more
                // happens until something resumes the instance.
                this.logger.InstanceSuspended(instance.DefinitionId, instance.CurrentStepName);
                return;

            case Progress.Failed:
                // Logged before the rollback, so the cause is on record even if
                // compensation then fails and floods the log with its own
                // trouble. The same ordering the engine already uses when it
                // records the failure before compensating.
                this.logger.InstanceFailed(
                    instance.DefinitionId,
                    instance.FailedStepName,
                    instance.ErrorType,
                    instance.ErrorMessage);

                // The instance stays Running through the rollback. ADR-0008
                // makes terminal states final, so compensation has to happen
                // before one is reached, never after.
                instance.Status = await this
                    .CompensateAsync(run, instance, steps, data, input, cancellationToken)
                    .ConfigureAwait(false);

                // Points at the step that stopped it, wherever in the graph that
                // was, rather than at whichever sequence position the rollback
                // finished on.
                instance.CurrentStepName = instance.FailedStepName;
                instance.CompletedAt = this.timeProvider.GetUtcNow();

                await run.CheckpointAsync([], cancellationToken).ConfigureAwait(false);

                // Only where a rollback actually ran. An instance with no
                // compensating actions settles as Failed, and saying it "rolled
                // back" would describe work that did not happen.
                if (instance.Status != InstanceStatus.Failed)
                {
                    this.logger.InstanceCompensated(instance.DefinitionId, instance.Status);
                }

                return;

            default:
                instance.CurrentStepName = null;
                instance.Status = InstanceStatus.Completed;
                instance.CompletedAt = this.timeProvider.GetUtcNow();

                await run.CheckpointAsync([], cancellationToken).ConfigureAwait(false);

                this.logger.InstanceCompleted(
                    instance.DefinitionId,
                    (instance.CompletedAt.Value - instance.CreatedAt).TotalMilliseconds);

                return;
        }
    }

    /// <summary>What a sequence of steps did before it handed control back.</summary>
    private enum Progress
    {
        /// <summary>Every step ran. For a branch, it reached its join.</summary>
        Completed,

        /// <summary>A step asked to be resumed later.</summary>
        Suspended,

        /// <summary>A step failed and had no attempts left.</summary>
        Failed,
    }

    /// <summary>
    /// One execution of one instance, across however many branches it forks
    /// into.
    /// </summary>
    /// <remarks>
    /// Exists because a fork gives an instance several simultaneous positions
    /// and one shared store revision. Both live here rather than on
    /// <see cref="WorkflowEngine"/>, which is shared by every instance, or on
    /// <see cref="WorkflowInstance"/>, which is also the shape callers hold
    /// after the run has finished.
    /// </remarks>
    private sealed class Run(
        WorkflowEngine engine,
        WorkflowInstance instance,
        IWorkflowData data,
        object? input)
    {
        /// <summary>
        /// The single writer of ADR-0024 decision 3.
        /// </summary>
        /// <remarks>
        /// Concurrent branches each holding a stale <c>Revision</c> would have
        /// every save but one rejected - #19's optimistic concurrency turned
        /// into a livelock by design rather than a race. Serialising the writes
        /// keeps the revision meaning what it always meant, and still lets the
        /// slow part, the step bodies, overlap.
        /// </remarks>
        private readonly SemaphoreSlim writer = new(1, 1);

        private readonly Lock cursorGate = new();
        private readonly List<Cursor> cursors = [];

        /// <summary>
        /// Runs a sequence of steps, fanning out wherever one branches.
        /// </summary>
        /// <param name="branchPath">
        /// The branches taken to reach this sequence, outermost first. Empty
        /// identifies the top-level sequence, which is the only one that owns
        /// the instance's linear position.
        /// </param>
        /// <param name="resumeFrom">
        /// The stored active set when this run is a recovery, or empty when it
        /// is starting fresh.
        /// </param>
        public async Task<Progress> SequenceAsync(
            IReadOnlyList<StepDeclaration> steps,
            int startIndex,
            IReadOnlyList<string> branchPath,
            IReadOnlyList<ActiveNode> resumeFrom,
            CancellationToken cancellationToken,
            Cursor? opened = null)
        {
            var topLevel = branchPath.Count == 0;
            var resumeAt = -1;
            var branchesOnly = false;

            if (resumeFrom.Count > 0)
            {
                var plan = Resumption(steps, resumeFrom);

                if (plan is null)
                {
                    // Nothing in the stored set belongs to this sequence, so it
                    // had already finished when the crash happened. Re-running
                    // it is precisely what NFR-1 forbids.
                    //
                    // A safety net rather than the path a recovered fork takes:
                    // BranchAsync drops finished arms before starting them, so
                    // it normally gets here first. Breaking this branch alone
                    // therefore fails nothing, which is deliberate - the filter
                    // there has to exist anyway, because an arm that reached
                    // this point would already have published a position on
                    // work that was done.
                    return Progress.Completed;
                }

                (resumeAt, branchesOnly) = plan.Value;
                startIndex = resumeAt;
            }

            // A fork opens its arms' cursors before it starts them, so the
            // checkpoint recording the fork already names every arm. An arm that
            // opened its own cursor here would not exist until it ran, and a
            // crash in between would find an instance recorded at the step that
            // forked rather than at the branches it forked into.
            var cursor = opened ?? this.Open(branchPath);

            try
            {
                for (var index = startIndex; index < steps.Count; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var step = steps[index];

                    cursor.StepName = step.Name;
                    cursor.Attempts = topLevel ? instance.StepAttempts : 0;

                    if (topLevel)
                    {
                        instance.CurrentStepName = step.Name;
                    }

                    var next = index + 1 < steps.Count ? steps[index + 1].Name : null;

                    // The one step a recovered fork must not re-run. Its
                    // branches were already open when the crash happened, so it
                    // had finished doing whatever it does; only the join is
                    // outstanding.
                    var reopeningAFork = index == resumeAt && branchesOnly;

                    if (!reopeningAFork)
                    {
                        var progress = await this
                            .StepAsync(step, cursor, topLevel, index, next, cancellationToken)
                            .ConfigureAwait(false);

                        if (progress != Progress.Completed)
                        {
                            return progress;
                        }
                    }

                    if (step.Branches.Count == 0)
                    {
                        continue;
                    }

                    // The branching step is not past until its branches have
                    // joined, so the position stayed on it through the
                    // checkpoint above. A crash mid-fork therefore re-enters the
                    // branching step on recovery, re-running it and every branch
                    // step that had completed - #166's problem, named here
                    // rather than left to be discovered.
                    // Not executing while its branches are: the step that forked
                    // sits at the join, and reporting it as active alongside them
                    // would name a place nothing is running.
                    cursor.StepName = null;

                    var branched = await this
                        .BranchAsync(step, branchPath, reopeningAFork ? resumeFrom : [], cancellationToken)
                        .ConfigureAwait(false);

                    if (branched != Progress.Completed)
                    {
                        return branched;
                    }

                    cursor.StepName = next;

                    if (topLevel)
                    {
                        instance.CurrentStepIndex = index + 1;
                    }

                    await this.CheckpointAsync([], cancellationToken).ConfigureAwait(false);
                }

                return Progress.Completed;
            }
            finally
            {
                // Whatever happened, this sequence is no longer anywhere. A
                // cursor left behind would report a finished branch as still
                // running for the rest of the instance's life.
                this.Close(cursor);
            }
        }

        /// <summary>
        /// Writes the instance's state and any history through the one writer.
        /// </summary>
        public async Task CheckpointAsync(
            IReadOnlyList<StepHistoryEntry> history,
            CancellationToken cancellationToken)
        {
            await this.writer.WaitAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                // The record is built inside the gate as well as saved inside
                // it. Building it outside would snapshot a revision another
                // branch had already superseded by the time this save ran.
                var saved = await engine.store
                    .SaveAsync(instance.ToRecord(data, input, this.Snapshot()), history, cancellationToken)
                    .ConfigureAwait(false);

                instance.Revision = saved.Revision;
            }
            finally
            {
                this.writer.Release();
            }
        }

        /// <summary>
        /// Executes one step, retrying it as its policy allows.
        /// </summary>
        private async Task<Progress> StepAsync(
            StepDeclaration step,
            Cursor cursor,
            bool topLevel,
            int index,
            string? nextStepName,
            CancellationToken cancellationToken)
        {
            while (true)
            {
                var startedAt = engine.timeProvider.GetUtcNow();

                var context = new StepContext(instance.Id, step.Name, data, input);
                var result = await StepExecutor
                    .ExecuteAsync(step.Factory(), context, cancellationToken)
                    .ConfigureAwait(false);

                // Recorded for every execution, including failures and
                // suspensions. History that only covered successes would be
                // silent about exactly the runs an operator opens it to
                // investigate.
                var entry = new StepHistoryEntry
                {
                    InstanceId = instance.Id,
                    Sequence = 0, // assigned by the store
                    StepName = step.Name,
                    StartedAt = startedAt,
                    CompletedAt = engine.timeProvider.GetUtcNow(),
                    Status = result.Status,

                    // The count holds attempts already finished, so the one just
                    // executed is the next number.
                    Attempt = cursor.Attempts + 1,
                    ErrorType = result.Error?.GetType().Name,
                    ErrorMessage = result.Error?.Message,
                };

                if (result.Status == StepStatus.Failed)
                {
                    cursor.Attempts++;

                    if (topLevel)
                    {
                        instance.StepAttempts = cursor.Attempts;
                    }

                    if (step.RetryPolicy.AllowsAnotherAttempt(cursor.Attempts))
                    {
                        // Checkpointed before waiting, so the attempt count is
                        // durable. An in-memory counter would reset on restart
                        // and a host recycling during an outage would retry
                        // forever.
                        await this.CheckpointAsync([entry], cancellationToken).ConfigureAwait(false);

                        var delay = step.RetryPolicy.DelayBefore(cursor.Attempts + 1, engine.random);

                        if (delay > TimeSpan.Zero)
                        {
                            // Blocks this branch. The engine is synchronous
                            // within a branch, so there is nowhere else for the
                            // wait to live yet - ADR-0020 leaves it open.
                            await Task.Delay(delay, engine.timeProvider, cancellationToken).ConfigureAwait(false);
                        }

                        continue;
                    }

                    // Recorded before rolling back, so the cause survives
                    // whatever the rollback does. A compensating action's error
                    // overwriting it would leave an operator debugging the
                    // cleanup instead of the problem.
                    this.RecordFailure(step.Name, result.Error);

                    await this.CheckpointAsync([entry], cancellationToken).ConfigureAwait(false);

                    return Progress.Failed;
                }

                if (!result.ShouldAdvance)
                {
                    if (!topLevel)
                    {
                        // What is unsettled is not where the instance is - the
                        // position has been set-valued since #166 - but what
                        // Suspended would mean while sibling branches are still
                        // running. Failure has an answer: siblings run on and
                        // the join fails. Suspension has none, so this fails
                        // loudly rather than parking an instance in a state no
                        // rule covers. Lifted by #179.
                        var unsupported = new NotSupportedException(
                            $"Step '{step.Name}' suspended inside branch '{string.Join('/', cursor.BranchPath)}'. "
                            + "Suspending inside a branch is not supported yet (#179).");

                        this.RecordFailure(step.Name, unsupported);

                        await this.CheckpointAsync(
                            [
                                entry with
                                {
                                    Status = StepStatus.Failed,
                                    ErrorType = nameof(NotSupportedException),
                                    ErrorMessage = unsupported.Message,
                                },
                            ],
                            cancellationToken).ConfigureAwait(false);

                        return Progress.Failed;
                    }

                    // The step asked to be resumed later. Stay positioned on it
                    // so resuming re-enters the same step rather than skipping
                    // it.
                    instance.Status = InstanceStatus.Suspended;

                    await this.CheckpointAsync([entry], cancellationToken).ConfigureAwait(false);

                    return Progress.Suspended;
                }

                // Reset on success: advancing past a step means the next arrival
                // at any step starts fresh, rather than inheriting a count from
                // work that has already succeeded.
                cursor.Attempts = 0;

                // A branching step is not finished until its branches have
                // joined, so only a plain step advances here. Checkpointing
                // after advancing is what stops recovery re-running a step that
                // already completed - the property NFR-1 rests on.
                if (topLevel)
                {
                    instance.StepAttempts = 0;
                }

                if (step.Branches.Count == 0)
                {
                    // Where this sequence would resume, not where it has been.
                    // Left naming the step it just finished, a recovered
                    // instance would re-run it; left naming nothing, a recovered
                    // instance would be nowhere at all.
                    cursor.StepName = nextStepName;

                    if (topLevel)
                    {
                        instance.CurrentStepIndex = index + 1;
                    }
                }

                await this.CheckpointAsync([entry], cancellationToken).ConfigureAwait(false);

                return Progress.Completed;
            }
        }

        /// <summary>
        /// Runs whatever branches leave a step: every arm of a fork, or the one
        /// arm of a choice whose condition holds.
        /// </summary>
        private async Task<Progress> BranchAsync(
            StepDeclaration step,
            IReadOnlyList<string> branchPath,
            IReadOnlyList<ActiveNode> resumeFrom,
            CancellationToken cancellationToken)
        {
            if (!step.Branches[0].IsParallel)
            {
                // On recovery the branch is whichever one the stored position is
                // inside. Re-evaluating the condition would usually agree and
                // would be wrong when it did not: a predicate over data a later
                // step has since changed would send the instance down a path it
                // had not taken.
                var chosen = resumeFrom.Count > 0
                    ? step.Branches.FirstOrDefault(branch => Holds(branch.Steps, resumeFrom))

                    // Conditions are tested in declaration order and the first
                    // match wins, so an author reads them the way they read an
                    // if/else if chain. No match is an ordinary shape, not an
                    // error: failing would make every branch set implicitly
                    // require a catch-all (ADR-0024 decision 6).
                    : step.Branches.FirstOrDefault(branch => branch.Condition?.Invoke(data) == true);

                return chosen is null
                    ? Progress.Completed
                    : await this
                        .SequenceAsync(
                            chosen.Steps, 0, [.. branchPath, chosen.Name], resumeFrom, cancellationToken)
                        .ConfigureAwait(false);
            }

            // On recovery, only the arms the stored set still names. An arm that
            // finished before the crash left no active node behind, and running
            // it again would re-execute completed work on a sibling branch -
            // worse than not recovering at all.
            var live = resumeFrom.Count == 0
                ? step.Branches
                : [.. step.Branches.Where(branch => Holds(branch.Steps, resumeFrom))];

            if (live.Count == 0)
            {
                return Progress.Completed;
            }

            // Task.Run rather than just calling the async method: an async
            // method runs synchronously until its first await, so a step body
            // that blocks instead of awaiting would hold up every later arm and
            // the fork would silently be a sequence. Author code is untrusted
            // about whether it awaits, the same way it is untrusted about
            // whether it throws.
            var arms = live
                .Select(branch =>
                {
                    IReadOnlyList<string> path = [.. branchPath, branch.Name];
                    var cursor = this.Open(path);

                    // Named before the arm starts, so the checkpoint below
                    // records where each arm is about to be rather than an empty
                    // set that says the instance is nowhere. On recovery that is
                    // where the arm stopped, not where it began.
                    cursor.StepName = Held(branch.Steps, resumeFrom)?.StepName ?? branch.Steps[0].Name;

                    return (Branch: branch, Path: path, Cursor: cursor);
                })
                .ToArray();

            // The fork is durable before any arm runs. A crash between opening
            // the fork and the first arm's first checkpoint would otherwise
            // leave an instance recorded at the step that forked, and recovery
            // would have no way to know that it had.
            await this.CheckpointAsync([], cancellationToken).ConfigureAwait(false);

            var running = arms
                .Select(arm => Task.Run(
                    () => this.SequenceAsync(
                        arm.Branch.Steps, 0, arm.Path, resumeFrom, cancellationToken, arm.Cursor),
                    cancellationToken))
                .ToArray();

            // Every arm is awaited even once one has failed. The join waits for
            // all of them (ADR-0024 decision 6): abandoning a sibling mid-step
            // would not stop its side effects, only stop the engine from
            // recording them.
            var results = await Task.WhenAll(running).ConfigureAwait(false);

            if (Array.IndexOf(results, Progress.Failed) >= 0)
            {
                return Progress.Failed;
            }

            return Array.IndexOf(results, Progress.Suspended) >= 0
                ? Progress.Suspended
                : Progress.Completed;
        }

        /// <summary>
        /// Where a sequence resumes, or <see langword="null"/> if it had already
        /// finished when the crash happened.
        /// </summary>
        /// <remarks>
        /// Matched on step <b>name</b>, not on the branch path. Names are unique
        /// graph-wide (#162), so a name identifies a node; a path does not -
        /// <c>Fork</c> labels every fork's arms <c>branch-1</c> and
        /// <c>branch-2</c>, so two forks in one workflow produce identical
        /// paths. The path is for reading a position, not for finding one.
        ///
        /// <para>
        /// Two answers, and the difference is what stops a fork being re-opened.
        /// If the set names a step of this sequence, the sequence stopped there
        /// and resumes by running it. If it instead names a step somewhere
        /// inside one of this sequence's branches, the sequence had already
        /// passed that branching step and is waiting at the join, so the step
        /// itself must not run again.
        /// </para>
        /// </remarks>
        private static (int Index, bool BranchesOnly)? Resumption(
            IReadOnlyList<StepDeclaration> steps,
            IReadOnlyList<ActiveNode> resumeFrom)
        {
            for (var index = 0; index < steps.Count; index++)
            {
                if (resumeFrom.Any(node => string.Equals(node.StepName, steps[index].Name, StringComparison.Ordinal)))
                {
                    return (index, false);
                }

                if (steps[index].Branches.Any(branch => Holds(branch.Steps, resumeFrom)))
                {
                    return (index, true);
                }
            }

            return null;
        }

        /// <summary>Whether any stored node names a step in this subtree.</summary>
        private static bool Holds(IReadOnlyList<StepDeclaration> steps, IReadOnlyList<ActiveNode> resumeFrom) =>
            Held(steps, resumeFrom) is not null;

        /// <summary>The stored node naming a step in this subtree, if any.</summary>
        private static ActiveNode? Held(IReadOnlyList<StepDeclaration> steps, IReadOnlyList<ActiveNode> resumeFrom)
        {
            foreach (var step in steps)
            {
                var here = resumeFrom.FirstOrDefault(
                    node => string.Equals(node.StepName, step.Name, StringComparison.Ordinal));

                if (here is not null)
                {
                    return here;
                }

                foreach (var branch in step.Branches)
                {
                    var deeper = Held(branch.Steps, resumeFrom);

                    if (deeper is not null)
                    {
                        return deeper;
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// Records the first failure to reach it, and ignores later ones.
        /// </summary>
        /// <remarks>
        /// Two branches can fail at once. The instance reports one of them, and
        /// which one is whichever failed first in wall-clock terms rather than
        /// in declaration order - there is no order between concurrent arms to
        /// prefer. Both failures are in history either way.
        /// </remarks>
        private void RecordFailure(string stepName, Exception? error)
        {
            lock (this.cursorGate)
            {
                if (instance.FailedStepName is not null)
                {
                    return;
                }

                instance.Error = error;
                instance.ErrorType = error?.GetType().Name;
                instance.ErrorMessage = error?.Message;
                instance.FailedStepName = stepName;
            }
        }

        private Cursor Open(IReadOnlyList<string> branchPath)
        {
            var cursor = new Cursor(branchPath);

            lock (this.cursorGate)
            {
                this.cursors.Add(cursor);
            }

            return cursor;
        }

        private void Close(Cursor cursor)
        {
            lock (this.cursorGate)
            {
                this.cursors.Remove(cursor);
            }
        }

        /// <summary>
        /// Every place this instance is right now, in the order the sequences
        /// started.
        /// </summary>
        private IReadOnlyList<ActiveNode> Snapshot()
        {
            lock (this.cursorGate)
            {
                return
                [
                    .. this.cursors
                        .Where(cursor => cursor.StepName is not null)
                        .Select(cursor => new ActiveNode(cursor.StepName!, cursor.Attempts, cursor.BranchPath)),
                ];
            }
        }

        /// <summary>One running sequence's position within itself.</summary>
        internal sealed class Cursor(IReadOnlyList<string> branchPath)
        {
            public IReadOnlyList<string> BranchPath { get; } = branchPath;

            /// <summary>The step being executed, or null between steps.</summary>
            public string? StepName { get; set; }

            public int Attempts { get; set; }
        }
    }

    /// <summary>
    /// Undoes the steps that already ran, most recent first.
    /// </summary>
    /// <returns>
    /// The terminal status the instance should take: <see cref="InstanceStatus.Failed"/>
    /// if nothing was rolled back, <see cref="InstanceStatus.Compensated"/> if
    /// every action succeeded, <see cref="InstanceStatus.CompensationFailed"/>
    /// otherwise.
    /// </returns>
    /// <remarks>
    /// Per ADR-0021. Two decisions are visible here and both are deliberate:
    ///
    /// <list type="bullet">
    /// <item>The step that just failed <b>is</b> compensated, exactly once. It
    /// may never have reported success and still have had an effect - the
    /// charge that reached the gateway and then timed out.</item>
    /// <item>A failing compensating action does <b>not</b> stop the rollback.
    /// Stopping would leave more un-undone work than continuing.</item>
    /// </list>
    ///
    /// <para>
    /// This makes the reverse pass asymmetric with the forward one, which stops
    /// at the first failure. That is a genuine inconsistency, chosen rather
    /// than overlooked.
    /// </para>
    ///
    /// <para>
    /// <b>What "most recent" means once branches exist.</b> Reverse execution
    /// order is not well defined when two branches ran at the same time, so
    /// ADR-0024 decision 7 orders by when each step <i>completed</i>. History is
    /// exactly that record: every execution is appended by the same single
    /// writer the moment the step finishes, so its sequence numbers <b>are</b>
    /// completion order. Walking history backwards therefore needs no second
    /// clock and no ordering the engine has to keep in step with the truth.
    /// </para>
    ///
    /// <para>
    /// Reading the record rather than remembering it also survives a restart: an
    /// instance resumed in a second process still unwinds what the first one
    /// did, which an in-memory list of completions could not do.
    /// </para>
    /// </remarks>
    private async Task<InstanceStatus> CompensateAsync(
        Run run,
        WorkflowInstance instance,
        IReadOnlyList<StepDeclaration> steps,
        IWorkflowData data,
        object? input,
        CancellationToken cancellationToken)
    {
        var compensated = 0;
        var failures = 0;

        var declarations = Flatten(steps);

        var history = await this.store.GetHistoryAsync(instance.Id, cancellationToken).ConfigureAwait(false);

        foreach (var (name, compensation) in Undoable(history, declarations))
        {
            var startedAt = this.timeProvider.GetUtcNow();

            var context = new StepContext(instance.Id, name, data, input);
            var result = await StepExecutor
                .ExecuteAsync(compensation(), context, cancellationToken)
                .ConfigureAwait(false);

            if (result.Status == StepStatus.Failed)
            {
                failures++;
            }
            else
            {
                compensated++;
            }

            // Recorded like any other execution, so "one of two undone" is a
            // fact an operator can read rather than infer.
            await run.CheckpointAsync(
                [
                    new StepHistoryEntry
                    {
                        InstanceId = instance.Id,
                        Sequence = 0, // assigned by the store
                        StepName = CompensationStepName(name),
                        StartedAt = startedAt,
                        CompletedAt = this.timeProvider.GetUtcNow(),
                        Status = result.Status,

                        // Compensation runs once per step regardless of how
                        // many times the step was attempted, so this is always
                        // 1 rather than inheriting the forward attempt count.
                        Attempt = 1,
                        ErrorType = result.Error?.GetType().Name,
                        ErrorMessage = result.Error?.Message,
                    },
                ],
                cancellationToken).ConfigureAwait(false);
        }

        if (failures > 0)
        {
            return InstanceStatus.CompensationFailed;
        }

        // Compensated has to mean something was undone. A workflow with no undo
        // actions reporting it would tell an operator the instance cleaned
        // itself up when nothing happened at all.
        return compensated > 0 ? InstanceStatus.Compensated : InstanceStatus.Failed;
    }

    /// <summary>
    /// The steps to undo, most recently completed first.
    /// </summary>
    /// <remarks>
    /// Driven by what history records, not by what the definition declares. Only
    /// steps that actually ran appear, so a step on a branch the instance never
    /// took is never undone - acting on the world because of work that never
    /// happened is the failure mode #119 named and branching makes easier to
    /// reach.
    ///
    /// <para>
    /// A step is undone <b>once</b> however many times it ran. Three retries of
    /// one step are three history entries and one effect to reverse; undoing it
    /// three times would be the compensation equivalent of the duplicate side
    /// effects retry exists to bound.
    /// </para>
    ///
    /// <para>
    /// A step that failed is included, because it may have had an effect
    /// (ADR-0021) - the charge that reached the gateway and then timed out.
    /// </para>
    ///
    /// <para>
    /// There is deliberately <b>no</b> filter on the <c>compensate:</c> prefix
    /// history uses for rollback entries. One was written and removed: the
    /// declaration lookup below already rejects those names, because no step is
    /// declared under them, so the filter never fired. Worse, it would have
    /// fired wrongly for an author who named a step <c>compensate:something</c>,
    /// silently refusing to undo a step that had run.
    /// </para>
    /// </remarks>
    private static IEnumerable<(string Name, Func<IStep> Compensation)> Undoable(
        IReadOnlyList<StepHistoryEntry> history,
        IReadOnlyDictionary<string, StepDeclaration> declarations)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);

        for (var index = history.Count - 1; index >= 0; index--)
        {
            var name = history[index].StepName;

            if (!seen.Add(name))
            {
                continue;
            }

            // A step in history with no declaration means the definition changed
            // under a running instance, which is #67's problem. Skipped rather
            // than thrown: a rollback that aborted here would leave more undone
            // than one that undoes what it still recognises.
            if (declarations.TryGetValue(name, out var step) && step.Compensation is not null)
            {
                yield return (step.Name, step.Compensation);
            }
        }
    }

    /// <summary>
    /// Every step in the graph, branches included, keyed by name.
    /// </summary>
    /// <remarks>
    /// Flat because names are unique graph-wide (#162), so a name identifies a
    /// step without needing the path that reached it. History records names, and
    /// this is what turns one back into the declaration that knows how to undo
    /// it.
    /// </remarks>
    private static IReadOnlyDictionary<string, StepDeclaration> Flatten(IReadOnlyList<StepDeclaration> steps)
    {
        var flat = new Dictionary<string, StepDeclaration>(StringComparer.Ordinal);

        void Walk(IReadOnlyList<StepDeclaration> sequence)
        {
            foreach (var step in sequence)
            {
                flat[step.Name] = step;

                foreach (var branch in step.Branches)
                {
                    Walk(branch.Steps);
                }
            }
        }

        Walk(steps);

        return flat;
    }

    /// <summary>
    /// The history name for a step's compensating action.
    /// </summary>
    /// <remarks>
    /// Prefixed rather than reusing the step's own name, so a timeline can tell
    /// the forward execution from the rollback without a second field. The
    /// prefix is part of the contract a dashboard reads.
    /// </remarks>
    internal static string CompensationStepName(string stepName) => CompensationPrefix + stepName;

    private const string CompensationPrefix = "compensate:";
}
