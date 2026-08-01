using FlowDeck.Core;
using FlowDeck.Core.Cluster;
using FlowDeck.Core.Persistence;
using FlowDeck.Persistence.EntityFrameworkCore;
using FlowDeck.Specs.Support;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Reqnroll;

namespace FlowDeck.Specs.Steps;

/// <summary>
/// Binds Features/Cluster/Recovery.feature.
/// </summary>
[Binding]
[Scope(Feature = "Recovering abandoned work")]
public sealed class RecoverySteps(EngineContext world) : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly FakeTimeProvider clock = new(T0);

    private SqliteConnection? connection;
    private IWorkflowStore? store;
    private Guid subject;
    private IReadOnlyList<WorkflowInstanceRecord> offered = [];
    private WorkflowDispatcher? dispatcher;

    private int[] dispatchCounts = [];

    /// <summary>The dispatcher a Given built, asserted rather than assumed.</summary>
    private WorkflowDispatcher Dispatcher =>
        this.dispatcher ?? throw new InvalidOperationException("No scenario step built a dispatcher.");

    private IWorkflowStore Store =>
        this.store ?? throw new InvalidOperationException("No scenario step established a store.");

    [Given(@"^the (.+) workflow store$")]
    public void GivenTheWorkflowStore(string provider) =>
        this.store = provider switch
        {
            "in-memory" => new InMemoryWorkflowStore(),
            "EF Core" => this.SqliteStore(),
            _ => throw new NotSupportedException($"Unknown provider '{provider}'."),
        };

    // ----------------------------------------------- the claimable query

    [Given("an instance left Running with an expired lease")]
    public async Task GivenAnInstanceLeftRunning() =>
        this.subject = await this.SeedAsync(InstanceStatus.Running, "dead-node", T0.AddSeconds(-1));

    [Given("an instance owned by a node whose lease is still live")]
    public async Task GivenAnInstanceActivelyWorked() =>
        this.subject = await this.SeedAsync(InstanceStatus.Running, "busy-node", T0.AddSeconds(30));

    [Given("a completed instance with an expired lease")]
    public async Task GivenACompletedInstanceWithAnExpiredLease() =>
        this.subject = await this.SeedAsync(InstanceStatus.Completed, "dead-node", T0.AddSeconds(-1));

    [When("claimable work is queried")]
    public async Task WhenClaimableWorkIsQueried() =>
        this.offered = await this.Store.FindClaimableAsync(this.clock.GetUtcNow(), limit: 10);

    [Then("that instance is offered")]
    public void ThenThatInstanceIsOffered() =>
        Assert.Contains(this.subject, this.offered.Select(record => record.Id));

    [Then("that instance is not offered")]
    public void ThenThatInstanceIsNotOffered()
    {
        Assert.DoesNotContain(this.subject, this.offered.Select(record => record.Id));

        // The instance exists. Without this the assertion above would pass
        // against a query that returned nothing at all, for any reason.
        Assert.NotNull(this.Store.FindAsync(this.subject).GetAwaiter().GetResult());
    }

    // --------------------------------------------------- recovery (#146)

    [Given("a crashed instance that had completed steps A and B")]
    public async Task GivenACrashedInstanceAfterAAndB()
    {
        world.Declare("crashed", 1, builder => builder
            .AddStep("A", () => new SpecSteps.Recording(world.Log, "A"))
            .AddStep("B", () => new SpecSteps.Recording(world.Log, "B"))
            .AddStep("C", () => new SpecSteps.Recording(world.Log, "C")));

        this.subject = Guid.NewGuid();

        // Positioned at C, left Running by a node that died mid-step, with a
        // lapsed lease. That is exactly what a crashed host leaves behind.
        await world.Store.CreateAsync(new WorkflowInstanceRecord
        {
            Id = this.subject,
            DefinitionId = "crashed",
            DefinitionVersion = 1,
            Status = InstanceStatus.Running,
            CurrentStepIndex = 2,
            CurrentStepName = "C",
            CreatedAt = T0,
            OwnerNodeId = "dead-node",
            LeaseExpiresAt = T0.AddSeconds(-1),
        });
    }

    [When("another node recovers it")]
    public async Task WhenAnotherNodeRecoversIt()
    {
        var recovered = this.NewDispatcher("node-b");

        Assert.Equal(1, await recovered.PollOnceAsync());
    }

    [Then("execution resumes at step C")]
    public async Task ThenExecutionResumesAtC()
    {
        // A and B never run again. Recovery that re-ran completed work would be
        // worse than no recovery: their side effects already happened.
        Assert.Equal(["C"], world.Log);

        var stored = await world.Store.FindAsync(this.subject);

        Assert.Equal(InstanceStatus.Completed, stored!.Status);
    }

    // ------------------------------------------------- dispatcher (#147)

    [Given("a suspended instance and a dispatcher")]
    public async Task GivenASuspendedInstanceAndADispatcher()
    {
        var seen = new HashSet<Guid>();

        world.Declare("waiting", 1, builder => builder
            .AddStep("wait", () => new SuspendsOnce(world.Log, seen))
            .AddStep("after", () => new SpecSteps.Recording(world.Log, "after")));

        world.Instance = await world.Engine().StartAsync("waiting", 1);
        this.subject = world.Instance.Id;

        Assert.Equal(InstanceStatus.Suspended, world.Instance.Status);

        this.dispatcher = this.NewDispatcher("node-a");
    }

    [Given("one claimable instance and two dispatchers")]
    public async Task GivenOneInstanceAndTwoDispatchers() => await this.GivenASuspendedInstanceAndADispatcher();

    [Given("a claimable instance whose step throws")]
    public async Task GivenAClaimableInstanceThatThrows()
    {
        var seen = new HashSet<Guid>();

        world.Declare("breaks", 1, builder => builder
            .AddStep("wait", () => new SuspendsOnce(world.Log, seen))
            .AddStep("boom", () => new SpecSteps.Throwing(world.Log, "boom")));

        // Declared now, started later. The dispatcher captures a registry when
        // it is built, so a definition declared after it exists is one this
        // node has never heard of - and the Then would be asserting that the
        // dispatcher survived a missing definition rather than a failing step.
        world.Declare("healthy", 1, builder => builder
            .AddStep("wait-again", () => new SuspendsOnce(world.Log, seen))
            .AddStep("recovered", () => new SpecSteps.Recording(world.Log, "recovered")));

        world.Instance = await world.Engine().StartAsync("breaks", 1);
        this.subject = world.Instance.Id;

        this.dispatcher = this.NewDispatcher("node-a");
    }

    [When("the dispatcher polls")]
    public async Task WhenTheDispatcherPolls() => await this.Dispatcher.PollOnceAsync();

    [When("the dispatcher polls twice")]
    public async Task WhenTheDispatcherPollsTwice()
    {
        await this.Dispatcher.PollOnceAsync();
        await this.Dispatcher.PollOnceAsync();
    }

    [When("both dispatchers poll at the same moment")]
    public async Task WhenBothDispatchersPoll()
    {
        var counts = await Task.WhenAll(
            this.NewDispatcher("node-a").PollOnceAsync(),
            this.NewDispatcher("node-b").PollOnceAsync());

        this.dispatchCounts = counts;
    }

    [Then("the instance is resumed")]
    public async Task ThenTheInstanceIsResumed()
    {
        Assert.Contains("after", world.Log);

        var stored = await world.Store.FindAsync(this.subject);

        Assert.Equal(InstanceStatus.Completed, stored!.Status);
    }

    [Then("exactly one of them runs it")]
    public void ThenExactlyOneRunsIt()
    {
        Assert.Equal(1, this.dispatchCounts.Sum());

        // And the step ran once, not twice. The count above says one dispatcher
        // claimed it; this says the work itself happened once.
        Assert.Single(world.Log, entry => entry == "after");
    }

    [Then("the dispatcher is still polling")]
    public async Task ThenTheDispatcherIsStillPolling()
    {
        // A dispatcher that died on the first failing workflow would leave its
        // node silently idle while still looking healthy - the worst failure a
        // cluster member can have. Proven by giving the same dispatcher fresh
        // work, not by inspecting a flag on it.
        await world.Engine().StartAsync("healthy", 1);
        await this.Dispatcher.PollOnceAsync();

        Assert.Contains("recovered", world.Log);
    }

    [Then("the failing instance is recorded as Failed")]
    public async Task ThenTheFailingInstanceIsRecordedAsFailed()
    {
        // Confirms the scenario is not passing because nothing went wrong in
        // the first place - the dispatcher survived something that did fail.
        var stored = await world.Store.FindAsync(this.subject);

        Assert.Equal(InstanceStatus.Failed, stored!.Status);
    }

    [Then("the instance has no owner afterwards")]
    public async Task ThenTheInstanceHasNoOwnerAfterwards()
    {
        // Released whatever happened, so a peer can pick it up immediately
        // rather than waiting out a lease nobody is using.
        var stored = await world.Store.FindAsync(this.subject);

        Assert.Null(stored!.OwnerNodeId);
        Assert.Null(stored.LeaseExpiresAt);
    }

    [Given("an instance a node has claimed")]
    public async Task GivenAnInstanceANodeHasClaimed()
    {
        var seen = new HashSet<Guid>();

        world.Declare("checkpointing", 1, builder => builder
            .AddStep("wait", () => new SuspendsOnce(world.Log, seen))
            .AddStep("after", () => new SpecSteps.Recording(world.Log, "after")));

        world.Instance = await world.Engine(this.clock).StartAsync("checkpointing", 1);
        this.subject = world.Instance.Id;

        var claims = new InstanceClaims(
            world.Store,
            new ClusterOptions { NodeId = "node-a", LeaseDuration = TimeSpan.FromSeconds(30) },
            this.clock);

        Assert.NotNull(await claims.TryClaimAsync(this.subject));
    }

    [When("the engine checkpoints it")]
    public async Task WhenTheEngineCheckpointsIt() =>
        // Resuming runs a step and checkpoints. That is the ordinary path a
        // working node takes, and the one that used to wipe the lease.
        await world.Engine(this.clock).ResumeAsync(this.subject);

    [Then("the node still owns it")]
    public async Task ThenTheNodeStillOwnsIt()
    {
        var stored = await world.Store.FindAsync(this.subject);

        // Checkpointing must preserve the claim. Without this the node lost its
        // lease on the first step it completed, and any peer could take the
        // instance out from under it while it was still running.
        Assert.Equal("node-a", stored!.OwnerNodeId);
        Assert.NotNull(stored.LeaseExpiresAt);
    }

    [Given("a claimable instance whose definition this node does not know")]
    public async Task GivenAnInstanceThisNodeCannotRun()
    {
        // Realistic in a rolling deploy: a node running an older build meets an
        // instance of a definition only the newer build registers. It must
        // leave the work alone, not die polling.
        this.subject = Guid.NewGuid();

        await world.Store.CreateAsync(new WorkflowInstanceRecord
        {
            Id = this.subject,
            DefinitionId = "unknown-here",
            DefinitionVersion = 1,
            Status = InstanceStatus.Suspended,
            CurrentStepIndex = 0,
            CurrentStepName = "wait",
            CreatedAt = T0,
        });

        var seen = new HashSet<Guid>();

        world.Declare("healthy", 1, builder => builder
            .AddStep("wait-again", () => new SuspendsOnce(world.Log, seen))
            .AddStep("recovered", () => new SpecSteps.Recording(world.Log, "recovered")));

        this.dispatcher = this.NewDispatcher("node-a");
    }

    [Then("that instance is left for another node")]
    public async Task ThenThatInstanceIsLeftForAnotherNode()
    {
        var stored = await world.Store.FindAsync(this.subject);

        // Released, not held. A node that kept a lease on work it cannot run
        // would block every node that can, for as long as it kept renewing.
        Assert.Null(stored!.OwnerNodeId);
        Assert.Equal(InstanceStatus.Suspended, stored.Status);
    }

    private WorkflowDispatcher NewDispatcher(string node)
    {
        var options = new ClusterOptions { NodeId = node, LeaseDuration = TimeSpan.FromSeconds(30) };

        // A fresh engine per dispatcher, over the shared store: that is what a
        // second node is. One engine shared between them would hide any
        // assumption that the two nodes are the same process.
        return new WorkflowDispatcher(
            new WorkflowEngine(world.BuildRegistry(), this.clock, world.Store),
            world.Store,
            options,
            this.clock);
    }

    private async Task<Guid> SeedAsync(InstanceStatus status, string owner, DateTimeOffset leaseExpiry)
    {
        var id = Guid.NewGuid();

        await this.Store.CreateAsync(new WorkflowInstanceRecord
        {
            Id = id,
            DefinitionId = "order",
            DefinitionVersion = 1,
            Status = status,
            CurrentStepIndex = 0,
            CurrentStepName = "wait",
            CreatedAt = T0,
            CompletedAt = status == InstanceStatus.Completed ? T0 : null,
            OwnerNodeId = owner,
            LeaseExpiresAt = leaseExpiry,
        });

        return id;
    }

    private IWorkflowStore SqliteStore()
    {
        this.connection = new SqliteConnection("DataSource=:memory:");
        this.connection.Open();

        using (var context = this.Context())
        {
            context.Database.EnsureCreated();
        }

        this.store = new EfCoreWorkflowStore(this.Context);

        return this.store;
    }

    private WorkflowDbContext Context() =>
        new(new DbContextOptionsBuilder<WorkflowDbContext>().UseSqlite(this.connection!).Options);

    public void Dispose() => this.connection?.Dispose();

    /// <summary>Suspends the first time it runs for an instance, advances after.</summary>
    private sealed class SuspendsOnce(List<string> log, HashSet<Guid> seen) : IStep
    {
        public ValueTask<Outcome> ExecuteAsync(IStepContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);

            lock (seen)
            {
                if (seen.Add(context.InstanceId))
                {
                    return ValueTask.FromResult(Outcome.Suspend);
                }
            }

            log.Add("wait");
            return ValueTask.FromResult(Outcome.Next);
        }
    }
}
