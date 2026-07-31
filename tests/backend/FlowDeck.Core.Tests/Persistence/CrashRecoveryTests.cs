using FlowDeck.Core;
using FlowDeck.Core.Persistence;

namespace FlowDeck.Core.Tests.Persistence;

/// <summary>
/// Issue #22 - Preserve prior progress when a step crashes mid-execution.
///
/// Scenario: Crash during step C keeps A and B results
/// </summary>
/// <remarks>
/// A host crash is not an exception the engine can catch - the process is gone.
/// It is modelled by a store that stops accepting writes at a chosen point,
/// then building a new engine over what was durably written. Anything the store
/// had already accepted is what a restarted process would find.
/// </remarks>
public class CrashRecoveryTests
{
    /// <summary>Simulates a host dying: after N saves, every write throws.</summary>
    private sealed class CrashingStore(IWorkflowStore inner, int failAfterSaves) : IWorkflowStore
    {
        private int saves;

        public sealed class HostDiedException : Exception;

        public Task CreateAsync(WorkflowInstanceRecord record, CancellationToken cancellationToken = default) =>
            inner.CreateAsync(record, cancellationToken);

        public Task<WorkflowInstanceRecord?> FindAsync(Guid instanceId, CancellationToken cancellationToken = default) =>
            inner.FindAsync(instanceId, cancellationToken);

        public Task<WorkflowInstanceRecord> SaveAsync(
            WorkflowInstanceRecord record,
            IReadOnlyList<StepHistoryEntry> history,
            CancellationToken cancellationToken = default)
        {
            if (this.saves >= failAfterSaves)
            {
                // The process is gone. Nothing after this point is written.
                throw new HostDiedException();
            }

            this.saves++;
            return inner.SaveAsync(record, history, cancellationToken);
        }

        public Task<IReadOnlyList<StepHistoryEntry>> GetHistoryAsync(
            Guid instanceId, CancellationToken cancellationToken = default) =>
            inner.GetHistoryAsync(instanceId, cancellationToken);

        public Task<IReadOnlyList<WorkflowInstanceRecord>> ListAsync(
            InstanceFilter filter, CancellationToken cancellationToken = default) =>
            inner.ListAsync(filter, cancellationToken);

        public Task<int> CountAsync(InstanceFilter filter, CancellationToken cancellationToken = default) =>
            inner.CountAsync(filter, cancellationToken);
    }

    private sealed class RecordingStep(string name, List<string> log) : IStep
    {
        public ValueTask<Outcome> ExecuteAsync(IStepContext context, CancellationToken cancellationToken = default)
        {
            log.Add(name);
            return ValueTask.FromResult(Outcome.Next);
        }
    }

    private sealed class ThreeStep(List<string> log) : IWorkflowDefinition
    {
        public string Id => "three-step";

        public int Version => 1;

        public void Build(IWorkflowBuilder builder)
        {
            builder.AddStep("A", () => new RecordingStep("A", log));
            builder.AddStep("B", () => new RecordingStep("B", log));
            builder.AddStep("C", () => new RecordingStep("C", log));
        }
    }

    private static WorkflowEngine NewHost(IWorkflowStore store, IWorkflowDefinition definition)
    {
        var registry = new WorkflowRegistry();
        registry.Register(definition);
        return new WorkflowEngine(registry, store: store);
    }

    [Fact]
    public async Task A_crash_during_step_C_keeps_the_A_and_B_results()
    {
        // Given steps A and B have completed
        // And step C crashes the host process
        var durable = new InMemoryWorkflowStore();
        var log = new List<string>();

        // Two saves get through: the checkpoints after A and after B. The
        // third - after C - is where the host dies.
        var crashing = new CrashingStore(durable, failAfterSaves: 2);

        var dyingHost = NewHost(crashing, new ThreeStep(log));

        await Assert.ThrowsAsync<CrashingStore.HostDiedException>(
            async () => await dyingHost.StartAsync("three-step", 1));

        Assert.Equal(["A", "B", "C"], log);

        // When the engine restarts and resumes the instance
        var survivor = (await durable.ListAsync(new InstanceFilter())).Single();
        var newLog = new List<string>();
        var newHost = NewHost(durable, new ThreeStep(newLog));

        // The instance is Running in the store - the crash left it mid-flight.
        // Recovery has to accept that state, so it is nudged back to Suspended
        // the way a recovery sweep (#39) would before resuming.
        var reloaded = (await durable.FindAsync(survivor.Id))!;
        await durable.SaveAsync(reloaded with { Status = InstanceStatus.Suspended }, []);

        var resumed = await newHost.ResumeAsync(survivor.Id);

        // Then steps A and B are not re-executed
        Assert.DoesNotContain("A", newLog);
        Assert.DoesNotContain("B", newLog);

        // And execution resumes at step C
        Assert.Equal(["C"], newLog);
        Assert.Equal(InstanceStatus.Completed, resumed.Status);
    }

    [Fact]
    public async Task Progress_up_to_the_last_accepted_checkpoint_is_durable()
    {
        // The guarantee ADR-0013 buys: at most one step of progress is lost.
        var durable = new InMemoryWorkflowStore();
        var log = new List<string>();
        var crashing = new CrashingStore(durable, failAfterSaves: 1);

        await Assert.ThrowsAsync<CrashingStore.HostDiedException>(
            async () => await NewHost(crashing, new ThreeStep(log)).StartAsync("three-step", 1));

        var survivor = (await durable.ListAsync(new InstanceFilter())).Single();

        // One checkpoint accepted, so the store knows A completed.
        Assert.Equal(1, survivor.CurrentStepIndex);
        Assert.Equal("A", survivor.CurrentStepName);
    }

    [Fact]
    public async Task History_never_contains_a_step_the_state_says_did_not_happen()
    {
        // The atomicity clause of ADR-0013, observed from above the store. A
        // rejected save must append nothing, or history would describe work the
        // instance state has no record of.
        var durable = new InMemoryWorkflowStore();
        var log = new List<string>();
        var crashing = new CrashingStore(durable, failAfterSaves: 2);

        await Assert.ThrowsAsync<CrashingStore.HostDiedException>(
            async () => await NewHost(crashing, new ThreeStep(log)).StartAsync("three-step", 1));

        var survivor = (await durable.ListAsync(new InstanceFilter())).Single();
        var history = await durable.GetHistoryAsync(survivor.Id);

        // C executed in the dead process but its checkpoint never landed, so it
        // must not appear in history either.
        Assert.Equal(["A", "B"], history.Select(entry => entry.StepName));
        Assert.Equal(2, survivor.CurrentStepIndex);
    }

    [Fact]
    public async Task A_crash_before_the_first_checkpoint_leaves_a_startable_instance()
    {
        // The instance is created before execution (ADR-0007), so even a crash
        // during the very first step leaves something an operator can find
        // rather than a silent disappearance.
        var durable = new InMemoryWorkflowStore();
        var log = new List<string>();
        var crashing = new CrashingStore(durable, failAfterSaves: 0);

        await Assert.ThrowsAsync<CrashingStore.HostDiedException>(
            async () => await NewHost(crashing, new ThreeStep(log)).StartAsync("three-step", 1));

        var survivor = (await durable.ListAsync(new InstanceFilter())).Single();

        Assert.Equal(0, survivor.CurrentStepIndex);
        Assert.Equal(InstanceStatus.Running, survivor.Status);
        Assert.Empty(await durable.GetHistoryAsync(survivor.Id));
    }
}
