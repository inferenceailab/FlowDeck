using FlowDeck.Core;

namespace FlowDeck.Core.Tests;

/// <summary>
/// Issue #10 - Support strongly typed workflow input.
///
/// Scenario: Typed input is available to the first step
/// Scenario: Input type mismatch is rejected
/// </summary>
public class TypedInputTests
{
    public sealed record OrderRequest(int Id);

    public sealed record ShipmentRequest(string Tracking);

    private sealed class ReadsInput(List<int> seen) : IStep
    {
        public ValueTask<Outcome> ExecuteAsync(IStepContext context, CancellationToken cancellationToken = default)
        {
            seen.Add(context.GetInput<OrderRequest>().Id);
            return ValueTask.FromResult(Outcome.Next);
        }
    }

    private sealed class NoopStep : IStep
    {
        public ValueTask<Outcome> ExecuteAsync(IStepContext context, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Outcome.Next);
    }

    private sealed class TypedWorkflow(List<int> seen) : IWorkflowDefinition<OrderRequest>
    {
        public string Id => "typed";

        public int Version => 1;

        public void Build(IWorkflowBuilder builder)
        {
            builder.AddStep("first", () => new ReadsInput(seen));
            builder.AddStep("second", () => new NoopStep());
        }
    }

    private sealed class UntypedWorkflow : IWorkflowDefinition
    {
        public string Id => "untyped";

        public int Version => 1;

        public void Build(IWorkflowBuilder builder) => builder.AddStep("only", () => new NoopStep());
    }

    private static WorkflowEngine EngineFor(params IWorkflowDefinition[] definitions)
    {
        var registry = new WorkflowRegistry();

        foreach (var definition in definitions)
        {
            registry.Register(definition);
        }

        return new WorkflowEngine(registry);
    }

    [Fact]
    public async Task Typed_input_is_available_to_the_first_step()
    {
        // Given a definition typed on input OrderRequest
        var seen = new List<int>();
        var engine = EngineFor(new TypedWorkflow(seen));

        // When an instance is started with OrderRequest { Id = 7 }
        var instance = await engine.StartAsync("typed", 1, new OrderRequest(7));

        // Then the first step reads Input.Id as 7
        Assert.Equal([7], seen);
        Assert.Equal(InstanceStatus.Completed, instance.Status);
    }

    [Fact]
    public async Task Input_of_the_wrong_type_is_rejected()
    {
        // Given a definition typed on input OrderRequest
        var engine = EngineFor(new TypedWorkflow([]));

        // When an instance is started with an input of a different type
        // Then the start call fails with an InvalidInputTypeException
        var ex = await Assert.ThrowsAsync<InvalidInputTypeException>(
            async () => await engine.StartAsync("typed", 1, new ShipmentRequest("XYZ")));

        Assert.Equal("typed", ex.DefinitionId);
        Assert.Equal(typeof(OrderRequest), ex.ExpectedType);
        Assert.Equal(typeof(ShipmentRequest), ex.ActualType);
    }

    [Fact]
    public async Task A_typed_definition_started_without_input_is_rejected()
    {
        // Omitting a required input is the same authoring mistake as supplying
        // the wrong one, and must fail before any step observes a null.
        var engine = EngineFor(new TypedWorkflow([]));

        var ex = await Assert.ThrowsAsync<InvalidInputTypeException>(
            async () => await engine.StartAsync("typed", 1));

        Assert.Equal(typeof(OrderRequest), ex.ExpectedType);
        Assert.Null(ex.ActualType);
    }

    [Fact]
    public async Task An_untyped_definition_given_input_is_rejected()
    {
        // Silently discarding it would let an author believe their input was
        // delivered when nothing can read it.
        var engine = EngineFor(new UntypedWorkflow());

        var ex = await Assert.ThrowsAsync<InvalidInputTypeException>(
            async () => await engine.StartAsync("untyped", 1, new OrderRequest(1)));

        Assert.Null(ex.ExpectedType);
        Assert.Equal(typeof(OrderRequest), ex.ActualType);
    }

    [Fact]
    public async Task An_untyped_definition_still_starts_without_input()
    {
        var engine = EngineFor(new UntypedWorkflow());

        var instance = await engine.StartAsync("untyped", 1);

        Assert.Equal(InstanceStatus.Completed, instance.Status);
    }

    [Fact]
    public async Task Input_is_visible_to_every_step_not_only_the_first()
    {
        // The scenario only names the first step, but an input readable only
        // once would be a trap: nothing signals when it stops being available.
        var seen = new List<int>();
        var registry = new WorkflowRegistry();
        registry.Register(new AllStepsReadInput(seen));
        var engine = new WorkflowEngine(registry);

        await engine.StartAsync("all-read", 1, new OrderRequest(7));

        Assert.Equal([7, 7], seen);
    }

    private sealed class AllStepsReadInput(List<int> seen) : IWorkflowDefinition<OrderRequest>
    {
        public string Id => "all-read";

        public int Version => 1;

        public void Build(IWorkflowBuilder builder)
        {
            builder.AddStep("A", () => new ReadsInput(seen));
            builder.AddStep("B", () => new ReadsInput(seen));
        }
    }

    [Fact]
    public async Task Reading_input_at_the_wrong_type_from_a_step_is_reported_clearly()
    {
        var registry = new WorkflowRegistry();
        registry.Register(new MisreadsInput());
        var engine = new WorkflowEngine(registry);

        var instance = await engine.StartAsync("misreads", 1, new OrderRequest(7));

        Assert.Equal(InstanceStatus.Failed, instance.Status);
        Assert.IsType<InvalidInputTypeException>(instance.Error);
    }

    private sealed class MisreadsInput : IWorkflowDefinition<OrderRequest>
    {
        public string Id => "misreads";

        public int Version => 1;

        public void Build(IWorkflowBuilder builder) => builder.AddStep("only", () => new WrongTypeStep());
    }

    private sealed class WrongTypeStep : IStep
    {
        public ValueTask<Outcome> ExecuteAsync(IStepContext context, CancellationToken cancellationToken = default)
        {
            _ = context.GetInput<ShipmentRequest>();
            return ValueTask.FromResult(Outcome.Next);
        }
    }

    [Fact]
    public async Task The_declared_input_type_is_discoverable_from_the_definition()
    {
        // #23 must reject a malformed start request over HTTP before executing
        // anything, which needs the input type without starting an instance.
        //
        // Read through IWorkflowDefinition: InputType is a default interface
        // member, so it is deliberately reachable only via the interface. That
        // keeps implementers from shadowing it with an unrelated property.
        IWorkflowDefinition typed = new TypedWorkflow([]);
        IWorkflowDefinition untyped = new UntypedWorkflow();

        Assert.Equal(typeof(OrderRequest), typed.InputType);
        Assert.Null(untyped.InputType);

        await Task.CompletedTask;
    }
}
