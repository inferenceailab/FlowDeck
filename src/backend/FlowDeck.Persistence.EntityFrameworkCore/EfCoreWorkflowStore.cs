using System.Text.Json;
using FlowDeck.Core;
using FlowDeck.Core.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FlowDeck.Persistence.EntityFrameworkCore;

/// <summary>
/// An <see cref="IWorkflowStore"/> backed by a relational database.
/// </summary>
/// <remarks>
/// Verified by the same conformance suite as the in-memory provider - the suite
/// is the contract, this is one implementation of it.
///
/// <para>
/// A new <see cref="WorkflowDbContext"/> is created per operation from the
/// supplied factory. Sharing one across calls would make the store
/// thread-unsafe and leak tracked entities between unrelated instances.
/// </para>
/// </remarks>
public sealed class EfCoreWorkflowStore(
    Func<WorkflowDbContext> contextFactory,
    WorkflowDataSerializer? serializer = null) : IWorkflowStore
{
    private readonly WorkflowDataSerializer serializer = serializer ?? new WorkflowDataSerializer();

    public async Task CreateAsync(WorkflowInstanceRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);

        await using var context = contextFactory();

        if (await context.Instances.AnyAsync(row => row.Id == record.Id, cancellationToken).ConfigureAwait(false))
        {
            throw new DuplicateInstanceException(record.Id);
        }

        context.Instances.Add(this.ToRow(record with { Revision = 1 }));

        try
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            // Two callers raced past the AnyAsync check and the primary key
            // caught it. Confirm that is what happened rather than swallowing
            // an unrelated write failure as a duplicate.
            if (await this.ExistsAsync(record.Id, cancellationToken).ConfigureAwait(false))
            {
                throw new DuplicateInstanceException(record.Id);
            }

            throw;
        }
    }

    public async Task<WorkflowInstanceRecord?> FindAsync(
        Guid instanceId,
        CancellationToken cancellationToken = default)
    {
        await using var context = contextFactory();

        var row = await context.Instances
            .AsNoTracking()
            .FirstOrDefaultAsync(instance => instance.Id == instanceId, cancellationToken)
            .ConfigureAwait(false);

        return row is null ? null : this.ToRecord(row);
    }

    public async Task<WorkflowInstanceRecord> SaveAsync(
        WorkflowInstanceRecord record,
        IReadOnlyList<StepHistoryEntry> history,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(history);

        await using var context = contextFactory();

        var row = await context.Instances
            .FirstOrDefaultAsync(instance => instance.Id == record.Id, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InstanceNotFoundException(record.Id);

        if (row.Revision != record.Revision)
        {
            // Rejected before anything is written, so no history is appended
            // either - the atomicity clause of the contract.
            throw new WorkflowStoreConcurrencyException(record.Id, record.Revision, row.Revision);
        }

        var next = record with { Revision = row.Revision + 1 };
        this.Apply(next, row);

        if (history.Count > 0)
        {
            var lastSequence = await context.History
                .Where(entry => entry.InstanceId == record.Id)
                .MaxAsync(entry => (int?)entry.Sequence, cancellationToken)
                .ConfigureAwait(false) ?? 0;

            foreach (var entry in history)
            {
                context.History.Add(new StoredHistoryEntry
                {
                    InstanceId = record.Id,
                    Sequence = ++lastSequence,
                    StepName = entry.StepName,
                    StartedAt = entry.StartedAt,
                    CompletedAt = entry.CompletedAt,
                    Status = entry.Status,
                    Attempt = entry.Attempt,
                    ErrorType = entry.ErrorType,
                    ErrorMessage = entry.ErrorMessage,
                });
            }
        }

        try
        {
            // One SaveChanges covers the state update and the history inserts,
            // so ADR-0013's atomicity requirement holds without an explicit
            // transaction: EF wraps a single SaveChanges in one.
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Another writer moved between the read above and this write. The
            // explicit check catches the common case; this catches the race.
            var current = await this.CurrentRevisionAsync(record.Id, cancellationToken).ConfigureAwait(false);
            throw new WorkflowStoreConcurrencyException(record.Id, record.Revision, current);
        }

        return next;
    }

    public async Task<IReadOnlyList<StepHistoryEntry>> GetHistoryAsync(
        Guid instanceId,
        CancellationToken cancellationToken = default)
    {
        await using var context = contextFactory();

        var rows = await context.History
            .AsNoTracking()
            .Where(entry => entry.InstanceId == instanceId)
            .OrderBy(entry => entry.Sequence)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return [.. rows.Select(row => new StepHistoryEntry
        {
            InstanceId = row.InstanceId,
            Sequence = row.Sequence,
            StepName = row.StepName,
            StartedAt = row.StartedAt,
            CompletedAt = row.CompletedAt,
            Status = row.Status,
            Attempt = row.Attempt,
            ErrorType = row.ErrorType,
            ErrorMessage = row.ErrorMessage,
        })];
    }

    public async Task<IReadOnlyList<WorkflowInstanceRecord>> ListAsync(
        InstanceFilter filter,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);

        await using var context = contextFactory();

        var query = Filtered(context.Instances.AsNoTracking(), filter)
            .OrderByDescending(instance => instance.CreatedAt)
            .Skip(filter.Skip);

        if (filter.Take is { } take)
        {
            query = query.Take(take);
        }

        var rows = await query.ToListAsync(cancellationToken).ConfigureAwait(false);

        return [.. rows.Select(this.ToRecord)];
    }

    public async Task<int> CountAsync(InstanceFilter filter, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);

        await using var context = contextFactory();

        // Ignores Skip and Take deliberately: a count that respected paging
        // would always equal the page size and tell a caller nothing.
        return await Filtered(context.Instances.AsNoTracking(), filter)
            .CountAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<int> PurgeAsync(
        DateTimeOffset completedBefore,
        CancellationToken cancellationToken = default)
    {
        await using var context = contextFactory();

        var doomed = await context.Instances
            .Where(instance => instance.Status == InstanceStatus.Completed
                || instance.Status == InstanceStatus.Failed
                || instance.Status == InstanceStatus.Cancelled)
            .Where(instance => instance.CompletedAt != null && instance.CompletedAt < completedBefore)
            .Select(instance => instance.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (doomed.Count == 0)
        {
            return 0;
        }

        // History goes with the instance: rows outliving their instance would
        // leak storage and orphan rows nothing can join back to.
        await context.History
            .Where(entry => doomed.Contains(entry.InstanceId))
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);

        return await context.Instances
            .Where(instance => doomed.Contains(instance.Id))
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<WorkflowInstanceRecord>> FindClaimableAsync(
        DateTimeOffset asOf,
        int limit,
        CancellationToken cancellationToken = default)
    {
        await using var context = contextFactory();

        var rows = await context.Instances
            .AsNoTracking()
            .Where(instance => instance.Status == InstanceStatus.Running
                || instance.Status == InstanceStatus.Suspended)
            .Where(instance => instance.OwnerNodeId == null || instance.LeaseExpiresAt <= asOf)

            // Oldest first, so work abandoned longest ago is recovered first
            // rather than starved by a steady arrival of newer instances.
            .OrderBy(instance => instance.CreatedAt)
            .Take(limit)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return [.. rows.Select(this.ToRecord)];
    }

    private static IQueryable<StoredInstance> Filtered(IQueryable<StoredInstance> query, InstanceFilter filter)
    {
        if (filter.Status is { } status)
        {
            query = query.Where(instance => instance.Status == status);
        }

        if (filter.DefinitionId is { } definitionId)
        {
            query = query.Where(instance => instance.DefinitionId == definitionId);
        }

        return query;
    }

    private async Task<bool> ExistsAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var context = contextFactory();
        return await context.Instances.AnyAsync(instance => instance.Id == id, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<int> CurrentRevisionAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var context = contextFactory();

        return await context.Instances
            .AsNoTracking()
            .Where(instance => instance.Id == id)
            .Select(instance => instance.Revision)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private StoredInstance ToRow(WorkflowInstanceRecord record)
    {
        var row = new StoredInstance();
        this.Apply(record, row);
        row.Id = record.Id;
        return row;
    }

    private void Apply(WorkflowInstanceRecord record, StoredInstance row)
    {
        row.DefinitionId = record.DefinitionId;
        row.DefinitionVersion = record.DefinitionVersion;
        row.Status = record.Status;
        row.CurrentStepIndex = record.CurrentStepIndex;
        row.CurrentStepName = record.CurrentStepName;
        row.CreatedAt = record.CreatedAt;
        row.CompletedAt = record.CompletedAt;
        row.FailedStepName = record.FailedStepName;
        row.ErrorType = record.ErrorType;
        row.ErrorMessage = record.ErrorMessage;
        row.StepAttempts = record.StepAttempts;
        row.OwnerNodeId = record.OwnerNodeId;
        row.LeaseExpiresAt = record.LeaseExpiresAt;
        row.Revision = record.Revision;
        row.DataJson = this.serializer.Serialize(record.Data);

        // Input rides through the same allow-list as workflow data, so an
        // unregistered input type fails at the same boundary and with the same
        // error rather than being a separate surprise.
        row.InputJson = record.Input is null
            ? null
            : this.serializer.Serialize(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["input"] = record.Input,
            });
    }

    private WorkflowInstanceRecord ToRecord(StoredInstance row) => new()
    {
        Id = row.Id,
        DefinitionId = row.DefinitionId,
        DefinitionVersion = row.DefinitionVersion,
        Status = row.Status,
        CurrentStepIndex = row.CurrentStepIndex,
        CurrentStepName = row.CurrentStepName,
        CreatedAt = row.CreatedAt,
        CompletedAt = row.CompletedAt,
        FailedStepName = row.FailedStepName,
        ErrorType = row.ErrorType,
        ErrorMessage = row.ErrorMessage,
        StepAttempts = row.StepAttempts,
        OwnerNodeId = row.OwnerNodeId,
        LeaseExpiresAt = row.LeaseExpiresAt,
        Data = this.serializer.Deserialize(row.DataJson),
        Input = row.InputJson is null
            ? null
            : this.serializer.Deserialize(row.InputJson).GetValueOrDefault("input"),
        Revision = row.Revision,
    };
}
