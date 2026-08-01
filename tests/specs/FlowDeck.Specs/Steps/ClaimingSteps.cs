using FlowDeck.Core;
using FlowDeck.Core.Cluster;
using FlowDeck.Core.Persistence;
using FlowDeck.Specs.Support;
using Microsoft.Extensions.Time.Testing;
using Reqnroll;

namespace FlowDeck.Specs.Steps;

/// <summary>
/// Binds Features/Cluster/Claiming.feature.
/// </summary>
[Binding]
[Scope(Feature = "Claiming an instance")]
public sealed class ClaimingSteps(EngineContext world)
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly FakeTimeProvider clock = new(T0);

    private Guid instanceId;
    private WorkflowInstanceRecord? claimed;
    private WorkflowInstanceRecord? renewed;
    private DateTimeOffset? expiryBefore;
    private bool[] raceOutcomes = [];

    private InstanceClaims ClaimsFor(string node) =>
        new(world.Store, new ClusterOptions { NodeId = node, LeaseDuration = TimeSpan.FromSeconds(30) }, this.clock);

    // ------------------------------------------------------------ claiming

    [Given("a suspended instance with no owner")]
    public async Task GivenASuspendedInstanceWithNoOwner() =>
        this.instanceId = await this.SeedAsync(InstanceStatus.Suspended);

    [Given("a completed instance")]
    public async Task GivenACompletedInstance() =>
        this.instanceId = await this.SeedAsync(InstanceStatus.Completed);

    [Given("an instance owned by node A with a live lease")]
    public async Task GivenAnInstanceOwnedByAWithALiveLease()
    {
        this.instanceId = await this.SeedAsync(InstanceStatus.Suspended);

        Assert.NotNull(await this.ClaimsFor("node-a").TryClaimAsync(this.instanceId));
    }

    [Given("an instance owned by node A")]
    public async Task GivenAnInstanceOwnedByA() => await this.GivenAnInstanceOwnedByAWithALiveLease();

    [Given("an instance owned by node A whose lease has expired")]
    public async Task GivenAnExpiredLease()
    {
        await this.GivenAnInstanceOwnedByAWithALiveLease();

        // Past the 30 second lease. Advanced rather than waited: a scenario
        // that slept for a real lease would take half a minute and prove the
        // same thing.
        this.clock.Advance(TimeSpan.FromSeconds(31));
    }

    [When("node {word} claims it")]
    public async Task WhenNodeClaimsIt(string node) =>
        this.claimed = await this.ClaimsFor($"node-{node.ToLowerInvariant()}").TryClaimAsync(this.instanceId);

    [When("node {word} tries to claim it")]
    public async Task WhenNodeTriesToClaimIt(string node) => await this.WhenNodeClaimsIt(node);

    [Then("node {word} owns it")]
    public async Task ThenNodeOwnsIt(string node)
    {
        Assert.NotNull(this.claimed);

        // Read back from the store, not from the returned record: the scenario
        // is about what every other node now sees.
        var stored = await world.Store.FindAsync(this.instanceId);

        Assert.Equal($"node-{node.ToLowerInvariant()}", stored!.OwnerNodeId);
    }

    [Then("the lease expires in the future")]
    public async Task ThenTheLeaseExpiresInTheFuture()
    {
        var stored = await world.Store.FindAsync(this.instanceId);

        Assert.NotNull(stored!.LeaseExpiresAt);
        Assert.True(stored.LeaseExpiresAt > this.clock.GetUtcNow());
    }

    [Then("the claim is refused")]
    public void ThenTheClaimIsRefused() => Assert.Null(this.claimed);

    [Then("node {word} still owns it")]
    public async Task ThenNodeStillOwnsIt(string node)
    {
        var stored = await world.Store.FindAsync(this.instanceId);

        Assert.Equal($"node-{node.ToLowerInvariant()}", stored!.OwnerNodeId);
    }

    // ------------------------------------------------------------ the race

    [When("node A and node B both read it before either writes")]
    public void WhenBothNodesReadBeforeEitherWrites()
    {
        // Nothing to arrange: the next step performs the interleave. Recorded
        // as a step because the scenario reads as a sequence, and because a
        // reader needs to know the two reads genuinely precede both writes.
    }

    [When("both then try to claim it")]
    public async Task WhenBothTryToClaim()
    {
        // A real interleave, not two sequential calls. Each node reads at the
        // same revision, and only then do they write - which is the situation a
        // claim has to survive. Two nodes simply asking one after the other
        // would prove nothing about atomicity: the second would read the first
        // node's write and decline politely.
        var gate = new ReadBarrier(world.Store, participants: 2);

        var nodeA = new InstanceClaims(gate, new ClusterOptions { NodeId = "node-a" }, this.clock);
        var nodeB = new InstanceClaims(gate, new ClusterOptions { NodeId = "node-b" }, this.clock);

        var results = await Task.WhenAll(
            nodeA.TryClaimAsync(this.instanceId),
            nodeB.TryClaimAsync(this.instanceId));

        this.raceOutcomes = [.. results.Select(result => result is not null)];
    }

    [Then("exactly one claim succeeds")]
    public async Task ThenExactlyOneClaimSucceeds()
    {
        Assert.Single(this.raceOutcomes, succeeded => succeeded);

        // And the store agrees: one owner, not a value written twice.
        var stored = await world.Store.FindAsync(this.instanceId);

        Assert.Contains(stored!.OwnerNodeId, new[] { "node-a", "node-b" });
    }

    /// <summary>
    /// Holds every reader until all participants have read.
    /// </summary>
    /// <remarks>
    /// Forces the interleave the racing scenario describes: both nodes see the
    /// same revision, then both write. Without it the two claims would be
    /// sequential and the scenario would pass against a store with no
    /// concurrency control at all.
    /// </remarks>
    private sealed class ReadBarrier(IWorkflowStore inner, int participants) : IWorkflowStore
    {
        private readonly TaskCompletionSource everyoneHasRead =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private int reads;

        public async Task<WorkflowInstanceRecord?> FindAsync(
            Guid instanceId,
            CancellationToken cancellationToken = default)
        {
            var record = await inner.FindAsync(instanceId, cancellationToken).ConfigureAwait(false);

            if (Interlocked.Increment(ref this.reads) >= participants)
            {
                this.everyoneHasRead.TrySetResult();
            }

            await this.everyoneHasRead.Task.ConfigureAwait(false);

            return record;
        }

        public Task CreateAsync(WorkflowInstanceRecord record, CancellationToken cancellationToken = default) =>
            inner.CreateAsync(record, cancellationToken);

        public Task<WorkflowInstanceRecord> SaveAsync(
            WorkflowInstanceRecord record,
            IReadOnlyList<StepHistoryEntry> history,
            CancellationToken cancellationToken = default) =>
            inner.SaveAsync(record, history, cancellationToken);

        public Task<IReadOnlyList<StepHistoryEntry>> GetHistoryAsync(
            Guid instanceId,
            CancellationToken cancellationToken = default) =>
            inner.GetHistoryAsync(instanceId, cancellationToken);

        public Task<IReadOnlyList<WorkflowInstanceRecord>> ListAsync(
            InstanceFilter filter,
            CancellationToken cancellationToken = default) =>
            inner.ListAsync(filter, cancellationToken);

        public Task<int> CountAsync(InstanceFilter filter, CancellationToken cancellationToken = default) =>
            inner.CountAsync(filter, cancellationToken);

        public Task<int> PurgeAsync(DateTimeOffset completedBefore, CancellationToken cancellationToken = default) =>
            inner.PurgeAsync(completedBefore, cancellationToken);

        public Task<IReadOnlyList<WorkflowInstanceRecord>> FindClaimableAsync(
            DateTimeOffset asOf, int limit, CancellationToken cancellationToken = default) =>
            inner.FindClaimableAsync(asOf, limit, cancellationToken);
    }

    // ------------------------------------------------------------- renewal

    [When("node A renews the lease")]
    public async Task WhenNodeARenews()
    {
        this.expiryBefore = (await world.Store.FindAsync(this.instanceId))!.LeaseExpiresAt;

        // Time has to move, or renewal computes the same expiry and the
        // scenario would assert that nothing changed.
        this.clock.Advance(TimeSpan.FromSeconds(5));

        this.renewed = await this.ClaimsFor("node-a").TryRenewAsync(this.instanceId);
    }

    [When("node B tries to renew it")]
    public async Task WhenNodeBTriesToRenew()
    {
        this.expiryBefore = (await world.Store.FindAsync(this.instanceId))!.LeaseExpiresAt;
        this.renewed = await this.ClaimsFor("node-b").TryRenewAsync(this.instanceId);
    }

    [Then("the expiry moves further into the future")]
    public async Task ThenTheExpiryMoves()
    {
        Assert.NotNull(this.renewed);

        var stored = await world.Store.FindAsync(this.instanceId);

        Assert.True(
            stored!.LeaseExpiresAt > this.expiryBefore,
            $"expiry was {this.expiryBefore} and is now {stored.LeaseExpiresAt}");
    }

    [Then("the renewal is refused")]
    public async Task ThenTheRenewalIsRefused()
    {
        Assert.Null(this.renewed);

        // And nothing moved. A refused renewal that still extended the lease
        // would hand node B control of node A's work by accident.
        var stored = await world.Store.FindAsync(this.instanceId);

        Assert.Equal(this.expiryBefore, stored!.LeaseExpiresAt);
    }

    // ------------------------------------------------------------- fencing

    [Given("node A is running an instance")]
    public async Task GivenNodeAIsRunningAnInstance()
    {
        this.instanceId = await this.SeedAsync(InstanceStatus.Suspended);

        var record = await this.ClaimsFor("node-a").TryClaimAsync(this.instanceId);

        // The state node A holds in memory while it works. Its next checkpoint
        // will be written from this revision.
        world.Captured["node-a-state"] = record!;
    }

    [Given("node B has taken the lease")]
    public async Task GivenNodeBHasTakenTheLease()
    {
        this.clock.Advance(TimeSpan.FromSeconds(31));

        Assert.NotNull(await this.ClaimsFor("node-b").TryClaimAsync(this.instanceId));
    }

    [When("node A reaches its next checkpoint")]
    public async Task WhenNodeAReachesItsNextCheckpoint()
    {
        var stale = (WorkflowInstanceRecord)world.Captured["node-a-state"]!;

        try
        {
            await world.Store.SaveAsync(stale with { CurrentStepIndex = stale.CurrentStepIndex + 1 }, []);
        }
        catch (Exception ex)
        {
            world.Error = ex;
        }
    }

    [Then("the save is rejected and node A stops")]
    public async Task ThenTheSaveIsRejected()
    {
        // Fencing comes free from the Revision guard: node B's claim bumped the
        // revision, so node A's checkpoint cannot land.
        Assert.IsType<WorkflowStoreConcurrencyException>(world.Error);

        var stored = await world.Store.FindAsync(this.instanceId);

        Assert.Equal("node-b", stored!.OwnerNodeId);

        // What this does NOT establish, and the ADR is explicit about: node A
        // may already have executed the step before trying to write. Fencing
        // bounds the damage to one recorded progression - it does not make the
        // step run once.
        Assert.Equal(0, stored.CurrentStepIndex);
    }

    private async Task<Guid> SeedAsync(InstanceStatus status)
    {
        var id = Guid.NewGuid();

        await world.Store.CreateAsync(new WorkflowInstanceRecord
        {
            Id = id,
            DefinitionId = "order",
            DefinitionVersion = 1,
            Status = status,
            CurrentStepIndex = 0,
            CurrentStepName = "wait",
            CreatedAt = T0,
            CompletedAt = status == InstanceStatus.Completed ? T0 : null,
        });

        return id;
    }
}
