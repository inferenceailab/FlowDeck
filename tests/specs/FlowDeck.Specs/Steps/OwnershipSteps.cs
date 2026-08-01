using System.Text.Json;
using FlowDeck.Core;
using FlowDeck.Core.Persistence;
using FlowDeck.Persistence.EntityFrameworkCore;
using FlowDeck.Specs.Support;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Reqnroll;

namespace FlowDeck.Specs.Steps;

/// <summary>
/// Binds Features/Cluster/Ownership.feature.
/// </summary>
[Binding]
[Scope(Feature = "Instance ownership")]
public sealed class OwnershipSteps(EngineContext world, ApiContext api) : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);

    private SqliteConnection? connection;
    private IWorkflowStore? store;
    private Guid instanceId;

    private IWorkflowStore Store =>
        this.store ?? throw new InvalidOperationException("No scenario step established a store.");

    [Given("a freshly started instance")]
    public async Task GivenAFreshlyStartedInstance()
    {
        world.Declare("owned", 1, builder =>
            builder.AddStep("work", () => new SpecSteps.Recording(world.Log, "work")));

        world.Instance = await world.Engine().StartAsync("owned", 1);
    }

    [Then("it has no owner and no lease expiry")]
    public async Task ThenItHasNoOwnerOrLease()
    {
        // Read from the store, not the returned object. The scenario is about
        // what a peer polling the database would see.
        var stored = await world.Store.FindAsync(world.Instance!.Id);

        Assert.Null(stored!.OwnerNodeId);
        Assert.Null(stored.LeaseExpiresAt);
    }

    [Given(@"^the (.+) workflow store$")]
    public void GivenTheWorkflowStore(string provider) =>
        this.store = provider switch
        {
            "in-memory" => new InMemoryWorkflowStore(),
            "EF Core" => this.SqliteStore(),

            // Not a fallback to in-memory: that would report both examples
            // green while only ever exercising one of them.
            _ => throw new NotSupportedException($"Unknown provider '{provider}'."),
        };

    [When("an instance is saved with an owner and a lease expiry")]
    public async Task WhenAnInstanceIsSavedWithAnOwner()
    {
        this.instanceId = await this.CreateAsync();

        var loaded = await this.Store.FindAsync(this.instanceId);

        await this.Store.SaveAsync(
            loaded! with { OwnerNodeId = "node-a", LeaseExpiresAt = T0.AddSeconds(30) },
            []);
    }

    [Then("reading it back returns both")]
    public async Task ThenReadingItBackReturnsBoth()
    {
        var reloaded = await this.Store.FindAsync(this.instanceId);

        Assert.Equal("node-a", reloaded!.OwnerNodeId);
        Assert.Equal(T0.AddSeconds(30), reloaded.LeaseExpiresAt);
    }

    [When("an instance with an owner has that owner cleared")]
    public async Task WhenTheOwnerIsCleared()
    {
        this.instanceId = await this.CreateAsync();

        var loaded = await this.Store.FindAsync(this.instanceId);

        var owned = await this.Store.SaveAsync(
            loaded! with { OwnerNodeId = "node-a", LeaseExpiresAt = T0.AddSeconds(30) },
            []);

        // Guards the assertion below from being vacuous: null is the default,
        // so a store that never persisted the field would "pass" the clear
        // check while failing this one.
        Assert.Equal("node-a", (await this.Store.FindAsync(this.instanceId))!.OwnerNodeId);

        await this.Store.SaveAsync(owned with { OwnerNodeId = null, LeaseExpiresAt = null }, []);
    }

    [Then("reading it back returns no owner")]
    public async Task ThenReadingItBackReturnsNoOwner()
    {
        var reloaded = await this.Store.FindAsync(this.instanceId);

        Assert.Null(reloaded!.OwnerNodeId);
        Assert.Null(reloaded.LeaseExpiresAt);
    }

    // -------------------------------------------------- over HTTP (#148)

    [Given("an instance owned by {string} over HTTP")]
    public async Task GivenAnInstanceOwnedOverHttp(string node) =>
        await this.SeedOverHttpAsync(InstanceStatus.Suspended, node, T0.AddMinutes(5));

    [Given("a completed instance with no owner over HTTP")]
    public async Task GivenACompletedInstanceOverHttp() =>
        await this.SeedOverHttpAsync(InstanceStatus.Completed, owner: null, leaseExpiry: null);

    [Given("a Running instance whose lease has expired over HTTP")]
    public async Task GivenAnExpiredLeaseOverHttp() =>
        await this.SeedOverHttpAsync(InstanceStatus.Running, "dead-node", T0.AddSeconds(-1));

    [Given("a Suspended instance whose lease has expired over HTTP")]
    public async Task GivenASuspendedExpiredLeaseOverHttp() =>
        await this.SeedOverHttpAsync(InstanceStatus.Suspended, "dead-node", T0.AddSeconds(-1));

    [When("I read that instance over HTTP")]
    public async Task WhenIReadThatInstanceOverHttp() =>
        await api.SendAsync(client => client.GetAsync($"/api/instances/{this.instanceId}"));

    [Then("the body reports the owning node and lease expiry")]
    public void ThenTheBodyReportsTheOwner()
    {
        var body = JsonDocument.Parse(api.Body).RootElement;

        Assert.Equal("node-a", body.GetProperty("ownerNodeId").GetString());
        Assert.Equal(T0.AddMinutes(5), body.GetProperty("leaseExpiresAt").GetDateTimeOffset());
    }

    [Then("the body reports no owning node")]
    public void ThenTheBodyReportsNoOwner()
    {
        var body = JsonDocument.Parse(api.Body).RootElement;

        // Present and null, not absent. A client rendering "running on {node}"
        // needs to distinguish "nobody" from "the API did not tell me".
        Assert.Equal(JsonValueKind.Null, body.GetProperty("ownerNodeId").ValueKind);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("leaseExpiresAt").ValueKind);
    }

    [Then("the body says it is awaiting recovery")]
    public void ThenTheBodySaysAwaitingRecovery() =>
        Assert.True(JsonDocument.Parse(api.Body).RootElement
            .GetProperty("awaitingRecovery").GetBoolean());

    [Then("the body does not say it is awaiting recovery")]
    public void ThenTheBodyDoesNotSayAwaitingRecovery() =>
        Assert.False(JsonDocument.Parse(api.Body).RootElement
            .GetProperty("awaitingRecovery").GetBoolean());

    /// <summary>
    /// Seeds an instance into the store the running API is using.
    /// </summary>
    /// <remarks>
    /// Through the API's own store rather than a separate one: the scenario is
    /// about what a client sees, and a fixture the API cannot read would be
    /// asserting against a different world.
    /// </remarks>
    private async Task SeedOverHttpAsync(InstanceStatus status, string? owner, DateTimeOffset? leaseExpiry)
    {
        // Pins the host's clock to T0, so "expired" and "live" mean what the
        // scenario says rather than depending on when the suite happens to run.
        // Without this the API judged a lease dated 12:00 against the real time
        // of day and reported a lapsed lease as healthy.
        api.UseClock(new Microsoft.Extensions.Time.Testing.FakeTimeProvider(T0));

        api.Declare(new SpecWorkflow("owned", 1, builder =>
            builder.AddStep("wait", () => new Parks())));

        this.instanceId = Guid.NewGuid();

        await api.RunningStore.CreateAsync(new WorkflowInstanceRecord
        {
            Id = this.instanceId,
            DefinitionId = "owned",
            DefinitionVersion = 1,
            Status = status,
            CurrentStepIndex = 0,
            CurrentStepName = "wait",
            CreatedAt = T0,
            CompletedAt = status == InstanceStatus.Completed ? T0 : null,
            OwnerNodeId = owner,
            LeaseExpiresAt = leaseExpiry,
        });
    }

    private sealed class Parks : IStep
    {
        public ValueTask<Outcome> ExecuteAsync(IStepContext context, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Outcome.Suspend);
    }

    private async Task<Guid> CreateAsync()
    {
        var id = Guid.NewGuid();

        await this.Store.CreateAsync(new WorkflowInstanceRecord
        {
            Id = id,
            DefinitionId = "order",
            DefinitionVersion = 1,
            Status = InstanceStatus.Suspended,
            CurrentStepIndex = 0,
            CurrentStepName = "wait",
            CreatedAt = T0,
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
}
