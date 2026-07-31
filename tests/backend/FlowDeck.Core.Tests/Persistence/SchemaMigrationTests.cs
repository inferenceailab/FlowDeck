using System.Data.Common;
using FlowDeck.Core;
using FlowDeck.Core.Persistence;
using FlowDeck.Persistence.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace FlowDeck.Core.Tests.Persistence;

/// <summary>
/// Issue #21 - Apply persistence schema migrations safely.
///
/// Scenario: Migration is idempotent
/// </summary>
public sealed class SchemaMigrationTests : IAsyncDisposable
{
    private readonly List<DbConnection> connections = [];

    private async Task<DbContextOptions<WorkflowDbContext>> NewDatabaseAsync()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        this.connections.Add(connection);

        return new DbContextOptionsBuilder<WorkflowDbContext>()
            .UseSqlite(connection)
            .Options;
    }

    [Fact]
    public async Task Applying_migrations_to_a_current_database_changes_nothing_and_does_not_throw()
    {
        // Given a store already at the current schema version
        var options = await this.NewDatabaseAsync();
        var migrator = new WorkflowStoreMigrator(() => new WorkflowDbContext(options));

        await migrator.EnsureCreatedAsync();

        // When migrations are applied again
        var applied = await migrator.MigrateAsync();

        // Then no changes are made and no error is raised
        Assert.Empty(applied);
    }

    [Fact]
    public async Task Migrating_twice_in_a_row_is_a_no_op_the_second_time()
    {
        var options = await this.NewDatabaseAsync();
        var migrator = new WorkflowStoreMigrator(() => new WorkflowDbContext(options));

        await migrator.MigrateAsync();
        var second = await migrator.MigrateAsync();

        Assert.Empty(second);
    }

    [Fact]
    public async Task Migration_never_destroys_existing_instances()
    {
        // The property that matters more than the mechanism. A migrator that
        // "fixed" a schema by recreating it would destroy in-flight work.
        var options = await this.NewDatabaseAsync();
        var migrator = new WorkflowStoreMigrator(() => new WorkflowDbContext(options));
        await migrator.EnsureCreatedAsync();

        var store = new EfCoreWorkflowStore(() => new WorkflowDbContext(options));
        var record = new WorkflowInstanceRecord
        {
            Id = Guid.NewGuid(),
            DefinitionId = "order",
            DefinitionVersion = 1,
            Status = InstanceStatus.Suspended,
            CurrentStepIndex = 1,
            CurrentStepName = "B",
            CreatedAt = new DateTimeOffset(2026, 7, 31, 12, 0, 0, TimeSpan.Zero),
        };

        await store.CreateAsync(record);

        await migrator.MigrateAsync();

        var survivor = await store.FindAsync(record.Id);

        Assert.NotNull(survivor);
        Assert.Equal(InstanceStatus.Suspended, survivor.Status);
        Assert.Equal("B", survivor.CurrentStepName);
    }

    [Fact]
    public async Task Pending_migrations_are_reportable_without_applying_them()
    {
        // A readiness probe (#29) needs to know the schema is behind without
        // taking the side effect of fixing it.
        var options = await this.NewDatabaseAsync();
        var migrator = new WorkflowStoreMigrator(() => new WorkflowDbContext(options));

        var pending = await migrator.GetPendingAsync();

        // This library ships no migrations (ADR-0015), so nothing is pending
        // and the call is still safe and side-effect free.
        Assert.Empty(pending);

        // The schema does not exist yet, proving GetPendingAsync created nothing.
        await using var context = new WorkflowDbContext(options);
        Assert.False(await context.Database.CanConnectAsync() && await AnyTableAsync(context));
    }

    private static async Task<bool> AnyTableAsync(WorkflowDbContext context)
    {
        try
        {
            await context.Instances.AnyAsync();
            return true;
        }
        catch (SqliteException)
        {
            // "no such table" - which is the point.
            return false;
        }
    }

    [Fact]
    public async Task EnsureCreated_reports_whether_it_created_the_schema()
    {
        var options = await this.NewDatabaseAsync();
        var migrator = new WorkflowStoreMigrator(() => new WorkflowDbContext(options));

        Assert.True(await migrator.EnsureCreatedAsync());
        Assert.False(await migrator.EnsureCreatedAsync());
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var connection in this.connections)
        {
            await connection.DisposeAsync();
        }

        this.connections.Clear();
    }
}
