using FlowDeck.Core;
using Microsoft.EntityFrameworkCore;

namespace FlowDeck.Persistence.EntityFrameworkCore;

/// <summary>
/// The relational shape of a workflow instance.
/// </summary>
/// <remarks>
/// Separate from <see cref="Core.Persistence.WorkflowInstanceRecord"/> because
/// the record holds <c>object</c> values while a table holds text. The mapping
/// between them is where serialisation happens.
/// </remarks>
public sealed class StoredInstance
{
    public Guid Id { get; set; }

    public string DefinitionId { get; set; } = string.Empty;

    public int DefinitionVersion { get; set; }

    public InstanceStatus Status { get; set; }

    public int CurrentStepIndex { get; set; }

    public string? CurrentStepName { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }

    public string? FailedStepName { get; set; }

    public string? ErrorType { get; set; }

    public string? ErrorMessage { get; set; }

    /// <summary>
    /// Executions of the current step so far, including the one in progress.
    /// </summary>
    /// <remarks>
    /// Durable rather than in-memory (#106): a host recycling during an outage
    /// would otherwise reload zero and retry past the policy's ceiling, however
    /// often it restarted.
    /// </remarks>
    public int StepAttempts { get; set; }

    /// <summary>Workflow data, serialised per ADR-0014.</summary>
    public string DataJson { get; set; } = string.Empty;

    /// <summary>Instance input, serialised per ADR-0014. Null if none.</summary>
    public string? InputJson { get; set; }

    /// <summary>The node currently running this instance (#143).</summary>
    public Guid? RetriedFromInstanceId { get; set; }

    public string? OwnerNodeId { get; set; }

    /// <summary>When that node's claim lapses if not renewed.</summary>
    public DateTimeOffset? LeaseExpiresAt { get; set; }

    /// <summary>
    /// The instance's active nodes, as JSON (#163).
    /// </summary>
    /// <remarks>
    /// A JSON column rather than a child table: active nodes are read and
    /// written only as a whole set, never queried individually, so a table
    /// would add a join and a migration for no query it serves.
    ///
    /// <para>
    /// Serialised with plain <c>System.Text.Json</c>, not
    /// <c>WorkflowDataSerializer</c>. That serialiser carries a type allow-list
    /// because workflow data holds arbitrary author types resolved by name on
    /// read (ADR-0014). An active node is a closed, engine-owned shape with no
    /// polymorphism — there is no type name to resolve, so there is nothing for
    /// an allow-list to protect against.
    /// </para>
    /// </remarks>
    public string? ActiveNodesJson { get; set; }

    /// <summary>Optimistic concurrency token.</summary>
    public int Revision { get; set; }
}

/// <summary>One row of an instance's append-only execution history.</summary>
public sealed class StoredHistoryEntry
{
    public long Id { get; set; }

    public Guid InstanceId { get; set; }

    public int Sequence { get; set; }

    public string StepName { get; set; } = string.Empty;

    public DateTimeOffset StartedAt { get; set; }

    public DateTimeOffset CompletedAt { get; set; }

    public StepStatus Status { get; set; }

    /// <summary>Which attempt at the step this was, from 1 (#107).</summary>
    public int Attempt { get; set; } = 1;

    public string? ErrorType { get; set; }

    public string? ErrorMessage { get; set; }
}

/// <summary>
/// EF Core context for FlowDeck's persistence tables.
/// </summary>
/// <remarks>
/// Deliberately database-agnostic: it references only
/// <c>Microsoft.EntityFrameworkCore.Relational</c>, so the host chooses
/// PostgreSQL, SQL Server or SQLite. Taking a dependency on one provider here
/// would force it on every consumer (ADR-0010).
/// </remarks>
public class WorkflowDbContext(DbContextOptions<WorkflowDbContext> options) : DbContext(options)
{
    public DbSet<StoredInstance> Instances => this.Set<StoredInstance>();

    public DbSet<StoredHistoryEntry> History => this.Set<StoredHistoryEntry>();

    /// <summary>
    /// Stores every <see cref="DateTimeOffset"/> as UTC ticks.
    /// </summary>
    /// <remarks>
    /// Not a SQLite workaround bolted on after the fact, though SQLite is what
    /// exposed it - SQLite refuses to <c>ORDER BY</c> a <c>DateTimeOffset</c>
    /// at all. Storing a comparable integer is correct on every provider:
    /// ordering and range predicates behave identically, and the value cannot
    /// be written with a non-UTC offset that would make two rows incomparable.
    ///
    /// The requirement that timestamps are UTC (NFR-2) means no information is
    /// lost: the offset was always zero.
    /// </remarks>
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        ArgumentNullException.ThrowIfNull(configurationBuilder);

        configurationBuilder
            .Properties<DateTimeOffset>()
            .HaveConversion<UtcTicksConverter>();

        base.ConfigureConventions(configurationBuilder);
    }

    private sealed class UtcTicksConverter()
        : Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<DateTimeOffset, long>(
            value => value.UtcTicks,
            ticks => new DateTimeOffset(ticks, TimeSpan.Zero));

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<StoredInstance>(entity =>
        {
            entity.ToTable("flowdeck_instances");
            entity.HasKey(instance => instance.Id);

            entity.Property(instance => instance.DefinitionId).IsRequired().HasMaxLength(200);
            entity.Property(instance => instance.CurrentStepName).HasMaxLength(200);
            entity.Property(instance => instance.FailedStepName).HasMaxLength(200);
            entity.Property(instance => instance.ErrorType).HasMaxLength(300);
            entity.Property(instance => instance.DataJson).IsRequired();

            // Status is stored as an int. Enum names read better in a database
            // but rename with the code; the numeric values are the contract.
            entity.Property(instance => instance.Status).HasConversion<int>();

            // The concurrency token from ADR-0013. Declared to EF as well as
            // checked explicitly, so a race that slips past the read-then-write
            // is still caught by the database.
            entity.Property(instance => instance.Revision).IsConcurrencyToken();

            // #25 lists by status, newest first. Without this the dashboard
            // scans the table on every page.
            entity.HasIndex(instance => new { instance.Status, instance.CreatedAt });

            // #20 sweeps terminal instances by completion time.
            entity.HasIndex(instance => instance.CompletedAt);

            entity.Property(instance => instance.OwnerNodeId).HasMaxLength(200);

            // #147 polls for claimable work on an interval, on every node.
            // Without this the dispatcher scans the table each tick, and that
            // scan grows with history a cluster never stops accumulating.
            entity.HasIndex(instance => new { instance.Status, instance.LeaseExpiresAt });
        });

        modelBuilder.Entity<StoredHistoryEntry>(entity =>
        {
            entity.ToTable("flowdeck_history");
            entity.HasKey(history => history.Id);
            entity.Property(history => history.Id).ValueGeneratedOnAdd();

            entity.Property(history => history.StepName).IsRequired().HasMaxLength(200);
            entity.Property(history => history.ErrorType).HasMaxLength(300);
            entity.Property(history => history.Status).HasConversion<int>();

            // History is read per instance in sequence order, always.
            entity.HasIndex(history => new { history.InstanceId, history.Sequence }).IsUnique();
        });
    }
}
