using FlowDeck.Core;

namespace FlowDeck.Core.Tests;

/// <summary>
/// The code samples from docs/guides/defining-a-workflow.md, compiled and run.
/// </summary>
/// <remarks>
/// Documentation that does not compile is worse than none: it is confidently
/// wrong. These tests exist so a change to the engine that invalidates the
/// guide breaks the build rather than silently making the guide a lie.
///
/// If you change a sample here, change the guide to match, and vice versa.
/// </remarks>
public class DocumentationExampleTests
{
    // --- "A minimal workflow" ------------------------------------------------

    public sealed class GreetWorkflow : IWorkflowDefinition
    {
        public string Id => "greet";

        public int Version => 1;

        public void Build(IWorkflowBuilder builder) =>
            builder.AddStep("say-hello", () => new SayHello());
    }

    public sealed class SayHello : IStep
    {
        public ValueTask<Outcome> ExecuteAsync(
            IStepContext context,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Outcome.Next);
    }

    [Fact]
    public async Task Minimal_workflow_sample_runs_to_completion()
    {
        var registry = new WorkflowRegistry();
        registry.Register(new GreetWorkflow());

        var engine = new WorkflowEngine(registry);
        var instance = await engine.StartAsync("greet", version: 1);

        Assert.Equal(InstanceStatus.Completed, instance.Status);
    }

    // --- "Step outcomes" -----------------------------------------------------

    public sealed class WaitForApproval : IStep
    {
        public ValueTask<Outcome> ExecuteAsync(IStepContext context, CancellationToken cancellationToken = default)
        {
            if (!context.Data.TryGet<bool>("approved", out var approved) || !approved)
            {
                return ValueTask.FromResult(Outcome.Suspend);
            }

            return ValueTask.FromResult(Outcome.Next);
        }
    }

    private sealed class ApprovalWorkflow : IWorkflowDefinition
    {
        public string Id => "approval";

        public int Version => 1;

        public void Build(IWorkflowBuilder builder) =>
            builder.AddStep("wait", () => new WaitForApproval());
    }

    [Fact]
    public async Task Suspending_sample_parks_the_instance()
    {
        var registry = new WorkflowRegistry();
        registry.Register(new ApprovalWorkflow());
        var engine = new WorkflowEngine(registry);

        var instance = await engine.StartAsync("approval", 1);

        Assert.Equal(InstanceStatus.Suspended, instance.Status);
        Assert.Equal("wait", instance.CurrentStepName);
    }

    // --- "Sharing data between steps" ---------------------------------------

    [Fact]
    public void Data_samples_behave_as_documented()
    {
        IWorkflowData data = new WorkflowData();

        data.Set("orderId", 42);
        Assert.Equal(42, data.Get<int>("orderId"));

        Assert.Throws<WorkflowDataTypeMismatchException>(() => _ = data.Get<string>("orderId"));
        Assert.Throws<WorkflowDataKeyNotFoundException>(() => _ = data.Get<int>("absent"));

        Assert.False(data.TryGet<string>("note", out _));

        // "A value explicitly set to null is present, not absent"
        data.Set<string?>("note", null);
        Assert.True(data.Contains("note"));
        Assert.True(data.TryGet<string?>("note", out var note));
        Assert.Null(note);
    }

    // --- "Typed input" -------------------------------------------------------

    public sealed record OrderRequest(int Id);

    public sealed class FulfilOrder : IWorkflowDefinition<OrderRequest>
    {
        public string Id => "fulfil-order";

        public int Version => 1;

        public void Build(IWorkflowBuilder builder) =>
            builder.AddStep("charge", () => new ChargeCard());
    }

    public sealed class ChargeCard : IStep
    {
        public ValueTask<Outcome> ExecuteAsync(IStepContext context, CancellationToken cancellationToken = default)
        {
            _ = context.GetInput<OrderRequest>().Id;
            return ValueTask.FromResult(Outcome.Next);
        }
    }

    private static WorkflowEngine OrderEngine()
    {
        var registry = new WorkflowRegistry();
        registry.Register(new FulfilOrder());
        registry.Register(new GreetWorkflow());
        return new WorkflowEngine(registry);
    }

    [Fact]
    public async Task Typed_input_sample_runs()
    {
        var engine = OrderEngine();

        var instance = await engine.StartAsync("fulfil-order", 1, new OrderRequest(7));

        Assert.Equal(InstanceStatus.Completed, instance.Status);
    }

    /// <summary>Every row of the guide's input-validation table.</summary>
    [Fact]
    public async Task Input_validation_table_is_accurate()
    {
        var engine = OrderEngine();

        // Typed workflow, correct input -> starts
        Assert.Equal(
            InstanceStatus.Completed,
            (await engine.StartAsync("fulfil-order", 1, new OrderRequest(7))).Status);

        // Typed workflow, wrong type -> InvalidInputTypeException
        await Assert.ThrowsAsync<InvalidInputTypeException>(
            async () => await engine.StartAsync("fulfil-order", 1, "not an order"));

        // Typed workflow, no input -> InvalidInputTypeException
        await Assert.ThrowsAsync<InvalidInputTypeException>(
            async () => await engine.StartAsync("fulfil-order", 1));

        // Untyped workflow, input supplied -> InvalidInputTypeException
        await Assert.ThrowsAsync<InvalidInputTypeException>(
            async () => await engine.StartAsync("greet", 1, new OrderRequest(7)));
    }

    // --- "Failure" -----------------------------------------------------------

    private sealed class FailingOrder : IWorkflowDefinition
    {
        public string Id => "failing";

        public int Version => 1;

        public void Build(IWorkflowBuilder builder) =>
            builder.AddStep("charge", () => new AlwaysThrows());
    }

    private sealed class AlwaysThrows : IStep
    {
        public ValueTask<Outcome> ExecuteAsync(IStepContext context, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("card declined");
    }

    [Fact]
    public async Task Failure_sample_reports_step_and_error()
    {
        var registry = new WorkflowRegistry();
        registry.Register(new FailingOrder());
        var engine = new WorkflowEngine(registry);

        var instance = await engine.StartAsync("failing", 1);

        Assert.Equal(InstanceStatus.Failed, instance.Status);
        Assert.Equal("charge", instance.FailedStepName);
        Assert.IsType<InvalidOperationException>(instance.Error);
    }

    // --- "Rules the engine enforces" ----------------------------------------

    private sealed class NoSteps : IWorkflowDefinition
    {
        public string Id => "no-steps";

        public int Version => 1;

        public void Build(IWorkflowBuilder builder)
        {
        }
    }

    private sealed class DuplicateNames : IWorkflowDefinition
    {
        public string Id => "duplicate";

        public int Version => 1;

        public void Build(IWorkflowBuilder builder)
        {
            builder.AddStep("same", () => new SayHello());
            builder.AddStep("same", () => new SayHello());
        }
    }

    [Fact]
    public async Task Enforced_rules_table_is_accurate()
    {
        var registry = new WorkflowRegistry();
        registry.Register(new NoSteps());
        registry.Register(new DuplicateNames());
        var engine = new WorkflowEngine(registry);

        await Assert.ThrowsAsync<InvalidWorkflowDefinitionException>(
            async () => await engine.StartAsync("no-steps", 1));

        await Assert.ThrowsAsync<InvalidWorkflowDefinitionException>(
            async () => await engine.StartAsync("duplicate", 1));

        await Assert.ThrowsAsync<DefinitionNotFoundException>(
            async () => await engine.StartAsync("never-registered", 1));

        // Every engine exception is a FlowDeckException
        Assert.IsAssignableFrom<FlowDeckException>(new DefinitionNotFoundException("x", 1));
        Assert.IsAssignableFrom<FlowDeckException>(new InstanceNotFoundException(Guid.NewGuid()));
    }

    // --- "Querying and cancelling" ------------------------------------------

    [Fact]
    public async Task Query_and_cancel_samples_behave_as_documented()
    {
        var registry = new WorkflowRegistry();
        registry.Register(new ApprovalWorkflow());
        var engine = new WorkflowEngine(registry);

        var started = await engine.StartAsync("approval", 1);

        // No longer Assert.Same: the store is the source of truth, so a query
        // returns a projection of persisted state rather than the live object.
        Assert.Equal(started.Id, (await engine.GetInstanceAsync(started.Id)).Id);
        Assert.NotNull(await engine.FindInstanceAsync(started.Id));
        Assert.Null(await engine.FindInstanceAsync(Guid.NewGuid()));
        Assert.Single(await engine.ListInstancesAsync());

        await engine.CancelAsync(started.Id);
        Assert.Equal(InstanceStatus.Cancelled, (await engine.GetInstanceAsync(started.Id)).Status);

        // Terminal states are final
        await Assert.ThrowsAsync<InvalidStateTransitionException>(async () => await engine.CancelAsync(started.Id));
        await Assert.ThrowsAsync<InvalidStateTransitionException>(
            async () => await engine.ResumeAsync(started.Id));
    }
}
