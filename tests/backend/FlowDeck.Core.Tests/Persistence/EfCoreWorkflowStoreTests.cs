using System.Data.Common;
using FlowDeck.Core.Persistence;
using FlowDeck.Persistence.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace FlowDeck.Core.Tests.Persistence;

/// <summary>
/// Runs the shared conformance suite against the EF Core provider.
/// </summary>
/// <remarks>
/// Issue #17. The suite is unchanged from #16 - that is the point. A provider
/// is conformant because it passes the same tests as every other provider, not
/// because it was reviewed and looked plausible.
///
/// <para>
/// <b>Runs against SQLite in memory, not PostgreSQL.</b> That keeps the test
/// suite fast and dependency-free, and it is an honest limitation: behaviour
/// that differs between SQLite and PostgreSQL is not covered here. See the
/// PostgreSQL verification issue.
/// </para>
/// </remarks>
public sealed class EfCoreWorkflowStoreTests : WorkflowStoreConformanceTests, IAsyncDisposable
{
    private readonly List<DbConnection> connections = [];

    protected override async Task<IWorkflowStore> CreateStoreAsync()
    {
        // A SQLite in-memory database lives only as long as a connection to it
        // is open, so one is held for the lifetime of the test.
        var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        this.connections.Add(connection);

        var options = new DbContextOptionsBuilder<WorkflowDbContext>()
            .UseSqlite(connection)
            .Options;

        await using (var context = new WorkflowDbContext(options))
        {
            await context.Database.EnsureCreatedAsync();
        }

        return new EfCoreWorkflowStore(() => new WorkflowDbContext(options));
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
