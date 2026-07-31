using FlowDeck.Core;

namespace FlowDeck.Core.Tests;

/// <summary>
/// Issue #12 - Cancel a running workflow instance.
///
/// Scenario: A suspended instance can be cancelled
/// Scenario: A completed instance cannot be cancelled
/// </summary>
public class InstanceCancellationTests
{
    private static readonly DateTimeOffset Start = new(2026, 7, 31, 12, 0, 0, TimeSpan.Zero);

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

    private sealed class TwoStep(Func<IStep> first) : IWorkflowDefinition
    {
        public string Id => "cancellable";

        public int Version => 1;

        public void Build(IWorkflowBuilder builder)
        {
            builder.AddStep("A", first);
            builder.AddStep("B", () => new NoopStep());
        }
    }

    private static WorkflowEngine EngineFor(Func<IStep> first, TimeProvider? clock = null)
    {
        var registry = new WorkflowRegistry();
        registry.Register(new TwoStep(first));
        return new WorkflowEngine(registry, clock);
    }

    [Fact]
    public async Task A_suspended_instance_can_be_cancelled()
    {
        // Given a suspended instance
        var engine = EngineFor(() => new SuspendingStep());
        var started = await engine.StartAsync("cancellable", 1);
        Assert.Equal(InstanceStatus.Suspended, started.Status);

        // When I cancel it
        var cancelled = engine.Cancel(started.Id);

        // Then the instance status becomes Cancelled
        Assert.Equal(InstanceStatus.Cancelled, cancelled.Status);
        Assert.Equal(InstanceStatus.Cancelled, engine.GetInstance(started.Id).Status);
    }

    [Fact]
    public async Task No_further_steps_execute_after_cancellation()
    {
        // "And no further steps execute" - resuming a cancelled instance must
        // be refused, otherwise cancellation is only advisory.
        var engine = EngineFor(() => new SuspendingStep());
        var started = await engine.StartAsync("cancellable", 1);

        engine.Cancel(started.Id);

        await Assert.ThrowsAsync<InvalidStateTransitionException>(
            async () => await engine.ResumeAsync(started.Id));
    }

    [Fact]
    public async Task A_completed_instance_cannot_be_cancelled()
    {
        // Given a completed instance
        var engine = EngineFor(() => new NoopStep());
        var started = await engine.StartAsync("cancellable", 1);
        Assert.Equal(InstanceStatus.Completed, started.Status);

        // When I cancel it
        // Then the call fails with an InvalidStateTransitionException
        var ex = Assert.Throws<InvalidStateTransitionException>(() => engine.Cancel(started.Id));

        Assert.Equal(InstanceStatus.Completed, ex.From);
        Assert.Equal(InstanceStatus.Cancelled, ex.To);
        Assert.Equal(started.Id, ex.InstanceId);
    }

    [Fact]
    public async Task A_failed_instance_cannot_be_cancelled()
    {
        // Failure is terminal too. Allowing it would rewrite history and lose
        // the recorded cause.
        var engine = EngineFor(() => new ThrowingStep());
        var started = await engine.StartAsync("cancellable", 1);

        var ex = Assert.Throws<InvalidStateTransitionException>(() => engine.Cancel(started.Id));

        Assert.Equal(InstanceStatus.Failed, ex.From);
    }

    [Fact]
    public async Task Cancelling_twice_is_refused_rather_than_silently_accepted()
    {
        // Silently accepting would overwrite the first cancellation timestamp
        // and make the audit trail lie about when work actually stopped.
        var engine = EngineFor(() => new SuspendingStep());
        var started = await engine.StartAsync("cancellable", 1);

        engine.Cancel(started.Id);

        Assert.Throws<InvalidStateTransitionException>(() => engine.Cancel(started.Id));
    }

    [Fact]
    public async Task Cancellation_is_timestamped_and_terminal()
    {
        var clock = new TestTimeProvider(Start);
        var engine = EngineFor(() => new SuspendingStep(), clock);
        var started = await engine.StartAsync("cancellable", 1);

        clock.Advance(TimeSpan.FromMinutes(3));
        var cancelled = engine.Cancel(started.Id);

        Assert.True(cancelled.IsTerminal);
        Assert.Equal(Start.AddMinutes(3), cancelled.CompletedAt);
    }

    [Fact]
    public async Task A_cancelled_instance_keeps_the_step_it_stopped_at()
    {
        // An operator asking "where did this stop?" needs the answer to survive
        // cancellation.
        var engine = EngineFor(() => new SuspendingStep());
        var started = await engine.StartAsync("cancellable", 1);

        var cancelled = engine.Cancel(started.Id);

        Assert.Equal("A", cancelled.CurrentStepName);
    }

    [Fact]
    public void Cancelling_an_unknown_instance_is_reported_clearly()
    {
        var engine = new WorkflowEngine(new WorkflowRegistry());
        var unknown = Guid.NewGuid();

        var ex = Assert.Throws<InstanceNotFoundException>(() => engine.Cancel(unknown));

        Assert.Equal(unknown, ex.InstanceId);
    }

    [Fact]
    public async Task A_suspended_instance_resumes_when_not_cancelled()
    {
        // The counterpart to the cancellation test: resume must actually work,
        // or "no further steps execute" proves nothing.
        var executed = new List<string>();
        var registry = new WorkflowRegistry();
        registry.Register(new ResumableWorkflow(executed));
        var engine = new WorkflowEngine(registry);

        var started = await engine.StartAsync("resumable", 1);
        Assert.Equal(InstanceStatus.Suspended, started.Status);

        var resumed = await engine.ResumeAsync(started.Id);

        Assert.Equal(InstanceStatus.Completed, resumed.Status);
        Assert.Equal(["A", "B"], executed);
    }

    /// <summary>Suspends on its first execution of A, then advances.</summary>
    private sealed class ResumableWorkflow(List<string> executed) : IWorkflowDefinition
    {
        public string Id => "resumable";

        public int Version => 1;

        public void Build(IWorkflowBuilder builder)
        {
            builder.AddStep("A", () => new SuspendOnceStep(executed));
            builder.AddStep("B", () => new RecordingStep("B", executed));
        }
    }

    private sealed class SuspendOnceStep(List<string> executed) : IStep
    {
        private static readonly HashSet<Guid> Seen = [];

        public ValueTask<Outcome> ExecuteAsync(IStepContext context, CancellationToken cancellationToken = default)
        {
            lock (Seen)
            {
                if (Seen.Add(context.InstanceId))
                {
                    return ValueTask.FromResult(Outcome.Suspend);
                }
            }

            executed.Add("A");
            return ValueTask.FromResult(Outcome.Next);
        }
    }

    private sealed class RecordingStep(string name, List<string> executed) : IStep
    {
        public ValueTask<Outcome> ExecuteAsync(IStepContext context, CancellationToken cancellationToken = default)
        {
            executed.Add(name);
            return ValueTask.FromResult(Outcome.Next);
        }
    }
}
