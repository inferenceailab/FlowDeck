using System.Data.Common;
using FlowDeck.Core.Persistence;
using FlowDeck.Persistence.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FlowDeck.Core.Tests.Persistence;

/// <summary>
/// Runs the conformance suite against a real database, when one is configured.
/// </summary>
/// <remarks>
/// FlowDeck claims to work on any relational provider because
/// <see cref="EfCoreWorkflowStore"/> depends only on
/// <c>EntityFrameworkCore.Relational</c>. That is a design claim. These
/// subclasses are what turn it into a tested one.
///
/// <para>
/// Each is skipped unless its connection string is present in the environment,
/// so <c>dotnet test</c> stays fast and dependency-free by default and reports
/// <b>skipped</b> - never a green tick for something that did not run.
/// </para>
/// </remarks>
public abstract class RelationalConformanceTests : WorkflowStoreConformanceTests, IAsyncDisposable
{
    private readonly List<WorkflowDbContext> created = [];

    /// <summary>Environment variable holding this database's connection string.</summary>
    protected abstract string ConnectionStringVariable { get; }

    /// <summary>Human name, used in the skip message.</summary>
    protected abstract string DatabaseName { get; }

    /// <summary>Applies the provider-specific <c>UseX</c> call.</summary>
    protected abstract void Configure(DbContextOptionsBuilder<WorkflowDbContext> builder, string connectionString);

    protected override async Task<IWorkflowStore> CreateStoreAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable(this.ConnectionStringVariable);

        Skip.If(
            string.IsNullOrWhiteSpace(connectionString),
            $"{this.DatabaseName} is not configured. Set {this.ConnectionStringVariable} to run these tests.");

        var builder = new DbContextOptionsBuilder<WorkflowDbContext>();
        this.Configure(builder, connectionString!);
        var options = builder.Options;

        // Each test gets a clean schema. Dropping first rather than trusting
        // the previous run to have tidied up: a leftover row would make results
        // depend on test order.
        var context = new WorkflowDbContext(options);
        this.created.Add(context);

        await context.Database.EnsureDeletedAsync();
        await context.Database.EnsureCreatedAsync();

        return new EfCoreWorkflowStore(() => new WorkflowDbContext(options));
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var context in this.created)
        {
            try
            {
                await context.Database.EnsureDeletedAsync();
            }
            catch (DbException)
            {
                // Best effort. A failure to tidy up must not mask a test result.
            }

            await context.DisposeAsync();
        }

        this.created.Clear();
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// Conformance against SQL Server.
/// </summary>
/// <remarks>
/// Run with:
/// <code>
/// $env:FLOWDECK_SQLSERVER = "Server=localhost;Database=flowdeck_test;Trusted_Connection=True;TrustServerCertificate=True"
/// dotnet test
/// </code>
/// </remarks>
public sealed class SqlServerConformanceTests : RelationalConformanceTests
{
    protected override string ConnectionStringVariable => "FLOWDECK_SQLSERVER";

    protected override string DatabaseName => "SQL Server";

    protected override void Configure(DbContextOptionsBuilder<WorkflowDbContext> builder, string connectionString) =>
        builder.UseSqlServer(connectionString);
}

/// <summary>
/// Conformance against PostgreSQL - the homelab deployment target.
/// </summary>
/// <remarks>
/// Run with:
/// <code>
/// $env:FLOWDECK_POSTGRES = "Host=localhost;Database=flowdeck_test;Username=postgres;Password=..."
/// dotnet test
/// </code>
/// </remarks>
public sealed class PostgresConformanceTests : RelationalConformanceTests
{
    protected override string ConnectionStringVariable => "FLOWDECK_POSTGRES";

    protected override string DatabaseName => "PostgreSQL";

    protected override void Configure(DbContextOptionsBuilder<WorkflowDbContext> builder, string connectionString) =>
        builder.UseNpgsql(connectionString);
}
