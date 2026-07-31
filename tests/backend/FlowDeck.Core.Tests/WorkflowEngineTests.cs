using FlowDeck.Core;

namespace FlowDeck.Core.Tests;

/// <summary>
/// Issue #3 - Execute a single-step workflow to completion.
///
/// Scenario: Single step workflow completes
/// </summary>
public class WorkflowEngineTests
{
    /// <summary>
    /// Counts executions across all instances of the step, so a test can prove
    /// a step ran exactly once rather than merely that it ran.
    /// </summary>
    private sealed class CountingStep : IStep
    {
        private int invocations;

        public int Invocations => Volatile.Read(ref this.invocations);

        public ValueTask<Outcome> ExecuteAsync(IStepContext context, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref this.invocations);
            return ValueTask.FromResult(Outcome.Next);
        }
    }

    private sealed class SuspendingStep : IStep
    {
        public ValueTask<Outcome> ExecuteAsync(IStepContext context, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Outcome.Suspend);
    }

    /// <summary>A definition of exactly one step, backed by a supplied body.</summary>
    private sealed class SingleStepWorkflow(IStep body) : IWorkflowDefinition
    {
        public string Id => "single-step";

        public int Version => 1;

        public void Build(IWorkflowBuilder builder) => builder.AddStep("only", () => body);
    }

    private static WorkflowEngine EngineFor(IWorkflowDefinition definition)
    {
        var registry = new WorkflowRegistry();
        registry.Register(definition);
        return new WorkflowEngine(registry);
    }

    [Fact]
    public async Task Single_step_workflow_runs_the_step_once_and_completes()
    {
        // Given a registered definition containing exactly one step
        var step = new CountingStep();
        var engine = EngineFor(new SingleStepWorkflow(step));

        // When an instance is started
        var instance = await engine.StartAsync("single-step", 1);

        // Then the step executes exactly once
        Assert.Equal(1, step.Invocations);

        // And the instance status becomes Completed
        Assert.Equal(InstanceStatus.Completed, instance.Status);
    }

    [Fact]
    public async Task Completed_instance_reports_the_definition_it_ran()
    {
        var engine = EngineFor(new SingleStepWorkflow(new CountingStep()));

        var instance = await engine.StartAsync("single-step", 1);

        Assert.Equal("single-step", instance.DefinitionId);
        Assert.Equal(1, instance.DefinitionVersion);
        Assert.NotEqual(Guid.Empty, instance.Id);
    }

    [Fact]
    public async Task Suspending_step_leaves_the_instance_suspended_not_completed()
    {
        // A workflow that parks must not be reported as finished - the whole
        // point of Suspend is that there is more to do later.
        var engine = EngineFor(new SingleStepWorkflow(new SuspendingStep()));

        var instance = await engine.StartAsync("single-step", 1);

        Assert.Equal(InstanceStatus.Suspended, instance.Status);
        Assert.Equal("only", instance.CurrentStepName);
    }

    [Fact]
    public async Task Each_start_produces_a_distinct_instance()
    {
        var engine = EngineFor(new SingleStepWorkflow(new CountingStep()));

        var first = await engine.StartAsync("single-step", 1);
        var second = await engine.StartAsync("single-step", 1);

        Assert.NotEqual(first.Id, second.Id);
    }

    [Fact]
    public async Task Starting_an_unregistered_definition_is_rejected()
    {
        var engine = new WorkflowEngine(new WorkflowRegistry());

        await Assert.ThrowsAsync<DefinitionNotFoundException>(
            async () => await engine.StartAsync("does-not-exist", 1));
    }

    [Fact]
    public async Task A_definition_declaring_no_steps_is_rejected_at_start()
    {
        // An empty workflow is a mistake in the definition, not a workflow that
        // instantly completes. Failing loudly beats silently doing nothing.
        var engine = EngineFor(new EmptyWorkflow());

        await Assert.ThrowsAsync<InvalidWorkflowDefinitionException>(
            async () => await engine.StartAsync("empty", 1));
    }

    private sealed class EmptyWorkflow : IWorkflowDefinition
    {
        public string Id => "empty";

        public int Version => 1;

        public void Build(IWorkflowBuilder builder)
        {
            // deliberately declares nothing
        }
    }
}
