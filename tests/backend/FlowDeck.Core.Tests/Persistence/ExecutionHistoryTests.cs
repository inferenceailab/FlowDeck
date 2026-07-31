using FlowDeck.Core;
using FlowDeck.Core.Persistence;

namespace FlowDeck.Core.Tests.Persistence;

/// <summary>
/// Issue #18 - Record an append-only execution history per instance.
///
/// Scenario: Each step execution appends a history entry
/// Scenario: History is never mutated
/// </summary>
public class ExecutionHistoryTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 31, 12, 0, 0, TimeSpan.Zero);

    private sealed class TickingStep(TestTimeProvider clock, TimeSpan duration, Outcome outcome = Outcome.Next)
        : IStep
    {
        public ValueTask<Outcome> ExecuteAsync(IStepContext context, CancellationToken cancellationToken = default)
        {
            clock.Advance(duration);
            return ValueTask.FromResult(outcome);
        }
    }

    private sealed class ThrowingStep : IStep
    {
        public ValueTask<Outcome> ExecuteAsync(IStepContext context, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("boom");
    }

    private sealed class SuspendOnce(HashSet<Guid> seen) : IStep
    {
        public ValueTask<Outcome> ExecuteAsync(IStepContext context, CancellationToken cancellationToken = default)
        {
            lock (seen)
            {
                return ValueTask.FromResult(seen.Add(context.InstanceId) ? Outcome.Suspend : Outcome.Next);
            }
        }
    }

    private sealed class ThreeStep(Func<IStep> a, Func<IStep> b, Func<IStep> c) : IWorkflowDefinition
    {
        public string Id => "three-step";

        public int Version => 1;

        public void Build(IWorkflowBuilder builder)
        {
            builder.AddStep("A", a);
            builder.AddStep("B", b);
            builder.AddStep("C", c);
        }
    }

    private static WorkflowEngine EngineFor(IWorkflowDefinition definition, TimeProvider? clock = null, IWorkflowStore? store = null)
    {
        var registry = new WorkflowRegistry();
        registry.Register(definition);
        return new WorkflowEngine(registry, clock, store);
    }

    [Fact]
    public async Task Each_step_execution_appends_a_history_entry()
    {
        // Given a three step workflow that completes
        var clock = new TestTimeProvider(T0);
        var engine = EngineFor(
            new ThreeStep(
                () => new TickingStep(clock, TimeSpan.FromSeconds(1)),
                () => new TickingStep(clock, TimeSpan.FromSeconds(2)),
                () => new TickingStep(clock, TimeSpan.FromSeconds(3))),
            clock);

        var instance = await engine.StartAsync("three-step", 1);

        // When I read the instance history
        var history = await engine.GetHistoryAsync(instance.Id);

        // Then there are three entries in execution order
        Assert.Equal(["A", "B", "C"], history.Select(entry => entry.StepName));
        Assert.Equal([1, 2, 3], history.Select(entry => entry.Sequence));

        // And each entry records step name, start time, end time and outcome
        Assert.All(history, entry => Assert.Equal(StepStatus.Success, entry.Status));
        Assert.Equal(T0, history[0].StartedAt);
        Assert.Equal(T0.AddSeconds(1), history[0].CompletedAt);
        Assert.Equal(T0.AddSeconds(1), history[1].StartedAt);
        Assert.Equal(T0.AddSeconds(3), history[1].CompletedAt);
    }

    [Fact]
    public async Task History_is_never_mutated()
    {
        // Given an instance with existing history
        var seen = new HashSet<Guid>();
        var store = new InMemoryWorkflowStore();
        var engine = EngineFor(
            new ThreeStep(
                () => new TickingStep(new TestTimeProvider(T0), TimeSpan.Zero),
                () => new SuspendOnce(seen),
                () => new TickingStep(new TestTimeProvider(T0), TimeSpan.Zero)),
            store: store);

        var instance = await engine.StartAsync("three-step", 1);
        var before = await engine.GetHistoryAsync(instance.Id);

        // When the instance executes a further step
        await engine.ResumeAsync(instance.Id);
        var after = await engine.GetHistoryAsync(instance.Id);

        // Then earlier history entries are unchanged
        Assert.True(after.Count > before.Count);

        for (var i = 0; i < before.Count; i++)
        {
            Assert.Equal(before[i].StepName, after[i].StepName);
            Assert.Equal(before[i].Sequence, after[i].Sequence);
            Assert.Equal(before[i].StartedAt, after[i].StartedAt);
            Assert.Equal(before[i].Status, after[i].Status);
        }
    }

    [Fact]
    public async Task A_failed_step_is_recorded_with_its_error()
    {
        // History that only covered successes would be silent about exactly the
        // runs an operator opens it to investigate.
        var engine = EngineFor(new ThreeStep(
            () => new TickingStep(new TestTimeProvider(T0), TimeSpan.Zero),
            () => new ThrowingStep(),
            () => new TickingStep(new TestTimeProvider(T0), TimeSpan.Zero)));

        var instance = await engine.StartAsync("three-step", 1);
        var history = await engine.GetHistoryAsync(instance.Id);

        Assert.Equal(["A", "B"], history.Select(entry => entry.StepName));
        Assert.Equal(StepStatus.Success, history[0].Status);
        Assert.Equal(StepStatus.Failed, history[1].Status);
        Assert.Equal("InvalidOperationException", history[1].ErrorType);
        Assert.Equal("boom", history[1].ErrorMessage);
    }

    [Fact]
    public async Task A_suspension_is_recorded()
    {
        var seen = new HashSet<Guid>();
        var engine = EngineFor(new ThreeStep(
            () => new TickingStep(new TestTimeProvider(T0), TimeSpan.Zero),
            () => new SuspendOnce(seen),
            () => new TickingStep(new TestTimeProvider(T0), TimeSpan.Zero)));

        var instance = await engine.StartAsync("three-step", 1);
        var history = await engine.GetHistoryAsync(instance.Id);

        // A suspended step succeeded - it just did not finish its work.
        Assert.Equal(["A", "B"], history.Select(entry => entry.StepName));
        Assert.Equal(StepStatus.Success, history[1].Status);
    }

    [Fact]
    public async Task A_resumed_step_appends_rather_than_replacing()
    {
        // The step is re-entered on resume, so it appears twice. That is the
        // truth of what executed, and hiding it would make the history lie.
        var seen = new HashSet<Guid>();
        var store = new InMemoryWorkflowStore();
        var engine = EngineFor(new ThreeStep(
            () => new TickingStep(new TestTimeProvider(T0), TimeSpan.Zero),
            () => new SuspendOnce(seen),
            () => new TickingStep(new TestTimeProvider(T0), TimeSpan.Zero)),
            store: store);

        var instance = await engine.StartAsync("three-step", 1);
        await engine.ResumeAsync(instance.Id);

        var history = await engine.GetHistoryAsync(instance.Id);

        Assert.Equal(["A", "B", "B", "C"], history.Select(entry => entry.StepName));
        Assert.Equal([1, 2, 3, 4], history.Select(entry => entry.Sequence));
    }

    [Fact]
    public async Task History_survives_a_restart()
    {
        var seen = new HashSet<Guid>();
        var store = new InMemoryWorkflowStore();

        IWorkflowDefinition Definition() => new ThreeStep(
            () => new TickingStep(new TestTimeProvider(T0), TimeSpan.Zero),
            () => new SuspendOnce(seen),
            () => new TickingStep(new TestTimeProvider(T0), TimeSpan.Zero));

        var instance = await EngineFor(Definition(), store: store).StartAsync("three-step", 1);

        var afterRestart = EngineFor(Definition(), store: store);
        var history = await afterRestart.GetHistoryAsync(instance.Id);

        Assert.Equal(["A", "B"], history.Select(entry => entry.StepName));
    }

    [Fact]
    public async Task History_for_an_unknown_instance_is_empty()
    {
        var engine = EngineFor(new ThreeStep(
            () => new TickingStep(new TestTimeProvider(T0), TimeSpan.Zero),
            () => new TickingStep(new TestTimeProvider(T0), TimeSpan.Zero),
            () => new TickingStep(new TestTimeProvider(T0), TimeSpan.Zero)));

        Assert.Empty(await engine.GetHistoryAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task Cancellation_appends_no_history_entry()
    {
        // Cancelling is an operator action on the instance, not a step
        // execution. Recording it as one would put work in the log that never
        // ran. Who cancelled and when belongs to #66's audit trail.
        var seen = new HashSet<Guid>();
        var store = new InMemoryWorkflowStore();
        var engine = EngineFor(new ThreeStep(
            () => new TickingStep(new TestTimeProvider(T0), TimeSpan.Zero),
            () => new SuspendOnce(seen),
            () => new TickingStep(new TestTimeProvider(T0), TimeSpan.Zero)),
            store: store);

        var instance = await engine.StartAsync("three-step", 1);
        var before = await engine.GetHistoryAsync(instance.Id);

        await engine.CancelAsync(instance.Id);
        var after = await engine.GetHistoryAsync(instance.Id);

        Assert.Equal(before.Count, after.Count);
    }
}
