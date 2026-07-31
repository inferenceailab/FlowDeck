using FlowDeck.Core;

namespace FlowDeck.Core.Tests;

/// <summary>
/// Issue #2 - Define a step as an atomic unit of work.
///
/// Scenario: A step executes and reports success
/// Scenario: A step signals it is not yet complete
/// </summary>
public class StepExecutorTests
{
    private static IStepContext Context(string stepName = "A") =>
        new StepContext(Guid.NewGuid(), stepName);

    private sealed class AdvancingStep : IStep
    {
        public int Invocations { get; private set; }

        public ValueTask<Outcome> ExecuteAsync(IStepContext context, CancellationToken cancellationToken = default)
        {
            this.Invocations++;
            return ValueTask.FromResult(Outcome.Next);
        }
    }

    private sealed class SuspendingStep : IStep
    {
        public ValueTask<Outcome> ExecuteAsync(IStepContext context, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Outcome.Suspend);
    }

    private sealed class ThrowingStep : IStep
    {
        public ValueTask<Outcome> ExecuteAsync(IStepContext context, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("step blew up");
    }

    [Fact]
    public async Task Step_returning_Next_succeeds_and_advances()
    {
        // Given a step implementing IStep that returns Outcome.Next
        var step = new AdvancingStep();

        // When the engine executes the step
        var result = await StepExecutor.ExecuteAsync(step, Context());

        // Then the step result is Success
        Assert.Equal(StepStatus.Success, result.Status);
        Assert.Equal(Outcome.Next, result.Outcome);

        // And the workflow advances past that step
        Assert.True(result.ShouldAdvance);
        Assert.Equal(1, step.Invocations);
    }

    [Fact]
    public async Task Step_returning_Suspend_succeeds_but_does_not_advance()
    {
        // Given a step returning Outcome.Suspend

        // When the engine executes the step
        var result = await StepExecutor.ExecuteAsync(new SuspendingStep(), Context("B"));

        // Then the instance remains at the same step
        Assert.False(result.ShouldAdvance);

        // And the instance status is Suspended
        Assert.Equal(StepStatus.Success, result.Status);
        Assert.Equal(Outcome.Suspend, result.Outcome);
        Assert.Equal(InstanceStatus.Suspended, result.ResultingInstanceStatus);
    }

    [Fact]
    public async Task Advancing_step_leaves_the_instance_running()
    {

        var result = await StepExecutor.ExecuteAsync(new AdvancingStep(), Context());

        Assert.Equal(InstanceStatus.Running, result.ResultingInstanceStatus);
    }

    [Fact]
    public async Task Throwing_step_is_reported_as_failed_without_escaping()
    {
        // A step is untrusted business code. Its exception must become data on
        // the result, never propagate out and take the engine loop down.

        var result = await StepExecutor.ExecuteAsync(new ThrowingStep(), Context("B"));

        Assert.Equal(StepStatus.Failed, result.Status);
        Assert.False(result.ShouldAdvance);
        Assert.Equal(InstanceStatus.Failed, result.ResultingInstanceStatus);
        Assert.IsType<InvalidOperationException>(result.Error);
        Assert.Equal("B", result.StepName);
    }

    [Fact]
    public async Task Cancellation_is_not_swallowed_as_a_step_failure()
    {
        // Cancellation is the engine shutting down, not the step failing.
        // Recording it as Failed would mark healthy instances as broken on
        // every deployment.
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await StepExecutor.ExecuteAsync(new AdvancingStep(), Context(), cts.Token));
    }

    [Fact]
    public async Task Null_step_is_rejected()
    {

        await Assert.ThrowsAsync<ArgumentNullException>(
            async () => await StepExecutor.ExecuteAsync(null!, Context()));
    }
}
