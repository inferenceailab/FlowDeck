using FlowDeck.Core;

namespace FlowDeck.Core.Tests;

/// <summary>
/// Issue #104 - Default a retry policy for a whole workflow.
///
/// Scenario: Steps inherit the workflow default
/// Scenario: A step policy overrides the workflow default
/// Scenario: A step can opt out of the workflow default
/// </summary>
public class WorkflowRetryDefaultTests
{
    private sealed class CountingThrower : IStep
    {
        public int Executions { get; private set; }

        public ValueTask<Outcome> ExecuteAsync(IStepContext context, CancellationToken cancellationToken = default)
        {
            this.Executions++;
            throw new InvalidOperationException("boom");
        }
    }

    private sealed class NoopStep : IStep
    {
        public ValueTask<Outcome> ExecuteAsync(IStepContext context, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Outcome.Next);
    }

    /// <summary>A workflow whose steps are declared by the test.</summary>
    private sealed class Configured(Action<IWorkflowBuilder> declare) : IWorkflowDefinition
    {
        public string Id => "configured";

        public int Version => 1;

        public void Build(IWorkflowBuilder builder) => declare(builder);
    }

    private static async Task<WorkflowInstance> RunAsync(Action<IWorkflowBuilder> declare)
    {
        var registry = new WorkflowRegistry();
        registry.Register(new Configured(declare));

        var engine = new WorkflowEngine(registry, random: new Random(1));

        return await engine.StartAsync("configured", 1);
    }

    [Fact]
    public async Task Steps_inherit_the_workflow_default()
    {
        // Given a workflow declaring a default of 2 attempts
        // And a step declared without its own policy
        var step = new CountingThrower();

        await RunAsync(builder => builder
            .WithRetryPolicy(RetryPolicy.FixedDelay(2, TimeSpan.Zero))
            .AddStep("work", () => step));

        // Then that step executes exactly 2 times
        Assert.Equal(2, step.Executions);
    }

    [Fact]
    public async Task Every_step_inherits_the_same_default()
    {
        var first = new CountingThrower();
        var second = new CountingThrower();

        await RunAsync(builder => builder
            .WithRetryPolicy(RetryPolicy.FixedDelay(2, TimeSpan.Zero))
            .AddStep("first", () => first)
            .AddStep("second", () => second));

        Assert.Equal(2, first.Executions);

        // The first step exhausts its attempts and fails the instance, so the
        // second never runs. Asserting that rather than pretending otherwise.
        Assert.Equal(0, second.Executions);
    }

    [Fact]
    public async Task A_step_policy_overrides_the_workflow_default()
    {
        // Given a default of 2 attempts and a step declaring 4
        var step = new CountingThrower();

        await RunAsync(builder => builder
            .WithRetryPolicy(RetryPolicy.FixedDelay(2, TimeSpan.Zero))
            .AddStep("work", () => step, RetryPolicy.FixedDelay(4, TimeSpan.Zero)));

        // Then it executes exactly 4 times
        Assert.Equal(4, step.Executions);
    }

    [Fact]
    public async Task A_step_can_opt_out_of_the_workflow_default()
    {
        // Given a default of 3 attempts and a step declaring RetryPolicy.None
        var step = new CountingThrower();

        await RunAsync(builder => builder
            .WithRetryPolicy(RetryPolicy.FixedDelay(3, TimeSpan.Zero))
            .AddStep("work", () => step, RetryPolicy.None));

        // Then it executes exactly once
        Assert.Equal(1, step.Executions);
    }

    [Fact]
    public async Task Without_a_default_a_step_does_not_retry()
    {
        // Retry stays opt-in at the workflow level too: declaring no default
        // is the same as declaring None.
        var step = new CountingThrower();

        await RunAsync(builder => builder.AddStep("work", () => step));

        Assert.Equal(1, step.Executions);
    }

    [Fact]
    public async Task The_default_applies_only_to_steps_declared_after_it()
    {
        // Resolved at declaration time, not execution time. Declaring the
        // default halfway down would otherwise reach backwards and change
        // steps already written above it, which reads as a bug at the call
        // site.
        var before = new CountingThrower();

        await RunAsync(builder => builder
            .AddStep("before", () => before)
            .WithRetryPolicy(RetryPolicy.FixedDelay(3, TimeSpan.Zero))
            .AddStep("after", () => new NoopStep()));

        Assert.Equal(1, before.Executions);
    }

    [Fact]
    public async Task A_later_default_replaces_an_earlier_one()
    {
        var step = new CountingThrower();

        await RunAsync(builder => builder
            .WithRetryPolicy(RetryPolicy.FixedDelay(2, TimeSpan.Zero))
            .WithRetryPolicy(RetryPolicy.FixedDelay(4, TimeSpan.Zero))
            .AddStep("work", () => step));

        Assert.Equal(4, step.Executions);
    }

    [Fact]
    public async Task Declaring_a_null_default_is_rejected()
    {
        var registry = new WorkflowRegistry();
        registry.Register(new Configured(builder => builder.WithRetryPolicy(null!)));
        var engine = new WorkflowEngine(registry);

        await Assert.ThrowsAsync<ArgumentNullException>(
            async () => await engine.StartAsync("configured", 1));
    }
}
