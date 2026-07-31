using FlowDeck.Core;

namespace FlowDeck.Core.Tests;

/// <summary>
/// Issue #103 - Declare a retry policy on a step.
///
/// Scenario: A step with no policy does not retry
/// Scenario: A step retries up to its attempt limit
/// Scenario: A step that succeeds on retry completes the workflow
/// </summary>
public class RetryTests
{
    /// <summary>Throws on every execution, counting them.</summary>
    private sealed class AlwaysThrows : IStep
    {
        public int Executions { get; private set; }

        public ValueTask<Outcome> ExecuteAsync(IStepContext context, CancellationToken cancellationToken = default)
        {
            this.Executions++;
            throw new InvalidOperationException("boom");
        }
    }

    /// <summary>Throws for the first <c>failures</c> executions, then succeeds.</summary>
    private sealed class ThrowsThenSucceeds(int failures) : IStep
    {
        public int Executions { get; private set; }

        public ValueTask<Outcome> ExecuteAsync(IStepContext context, CancellationToken cancellationToken = default)
        {
            this.Executions++;

            if (this.Executions <= failures)
            {
                throw new InvalidOperationException("transient");
            }

            return ValueTask.FromResult(Outcome.Next);
        }
    }

    private sealed class OneStep(IStep body, RetryPolicy? policy) : IWorkflowDefinition
    {
        public string Id => "retrying";

        public int Version => 1;

        public void Build(IWorkflowBuilder builder) => builder.AddStep("work", () => body, policy);
    }

    /// <summary>
    /// An engine whose clock advances instantly, so a backoff delay does not
    /// make the test wait for it.
    /// </summary>
    private static WorkflowEngine EngineFor(IStep body, RetryPolicy? policy)
    {
        var registry = new WorkflowRegistry();
        registry.Register(new OneStep(body, policy));

        return new WorkflowEngine(registry, TimeProvider.System, random: new Random(1));
    }

    [Fact]
    public async Task A_step_with_no_policy_does_not_retry()
    {
        // Given a step declared without a retry policy
        var step = new AlwaysThrows();
        var engine = EngineFor(step, policy: null);

        // When an instance is started
        var instance = await engine.StartAsync("retrying", 1);

        // Then the step executes exactly once
        Assert.Equal(1, step.Executions);

        // And the instance status becomes Failed
        Assert.Equal(InstanceStatus.Failed, instance.Status);
    }

    [Fact]
    public async Task RetryPolicy_None_is_the_same_as_declaring_nothing()
    {
        var step = new AlwaysThrows();
        var engine = EngineFor(step, RetryPolicy.None);

        await engine.StartAsync("retrying", 1);

        Assert.Equal(1, step.Executions);
    }

    [Fact]
    public async Task A_step_retries_up_to_its_attempt_limit()
    {
        // Given a policy allowing 3 attempts, and a step that always throws
        var step = new AlwaysThrows();
        var engine = EngineFor(step, RetryPolicy.FixedDelay(3, TimeSpan.Zero));

        // When an instance is started
        var instance = await engine.StartAsync("retrying", 1);

        // Then the step executes exactly 3 times
        Assert.Equal(3, step.Executions);

        // And the instance status becomes Failed
        Assert.Equal(InstanceStatus.Failed, instance.Status);
    }

    [Fact]
    public async Task MaxAttempts_counts_executions_not_retries()
    {
        // The off-by-one that causes arguments. MaxAttempts = 3 means the step
        // runs at most 3 times, not 1 + 3.
        var step = new AlwaysThrows();
        var engine = EngineFor(step, RetryPolicy.FixedDelay(3, TimeSpan.Zero));

        await engine.StartAsync("retrying", 1);

        Assert.Equal(3, step.Executions);
    }

    [Fact]
    public async Task A_step_that_succeeds_on_retry_completes_the_workflow()
    {
        // Given a step that throws once and then succeeds
        var step = new ThrowsThenSucceeds(failures: 1);
        var engine = EngineFor(step, RetryPolicy.FixedDelay(3, TimeSpan.Zero));

        var instance = await engine.StartAsync("retrying", 1);

        Assert.Equal(2, step.Executions);
        Assert.Equal(InstanceStatus.Completed, instance.Status);
    }

    [Fact]
    public async Task An_exhausted_retry_reports_the_last_failure()
    {
        // Not the first. An operator investigating wants the error that ended
        // it, and reporting the first would hide a failure mode that changed
        // between attempts.
        var step = new AlwaysThrows();
        var engine = EngineFor(step, RetryPolicy.FixedDelay(2, TimeSpan.Zero));

        var instance = await engine.StartAsync("retrying", 1);

        Assert.Equal("work", instance.FailedStepName);
        Assert.Equal("InvalidOperationException", instance.ErrorType);
        Assert.Equal("boom", instance.ErrorMessage);
    }

    [Fact]
    public async Task The_attempt_count_resets_once_a_step_succeeds()
    {
        // Otherwise a later step inherits a count from work that already
        // succeeded, and gets fewer attempts than its policy allows.
        var first = new ThrowsThenSucceeds(failures: 2);
        var second = new AlwaysThrows();

        var registry = new WorkflowRegistry();
        registry.Register(new TwoStep(first, second));
        var engine = new WorkflowEngine(registry, random: new Random(1));

        await engine.StartAsync("two-step", 1);

        Assert.Equal(3, first.Executions);

        // The second step gets its own three attempts, not the one remaining
        // from the first step's budget.
        Assert.Equal(3, second.Executions);
    }

    private sealed class TwoStep(IStep first, IStep second) : IWorkflowDefinition
    {
        public string Id => "two-step";

        public int Version => 1;

        public void Build(IWorkflowBuilder builder)
        {
            var policy = RetryPolicy.FixedDelay(3, TimeSpan.Zero);

            builder.AddStep("first", () => first, policy);
            builder.AddStep("second", () => second, policy);
        }
    }

    [Fact]
    public void A_policy_must_allow_at_least_one_attempt()
    {
        // Zero attempts would mean the step never runs, which is a
        // configuration mistake rather than an intent.
        Assert.Throws<ArgumentOutOfRangeException>(() => RetryPolicy.ExponentialBackoff(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => RetryPolicy.FixedDelay(0, TimeSpan.Zero));
    }

    [Fact]
    public async Task A_suspending_step_is_not_a_failure_and_is_not_retried()
    {
        // Suspension is the step saying "not yet", not "that went wrong".
        // Counting it as an attempt would exhaust the budget of a step that is
        // waiting perfectly correctly.
        var step = new SuspendsAlways();
        var engine = EngineFor(step, RetryPolicy.FixedDelay(3, TimeSpan.Zero));

        var instance = await engine.StartAsync("retrying", 1);

        Assert.Equal(1, step.Executions);
        Assert.Equal(InstanceStatus.Suspended, instance.Status);
        Assert.Equal(0, instance.StepAttempts);
    }

    private sealed class SuspendsAlways : IStep
    {
        public int Executions { get; private set; }

        public ValueTask<Outcome> ExecuteAsync(IStepContext context, CancellationToken cancellationToken = default)
        {
            this.Executions++;
            return ValueTask.FromResult(Outcome.Suspend);
        }
    }
}
