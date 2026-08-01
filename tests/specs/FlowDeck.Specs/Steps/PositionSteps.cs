using FlowDeck.Core;
using FlowDeck.Core.Persistence;
using FlowDeck.Specs.Support;
using Reqnroll;

namespace FlowDeck.Specs.Steps;

/// <summary>
/// Binds Features/Graph/Position.feature.
/// </summary>
[Binding]
public sealed class PositionSteps(EngineContext world, StoreContext stores)
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);

    private static readonly IReadOnlyList<ActiveNode> Three =
    [
        new ActiveNode("audit", Attempts: 0, BranchPath: []),
        new ActiveNode("charge", Attempts: 2, BranchPath: ["payment"]),
        new ActiveNode("reserve", Attempts: 1, BranchPath: ["fulfilment", "warehouse"]),
    ];

    private Guid instanceId;
    private WorkflowInstanceRecord? firstRead;
    private WorkflowInstanceRecord? secondRead;
    private CapturingStore? checkpoints;

    [When("an instance is saved with three active nodes")]
    public async Task WhenAnInstanceIsSavedWithThreeActiveNodes()
    {
        this.instanceId = await this.Store(Three);

        // Saved as well as created: the two write paths map the column
        // separately, and a provider that mapped only one would round-trip on
        // create and lose the set on the first checkpoint after it.
        var loaded = await stores.Store.FindAsync(this.instanceId);
        await stores.Store.SaveAsync(loaded!, []);
    }

    [Then("reading it back returns all three")]
    public async Task ThenReadingItBackReturnsAllThree()
    {
        var loaded = await stores.Store.FindAsync(this.instanceId);

        // Every field of every node. A provider that stored only the names
        // would satisfy a count check while losing the attempt counts that
        // decide whether a recovered branch retries or gives up.
        Assert.Equal(Three, loaded!.ActiveNodes);
    }

    [When("an instance with active nodes has them cleared")]
    public async Task WhenActiveNodesAreCleared()
    {
        this.instanceId = await this.Store([ActiveNode.At("charge")]);

        var loaded = await stores.Store.FindAsync(this.instanceId);

        // Not vacuous: empty is the default, so a provider that never persisted
        // the set at all would pass the clearing assertion below on its own.
        Assert.Single(loaded!.ActiveNodes);

        await stores.Store.SaveAsync(loaded with { ActiveNodes = [] }, []);
    }

    [Then("reading it back returns no active nodes")]
    public async Task ThenReadingItBackReturnsNoActiveNodes()
    {
        var loaded = await stores.Store.FindAsync(this.instanceId);

        Assert.Empty(loaded!.ActiveNodes);
    }

    // ------------------------------------------------- the projection (#163)

    [Given("a linear workflow paused at its second step")]
    public async Task GivenALinearWorkflowPausedAtItsSecondStep()
    {
        world.Declare("linear", 1, builder => builder
            .AddStep("first", () => new SpecSteps.Recording(world.Log, "first"))
            .AddStep("second", () => new SpecSteps.Suspending(world.Log, "second"))
            .AddStep("third", () => new SpecSteps.Recording(world.Log, "third")));

        world.Instance = await world.Engine().StartAsync("linear", 1);
    }

    [Then("CurrentStepIndex is {int}")]
    public void ThenCurrentStepIndexIs(int expected) =>
        Assert.Equal(expected, world.Instance!.CurrentStepIndex);

    [Then("the active node set contains exactly that step")]
    public async Task ThenTheActiveNodeSetContainsExactlyThatStep()
    {
        var node = Assert.Single(world.Instance!.ActiveNodes);

        Assert.Equal("second", node.StepName);
        Assert.Empty(node.BranchPath);

        // Durable, not merely present on the in-memory object. The projection
        // is only useful if a restarted host reads back the same position.
        var stored = await world.Store.FindAsync(world.Instance.Id);

        Assert.Equal(world.Instance.ActiveNodes, stored!.ActiveNodes);
    }

    [Given("a linear workflow whose second step has failed twice and will retry")]
    public async Task GivenASecondStepThatHasFailedTwice()
    {
        world.Declare("retrying", 1, builder => builder
            .AddStep("first", () => new SpecSteps.Recording(world.Log, "first"))
            .AddStep(
                "second",
                () => new SpecSteps.Throwing(world.Log, "second"),
                RetryPolicy.FixedDelay(maxAttempts: 5, TimeSpan.Zero)));

        // Every checkpoint is kept, because the state this scenario describes -
        // failed twice, about to try again - exists only between saves. Reading
        // the store at the end would find the instance five failures later.
        this.checkpoints = new CapturingStore(world.Store);

        world.Instance = await new WorkflowEngine(world.BuildRegistry(), store: this.checkpoints)
            .StartAsync("retrying", 1);
    }

    [Then("the active node reports two attempts")]
    public void ThenTheActiveNodeReportsTwoAttempts()
    {
        var afterSecondFailure = this.checkpoints!.Saved
            .Where(record => record.CurrentStepName == "second" && record.StepAttempts == 2)
            .ToList();

        // The checkpoint has to exist at all, or the assertion below is about an
        // empty set and proves nothing.
        Assert.NotEmpty(afterSecondFailure);

        Assert.All(afterSecondFailure, record =>
        {
            var node = Assert.Single(record.ActiveNodes);

            // The count travels with the node, not merely with the instance. A
            // projection that always wrote zero would restart a recovered
            // branch's retry budget from scratch after every crash.
            Assert.Equal("second", node.StepName);
            Assert.Equal(2, node.Attempts);
        });
    }

    [Given("a linear workflow that runs to completion")]
    public async Task GivenALinearWorkflowThatCompletes()
    {
        world.Declare("done", 1, builder => builder
            .AddStep("only", () => new SpecSteps.Recording(world.Log, "only")));

        world.Instance = await world.Engine().StartAsync("done", 1);
    }

    [Given("a linear workflow whose step fails without a retry policy")]
    public async Task GivenALinearWorkflowThatFails()
    {
        world.Declare("broken", 1, builder => builder
            .AddStep("only", () => new SpecSteps.Throwing(world.Log, "only"), RetryPolicy.None));

        world.Instance = await world.Engine().StartAsync("broken", 1);
    }

    [Then("the active node set is empty")]
    public async Task ThenTheActiveNodeSetIsEmpty()
    {
        Assert.True(world.Instance!.IsTerminal, "the instance did not reach a terminal state");
        Assert.Empty(world.Instance.ActiveNodes);

        var stored = await world.Store.FindAsync(world.Instance.Id);

        Assert.Empty(stored!.ActiveNodes);
    }

    [Then("the failed step is still named")]
    public void ThenTheFailedStepIsStillNamed()
    {
        // The distinction the empty set exists to make. A failed instance keeps
        // pointing at the step that stopped it so an operator can see where,
        // and that gravestone must not read as a place it is still running.
        Assert.Equal("only", world.Instance!.FailedStepName);
        Assert.Equal("only", world.Instance.CurrentStepName);
    }

    [Given("an instance active at two nodes")]
    public void GivenAnInstanceActiveAtTwoNodes() =>
        world.Captured["nodes"] = new List<ActiveNode>
        {
            new("charge", Attempts: 0, BranchPath: ["payment"]),
            new("reserve", Attempts: 0, BranchPath: ["fulfilment"]),
        };

    [When("one node has failed twice and the other once")]
    public void WhenOneNodeHasFailedTwiceAndTheOtherOnce()
    {
        var nodes = (List<ActiveNode>)world.Captured["nodes"]!;

        world.Captured["nodes"] = new List<ActiveNode>
        {
            nodes[0] with { Attempts = 2 },
            nodes[1] with { Attempts = 1 },
        };
    }

    [Then("each node reports its own attempt count")]
    public void ThenEachNodeReportsItsOwnAttemptCount()
    {
        var nodes = (List<ActiveNode>)world.Captured["nodes"]!;

        // The point of the story: one instance, two counts. A single
        // per-instance counter could not hold both, so a fork whose branches
        // both retry would either give up early or retry past its ceiling.
        Assert.Equal(2, nodes.Single(node => node.StepName == "charge").Attempts);
        Assert.Equal(1, nodes.Single(node => node.StepName == "reserve").Attempts);
    }

    [Given("an instance saved with three active nodes")]
    public async Task GivenAnInstanceSavedWithThreeActiveNodes() =>
        this.instanceId = await this.Store(Three);

    [When("it is read back twice")]
    public async Task WhenItIsReadBackTwice()
    {
        this.firstRead = await stores.Store.FindAsync(this.instanceId);
        this.secondRead = await stores.Store.FindAsync(this.instanceId);
    }

    [Then("the nodes come back in the same order both times")]
    public void ThenTheNodesComeBackInTheSameOrderBothTimes()
    {
        Assert.Equal(this.firstRead!.ActiveNodes, this.secondRead!.ActiveNodes);

        // Declaration order specifically. Sorting would also be stable, but a
        // resumed fork would then re-enter branches in an order the author
        // never wrote, which reads as arbitrary in a timeline.
        Assert.Equal(
            Three.Select(node => node.StepName),
            this.firstRead.ActiveNodes.Select(node => node.StepName));
    }

    private async Task<Guid> Store(IReadOnlyList<ActiveNode> nodes)
    {
        // Defaults to in-memory for the scenarios that do not name a provider:
        // stable ordering and per-node attempts are properties of the record,
        // not of one database.
        var store = stores.UseInMemoryIfUnset();

        var record = new WorkflowInstanceRecord
        {
            Id = Guid.NewGuid(),
            DefinitionId = "order",
            DefinitionVersion = 1,
            Status = InstanceStatus.Running,
            CurrentStepIndex = 0,
            CurrentStepName = nodes.Count > 0 ? nodes[0].StepName : null,
            CreatedAt = T0,
            ActiveNodes = nodes,
        };

        await store.CreateAsync(record);

        return record.Id;
    }

    /// <summary>
    /// Delegates to a real store and keeps every record written through it.
    /// </summary>
    /// <remarks>
    /// A mid-flight state is only observable while it is mid-flight. Asserting
    /// on the store afterwards would describe wherever the instance ended up,
    /// which for a retrying step is several attempts later.
    /// </remarks>
    private sealed class CapturingStore(IWorkflowStore inner) : IWorkflowStore
    {
        private readonly List<WorkflowInstanceRecord> saved = [];

        public IReadOnlyList<WorkflowInstanceRecord> Saved => this.saved;

        public async Task CreateAsync(WorkflowInstanceRecord record, CancellationToken cancellationToken = default)
        {
            await inner.CreateAsync(record, cancellationToken);
            this.saved.Add(record);
        }

        public async Task<WorkflowInstanceRecord> SaveAsync(
            WorkflowInstanceRecord record,
            IReadOnlyList<StepHistoryEntry> history,
            CancellationToken cancellationToken = default)
        {
            var result = await inner.SaveAsync(record, history, cancellationToken);
            this.saved.Add(result);
            return result;
        }

        public Task<WorkflowInstanceRecord?> FindAsync(Guid instanceId, CancellationToken cancellationToken = default) =>
            inner.FindAsync(instanceId, cancellationToken);

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
            DateTimeOffset asOf,
            int limit,
            CancellationToken cancellationToken = default) =>
            inner.FindClaimableAsync(asOf, limit, cancellationToken);
    }
}
