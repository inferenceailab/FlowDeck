using FlowDeck.Core;
using FlowDeck.Core.Persistence;

namespace FlowDeck.Core.Tests.Persistence;

/// <summary>
/// Issue #13 - Persist instance state after every step.
///
/// Scenario: State is written after each step
/// </summary>
public class CheckpointingTests
{
    /// <summary>
    /// Counts calls without changing behaviour, so a test can assert *that*
    /// persistence happened rather than inferring it from side effects.
    /// </summary>
    private sealed class CountingStore(IWorkflowStore inner) : IWorkflowStore
    {
        public int Creates { get; private set; }

        public int Saves { get; private set; }

        public Task CreateAsync(WorkflowInstanceRecord record, CancellationToken cancellationToken = default)
        {
            this.Creates++;
            return inner.CreateAsync(record, cancellationToken);
        }

        public Task<WorkflowInstanceRecord?> FindAsync(Guid instanceId, CancellationToken cancellationToken = default) =>
            inner.FindAsync(instanceId, cancellationToken);

        public Task<WorkflowInstanceRecord> SaveAsync(
            WorkflowInstanceRecord record,
            IReadOnlyList<StepHistoryEntry> history,
            CancellationToken cancellationToken = default)
        {
            this.Saves++;
            return inner.SaveAsync(record, history, cancellationToken);
        }

        public Task<IReadOnlyList<StepHistoryEntry>> GetHistoryAsync(
            Guid instanceId, CancellationToken cancellationToken = default) =>
            inner.GetHistoryAsync(instanceId, cancellationToken);

        public Task<IReadOnlyList<WorkflowInstanceRecord>> ListAsync(
            InstanceFilter filter, CancellationToken cancellationToken = default) =>
            inner.ListAsync(filter, cancellationToken);

        public Task<int> CountAsync(InstanceFilter filter, CancellationToken cancellationToken = default) =>
            inner.CountAsync(filter, cancellationToken);
    }

    private sealed class NoopStep : IStep
    {
        public ValueTask<Outcome> ExecuteAsync(IStepContext context, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Outcome.Next);
    }

    private sealed class SuspendingStep : IStep
    {
        public ValueTask<Outcome> ExecuteAsync(IStepContext context, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Outcome.Suspend);
    }

    private sealed class ThrowingStep : IStep
    {
        public ValueTask<Outcome> ExecuteAsync(IStepContext context, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("boom");
    }

    private sealed class ThreeStep(Func<IStep> middle) : IWorkflowDefinition
    {
        public string Id => "three-step";

        public int Version => 1;

        public void Build(IWorkflowBuilder builder)
        {
            builder.AddStep("A", () => new NoopStep());
            builder.AddStep("B", middle);
            builder.AddStep("C", () => new NoopStep());
        }
    }

    private static (WorkflowEngine Engine, CountingStore Store) Build(Func<IStep> middle)
    {
        var registry = new WorkflowRegistry();
        registry.Register(new ThreeStep(middle));
        var store = new CountingStore(new InMemoryWorkflowStore());
        return (new WorkflowEngine(registry, store: store), store);
    }

    [Fact]
    public async Task State_is_written_after_each_step()
    {
        // Given a three step workflow
        var (engine, store) = Build(() => new NoopStep());

        // When the instance executes to completion
        var instance = await engine.StartAsync("three-step", 1);

        // Then the persistence provider received at least three saves
        Assert.True(
            store.Saves >= 3,
            $"expected at least three saves, one per step; got {store.Saves}");

        // And the final saved state has status Completed
        var persisted = await engine.GetInstanceAsync(instance.Id);
        Assert.Equal(InstanceStatus.Completed, persisted.Status);
    }

    [Fact]
    public async Task The_instance_is_created_before_the_first_step_runs()
    {
        // Creating it afterwards would hide exactly the instances an operator
        // needs: the ones that suspended or failed partway.
        var (engine, store) = Build(() => new SuspendingStep());

        await engine.StartAsync("three-step", 1);

        Assert.Equal(1, store.Creates);
    }

    [Fact]
    public async Task A_suspended_instance_is_checkpointed_where_it_stopped()
    {
        var (engine, _) = Build(() => new SuspendingStep());

        var instance = await engine.StartAsync("three-step", 1);
        var persisted = await engine.GetInstanceAsync(instance.Id);

        Assert.Equal(InstanceStatus.Suspended, persisted.Status);
        Assert.Equal("B", persisted.CurrentStepName);

        // Positioned on B, not past it - resuming must re-enter the same step.
        Assert.Equal(1, persisted.CurrentStepIndex);
    }

    [Fact]
    public async Task A_failed_instance_is_checkpointed_with_its_failure()
    {
        var (engine, _) = Build(() => new ThrowingStep());

        var instance = await engine.StartAsync("three-step", 1);
        var persisted = await engine.GetInstanceAsync(instance.Id);

        Assert.Equal(InstanceStatus.Failed, persisted.Status);
        Assert.Equal("B", persisted.FailedStepName);
        Assert.Equal("InvalidOperationException", persisted.ErrorType);
        Assert.Equal("boom", persisted.ErrorMessage);
        Assert.NotNull(persisted.CompletedAt);
    }

    [Fact]
    public async Task Each_checkpoint_advances_the_concurrency_revision()
    {
        // Without this, two writers could not detect each other (#19) - every
        // save would look like the first.
        var (engine, _) = Build(() => new NoopStep());

        var instance = await engine.StartAsync("three-step", 1);

        Assert.True(
            instance.Revision > 1,
            $"expected revision to advance past creation, got {instance.Revision}");
    }

    [Fact]
    public async Task A_completed_instance_holds_no_current_step()
    {
        var (engine, _) = Build(() => new NoopStep());

        var instance = await engine.StartAsync("three-step", 1);
        var persisted = await engine.GetInstanceAsync(instance.Id);

        Assert.Null(persisted.CurrentStepName);
        Assert.True(persisted.IsTerminal);
    }

    [Fact]
    public async Task Nothing_is_persisted_when_the_definition_is_unknown()
    {
        var (engine, store) = Build(() => new NoopStep());

        await Assert.ThrowsAsync<DefinitionNotFoundException>(
            async () => await engine.StartAsync("does-not-exist", 1));

        Assert.Equal(0, store.Creates);
        Assert.Empty(await engine.ListInstancesAsync());
    }

    [Fact]
    public async Task Nothing_is_persisted_when_the_input_is_wrong()
    {
        // Validation runs before creation, so a mismatched start leaves no
        // half-built instance behind.
        var registry = new WorkflowRegistry();
        registry.Register(new TypedWorkflow());
        var store = new CountingStore(new InMemoryWorkflowStore());
        var engine = new WorkflowEngine(registry, store: store);

        await Assert.ThrowsAsync<InvalidInputTypeException>(
            async () => await engine.StartAsync("typed", 1, "wrong type"));

        Assert.Equal(0, store.Creates);
    }

    private sealed record Order(int Id);

    private sealed class TypedWorkflow : IWorkflowDefinition<Order>
    {
        public string Id => "typed";

        public int Version => 1;

        public void Build(IWorkflowBuilder builder) => builder.AddStep("only", () => new NoopStep());
    }
}
