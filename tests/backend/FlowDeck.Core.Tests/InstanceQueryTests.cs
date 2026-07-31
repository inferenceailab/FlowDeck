using FlowDeck.Core;

namespace FlowDeck.Core.Tests;

/// <summary>
/// Issue #11 - Query the status of an in-flight instance.
///
/// Scenario: Status reflects the current step
/// </summary>
public class InstanceQueryTests
{
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

    /// <summary>A → B → C, where B's behaviour is supplied by the test.</summary>
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

    private static WorkflowEngine EngineFor(IWorkflowDefinition definition)
    {
        var registry = new WorkflowRegistry();
        registry.Register(definition);
        return new WorkflowEngine(registry);
    }

    [Fact]
    public async Task Status_reflects_the_current_step()
    {
        // Given a running instance suspended at step B
        var engine = EngineFor(new ThreeStep(() => new SuspendingStep()));
        var started = await engine.StartAsync("three-step", 1);

        // When I query the instance
        var found = engine.GetInstance(started.Id);

        // Then the status is Suspended
        Assert.Equal(InstanceStatus.Suspended, found.Status);

        // And the current step name is "B"
        Assert.Equal("B", found.CurrentStepName);
    }

    [Fact]
    public async Task A_completed_instance_remains_queryable()
    {
        // An instance that finished is exactly what an operator looks up after
        // the fact. Discarding it on completion would make the dashboard blind
        // to everything that already succeeded.
        var engine = EngineFor(new ThreeStep(() => new NoopStep()));
        var started = await engine.StartAsync("three-step", 1);

        var found = engine.GetInstance(started.Id);

        Assert.Equal(InstanceStatus.Completed, found.Status);
        Assert.Null(found.CurrentStepName);
    }

    [Fact]
    public async Task A_failed_instance_remains_queryable_with_its_failure()
    {
        var engine = EngineFor(new ThreeStep(() => new ThrowingStep()));
        var started = await engine.StartAsync("three-step", 1);

        var found = engine.GetInstance(started.Id);

        Assert.Equal(InstanceStatus.Failed, found.Status);
        Assert.Equal("B", found.FailedStepName);
        Assert.NotNull(found.Error);
    }

    [Fact]
    public void Querying_an_unknown_instance_is_reported_clearly()
    {
        var engine = new WorkflowEngine(new WorkflowRegistry());
        var unknown = Guid.NewGuid();

        var ex = Assert.Throws<InstanceNotFoundException>(() => engine.GetInstance(unknown));

        Assert.Equal(unknown, ex.InstanceId);
    }

    [Fact]
    public void TryGetInstance_reports_absence_without_throwing()
    {
        var engine = new WorkflowEngine(new WorkflowRegistry());

        Assert.False(engine.TryGetInstance(Guid.NewGuid(), out var instance));
        Assert.Null(instance);
    }

    [Fact]
    public async Task The_queried_instance_is_the_one_that_was_started()
    {
        // Returning a copy would let a caller act on stale state while the
        // engine advances the real one.
        var engine = EngineFor(new ThreeStep(() => new SuspendingStep()));
        var started = await engine.StartAsync("three-step", 1);

        Assert.Same(started, engine.GetInstance(started.Id));
    }

    [Fact]
    public async Task All_started_instances_are_listed()
    {
        // #25 pages over this. Establishing enumeration now keeps that story
        // about paging rather than about inventing a store.
        var engine = EngineFor(new ThreeStep(() => new NoopStep()));

        var first = await engine.StartAsync("three-step", 1);
        var second = await engine.StartAsync("three-step", 1);

        var all = engine.GetInstances();

        Assert.Equal(2, all.Count);
        Assert.Contains(all, i => i.Id == first.Id);
        Assert.Contains(all, i => i.Id == second.Id);
    }

    [Fact]
    public async Task Nothing_is_recorded_when_the_definition_is_unknown()
    {
        // The structural assertion from #9 becomes directly observable here.
        var engine = EngineFor(new ThreeStep(() => new NoopStep()));

        await Assert.ThrowsAsync<DefinitionNotFoundException>(
            async () => await engine.StartAsync("does-not-exist", 1));

        Assert.Empty(engine.GetInstances());
    }

    [Fact]
    public async Task Concurrently_started_instances_are_all_recorded()
    {
        var engine = EngineFor(new ThreeStep(() => new NoopStep()));
        const int count = 100;

        await Task.WhenAll(Enumerable.Range(0, count).Select(_ => engine.StartAsync("three-step", 1)));

        Assert.Equal(count, engine.GetInstances().Count);
    }
}
