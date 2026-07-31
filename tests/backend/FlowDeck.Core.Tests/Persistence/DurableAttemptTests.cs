using FlowDeck.Core;
using FlowDeck.Core.Persistence;

namespace FlowDeck.Core.Tests.Persistence;

/// <summary>
/// Issue #106 - Persist the attempt count so retries survive a restart.
///
/// Scenario: The attempt count survives a restart
/// Scenario: The attempt count resets when execution advances
/// </summary>
/// <remarks>
/// A restart is simulated the same way <see cref="RestartRecoveryTests"/> does
/// it: throw the engine and registry away and build new ones over the same
/// store. Nothing survives except what was written.
/// </remarks>
public class DurableAttemptTests
{
    private sealed class AlwaysFails(List<string> log, string name) : IStep
    {
        public ValueTask<Outcome> ExecuteAsync(IStepContext context, CancellationToken cancellationToken = default)
        {
            log.Add(name);
            throw new InvalidOperationException("still down");
        }
    }

    /// <summary>Fails the first <paramref name="failures"/> executions.</summary>
    /// <remarks>
    /// Counts from the shared log rather than a field, because the engine calls
    /// the factory afresh for every execution - a field would reset each time
    /// and the step would fail forever. That is deliberate engine behaviour:
    /// steps are recompiled from the registry so any host can resume an
    /// instance, which means a step object cannot carry state between attempts.
    /// </remarks>
    private sealed class FailsThenSucceeds(List<string> log, string name, int failures) : IStep
    {
        public ValueTask<Outcome> ExecuteAsync(IStepContext context, CancellationToken cancellationToken = default)
        {
            var previous = log.Count(entry => entry == name);
            log.Add(name);

            if (previous < failures)
            {
                throw new InvalidOperationException("transient");
            }

            return ValueTask.FromResult(Outcome.Next);
        }
    }

    private sealed class Declared(string id, Action<IWorkflowBuilder> declare) : IWorkflowDefinition
    {
        public string Id => id;

        public int Version => 1;

        public void Build(IWorkflowBuilder builder) => declare(builder);
    }

    private static WorkflowEngine NewHost(IWorkflowStore store, IWorkflowDefinition definition)
    {
        var registry = new WorkflowRegistry();
        registry.Register(definition);
        return new WorkflowEngine(registry, store: store);
    }

    [Fact]
    public async Task The_attempt_count_is_written_before_each_wait()
    {
        // The count is only durable if it reaches the store *between* attempts.
        // Writing it once at the end would be useless: a host that dies mid-
        // retry is exactly the case this exists for.
        var store = new RecordingStore();
        var log = new List<string>();

        var definition = new Declared("retrying", builder => builder
            .AddStep("work", () => new AlwaysFails(log, "work"), RetryPolicy.FixedDelay(3, TimeSpan.Zero)));

        await NewHost(store, definition).StartAsync("retrying", 1);

        // One write per attempt, each carrying the count reached so far.
        Assert.Equal([1, 2, 3], store.AttemptsWritten);
    }

    [Fact]
    public async Task The_attempt_count_survives_a_restart()
    {
        // Given a step with a policy allowing 3 attempts
        // And the step has already failed twice
        var store = new InMemoryWorkflowStore();
        var log = new List<string>();

        IWorkflowDefinition Definition() => new Declared("retrying", builder => builder
            .AddStep("work", () => new AlwaysFails(log, "work"), RetryPolicy.FixedDelay(3, TimeSpan.Zero)));

        var id = await SeedAsync(store, "retrying", attempts: 2);

        // When the host restarts and the instance resumes
        var instance = await NewHost(store, Definition()).ResumeAsync(id);

        // Then the step executes once more - not three more times
        Assert.Equal(["work"], log);

        // And the instance status becomes Failed
        Assert.Equal(InstanceStatus.Failed, instance.Status);
    }

    [Fact]
    public async Task A_restart_cannot_make_a_step_retry_forever()
    {
        // The failure this story exists to prevent. With an in-memory counter,
        // a host recycling during an outage reloads zero attempts every time,
        // so the policy's ceiling never arrives however often it restarts.
        var store = new InMemoryWorkflowStore();
        var log = new List<string>();

        IWorkflowDefinition Definition() => new Declared("retrying", builder => builder
            .AddStep("work", () => new AlwaysFails(log, "work"), RetryPolicy.FixedDelay(4, TimeSpan.Zero)));

        var id = await SeedAsync(store, "retrying", attempts: 0);

        // Five restarts, each resuming from whatever the last one persisted.
        for (var restart = 0; restart < 5; restart++)
        {
            var found = await NewHost(store, Definition()).FindInstanceAsync(id);

            if (found!.Status != InstanceStatus.Suspended)
            {
                break;
            }

            await NewHost(store, Definition()).ResumeAsync(id);
        }

        // Four attempts total across every host, because the policy allows
        // four - not four per restart.
        Assert.Equal(4, log.Count);
        Assert.Equal(InstanceStatus.Failed, (await NewHost(store, Definition()).GetInstanceAsync(id)).Status);
    }

    [Fact]
    public async Task The_attempt_count_resets_when_execution_advances()
    {
        // Given a step that failed once and then succeeded
        // When a later step fails
        // Then the later step attempt count starts at one
        var store = new InMemoryWorkflowStore();
        var log = new List<string>();

        var definition = new Declared("two", builder => builder
            .WithRetryPolicy(RetryPolicy.FixedDelay(2, TimeSpan.Zero))
            .AddStep("A", () => new FailsThenSucceeds(log, "A", failures: 1))
            .AddStep("B", () => new AlwaysFails(log, "B")));

        await NewHost(store, definition).StartAsync("two", 1);

        // B gets its own full allowance of 2. Inheriting A's count would leave
        // it only one attempt, and the log would read A, A, B.
        Assert.Equal(["A", "A", "B", "B"], log);
    }

    [Fact]
    public async Task The_reset_is_persisted_not_only_held_in_memory()
    {
        // Same reset, observed through the store rather than the engine, so a
        // provider that wrote the increment but skipped the reset is caught.
        var store = new RecordingStore();
        var log = new List<string>();

        var definition = new Declared("two", builder => builder
            .WithRetryPolicy(RetryPolicy.FixedDelay(2, TimeSpan.Zero))
            .AddStep("A", () => new FailsThenSucceeds(log, "A", failures: 1))
            .AddStep("B", () => new AlwaysFails(log, "B")));

        await NewHost(store, definition).StartAsync("two", 1);

        // A fails (1), A succeeds (reset to 0), B fails (1), B fails (2).
        Assert.Equal([1, 0, 1, 2], store.AttemptsWritten);
    }

    /// <summary>
    /// Seeds a suspended instance carrying an attempt count.
    /// </summary>
    /// <remarks>
    /// Written straight to the store rather than reached by running a workflow,
    /// and that is worth being plain about. The engine only ever suspends with
    /// a zero count, so there is no execution path today that produces a
    /// suspended instance mid-retry.
    ///
    /// <para>
    /// What actually happens on a crash mid-retry is that the instance stays
    /// <c>Running</c> - and nothing resumes a <c>Running</c> instance yet.
    /// Orphan recovery is #39. Until it lands, this story makes the count
    /// durable and honoured on load; it does not make the crash recoverable.
    /// Seeding is how the load path gets tested before the recovery path
    /// exists, not a claim that the loop is closed.
    /// </para>
    /// </remarks>
    private static async Task<Guid> SeedAsync(IWorkflowStore store, string definitionId, int attempts)
    {
        var id = Guid.NewGuid();

        await store.CreateAsync(new WorkflowInstanceRecord
        {
            Id = id,
            DefinitionId = definitionId,
            DefinitionVersion = 1,
            Status = InstanceStatus.Suspended,
            CurrentStepIndex = 0,
            CurrentStepName = "work",
            CreatedAt = new DateTimeOffset(2026, 7, 31, 12, 0, 0, TimeSpan.Zero),
            StepAttempts = attempts,
        });

        return id;
    }

    /// <summary>
    /// An in-memory store that records the attempt count of every save.
    /// </summary>
    private sealed class RecordingStore : IWorkflowStore
    {
        private readonly InMemoryWorkflowStore inner = new();

        public List<int> AttemptsWritten { get; } = [];

        public Task CreateAsync(WorkflowInstanceRecord record, CancellationToken cancellationToken = default) =>
            this.inner.CreateAsync(record, cancellationToken);

        public Task<WorkflowInstanceRecord> SaveAsync(
            WorkflowInstanceRecord record,
            IReadOnlyList<StepHistoryEntry> history,
            CancellationToken cancellationToken = default)
        {
            // Only saves that carry history are step outcomes. The final write
            // completing an instance carries none, and counting it would add a
            // trailing zero that says nothing about retries.
            if (history.Count > 0)
            {
                this.AttemptsWritten.Add(record.StepAttempts);
            }

            return this.inner.SaveAsync(record, history, cancellationToken);
        }

        public Task<WorkflowInstanceRecord?> FindAsync(Guid instanceId, CancellationToken cancellationToken = default) =>
            this.inner.FindAsync(instanceId, cancellationToken);

        public Task<IReadOnlyList<StepHistoryEntry>> GetHistoryAsync(
            Guid instanceId,
            CancellationToken cancellationToken = default) =>
            this.inner.GetHistoryAsync(instanceId, cancellationToken);

        public Task<IReadOnlyList<WorkflowInstanceRecord>> ListAsync(
            InstanceFilter filter,
            CancellationToken cancellationToken = default) =>
            this.inner.ListAsync(filter, cancellationToken);

        public Task<int> CountAsync(InstanceFilter filter, CancellationToken cancellationToken = default) =>
            this.inner.CountAsync(filter, cancellationToken);

        public Task<int> PurgeAsync(DateTimeOffset completedBefore, CancellationToken cancellationToken = default) =>
            this.inner.PurgeAsync(completedBefore, cancellationToken);
    }
}
