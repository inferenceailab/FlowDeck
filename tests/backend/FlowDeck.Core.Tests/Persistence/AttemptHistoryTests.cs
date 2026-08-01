using FlowDeck.Core;
using FlowDeck.Core.Persistence;

namespace FlowDeck.Core.Tests.Persistence;

/// <summary>
/// Issue #107 - Record every attempt in execution history.
///
/// Scenario: Each attempt appends a history entry
/// Scenario: The attempt number is visible
/// </summary>
/// <remarks>
/// History already appended one entry per execution (#18). What was missing is
/// the attempt number, without which "three entries for one step" is ambiguous:
/// it reads identically to a step re-entered three times by a resume.
/// </remarks>
public class AttemptHistoryTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 31, 12, 0, 0, TimeSpan.Zero);

    private sealed class AlwaysThrows : IStep
    {
        public ValueTask<Outcome> ExecuteAsync(IStepContext context, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("service unavailable");
    }

    /// <summary>Fails the first <paramref name="failures"/> executions.</summary>
    private sealed class FailsThenSucceeds(List<string> log, string name, int failures) : IStep
    {
        public ValueTask<Outcome> ExecuteAsync(IStepContext context, CancellationToken cancellationToken = default)
        {
            var previous = log.Count(entry => entry == name);
            log.Add(name);

            return previous < failures
                ? throw new InvalidOperationException($"transient {previous + 1}")
                : ValueTask.FromResult(Outcome.Next);
        }
    }

    private sealed class Declared(Action<IWorkflowBuilder> declare) : IWorkflowDefinition
    {
        public string Id => "history";

        public int Version => 1;

        public void Build(IWorkflowBuilder builder) => declare(builder);
    }

    private static WorkflowEngine NewEngine(IWorkflowStore store, Action<IWorkflowBuilder> declare)
    {
        var registry = new WorkflowRegistry();
        registry.Register(new Declared(declare));
        return new WorkflowEngine(registry, store: store);
    }

    [Fact]
    public async Task Each_attempt_appends_a_history_entry()
    {
        // Given a step with a policy allowing 3 attempts
        // And the step always throws
        var store = new InMemoryWorkflowStore();
        var engine = NewEngine(store, builder => builder
            .AddStep("charge", () => new AlwaysThrows(), RetryPolicy.FixedDelay(3, TimeSpan.Zero)));

        // When an instance is started
        var instance = await engine.StartAsync("history", 1);

        // Then the history contains three entries for that step
        var history = await engine.GetHistoryAsync(instance.Id);
        var charges = history.Where(entry => entry.StepName == "charge").ToArray();

        Assert.Equal(3, charges.Length);

        // And each records its own error
        Assert.All(charges, entry =>
        {
            Assert.Equal(StepStatus.Failed, entry.Status);
            Assert.Equal("InvalidOperationException", entry.ErrorType);
            Assert.Equal("service unavailable", entry.ErrorMessage);
        });
    }

    [Fact]
    public async Task The_attempt_number_is_visible()
    {
        // Given a step that failed twice before succeeding
        var store = new InMemoryWorkflowStore();
        var log = new List<string>();

        var engine = NewEngine(store, builder => builder
            .AddStep("charge", () => new FailsThenSucceeds(log, "charge", failures: 2),
                RetryPolicy.FixedDelay(5, TimeSpan.Zero)));

        var instance = await engine.StartAsync("history", 1);

        // When I read the history
        var history = await engine.GetHistoryAsync(instance.Id);

        // Then each entry reports which attempt it was
        Assert.Equal([1, 2, 3], history.Select(entry => entry.Attempt));

        // The numbering is what makes the run legible: two failures then a
        // success, rather than three indistinguishable entries.
        Assert.Equal(
            [StepStatus.Failed, StepStatus.Failed, StepStatus.Success],
            history.Select(entry => entry.Status));
    }

    [Fact]
    public async Task An_error_is_recorded_per_attempt_not_copied_from_the_first()
    {
        // "Failed three times" is only useful if the failures can differ. A
        // step that times out twice and then gets a 500 tells an operator
        // something a repeated message would hide.
        var store = new InMemoryWorkflowStore();
        var log = new List<string>();

        var engine = NewEngine(store, builder => builder
            .AddStep("charge", () => new FailsThenSucceeds(log, "charge", failures: 3),
                RetryPolicy.FixedDelay(3, TimeSpan.Zero)));

        var instance = await engine.StartAsync("history", 1);
        var history = await engine.GetHistoryAsync(instance.Id);

        Assert.Equal(
            ["transient 1", "transient 2", "transient 3"],
            history.Select(entry => entry.ErrorMessage));
    }

    [Fact]
    public async Task A_step_that_never_retries_reports_attempt_one()
    {
        // Not zero. An execution is the first attempt whether or not a policy
        // exists, so a timeline reads consistently across both.
        var store = new InMemoryWorkflowStore();
        var log = new List<string>();

        var engine = NewEngine(store, builder => builder
            .AddStep("a", () => new FailsThenSucceeds(log, "a", failures: 0))
            .AddStep("b", () => new FailsThenSucceeds(log, "b", failures: 0)));

        var instance = await engine.StartAsync("history", 1);
        var history = await engine.GetHistoryAsync(instance.Id);

        Assert.Equal([1, 1], history.Select(entry => entry.Attempt));
    }

    [Fact]
    public async Task Each_step_numbers_its_own_attempts()
    {
        // The count is per step, not per instance. A workflow where the third
        // step failed once should not report that as attempt four.
        var store = new InMemoryWorkflowStore();
        var log = new List<string>();

        var engine = NewEngine(store, builder => builder
            .WithRetryPolicy(RetryPolicy.FixedDelay(3, TimeSpan.Zero))
            .AddStep("a", () => new FailsThenSucceeds(log, "a", failures: 1))
            .AddStep("b", () => new FailsThenSucceeds(log, "b", failures: 1)));

        var instance = await engine.StartAsync("history", 1);
        var history = await engine.GetHistoryAsync(instance.Id);

        Assert.Equal(["a", "a", "b", "b"], history.Select(entry => entry.StepName));
        Assert.Equal([1, 2, 1, 2], history.Select(entry => entry.Attempt));
    }

    [Fact]
    public async Task A_suspension_is_attempt_one_and_re_entry_is_attempt_one_again()
    {
        // Re-entering a suspended step is not a retry: the step never failed,
        // and nothing about the previous execution is being reattempted.
        // Numbering it as attempt two would report a failure that never
        // happened.
        var store = new InMemoryWorkflowStore();

        var registry = new WorkflowRegistry();
        var seen = new HashSet<Guid>();
        registry.Register(new Declared(builder => builder
            .AddStep("wait", () => new SuspendsOnce(seen))));

        var engine = new WorkflowEngine(registry, store: store);

        var instance = await engine.StartAsync("history", 1);
        Assert.Equal(InstanceStatus.Suspended, instance.Status);

        await engine.ResumeAsync(instance.Id);

        var history = await engine.GetHistoryAsync(instance.Id);

        // Two entries for one step, both attempt 1. A suspension records
        // StepStatus.Success - the step did not throw - so the attempt number
        // is the only thing distinguishing this from a retry, which is exactly
        // the ambiguity #107 exists to remove.
        Assert.Equal(2, history.Count);
        Assert.All(history, entry => Assert.Equal(StepStatus.Success, entry.Status));
        Assert.Equal([1, 1], history.Select(entry => entry.Attempt));
    }

    private sealed class SuspendsOnce(HashSet<Guid> seen) : IStep
    {
        public ValueTask<Outcome> ExecuteAsync(IStepContext context, CancellationToken cancellationToken = default)
        {
            lock (seen)
            {
                return ValueTask.FromResult(seen.Add(context.InstanceId) ? Outcome.Suspend : Outcome.Next);
            }
        }
    }

    [Fact]
    public async Task The_gap_between_attempts_is_visible_in_the_timestamps()
    {
        // The story asks for "failed three times, two seconds apart" to be a
        // fact rather than an inference. The attempt number gives the count;
        // the timestamps have to give the spacing.
        var store = new InMemoryWorkflowStore();
        var clock = new TestTimeProvider(T0);

        var registry = new WorkflowRegistry();
        registry.Register(new Declared(builder => builder
            .AddStep("charge", () => new AlwaysThrows(), RetryPolicy.FixedDelay(3, TimeSpan.FromSeconds(2)))));

        var engine = new WorkflowEngine(registry, clock, store);

        var run = engine.StartAsync("history", 1);

        while (!run.IsCompleted)
        {
            clock.Advance(TimeSpan.FromSeconds(2));
            await Task.Yield();
        }

        var history = await engine.GetHistoryAsync((await run).Id);
        var starts = history.Select(entry => entry.StartedAt).ToArray();

        // At least the configured delay, not exactly it.
        //
        // The guarantee a backoff makes is a minimum: retrying sooner than the
        // policy says would hammer a failing service, retrying later would not.
        // With jitter a minimum is all it can promise anyway.
        //
        // Exact equality is also not something this test can observe. It drives
        // the clock from outside the engine, so it may advance past the delay
        // before the engine has registered its timer at all - which is how this
        // assertion passed on one machine and reported a four second gap on
        // another. CI found that; the developer machine never would have.
        Assert.True(
            starts[1] - starts[0] >= TimeSpan.FromSeconds(2),
            $"attempt 2 began {starts[1] - starts[0]} after attempt 1, less than the 2s delay");

        Assert.True(
            starts[2] - starts[1] >= TimeSpan.FromSeconds(2),
            $"attempt 3 began {starts[2] - starts[1]} after attempt 2, less than the 2s delay");
    }
}
