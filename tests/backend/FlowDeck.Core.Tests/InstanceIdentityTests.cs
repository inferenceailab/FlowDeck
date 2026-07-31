using FlowDeck.Core;

namespace FlowDeck.Core.Tests;

/// <summary>
/// Issue #7 - Assign every instance a unique identifier.
///
/// Scenario: Starting an instance returns a unique id
/// </summary>
public class InstanceIdentityTests
{
    private sealed class NoopStep : IStep
    {
        public ValueTask<Outcome> ExecuteAsync(IStepContext context, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Outcome.Next);
    }

    private sealed class TrivialWorkflow : IWorkflowDefinition
    {
        public string Id => "trivial";

        public int Version => 1;

        public void Build(IWorkflowBuilder builder) => builder.AddStep("only", () => new NoopStep());
    }

    private static WorkflowEngine Engine()
    {
        var registry = new WorkflowRegistry();
        registry.Register(new TrivialWorkflow());
        return new WorkflowEngine(registry);
    }

    [Fact]
    public async Task Two_instances_receive_distinct_non_empty_ids()
    {
        // Given a registered definition
        var engine = Engine();

        // When two instances are started
        var first = await engine.StartAsync("trivial", 1);
        var second = await engine.StartAsync("trivial", 1);

        // Then each returns a distinct non-empty instance id
        Assert.NotEqual(Guid.Empty, first.Id);
        Assert.NotEqual(Guid.Empty, second.Id);
        Assert.NotEqual(first.Id, second.Id);
    }

    [Fact]
    public async Task Ids_remain_distinct_across_many_sequential_starts()
    {
        // Two is a weak sample. Correlation of logs, API calls and dashboard
        // rows all rest on this, so assert it at a scale where a naive
        // counter or a reused seed would show up.
        var engine = Engine();
        const int count = 500;

        var ids = new List<Guid>(count);

        for (var i = 0; i < count; i++)
        {
            ids.Add((await engine.StartAsync("trivial", 1)).Id);
        }

        Assert.Equal(count, ids.Distinct().Count());
    }

    [Fact]
    public async Task Ids_remain_distinct_when_instances_start_concurrently()
    {
        // Id assignment must not depend on the engine being called serially.
        var engine = Engine();
        const int count = 200;

        var started = await Task.WhenAll(
            Enumerable.Range(0, count).Select(_ => engine.StartAsync("trivial", 1)));

        Assert.Equal(count, started.Select(instance => instance.Id).Distinct().Count());
    }

    [Fact]
    public async Task An_instance_id_is_stable_for_the_life_of_the_instance()
    {
        // The id a caller receives must be the same one the steps observed,
        // otherwise nothing logged during execution can be correlated back.
        var seen = new List<Guid>();
        var registry = new WorkflowRegistry();
        registry.Register(new IdCapturingWorkflow(seen));
        var engine = new WorkflowEngine(registry);

        var instance = await engine.StartAsync("id-capturing", 1);

        Assert.All(seen, id => Assert.Equal(instance.Id, id));
        Assert.Equal(2, seen.Count);
    }

    private sealed class IdCapturingWorkflow(List<Guid> seen) : IWorkflowDefinition
    {
        public string Id => "id-capturing";

        public int Version => 1;

        public void Build(IWorkflowBuilder builder)
        {
            builder.AddStep("A", () => new IdCapturingStep(seen));
            builder.AddStep("B", () => new IdCapturingStep(seen));
        }
    }

    private sealed class IdCapturingStep(List<Guid> seen) : IStep
    {
        public ValueTask<Outcome> ExecuteAsync(IStepContext context, CancellationToken cancellationToken = default)
        {
            seen.Add(context.InstanceId);
            return ValueTask.FromResult(Outcome.Next);
        }
    }
}
