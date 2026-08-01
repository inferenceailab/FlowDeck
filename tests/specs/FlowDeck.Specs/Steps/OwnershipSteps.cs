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
public sealed class OwnershipSteps(EngineContext world) : IDisposable
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
