using FlowDeck.Core;
using FlowDeck.Core.Persistence;

namespace FlowDeck.Core.Tests;

/// <summary>
/// Issue #119 - Roll back completed steps in reverse order when a workflow
/// fails, and #120 - report the outcome as a distinct terminal status.
///
/// Scenario: A failure rolls back completed steps
/// Scenario: Rollback runs in reverse execution order
/// Scenario: A step without a compensating action is skipped
/// Scenario: The failing step itself is compensated
/// Scenario: A fully compensated instance reports Compensated
/// Scenario: A failure with no compensating actions still reports Failed
/// Scenario: The new statuses are terminal
/// </summary>
/// <remarks>
/// Rollback and its status are one mechanism, so they are implemented together:
/// shipping the rollback without the status would mean shipping a rollback that
/// reports the wrong thing, and a test asserting <c>Failed</c> on a fully
/// compensated instance would have to be written and then immediately deleted.
/// </remarks>
public class CompensationTests
{
    /// <summary>Records what ran, in order.</summary>
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
        public string Id => "compensating";

        public int Version => 1;

        public void Build(IWorkflowBuilder builder) => declare(builder);
    }

    private static async Task<WorkflowInstance> RunAsync(
        Action<IWorkflowBuilder> declare,
        IWorkflowStore? store = null)
    {
        var registry = new WorkflowRegistry();
        registry.Register(new Declared(declare));

        return await new WorkflowEngine(registry, store: store).StartAsync("compensating", 1);
    }

    // ------------------------------------------------------------- #119

    [Fact]
    public async Task A_failure_rolls_back_completed_steps()
    {
        // Given a workflow whose first step has a compensating action
        // And whose second step throws
        var log = new List<string>();

        await RunAsync(builder => builder
            .AddStep("charge", () => new Recording(log, "charge"))
                .WithCompensation(() => new Recording(log, "refund"))
            .AddStep("ship", () => new Recording(log, "ship", throws: true)));

        // Then the compensating action runs
        Assert.Equal(["charge", "ship", "refund"], log);
    }

    [Fact]
    public async Task Rollback_runs_in_reverse_execution_order()
    {
        // Later steps may depend on what earlier ones did, so releasing stock
        // before refunding the charge that paid for it inverts a dependency the
        // forward pass established.
        var log = new List<string>();

        await RunAsync(builder => builder
            .AddStep("a", () => new Recording(log, "a")).WithCompensation(() => new Recording(log, "undo-a"))
            .AddStep("b", () => new Recording(log, "b")).WithCompensation(() => new Recording(log, "undo-b"))
            .AddStep("c", () => new Recording(log, "c", throws: true))
                .WithCompensation(() => new Recording(log, "undo-c")));

        Assert.Equal(["a", "b", "c", "undo-c", "undo-b", "undo-a"], log);
    }

    [Fact]
    public async Task A_step_without_a_compensating_action_is_skipped()
    {
        // Skipped, not treated as a failure. Most steps have nothing to undo -
        // a validation that read data changed nothing - and reporting those as
        // rollback failures would make every partial rollback look broken.
        var log = new List<string>();

        var instance = await RunAsync(builder => builder
            .AddStep("a", () => new Recording(log, "a"))
            .AddStep("b", () => new Recording(log, "b")).WithCompensation(() => new Recording(log, "undo-b"))
            .AddStep("c", () => new Recording(log, "c", throws: true)));

        Assert.Equal(["a", "b", "c", "undo-b"], log);
        Assert.Equal(InstanceStatus.Compensated, instance.Status);
    }

    [Fact]
    public async Task The_failing_step_itself_is_compensated_exactly_once()
    {
        // The least obvious decision in ADR-0021, and the one that matters
        // most. A step that never reported success may still have had an
        // effect - the charge that reached the gateway and then timed out. Once
        // rather than per attempt, because #108 requires the attempts to be
        // idempotent, so they shared one side effect.
        var log = new List<string>();

        await RunAsync(builder => builder
            .AddStep("charge", () => new Recording(log, "charge", throws: true),
                RetryPolicy.FixedDelay(3, TimeSpan.Zero))
                .WithCompensation(() => new Recording(log, "refund")));

        Assert.Equal(["charge", "charge", "charge", "refund"], log);
        Assert.Single(log, entry => entry == "refund");
    }

    [Fact]
    public async Task A_successful_workflow_rolls_nothing_back()
    {
        var log = new List<string>();

        var instance = await RunAsync(builder => builder
            .AddStep("a", () => new Recording(log, "a")).WithCompensation(() => new Recording(log, "undo-a"))
            .AddStep("b", () => new Recording(log, "b")).WithCompensation(() => new Recording(log, "undo-b")));

        Assert.Equal(["a", "b"], log);
        Assert.Equal(InstanceStatus.Completed, instance.Status);
    }

    [Fact]
    public async Task A_step_that_never_executed_is_not_compensated()
    {
        // Rolling back work that never happened would be worse than not rolling
        // back at all: it acts on the world based on a step that did nothing.
        var log = new List<string>();

        await RunAsync(builder => builder
            .AddStep("a", () => new Recording(log, "a", throws: true))
                .WithCompensation(() => new Recording(log, "undo-a"))
            .AddStep("b", () => new Recording(log, "b")).WithCompensation(() => new Recording(log, "undo-b")));

        Assert.DoesNotContain("undo-b", log);
        Assert.Equal(["a", "undo-a"], log);
    }

    [Fact]
    public async Task A_compensating_action_sees_the_workflow_data()
    {
        // An undo action needs what the forward step recorded - a transaction
        // id, a reservation reference. Without the data it cannot undo
        // anything specific.
        string? seen = null;

        await RunAsync(builder => builder
            .AddStep("charge", () => new WritesData("txn-1"))
                .WithCompensation(() => new ReadsData(value => seen = value))
            .AddStep("ship", () => new Recording([], "ship", throws: true)));

        Assert.Equal("txn-1", seen);
    }

    private sealed class WritesData(string value) : IStep
    {
        public ValueTask<Outcome> ExecuteAsync(IStepContext context, CancellationToken cancellationToken = default)
        {
            context.Data.Set("transaction", value);
            return ValueTask.FromResult(Outcome.Next);
        }
    }

    private sealed class ReadsData(Action<string?> capture) : IStep
    {
        public ValueTask<Outcome> ExecuteAsync(IStepContext context, CancellationToken cancellationToken = default)
        {
            capture(context.Data.TryGet<string>("transaction", out var value) ? value : null);
            return ValueTask.FromResult(Outcome.Next);
        }
    }

    // ------------------------------------------------------------- #120

    [Fact]
    public async Task A_fully_compensated_instance_reports_Compensated()
    {
        var log = new List<string>();

        var instance = await RunAsync(builder => builder
            .AddStep("charge", () => new Recording(log, "charge"))
                .WithCompensation(() => new Recording(log, "refund"))
            .AddStep("ship", () => new Recording(log, "ship", throws: true)));

        Assert.Equal(InstanceStatus.Compensated, instance.Status);
    }

    [Fact]
    public async Task A_failure_with_no_compensating_actions_still_reports_Failed()
    {
        // Compensated must mean something was undone. A workflow with no undo
        // actions reporting Compensated would tell an operator it cleaned up
        // when nothing happened at all.
        var log = new List<string>();

        var instance = await RunAsync(builder => builder
            .AddStep("a", () => new Recording(log, "a"))
            .AddStep("b", () => new Recording(log, "b", throws: true)));

        Assert.Equal(InstanceStatus.Failed, instance.Status);
    }

    [Fact]
    public async Task A_failed_compensating_action_reports_CompensationFailed()
    {
        var log = new List<string>();

        var instance = await RunAsync(builder => builder
            .AddStep("charge", () => new Recording(log, "charge"))
                .WithCompensation(() => new Recording(log, "refund", throws: true))
            .AddStep("ship", () => new Recording(log, "ship", throws: true)));

        Assert.Equal(InstanceStatus.CompensationFailed, instance.Status);
    }

    [Fact]
    public async Task The_original_failure_is_not_overwritten_by_the_rollback()
    {
        // An operator needs to know why the workflow failed. A compensation
        // error replacing it would lose the actual cause and leave them
        // debugging the cleanup instead of the problem.
        var log = new List<string>();

        var instance = await RunAsync(builder => builder
            .AddStep("charge", () => new Recording(log, "charge"))
                .WithCompensation(() => new Recording(log, "refund", throws: true))
            .AddStep("ship", () => new Recording(log, "ship", throws: true)));

        Assert.Equal("ship", instance.FailedStepName);
        Assert.Equal("ship failed", instance.ErrorMessage);
    }

    [Fact]
    public async Task The_new_statuses_are_terminal()
    {
        var log = new List<string>();

        var registry = new WorkflowRegistry();
        registry.Register(new Declared(builder => builder
            .AddStep("charge", () => new Recording(log, "charge"))
                .WithCompensation(() => new Recording(log, "refund"))
            .AddStep("ship", () => new Recording(log, "ship", throws: true))));

        var engine = new WorkflowEngine(registry);
        var instance = await engine.StartAsync("compensating", 1);

        Assert.Equal(InstanceStatus.Compensated, instance.Status);
        Assert.True(instance.IsTerminal);

        await Assert.ThrowsAsync<InvalidStateTransitionException>(
            async () => await engine.CancelAsync(instance.Id));
    }

    [Fact]
    public async Task CompensationFailed_is_terminal_too()
    {
        var log = new List<string>();

        var registry = new WorkflowRegistry();
        registry.Register(new Declared(builder => builder
            .AddStep("charge", () => new Recording(log, "charge"))
                .WithCompensation(() => new Recording(log, "refund", throws: true))
            .AddStep("ship", () => new Recording(log, "ship", throws: true))));

        var engine = new WorkflowEngine(registry);
        var instance = await engine.StartAsync("compensating", 1);

        Assert.True(instance.IsTerminal);

        await Assert.ThrowsAsync<InvalidStateTransitionException>(
            async () => await engine.CancelAsync(instance.Id));
    }

    [Fact]
    public async Task The_instance_is_Running_while_rolling_back()
    {
        // ADR-0008 makes terminal states final, so compensation has to happen
        // before the instance reaches one. A rollback that ran after the
        // instance was already Failed would be mutating a terminal instance.
        var store = new InMemoryWorkflowStore();
        InstanceStatus? observed = null;

        await RunAsync(
            builder => builder
                .AddStep("charge", () => new Recording([], "charge"))
                    .WithCompensation(() => new ObservesStatus(store, status => observed = status))
                .AddStep("ship", () => new Recording([], "ship", throws: true)),
            store);

        Assert.Equal(InstanceStatus.Running, observed);
    }

    /// <summary>
    /// Reads the instance's persisted status from inside a compensating action.
    /// </summary>
    /// <remarks>
    /// Through the store rather than from a field, so this asserts what a
    /// concurrent reader - an operator refreshing the dashboard mid-rollback -
    /// would actually see.
    /// </remarks>
    private sealed class ObservesStatus(IWorkflowStore store, Action<InstanceStatus> capture) : IStep
    {
        public async ValueTask<Outcome> ExecuteAsync(
            IStepContext context,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);

            var record = await store.FindAsync(context.InstanceId, cancellationToken).ConfigureAwait(false);
            capture(record!.Status);

            return Outcome.Next;
        }
    }

    [Fact]
    public async Task The_terminal_status_is_persisted()
    {
        var store = new InMemoryWorkflowStore();
        var log = new List<string>();

        var instance = await RunAsync(
            builder => builder
                .AddStep("charge", () => new Recording(log, "charge"))
                    .WithCompensation(() => new Recording(log, "refund"))
                .AddStep("ship", () => new Recording(log, "ship", throws: true)),
            store);

        var stored = await store.FindAsync(instance.Id);

        Assert.Equal(InstanceStatus.Compensated, stored!.Status);
        Assert.NotNull(stored.CompletedAt);
    }
}
