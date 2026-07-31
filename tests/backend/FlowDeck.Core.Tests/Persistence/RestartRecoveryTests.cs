using FlowDeck.Core;
using FlowDeck.Core.Persistence;

namespace FlowDeck.Core.Tests.Persistence;

/// <summary>
/// Issue #14 - Resume an interrupted instance after process restart.
///
/// Scenario: Suspended instance resumes on a new host
/// </summary>
/// <remarks>
/// A restart is simulated by discarding the engine and registry and building
/// new ones over the same store. That is exactly what a restarted process does:
/// nothing survives except what was persisted. Any test that reused the
/// original engine would prove nothing about recovery.
/// </remarks>
public class RestartRecoveryTests
{
    private sealed class RecordingStep(string name, List<string> log, Outcome outcome = Outcome.Next) : IStep
    {
        public ValueTask<Outcome> ExecuteAsync(IStepContext context, CancellationToken cancellationToken = default)
        {
            log.Add(name);
            return ValueTask.FromResult(outcome);
        }
    }

    /// <summary>
    /// Suspends the first time it runs for an instance, advances thereafter.
    /// Models a step waiting on something external.
    /// </summary>
    private sealed class SuspendsOnce(string name, List<string> log, HashSet<Guid> seen) : IStep
    {
        public ValueTask<Outcome> ExecuteAsync(IStepContext context, CancellationToken cancellationToken = default)
        {
            lock (seen)
            {
                if (seen.Add(context.InstanceId))
                {
                    return ValueTask.FromResult(Outcome.Suspend);
                }
            }

            log.Add(name);
            return ValueTask.FromResult(Outcome.Next);
        }
    }

    private sealed class TwoStep(Func<IStep> first, Func<IStep> second) : IWorkflowDefinition
    {
        public string Id => "two-step";

        public int Version => 1;

        public void Build(IWorkflowBuilder builder)
        {
            builder.AddStep("A", first);
            builder.AddStep("B", second);
        }
    }

    /// <summary>Builds a fresh engine over an existing store - a "restart".</summary>
    private static WorkflowEngine NewHost(IWorkflowStore store, IWorkflowDefinition definition)
    {
        var registry = new WorkflowRegistry();
        registry.Register(definition);
        return new WorkflowEngine(registry, store: store);
    }

    [Fact]
    public async Task A_suspended_instance_resumes_on_a_new_host()
    {
        // Given an instance suspended after step A
        var store = new InMemoryWorkflowStore();
        var log = new List<string>();
        var seen = new HashSet<Guid>();

        IWorkflowDefinition Definition() => new TwoStep(
            () => new SuspendsOnce("A", log, seen),
            () => new RecordingStep("B", log));

        var first = NewHost(store, Definition());
        var started = await first.StartAsync("two-step", 1);

        Assert.Equal(InstanceStatus.Suspended, started.Status);
        Assert.Empty(log);

        // And the engine host is restarted
        var second = NewHost(store, Definition());

        // When the engine resumes pending instances
        var resumed = await second.ResumeAsync(started.Id);

        // Then step B executes
        Assert.Contains("B", log);

        // And step A is not executed a second time... but it *is* re-entered,
        // because a suspended instance stays positioned on the suspending step.
        // Re-entry is not re-execution of completed work: A never completed.
        Assert.Equal(["A", "B"], log);
        Assert.Equal(InstanceStatus.Completed, resumed.Status);
    }

    [Fact]
    public async Task A_completed_step_is_never_re_executed_after_a_restart()
    {
        // The property NFR-1 rests on. Step A completes, then B suspends. After
        // a restart, A must not run again - its side effects already happened.
        var store = new InMemoryWorkflowStore();
        var log = new List<string>();
        var seen = new HashSet<Guid>();

        IWorkflowDefinition Definition() => new TwoStep(
            () => new RecordingStep("A", log),
            () => new SuspendsOnce("B", log, seen));

        var first = NewHost(store, Definition());
        var started = await first.StartAsync("two-step", 1);

        Assert.Equal(["A"], log);
        Assert.Equal(InstanceStatus.Suspended, started.Status);

        var second = NewHost(store, Definition());
        await second.ResumeAsync(started.Id);

        // A appears exactly once across both hosts.
        Assert.Equal(["A", "B"], log);
        Assert.Single(log.Where(entry => entry == "A"));
    }

    [Fact]
    public async Task A_restarted_host_sees_the_instance_it_did_not_start()
    {
        var store = new InMemoryWorkflowStore();
        var log = new List<string>();
        var seen = new HashSet<Guid>();

        IWorkflowDefinition Definition() => new TwoStep(
            () => new SuspendsOnce("A", log, seen),
            () => new RecordingStep("B", log));

        var started = await NewHost(store, Definition()).StartAsync("two-step", 1);

        var second = NewHost(store, Definition());
        var found = await second.GetInstanceAsync(started.Id);

        Assert.Equal(started.Id, found.Id);
        Assert.Equal(InstanceStatus.Suspended, found.Status);
        Assert.Equal("A", found.CurrentStepName);
    }

    [Fact]
    public async Task Resuming_a_completed_instance_is_refused_after_a_restart()
    {
        var store = new InMemoryWorkflowStore();
        var log = new List<string>();

        IWorkflowDefinition Definition() => new TwoStep(
            () => new RecordingStep("A", log),
            () => new RecordingStep("B", log));

        var started = await NewHost(store, Definition()).StartAsync("two-step", 1);
        Assert.Equal(InstanceStatus.Completed, started.Status);

        var second = NewHost(store, Definition());

        await Assert.ThrowsAsync<InvalidStateTransitionException>(
            async () => await second.ResumeAsync(started.Id));
    }

    [Fact]
    public async Task Resuming_a_cancelled_instance_is_refused_after_a_restart()
    {
        // Cancellation must remain binding across a restart, or an operator's
        // decision would be undone by a deployment.
        var store = new InMemoryWorkflowStore();
        var log = new List<string>();
        var seen = new HashSet<Guid>();

        IWorkflowDefinition Definition() => new TwoStep(
            () => new SuspendsOnce("A", log, seen),
            () => new RecordingStep("B", log));

        var first = NewHost(store, Definition());
        var started = await first.StartAsync("two-step", 1);
        await first.CancelAsync(started.Id);

        var second = NewHost(store, Definition());

        await Assert.ThrowsAsync<InvalidStateTransitionException>(
            async () => await second.ResumeAsync(started.Id));

        Assert.Empty(log);
    }

    [Fact]
    public async Task Resuming_an_unknown_instance_is_reported_clearly()
    {
        var store = new InMemoryWorkflowStore();
        var log = new List<string>();
        var engine = NewHost(store, new TwoStep(
            () => new RecordingStep("A", log),
            () => new RecordingStep("B", log)));

        var unknown = Guid.NewGuid();
        var ex = await Assert.ThrowsAsync<InstanceNotFoundException>(
            async () => await engine.ResumeAsync(unknown));

        Assert.Equal(unknown, ex.InstanceId);
    }

    [Fact]
    public async Task A_resumed_instance_runs_the_definition_version_it_started_on()
    {
        // A restart is exactly when a newer definition is likely to have been
        // deployed. The instance pinned its version at start, so it must keep
        // executing that one.
        var store = new InMemoryWorkflowStore();
        var log = new List<string>();
        var seen = new HashSet<Guid>();

        var v1 = new VersionedWorkflow(1, () => new SuspendsOnce("v1-A", log, seen), () => new RecordingStep("v1-B", log));
        var started = await NewHost(store, v1).StartAsync("versioned", 1);

        // Restart, with both versions now registered.
        var registry = new WorkflowRegistry();
        registry.Register(v1);
        registry.Register(new VersionedWorkflow(2, () => new RecordingStep("v2-A", log), () => new RecordingStep("v2-B", log)));
        var second = new WorkflowEngine(registry, store: store);

        await second.ResumeAsync(started.Id);

        Assert.DoesNotContain(log, entry => entry.StartsWith("v2-", StringComparison.Ordinal));
        Assert.Equal(["v1-A", "v1-B"], log);
    }

    private sealed class VersionedWorkflow(int version, Func<IStep> first, Func<IStep> second) : IWorkflowDefinition
    {
        public string Id => "versioned";

        public int Version => version;

        public void Build(IWorkflowBuilder builder)
        {
            builder.AddStep("A", first);
            builder.AddStep("B", second);
        }
    }
}
