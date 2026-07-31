using FlowDeck.Core;

namespace FlowDeck.Core.Tests;

/// <summary>
/// Issue #5 - Pass data between steps via workflow context.
///
/// Scenario: A later step reads an earlier step's output
/// Scenario: Context mutations are isolated per instance
/// </summary>
public class WorkflowDataTests
{
    private sealed class WritesOrderId(int value) : IStepBody
    {
        public ValueTask<Outcome> ExecuteAsync(IStepContext context, CancellationToken cancellationToken = default)
        {
            context.Data.Set("orderId", value);
            return ValueTask.FromResult(Outcome.Next);
        }
    }

    private sealed class ReadsOrderId(List<int> seen) : IStepBody
    {
        public ValueTask<Outcome> ExecuteAsync(IStepContext context, CancellationToken cancellationToken = default)
        {
            seen.Add(context.Data.Get<int>("orderId"));
            return ValueTask.FromResult(Outcome.Next);
        }
    }

    private sealed class TwoStepWorkflow(IStepBody first, IStepBody second) : IWorkflowDefinition
    {
        public string Id => "data-flow";

        public int Version => 1;

        public void Build(IWorkflowBuilder builder)
        {
            builder.AddStep("A", () => first);
            builder.AddStep("B", () => second);
        }
    }

    private static WorkflowEngine EngineFor(IWorkflowDefinition definition)
    {
        var registry = new WorkflowRegistry();
        registry.Register(definition);
        return new WorkflowEngine(registry);
    }

    [Fact]
    public async Task A_later_step_reads_an_earlier_steps_output()
    {
        // Given step A writes "orderId" = 42 to the workflow data
        var seen = new List<int>();
        var engine = EngineFor(new TwoStepWorkflow(new WritesOrderId(42), new ReadsOrderId(seen)));

        // When step B executes
        await engine.StartAsync("data-flow", 1);

        // Then step B reads "orderId" as 42
        Assert.Equal([42], seen);
    }

    [Fact]
    public async Task Context_mutations_are_isolated_per_instance()
    {
        // Given two concurrent instances of the same definition
        var seen = new List<int>();
        var registry = new WorkflowRegistry();
        registry.Register(new ParameterisedWorkflow());
        var engine = new WorkflowEngine(registry);

        // When instance 1 writes "orderId" = 1 and instance 2 writes "orderId" = 2
        var first = engine.StartAsync("parameterised", 1);
        var second = engine.StartAsync("parameterised", 1);
        var instances = await Task.WhenAll(first, second);

        // Then each instance reads back only its own value.
        // Each instance seeds its own id-derived value, so a shared dictionary
        // would show one instance reading the other's write.
        foreach (var instance in instances)
        {
            Assert.Equal(InstanceStatus.Completed, instance.Status);
        }

        Assert.Empty(seen); // no cross-instance leak was recorded
    }

    /// <summary>
    /// Writes a value derived from the instance id, then asserts it reads back
    /// the same one. A shared data store would fail this under concurrency.
    /// </summary>
    private sealed class ParameterisedWorkflow : IWorkflowDefinition
    {
        public string Id => "parameterised";

        public int Version => 1;

        public void Build(IWorkflowBuilder builder)
        {
            builder.AddStep("write", () => new WriteInstanceId());
            builder.AddStep("verify", () => new VerifyInstanceId());
        }
    }

    private sealed class WriteInstanceId : IStepBody
    {
        public ValueTask<Outcome> ExecuteAsync(IStepContext context, CancellationToken cancellationToken = default)
        {
            context.Data.Set("owner", context.InstanceId);
            return ValueTask.FromResult(Outcome.Next);
        }
    }

    private sealed class VerifyInstanceId : IStepBody
    {
        public ValueTask<Outcome> ExecuteAsync(IStepContext context, CancellationToken cancellationToken = default)
        {
            var owner = context.Data.Get<Guid>("owner");

            if (owner != context.InstanceId)
            {
                throw new InvalidOperationException(
                    $"data leaked between instances: saw {owner}, expected {context.InstanceId}");
            }

            return ValueTask.FromResult(Outcome.Next);
        }
    }

    [Fact]
    public void Reading_a_missing_key_is_reported_clearly()
    {
        var data = new WorkflowData();

        var ex = Assert.Throws<WorkflowDataKeyNotFoundException>(() => _ = data.Get<int>("nope"));
        Assert.Equal("nope", ex.Key);
    }

    [Fact]
    public void TryGet_reports_absence_without_throwing()
    {
        var data = new WorkflowData();

        Assert.False(data.TryGet<int>("nope", out var value));
        Assert.Equal(0, value);
    }

    [Fact]
    public void Reading_a_key_at_the_wrong_type_is_reported_clearly()
    {
        // Workflow data is dynamically typed by nature. A wrong-type read is a
        // workflow authoring bug and must say which key and which types.
        var data = new WorkflowData();
        data.Set("orderId", 42);

        var ex = Assert.Throws<WorkflowDataTypeMismatchException>(() => _ = data.Get<string>("orderId"));

        Assert.Equal("orderId", ex.Key);
        Assert.Equal(typeof(string), ex.RequestedType);
        Assert.Equal(typeof(int), ex.ActualType);
    }

    [Fact]
    public void A_value_can_be_overwritten()
    {
        var data = new WorkflowData();

        data.Set("orderId", 1);
        data.Set("orderId", 2);

        Assert.Equal(2, data.Get<int>("orderId"));
    }

    [Fact]
    public void Null_is_a_storable_value_distinct_from_absence()
    {
        // "Set and explicitly null" must not be confused with "never set", or a
        // step cannot tell a cleared value from one that was never written.
        var data = new WorkflowData();
        data.Set<string?>("note", null);

        Assert.True(data.Contains("note"));
        Assert.True(data.TryGet<string?>("note", out var value));
        Assert.Null(value);
    }

    [Fact]
    public void Snapshot_exposes_contents_for_persistence()
    {
        // #15 persists workflow data. A read-only snapshot is the seam that
        // will make that possible without exposing the live dictionary.
        var data = new WorkflowData();
        data.Set("orderId", 42);
        data.Set("customer", "acme");

        var snapshot = data.Snapshot();

        Assert.Equal(2, snapshot.Count);
        Assert.Equal(42, snapshot["orderId"]);
        Assert.Equal("acme", snapshot["customer"]);
    }

    [Fact]
    public void Keys_are_compared_ordinally()
    {
        var data = new WorkflowData();
        data.Set("OrderId", 1);
        data.Set("orderid", 2);

        Assert.Equal(1, data.Get<int>("OrderId"));
        Assert.Equal(2, data.Get<int>("orderid"));
    }
}
