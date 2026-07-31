using FlowDeck.Core;
using FlowDeck.Core.Persistence;

namespace FlowDeck.Core.Tests;

/// <summary>
/// Issue #121 - Continue rollback past a failing compensating action.
///
/// Scenario: Rollback continues past a failure
/// Scenario: Every failure is recorded
/// Scenario: The original failure is not overwritten
/// </summary>
/// <remarks>
/// These exist because a mutation test found the gap. Replacing "continue" with
/// "stop at the first failure" in <c>CompensateAsync</c> left every #119 and
/// #120 test green - the behaviour ADR-0021 decided was not pinned by anything.
/// A decision nothing tests is a comment.
/// </remarks>
public class CompensationFailureTests
{
    private sealed class Recording(List<string> log, string name, bool throws = false) : IStep
    {
        public ValueTask<Outcome> ExecuteAsync(IStepContext context, CancellationToken cancellationToken = default)
        {
            log.Add(name);

            return throws
                ? throw new InvalidOperationException($"{name} failed")
                : ValueTask.FromResult(Outcome.Next);
        }
    }

    private sealed class Declared(Action<IWorkflowBuilder> declare) : IWorkflowDefinition
    {
        public string Id => "rollback";

        public int Version => 1;

        public void Build(IWorkflowBuilder builder) => declare(builder);
    }

    private static async Task<(WorkflowInstance Instance, IReadOnlyList<StepHistoryEntry> History)> RunAsync(
        Action<IWorkflowBuilder> declare)
    {
        var registry = new WorkflowRegistry();
        registry.Register(new Declared(declare));

        var engine = new WorkflowEngine(registry);
        var instance = await engine.StartAsync("rollback", 1);

        return (instance, await engine.GetHistoryAsync(instance.Id));
    }

    [Fact]
    public async Task Rollback_continues_past_a_failure()
    {
        // Given three steps with compensating actions
        // And the second action to run throws
        var log = new List<string>();

        await RunAsync(builder => builder
            .AddStep("a", () => new Recording(log, "a")).WithCompensation(() => new Recording(log, "undo-a"))
            .AddStep("b", () => new Recording(log, "b"))
                .WithCompensation(() => new Recording(log, "undo-b", throws: true))
            .AddStep("c", () => new Recording(log, "c", throws: true))
                .WithCompensation(() => new Recording(log, "undo-c")));

        // Then all three actions are attempted. Refusing to refund because
        // releasing stock failed does not make the situation safer - it adds a
        // second unresolved side effect to the first.
        Assert.Equal(["a", "b", "c", "undo-c", "undo-b", "undo-a"], log);
    }

    [Fact]
    public async Task Every_failure_is_recorded()
    {
        // Given two compensating actions that both throw
        var log = new List<string>();

        var (_, history) = await RunAsync(builder => builder
            .AddStep("a", () => new Recording(log, "a"))
                .WithCompensation(() => new Recording(log, "undo-a", throws: true))
            .AddStep("b", () => new Recording(log, "b", throws: true))
                .WithCompensation(() => new Recording(log, "undo-b", throws: true)));

        // Then history records both, each with its own error
        var rollback = history.Where(entry => entry.StepName.StartsWith("compensate:", StringComparison.Ordinal))
            .ToArray();

        Assert.Equal(["compensate:b", "compensate:a"], rollback.Select(entry => entry.StepName));
        Assert.All(rollback, entry => Assert.Equal(StepStatus.Failed, entry.Status));
        Assert.Equal(["undo-b failed", "undo-a failed"], rollback.Select(entry => entry.ErrorMessage));
    }

    [Fact]
    public async Task A_successful_rollback_is_recorded_too()
    {
        // Not only failures. "One of two undone" needs both halves on the
        // record, or an operator can only see what went wrong and has to
        // assume the rest.
        var log = new List<string>();

        var (_, history) = await RunAsync(builder => builder
            .AddStep("a", () => new Recording(log, "a")).WithCompensation(() => new Recording(log, "undo-a"))
            .AddStep("b", () => new Recording(log, "b", throws: true))
                .WithCompensation(() => new Recording(log, "undo-b", throws: true)));

        var rollback = history.Where(entry => entry.StepName.StartsWith("compensate:", StringComparison.Ordinal))
            .ToArray();

        Assert.Equal([StepStatus.Failed, StepStatus.Success], rollback.Select(entry => entry.Status));
    }

    [Fact]
    public async Task The_original_failure_survives_the_rollback()
    {
        // Given a workflow whose step failed with "card declined"
        // And whose compensating action fails with something else
        var log = new List<string>();

        var (instance, _) = await RunAsync(builder => builder
            .AddStep("charge", () => new Recording(log, "charge"))
                .WithCompensation(() => new Throws("refund gateway unreachable"))
            .AddStep("ship", () => new Throws("card declined")));

        // Then the instance still reports the original failure. Losing it would
        // leave an operator debugging the cleanup instead of the problem.
        Assert.Equal("ship", instance.FailedStepName);
        Assert.Equal("card declined", instance.ErrorMessage);
        Assert.Equal(InstanceStatus.CompensationFailed, instance.Status);
    }

    private sealed class Throws(string message) : IStep
    {
        public ValueTask<Outcome> ExecuteAsync(IStepContext context, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException(message);
    }

    [Fact]
    public async Task One_failure_among_many_successes_still_reports_CompensationFailed()
    {
        // Best-effort is not the same as "mostly worked". Any unresolved side
        // effect needs a human, so a single failure decides the status.
        var log = new List<string>();

        var (instance, _) = await RunAsync(builder => builder
            .AddStep("a", () => new Recording(log, "a")).WithCompensation(() => new Recording(log, "undo-a"))
            .AddStep("b", () => new Recording(log, "b"))
                .WithCompensation(() => new Recording(log, "undo-b", throws: true))
            .AddStep("c", () => new Recording(log, "c")).WithCompensation(() => new Recording(log, "undo-c"))
            .AddStep("d", () => new Recording(log, "d", throws: true)));

        Assert.Equal(InstanceStatus.CompensationFailed, instance.Status);
    }

    [Fact]
    public async Task A_compensating_action_that_throws_does_not_unwind_the_engine()
    {
        // NFR-3: step code is untrusted, and a compensating action is step
        // code. An exception escaping to the caller would make rollback
        // something callers must wrap in try/catch.
        var log = new List<string>();

        var (instance, _) = await RunAsync(builder => builder
            .AddStep("a", () => new Recording(log, "a"))
                .WithCompensation(() => new Throws("undo exploded"))
            .AddStep("b", () => new Recording(log, "b", throws: true)));

        // Reached here at all, which is the assertion.
        Assert.Equal(InstanceStatus.CompensationFailed, instance.Status);
    }
}
