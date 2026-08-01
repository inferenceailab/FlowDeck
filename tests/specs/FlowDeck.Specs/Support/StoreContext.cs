using FlowDeck.Core;
using FlowDeck.Core.Persistence;
using FlowDeck.Persistence.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace FlowDeck.Specs.Support;

/// <summary>
/// The store a scenario chose, shared across step classes.
/// </summary>
/// <remarks>
/// More than one feature says "Given the &lt;provider&gt; workflow store" and
/// means the same thing by it. Held here rather than privately in one step class
/// so a second feature can reuse the binding instead of copying it - two copies
/// would drift, and the one that drifted would still be green.
///
/// <para>
/// Reqnroll creates one per scenario and disposes it afterwards, which closes
/// the SQLite connection and with it the database.
/// </para>
/// </remarks>
public sealed class StoreContext : IDisposable
{
    private SqliteConnection? connection;
    private IWorkflowStore? store;

    /// <summary>
    /// The store a Given established.
    /// </summary>
    /// <remarks>
    /// An asserting accessor rather than a null-forgiving operator at each use.
    /// If a Given is missing, a scenario fails saying so instead of throwing a
    /// NullReferenceException from whichever line happened to dereference first.
    /// </remarks>
    public IWorkflowStore Store =>
        this.store ?? throw new InvalidOperationException("No scenario step established a store.");

    /// <summary>Selects a provider by the name a feature file uses.</summary>
    public IWorkflowStore Use(string provider) => this.store = provider switch
    {
        "in-memory" => new InMemoryWorkflowStore(),
        "EF Core" => this.Sqlite(),

        // Not a fallback to in-memory. A misspelt provider silently running the
        // in-memory store would report both examples green while only ever
        // exercising one of them.
        _ => throw new NotSupportedException($"Unknown provider '{provider}'."),
    };

    /// <summary>Falls back to the in-memory store for scenarios that name none.</summary>
    public IWorkflowStore UseInMemoryIfUnset() => this.store ??= new InMemoryWorkflowStore();

    public WorkflowDbContext ContextFactory() =>
        new(new DbContextOptionsBuilder<WorkflowDbContext>().UseSqlite(this.connection!).Options);

    private IWorkflowStore Sqlite()
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

        return new EfCoreWorkflowStore(this.ContextFactory);
    }

    public void Dispose() => this.connection?.Dispose();
}
