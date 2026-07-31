using FlowDeck.Core;

namespace FlowDeck.Core.Tests;

/// <summary>
/// Issue #8 - Record instance lifecycle timestamps.
///
/// Scenario: Timestamps are recorded
/// Scenario: An incomplete instance has no completion time
/// </summary>
public class InstanceTimestampTests
{
    private static readonly DateTimeOffset Start = new(2026, 7, 31, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Advances the shared clock, so execution appears to take time.</summary>
    private sealed class SlowStep(TestTimeProvider clock, TimeSpan duration, Outcome outcome = Outcome.Next)
        : IStep
    {
        public ValueTask<Outcome> ExecuteAsync(IStepContext context, CancellationToken cancellationToken = default)
        {
            clock.Advance(duration);
            return ValueTask.FromResult(outcome);
        }
    }

    private sealed class ThrowingStep(TestTimeProvider clock, TimeSpan duration) : IStep
    {
        public ValueTask<Outcome> ExecuteAsync(IStepContext context, CancellationToken cancellationToken = default)
        {
            clock.Advance(duration);
            throw new InvalidOperationException("boom");
        }
    }

    private sealed class OneStep(Func<IStep> factory) : IWorkflowDefinition
    {
        public string Id => "timed";

        public int Version => 1;

        public void Build(IWorkflowBuilder builder) => builder.AddStep("only", factory);
    }

    private static WorkflowEngine EngineFor(IWorkflowDefinition definition, TimeProvider clock)
    {
        var registry = new WorkflowRegistry();
        registry.Register(definition);
        return new WorkflowEngine(registry, clock);
    }

    [Fact]
    public async Task A_completed_instance_records_both_timestamps()
    {
        // Given an instance that runs to completion
        var clock = new TestTimeProvider(Start);
        var engine = EngineFor(new OneStep(() => new SlowStep(clock, TimeSpan.FromSeconds(5))), clock);

        var instance = await engine.StartAsync("timed", 1);

        // Then CreatedAt is set
        Assert.Equal(Start, instance.CreatedAt);

        // And CompletedAt is set
        Assert.NotNull(instance.CompletedAt);

        // And CompletedAt is greater than or equal to CreatedAt
        Assert.True(instance.CompletedAt >= instance.CreatedAt);
        Assert.Equal(Start.AddSeconds(5), instance.CompletedAt);
    }

    [Fact]
    public async Task An_incomplete_instance_has_no_completion_time()
    {
        // Given a suspended instance
        var clock = new TestTimeProvider(Start);
        var engine = EngineFor(
            new OneStep(() => new SlowStep(clock, TimeSpan.FromSeconds(3), Outcome.Suspend)), clock);

        var instance = await engine.StartAsync("timed", 1);

        // Then CompletedAt is null
        Assert.Equal(InstanceStatus.Suspended, instance.Status);
        Assert.Null(instance.CompletedAt);
        Assert.Equal(Start, instance.CreatedAt);
    }

    [Fact]
    public async Task A_failed_instance_is_timestamped_too()
    {
        // Failure is a terminal state, so it must carry a completion time -
        // otherwise duration cannot be measured for exactly the instances an
        // operator most wants to measure.
        var clock = new TestTimeProvider(Start);
        var engine = EngineFor(
            new OneStep(() => new ThrowingStep(clock, TimeSpan.FromSeconds(2))), clock);

        var instance = await engine.StartAsync("timed", 1);

        Assert.Equal(InstanceStatus.Failed, instance.Status);
        Assert.Equal(Start.AddSeconds(2), instance.CompletedAt);
    }

    [Fact]
    public async Task An_instantaneous_workflow_still_satisfies_the_ordering_invariant()
    {
        // A workflow fast enough to start and finish within one clock tick must
        // not produce CompletedAt < CreatedAt.
        var clock = new TestTimeProvider(Start);
        var engine = EngineFor(new OneStep(() => new SlowStep(clock, TimeSpan.Zero)), clock);

        var instance = await engine.StartAsync("timed", 1);

        Assert.Equal(instance.CreatedAt, instance.CompletedAt);
        Assert.True(instance.CompletedAt >= instance.CreatedAt);
    }

    [Fact]
    public async Task CreatedAt_is_taken_at_start_not_at_completion()
    {
        // Recording both at the end would make every instance look
        // instantaneous and destroy duration reporting.
        var clock = new TestTimeProvider(Start);
        var engine = EngineFor(new OneStep(() => new SlowStep(clock, TimeSpan.FromMinutes(10))), clock);

        var instance = await engine.StartAsync("timed", 1);

        Assert.Equal(Start, instance.CreatedAt);
        Assert.Equal(TimeSpan.FromMinutes(10), instance.CompletedAt - instance.CreatedAt);
    }

    [Fact]
    public async Task Timestamps_are_recorded_in_UTC()
    {
        // Mixed offsets across engine nodes would make instances impossible to
        // order against each other.
        var clock = new TestTimeProvider(Start);
        var engine = EngineFor(new OneStep(() => new SlowStep(clock, TimeSpan.Zero)), clock);

        var instance = await engine.StartAsync("timed", 1);

        Assert.Equal(TimeSpan.Zero, instance.CreatedAt.Offset);
        Assert.Equal(TimeSpan.Zero, instance.CompletedAt!.Value.Offset);
    }
}
