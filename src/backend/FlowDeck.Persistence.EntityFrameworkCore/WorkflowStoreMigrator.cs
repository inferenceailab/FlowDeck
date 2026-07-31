using Microsoft.EntityFrameworkCore;

namespace FlowDeck.Persistence.EntityFrameworkCore;

/// <summary>
/// Brings a database up to the schema this build expects.
/// </summary>
/// <remarks>
/// A thin, deliberately boring wrapper. The value is not in the code but in
/// what it refuses to do:
///
/// <list type="bullet">
/// <item>It never drops or recreates anything. A "fix" that recreates the
/// schema would destroy in-flight instances.</item>
/// <item>It is safe to call on every start. EF skips migrations already
/// recorded in <c>__EFMigrationsHistory</c>, so a second call is a no-op.</item>
/// <item>It reports what it did, so a deployment can log the difference between
/// "nothing to do" and "six migrations applied".</item>
/// </list>
///
/// <para>
/// <b>Migrations are provider-specific and this library ships none.</b> See
/// ADR-0015: a migration generated for PostgreSQL is not valid for SQLite or
/// SQL Server, so the host owns them.
/// </para>
/// </remarks>
public sealed class WorkflowStoreMigrator(Func<WorkflowDbContext> contextFactory)
{
    /// <summary>
    /// Applies any migrations the host has defined that are not yet recorded.
    /// </summary>
    /// <returns>The migrations applied by this call, in order. Empty if none.</returns>
    public async Task<IReadOnlyList<string>> MigrateAsync(CancellationToken cancellationToken = default)
    {
        await using var context = contextFactory();

        var pending = (await context.Database
            .GetPendingMigrationsAsync(cancellationToken)
            .ConfigureAwait(false))
            .ToArray();

        if (pending.Length == 0)
        {
            // Idempotent: already at the current schema, so nothing is written
            // and no error is raised.
            return [];
        }

        await context.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);

        return pending;
    }

    /// <summary>
    /// Reports migrations this build defines that the database has not applied.
    /// </summary>
    /// <remarks>
    /// For a readiness probe (#29): a node whose schema is behind should not
    /// take traffic.
    /// </remarks>
    public async Task<IReadOnlyList<string>> GetPendingAsync(CancellationToken cancellationToken = default)
    {
        await using var context = contextFactory();

        return [.. await context.Database.GetPendingMigrationsAsync(cancellationToken).ConfigureAwait(false)];
    }

    /// <summary>
    /// Creates the schema directly, without migrations.
    /// </summary>
    /// <remarks>
    /// For tests and throwaway development databases only. It cannot upgrade an
    /// existing schema, so a database created this way has no upgrade path -
    /// which is exactly why production must use <see cref="MigrateAsync"/>.
    /// </remarks>
    public async Task<bool> EnsureCreatedAsync(CancellationToken cancellationToken = default)
    {
        await using var context = contextFactory();

        return await context.Database.EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);
    }
}
