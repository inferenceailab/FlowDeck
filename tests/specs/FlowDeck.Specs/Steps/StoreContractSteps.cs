using FlowDeck.Core;
using FlowDeck.Core.Persistence;
using FlowDeck.Persistence.EntityFrameworkCore;
using FlowDeck.Specs.Support;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Reqnroll;

namespace FlowDeck.Specs.Steps;

/// <summary>
/// Binds Features/Persistence/StoreContract.feature.
/// </summary>
[Binding]
public sealed class StoreContractSteps(EngineContext world) : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);

    private SqliteConnection? connection;
    private IWorkflowStore? store;
    private WorkflowInstanceRecord? record;
    private WorkflowInstanceRecord? saved;
    private WorkflowInstanceRecord? stale;
    private Exception? storeError;
    private IReadOnlyList<string>? appliedTwice;

    /// <summary>
    /// The store a Given established.
    /// </summary>
    /// <remarks>
    /// An asserting accessor rather than a null-forgiving operator at each use.
    /// If a Given is missing, a scenario fails saying so instead of throwing a
    /// NullReferenceException from whichever line happened to dereference first.
    /// </remarks>
    private IWorkflowStore Store =>
        this.store ?? throw new InvalidOperationException("No scenario step established a store.");

    private WorkflowInstanceRecord Record =>
        this.record ?? throw new InvalidOperationException("No scenario step created an instance.");

    private WorkflowInstanceRecord Saved =>
        this.saved ?? throw new InvalidOperationException("No scenario step saved an instance.");

    // A regex rather than {word}: the outline's second example is "EF Core",
    // and {word} captures a single word, so that row silently failed to bind.
    [Given(@"^the (.+) workflow store$")]
    public void GivenTheWorkflowStore(string provider) =>
        this.store = provider switch
        {
            "in-memory" => new InMemoryWorkflowStore(),
            "EF Core" => this.SqliteStore(),

            // Not a fallback to in-memory. A misspelt provider silently running
            // the in-memory store would report both examples green while only
            // ever exercising one of them.
            _ => throw new NotSupportedException($"Unknown provider '{provider}'."),
        };

    [When("an instance is created and then saved with new state")]
    public async Task WhenAnInstanceIsCreatedAndSaved()
    {
        this.record = NewRecord();

        await this.Store.CreateAsync(this.Record);

        var loaded = await this.Store.FindAsync(this.Record.Id);

        this.saved = await this.Store.SaveAsync(
            loaded! with { Status = InstanceStatus.Completed, CurrentStepName = null, CompletedAt = T0.AddMinutes(1) },
            []);
    }

    [Then("reading it back returns the saved state")]
    public async Task ThenReadingItBackReturnsTheSavedState()
    {
        var reloaded = await this.Store.FindAsync(this.Record.Id);

        Assert.Equal(InstanceStatus.Completed, reloaded!.Status);
        Assert.Null(reloaded.CurrentStepName);
        Assert.Equal(T0.AddMinutes(1), reloaded.CompletedAt);
    }

    [Then("the revision has advanced")]
    public async Task ThenTheRevisionHasAdvanced()
    {
        var reloaded = await this.Store.FindAsync(this.Record.Id);

        Assert.True(this.Saved.Revision > 1, "the save did not advance the revision");
        Assert.Equal(this.Saved.Revision, reloaded!.Revision);
    }

    [When("two batches of history are appended")]
    public async Task WhenTwoBatchesOfHistoryAreAppended()
    {
        this.record = NewRecord();
        await this.Store.CreateAsync(this.Record);

        var loaded = await this.Store.FindAsync(this.Record.Id);
        var afterFirst = await this.Store.SaveAsync(loaded!, [History(this.Record.Id, "A")]);

        await this.Store.SaveAsync(
            afterFirst,
            [History(this.Record.Id, "B"), History(this.Record.Id, "C")]);
    }

    [Then("the entries are returned in execution order")]
    public async Task ThenEntriesAreReturnedInOrder()
    {
        var history = await this.Store.GetHistoryAsync(this.Record.Id);

        Assert.Equal(["A", "B", "C"], history.Select(entry => entry.StepName));
        Assert.Equal([1, 2, 3], history.Select(entry => entry.Sequence));
    }

    [Then("the earlier entries are unchanged")]
    public async Task ThenEarlierEntriesAreUnchanged()
    {
        var history = await this.Store.GetHistoryAsync(this.Record.Id);

        // The first batch keeps its sequence and its timestamps. A provider
        // that renumbered on append would still return three entries in order,
        // which is why order alone is not enough here.
        Assert.Equal(1, history[0].Sequence);
        Assert.Equal(T0, history[0].StartedAt);
    }

    // ------------------------------------------------------- history (#18)

    [Given("a three step workflow that completes")]
    public async Task GivenAThreeStepWorkflowThatCompletes()
    {
        world.Declare("history", 1, builder => builder
            .AddStep("A", () => new SpecSteps.Recording(world.Log, "A"))
            .AddStep("B", () => new SpecSteps.Recording(world.Log, "B"))
            .AddStep("C", () => new SpecSteps.Recording(world.Log, "C")));

        world.Instance = await world.Engine().StartAsync("history", 1);
    }

    [When("I read the instance history")]
    public async Task WhenIReadTheInstanceHistory() =>
        world.Captured["history"] = await world.Engine().GetHistoryAsync(world.Instance!.Id);

    private IReadOnlyList<StepHistoryEntry> CapturedHistory =>
        (IReadOnlyList<StepHistoryEntry>)world.Captured["history"]!;

    [Then("there are three entries in execution order")]
    public void ThenThereAreThreeEntriesInOrder() =>
        Assert.Equal(["A", "B", "C"], this.CapturedHistory.Select(entry => entry.StepName));

    [Then("each entry records step name, start time, end time and outcome")]
    public void ThenEachEntryRecordsItsDetails() =>
        Assert.All(this.CapturedHistory, entry =>
        {
            Assert.False(string.IsNullOrWhiteSpace(entry.StepName));
            Assert.NotEqual(default, entry.StartedAt);
            Assert.True(entry.CompletedAt >= entry.StartedAt);
            Assert.Equal(StepStatus.Success, entry.Status);
        });

    [Given("an instance with existing history")]
    public async Task GivenAnInstanceWithExistingHistory()
    {
        world.Declare("appending", 1, builder => builder
            .AddStep("A", () => new SpecSteps.Recording(world.Log, "A"))
            .AddStep("B", () => new SuspendsOnce(world.Captured))
            .AddStep("C", () => new SpecSteps.Recording(world.Log, "C")));

        world.Instance = await world.Engine().StartAsync("appending", 1);

        world.Captured["before"] = await world.Engine().GetHistoryAsync(world.Instance.Id);
    }

    [When("the instance executes a further step")]
    public async Task WhenTheInstanceExecutesAFurtherStep()
    {
        world.Instance = await world.Engine().ResumeAsync(world.Instance!.Id);
        world.Captured["history"] = await world.Engine().GetHistoryAsync(world.Instance.Id);
    }

    [Then("earlier history entries are unchanged")]
    public void ThenEarlierHistoryEntriesAreUnchanged()
    {
        var before = (IReadOnlyList<StepHistoryEntry>)world.Captured["before"]!;
        var after = this.CapturedHistory;

        Assert.True(after.Count > before.Count, "no further history was appended");

        // Compared entry by entry rather than by count. An append that rewrote
        // an earlier row would leave the count correct and the record wrong,
        // which is the whole point of "append-only".
        Assert.Equal(before, after.Take(before.Count));
    }

    // --------------------------------------------------- concurrency (#19)

    [Given("an instance loaded at its current revision")]
    public async Task GivenAnInstanceLoadedAtItsCurrentRevision()
    {
        this.store ??= new InMemoryWorkflowStore();
        this.record = NewRecord();

        await this.Store.CreateAsync(this.Record);
        this.stale = await this.Store.FindAsync(this.Record.Id);
    }

    [Given("another writer has since saved a newer revision")]
    public async Task GivenAnotherWriterHasSaved()
    {
        var theirs = await this.Store.FindAsync(this.Record.Id);

        this.saved = await this.Store.SaveAsync(theirs! with { CurrentStepName = "theirs" }, []);
    }

    [When("the first writer saves")]
    public async Task WhenTheFirstWriterSaves()
    {
        try
        {
            await this.Store.SaveAsync(this.stale! with { CurrentStepName = "mine" }, []);
        }
        catch (Exception ex)
        {
            this.storeError = ex;
        }
    }

    [Then("a WorkflowStoreConcurrencyException is raised")]
    public void ThenAConcurrencyExceptionIsRaised() =>
        Assert.IsType<WorkflowStoreConcurrencyException>(this.storeError);

    [Then("the stored state remains at the newer revision")]
    public async Task ThenTheStoredStateRemainsAtTheNewerRevision()
    {
        var current = await this.Store.FindAsync(this.Record.Id);

        Assert.Equal(this.Saved.Revision, current!.Revision);

        // The rejected write must have changed nothing, not merely failed.
        Assert.Equal("theirs", current.CurrentStepName);
    }

    // ---------------------------------------------------- migration (#21)

    [Given("a store already at the current schema version")]
    public async Task GivenAStoreAtTheCurrentSchemaVersion()
    {
        this.SqliteStore();

        await new WorkflowStoreMigrator(this.ContextFactory).EnsureCreatedAsync();
    }

    [When("migrations are applied again")]
    public async Task WhenMigrationsAreAppliedAgain()
    {
        var migrator = new WorkflowStoreMigrator(this.ContextFactory);

        this.appliedTwice = await migrator.MigrateAsync();
    }

    [Then("no changes are made and no error is raised")]
    public async Task ThenNoChangesAreMade()
    {
        Assert.Empty(this.appliedTwice!);

        // Still usable afterwards. "No error raised" would be satisfied by a
        // migrator that quietly dropped the schema and reported nothing.
        var probe = NewRecord();
        await this.Store.CreateAsync(probe);

        Assert.NotNull(await this.Store.FindAsync(probe.Id));
    }

    private WorkflowDbContext ContextFactory() =>
        new(new DbContextOptionsBuilder<WorkflowDbContext>().UseSqlite(this.connection!).Options);

    private IWorkflowStore SqliteStore()
    {
        // A shared in-memory SQLite connection: a real relational provider with
        // no file to clean up. Closing the connection drops the database, which
        // is what Dispose does.
        this.connection = new SqliteConnection("DataSource=:memory:");
        this.connection.Open();

        using (var context = this.ContextFactory())
        {
            context.Database.EnsureCreated();
        }

        this.store = new EfCoreWorkflowStore(this.ContextFactory);

        return this.store;
    }

    private static WorkflowInstanceRecord NewRecord() => new()
    {
        Id = Guid.NewGuid(),
        DefinitionId = "order",
        DefinitionVersion = 1,
        Status = InstanceStatus.Running,
        CurrentStepIndex = 0,
        CurrentStepName = "A",
        CreatedAt = T0,
    };

    private static StepHistoryEntry History(Guid instanceId, string stepName) => new()
    {
        InstanceId = instanceId,
        Sequence = 0,
        StepName = stepName,
        StartedAt = T0,
        CompletedAt = T0.AddSeconds(1),
        Status = StepStatus.Success,
        Attempt = 1,
    };

    public void Dispose() => this.connection?.Dispose();

    /// <summary>Suspends the first time it runs for an instance.</summary>
    private sealed class SuspendsOnce(Dictionary<string, object?> seen) : IStep
    {
        public ValueTask<Outcome> ExecuteAsync(IStepContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);

            var key = $"suspended-{context.InstanceId}";

            if (seen.ContainsKey(key))
            {
                return ValueTask.FromResult(Outcome.Next);
            }

            seen[key] = true;
            return ValueTask.FromResult(Outcome.Suspend);
        }
    }
}
