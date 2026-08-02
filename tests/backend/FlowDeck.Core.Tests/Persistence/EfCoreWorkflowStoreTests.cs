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

    /// <summary>
    /// The harness shares one connection, and Sqlite cannot take two at once.
    /// </summary>
    /// <remarks>
    /// A SQLite in-memory database lives only as long as a connection to it is
    /// open, so every context here is handed the <i>same</i>
    /// <see cref="SqliteConnection"/>. Microsoft.Data.Sqlite does not support
    /// concurrent commands on one connection and fails with
    /// <c>SQLite Error 5: unable to delete/modify user-function due to active
    /// statements</c>.
    ///
    /// <para>
    /// That is this test harness's limitation, not FlowDeck's: the same case
    /// passes against PostgreSQL and against the in-memory store. It is the
    /// divergence #78 was filed about, found by running the suite against the
    /// real target rather than reasoned about.
    /// </para>
    /// </remarks>
    protected override string? ConcurrentWritersUnsupported =>
        "the SQLite in-memory harness shares one connection across every context";

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
