using FlowDeck.Core;
using FlowDeck.Core.Persistence;
using FlowDeck.Specs.Support;
using Reqnroll;

namespace FlowDeck.Specs.Steps;

/// <summary>
/// Binds Features/Persistence/Durability.feature.
/// </summary>
[Binding]
public sealed class DurabilitySteps(EngineContext world)
{
    private CountingStore? counting;
    private CrashingStore? crashing;

    [Given("a three step workflow")]
    public void GivenAThreeStepWorkflow()
    {
        this.counting = new CountingStore(world.Store);

        world.Declare("three-step", 1, builder => builder
            .AddStep("A", () => new SpecSteps.Recording(world.Log, "A"))
            .AddStep("B", () => new SpecSteps.Recording(world.Log, "B"))
            .AddStep("C", () => new SpecSteps.Recording(world.Log, "C")));
    }

    [When("the instance executes to completion")]
    public async Task WhenTheInstanceExecutesToCompletion() =>
        world.Instance = await new WorkflowEngine(world.BuildRegistry(), store: this.counting)
            .StartAsync("three-step", 1);

    [Then("the persistence provider received at least three saves")]
    public void ThenTheProviderReceivedAtLeastThreeSaves() =>
        Assert.True(
            this.counting!.Saves >= 3,
            $"expected at least 3 saves, the provider received {this.counting.Saves}");

    [Then("the final saved state has status Completed")]
    public async Task ThenTheFinalSavedStateIsCompleted()
    {
        // Read back out of the store, not off the returned instance. The
        // scenario is about what was persisted, and asserting the in-process
        // object would pass even if nothing had been written at all.
        var stored = await world.Store.FindAsync(world.Instance!.Id);

        Assert.Equal(InstanceStatus.Completed, stored!.Status);
    }

    [Given("an instance suspended after step A")]
    public async Task GivenAnInstanceSuspendedAfterStepA()
    {
        world.Declare("resumable", 1, builder => builder
            .AddStep("A", () => new SuspendsFirstTime(world.Log, "A", world.Captured))
            .AddStep("B", () => new SpecSteps.Recording(world.Log, "B")));

        world.Instance = await world.Engine().StartAsync("resumable", 1);

        Assert.Equal(InstanceStatus.Suspended, world.Instance.Status);
    }

    [Given("the engine host is restarted")]
    public void GivenTheEngineHostIsRestarted()
    {
        // Nothing to tear down: the next step builds a new engine and registry
        // over the same store, which is what a restarted process has. Reusing
        // the original engine would prove nothing about recovery, so the
        // restart lives in RestartedHost rather than being implied here.
    }

    [When("the engine resumes pending instances")]
    public async Task WhenTheEngineResumesPendingInstances() =>
        world.Instance = await world.RestartedHost().ResumeAsync(world.Instance!.Id);

    [Then("step B executes")]
    public void ThenStepBExecutes() => Assert.Contains("B", world.Log);

    [Then("step A is not executed a second time")]
    public void ThenStepAIsNotExecutedTwice()
    {
        // A is re-entered, because a suspended instance stays positioned on the
        // suspending step - but it never *completed*, so re-entry is not
        // re-execution of finished work. What must not happen is A's body
        // running its side effect twice, which is what the log records.
        Assert.Single(world.Log, entry => entry == "A");
    }

    [Given("step A wrote {string} = {int} before suspension")]
    public async Task GivenStepAWroteBeforeSuspension(string key, int value)
    {
        world.Declare("data-restart", 1, builder => builder
            .AddStep("A", () => new WritesThenSuspendsOnce(key, value, world.Captured))
            .AddStep("B", () => new SpecSteps.Reading<int>(key, read => world.Captured["B"] = read)));

        world.Instance = await world.Engine().StartAsync("data-restart", 1);

        Assert.Equal(InstanceStatus.Suspended, world.Instance.Status);
    }

    [When("the instance resumes after a restart")]
    public async Task WhenTheInstanceResumesAfterARestart() =>
        world.Instance = await world.RestartedHost().ResumeAsync(world.Instance!.Id);

    [Given("steps A and B have completed")]
    public void GivenStepsAAndBHaveCompleted()
    {
        world.Declare("crashing", 1, builder => builder
            .AddStep("A", () => new SpecSteps.Recording(world.Log, "A"))
            .AddStep("B", () => new SpecSteps.Recording(world.Log, "B"))
            .AddStep("C", () => new SpecSteps.Recording(world.Log, "C")));
    }

    [Given("step C crashes the host process")]
    public async Task GivenStepCCrashesTheHost()
    {
        // A crash is not an exception the engine can catch - the process is
        // gone. Modelled by a store that stops accepting writes once A and B
        // are durable, so what survives is exactly what a restarted process
        // would find.
        this.crashing = new CrashingStore(world.Store, acceptSaves: 2);

        await Assert.ThrowsAsync<CrashingStore.HostDiedException>(async () =>
            await new WorkflowEngine(world.BuildRegistry(), store: this.crashing).StartAsync("crashing", 1));

        Assert.Equal(["A", "B", "C"], world.Log);
        world.Log.Clear();
    }

    [When("the engine restarts and resumes the instance")]
    public async Task WhenTheEngineRestartsAndResumes()
    {
        var stored = (await world.Store.ListAsync(new InstanceFilter()))[0];

        // The crash left the instance Running, and nothing sweeps a Running
        // instance back to Suspended yet (#39). Marking it here is that sweep,
        // stood in for by hand: the scenario is about what resume does with the
        // durable state, not about how the instance became eligible.
        await world.Store.SaveAsync(stored with { Status = InstanceStatus.Suspended }, []);

        world.Instance = await world.RestartedHost().ResumeAsync(stored.Id);
    }

    [Then("steps A and B are not re-executed")]
    public void ThenStepsAAndBAreNotReExecuted()
    {
        Assert.DoesNotContain("A", world.Log);
        Assert.DoesNotContain("B", world.Log);
    }

    [Then("execution resumes at step C")]
    public void ThenExecutionResumesAtStepC()
    {
        Assert.Equal(["C"], world.Log);
        Assert.Equal(InstanceStatus.Completed, world.Instance!.Status);
    }

    /// <summary>Suspends the first time it runs for an instance, advances after.</summary>
    private sealed class SuspendsFirstTime(List<string> log, string name, Dictionary<string, object?> seen) : IStep
    {
        public ValueTask<Outcome> ExecuteAsync(IStepContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);

            var key = $"{name}-seen-{context.InstanceId}";

            if (!seen.ContainsKey(key))
            {
                seen[key] = true;
                return ValueTask.FromResult(Outcome.Suspend);
            }

            log.Add(name);
            return ValueTask.FromResult(Outcome.Next);
        }
    }

    /// <summary>
    /// Writes a value on its first execution only, then suspends.
    /// </summary>
    /// <remarks>
    /// The write happens <b>once</b>, and that is the whole point. An earlier
    /// version wrote on every execution, including re-entry after the restart -
    /// so the later step read the value whether or not the store had preserved
    /// anything. A mutation making resume discard persisted data passed the
    /// scenario cleanly.
    ///
    /// <para>
    /// Writing only the first time means the value the reader sees can only
    /// have come back out of the store.
    /// </para>
    /// </remarks>
    private sealed class WritesThenSuspendsOnce(string key, object? value, Dictionary<string, object?> seen) : IStep
    {
        public ValueTask<Outcome> ExecuteAsync(IStepContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);

            var marker = $"wrote-{context.InstanceId}";

            if (seen.ContainsKey(marker))
            {
                return ValueTask.FromResult(Outcome.Next);
            }

            seen[marker] = true;
            context.Data.Set(key, value);

            return ValueTask.FromResult(Outcome.Suspend);
        }
    }

    /// <summary>Counts saves, so "checkpointed after every step" is observable.</summary>
    private sealed class CountingStore(IWorkflowStore inner) : IWorkflowStore
    {
        public int Saves { get; private set; }

        public Task CreateAsync(WorkflowInstanceRecord record, CancellationToken cancellationToken = default) =>
            inner.CreateAsync(record, cancellationToken);

        public Task<WorkflowInstanceRecord?> FindAsync(Guid instanceId, CancellationToken cancellationToken = default) =>
            inner.FindAsync(instanceId, cancellationToken);

        public Task<WorkflowInstanceRecord> SaveAsync(
            WorkflowInstanceRecord record,
            IReadOnlyList<StepHistoryEntry> history,
            CancellationToken cancellationToken = default)
        {
            this.Saves++;
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

        public Task<int> PurgeAsync(DateTimeOffset completedBefore, CancellationToken cancellationToken = default) =>
            inner.PurgeAsync(completedBefore, cancellationToken);
    }

    /// <summary>Stops accepting writes after N saves — a host that died.</summary>
    private sealed class CrashingStore(IWorkflowStore inner, int acceptSaves) : IWorkflowStore
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
            if (this.saves >= acceptSaves)
            {
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

        public Task<int> PurgeAsync(DateTimeOffset completedBefore, CancellationToken cancellationToken = default) =>
            inner.PurgeAsync(completedBefore, cancellationToken);
    }
}
