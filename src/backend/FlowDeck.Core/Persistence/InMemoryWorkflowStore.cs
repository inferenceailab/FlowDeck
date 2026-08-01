namespace FlowDeck.Core.Persistence;

/// <summary>
/// An <see cref="IWorkflowStore"/> that keeps everything in process memory.
/// </summary>
/// <remarks>
/// Issue #16. Exists so the test suite runs fast with no external dependency,
/// and so the conformance suite has something to constrain before #17's EF Core
/// provider is written.
///
/// <para>
/// <b>Not durable.</b> Everything is lost when the process exits, and nothing is
/// bounded. This is a test double and a development convenience, not a
/// production store.
/// </para>
///
/// <para>
/// A single lock guards all mutations. Real providers get atomicity from a
/// database transaction; here the lock is what makes a state write and its
/// history append indivisible, which the contract requires. Contention is
/// irrelevant for the scale this is used at, and correctness is not.
/// </para>
/// </remarks>
public sealed class InMemoryWorkflowStore(WorkflowDataSerializer? serializer = null) : IWorkflowStore
{
    private readonly Lock gate = new();
    private readonly Dictionary<Guid, WorkflowInstanceRecord> instances = [];
    private readonly Dictionary<Guid, List<StepHistoryEntry>> history = [];

    public Task CreateAsync(WorkflowInstanceRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        cancellationToken.ThrowIfCancellationRequested();

        lock (this.gate)
        {
            if (this.instances.ContainsKey(record.Id))
            {
                throw new DuplicateInstanceException(record.Id);
            }

            this.instances[record.Id] = Copy(record with { Revision = 1 });
        }

        return Task.CompletedTask;
    }

    public Task<WorkflowInstanceRecord?> FindAsync(Guid instanceId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (this.gate)
        {
            return Task.FromResult(
                this.instances.TryGetValue(instanceId, out var record) ? Copy(record) : null);
        }
    }

    public Task<WorkflowInstanceRecord> SaveAsync(
        WorkflowInstanceRecord record,
        IReadOnlyList<StepHistoryEntry> history,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(history);
        cancellationToken.ThrowIfCancellationRequested();

        lock (this.gate)
        {
            if (!this.instances.TryGetValue(record.Id, out var stored))
            {
                throw new InstanceNotFoundException(record.Id);
            }

            if (stored.Revision != record.Revision)
            {
                // Rejected before anything is written, which is what makes the
                // atomicity guarantee hold: no history is appended either.
                throw new WorkflowStoreConcurrencyException(record.Id, record.Revision, stored.Revision);
            }

            var saved = Copy(record with { Revision = stored.Revision + 1 });
            this.instances[record.Id] = saved;

            if (history.Count > 0)
            {
                if (!this.history.TryGetValue(record.Id, out var log))
                {
                    log = [];
                    this.history[record.Id] = log;
                }

                // Sequence is assigned here so callers never have to track it,
                // and so it stays contiguous per instance.
                foreach (var entry in history)
                {
                    log.Add(entry with { Sequence = log.Count + 1 });
                }
            }

            return Task.FromResult(Copy(saved));
        }
    }

    public Task<IReadOnlyList<StepHistoryEntry>> GetHistoryAsync(
        Guid instanceId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (this.gate)
        {
            IReadOnlyList<StepHistoryEntry> result = this.history.TryGetValue(instanceId, out var log)
                ? [.. log.OrderBy(entry => entry.Sequence)]
                : [];

            return Task.FromResult(result);
        }
    }

    public Task<IReadOnlyList<WorkflowInstanceRecord>> ListAsync(
        InstanceFilter filter,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        cancellationToken.ThrowIfCancellationRequested();

        lock (this.gate)
        {
            var query = this.Matching(filter)
                .OrderByDescending(record => record.CreatedAt)
                .Skip(filter.Skip);

            if (filter.Take is { } take)
            {
                query = query.Take(take);
            }

            IReadOnlyList<WorkflowInstanceRecord> result = [.. query.Select(Copy)];
            return Task.FromResult(result);
        }
    }

    public Task<IReadOnlyList<DefinitionUsage>> CountActiveByDefinitionAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (this.gate)
        {
            IReadOnlyList<DefinitionUsage> usage =
            [
                .. this.instances.Values
                    .Where(record => IsActive(record.Status))
                    .GroupBy(record => (record.DefinitionId, record.DefinitionVersion))
                    .Select(group => new DefinitionUsage(group.Key.DefinitionId, group.Key.DefinitionVersion, group.Count()))
                    .OrderBy(entry => entry.DefinitionId, StringComparer.Ordinal)
                    .ThenBy(entry => entry.DefinitionVersion),
            ];

            return Task.FromResult(usage);
        }
    }

    public Task<int> CountAsync(InstanceFilter filter, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        cancellationToken.ThrowIfCancellationRequested();

        lock (this.gate)
        {
            // Deliberately ignores Skip and Take: a count that respected paging
            // would always equal the page size and tell a caller nothing.
            return Task.FromResult(this.Matching(filter).Count());
        }
    }

    public Task<int> PurgeAsync(DateTimeOffset completedBefore, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (this.gate)
        {
            var doomed = this.instances.Values
                .Where(record => record.Status
                    is InstanceStatus.Completed
                    or InstanceStatus.Failed
                    or InstanceStatus.Cancelled)

                // A terminal instance with no completion time is a data defect.
                // Guessing its age would delete something on the strength of a
                // bug, so it is left alone and stays visible.
                .Where(record => record.CompletedAt is { } completedAt && completedAt < completedBefore)
                .Select(record => record.Id)
                .ToArray();

            foreach (var id in doomed)
            {
                this.instances.Remove(id);

                // History outliving its instance would leak storage forever and
                // orphan rows nothing can join back to.
                this.history.Remove(id);
            }

            return Task.FromResult(doomed.Length);
        }
    }

    private IEnumerable<WorkflowInstanceRecord> Matching(InstanceFilter filter) =>
        this.instances.Values
            .Where(record => filter.Status is null || record.Status == filter.Status)
            .Where(record => filter.DefinitionId is null
                || string.Equals(record.DefinitionId, filter.DefinitionId, StringComparison.Ordinal))
            .Where(record => filter.DefinitionVersion is null
                || record.DefinitionVersion == filter.DefinitionVersion)
            .Where(record => !filter.ActiveOnly || IsActive(record.Status));

    /// <summary>
    /// Whether an instance in this status can still execute.
    /// </summary>
    /// <remarks>
    /// Listed rather than derived by excluding the terminal ones, so a status
    /// added later is <b>not</b> silently treated as active. The default for
    /// something nobody has classified should be "does not hold a definition
    /// version open", because the alternative is a version nobody can retire.
    /// </remarks>
    private static bool IsActive(InstanceStatus status) =>
        status is InstanceStatus.Running or InstanceStatus.Suspended;

    /// <summary>
    /// Deep-copies the mutable part of a record.
    /// </summary>
    /// <remarks>
    /// A record is immutable, but its <see cref="WorkflowInstanceRecord.Data"/>
    /// dictionary is a reference. Handing the stored instance out would let a
    /// caller mutate persisted state without saving, and would behave unlike a
    /// database-backed provider - which is exactly what the conformance suite
    /// exists to prevent.
    /// </remarks>
    private WorkflowInstanceRecord Copy(WorkflowInstanceRecord record)
    {
        // When a serialiser is supplied, data round-trips through it on every
        // store and load, so this double behaves like a text-backed provider.
        // A workflow holding an unserialisable value then fails in the fast
        // test suite instead of only in production against #17.
        if (serializer is not null)
        {
            return record with { Data = serializer.Deserialize(serializer.Serialize(record.Data)) };
        }

        return record with { Data = new Dictionary<string, object?>(record.Data, StringComparer.Ordinal) };
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<WorkflowInstanceRecord>> FindClaimableAsync(
        DateTimeOffset asOf,
        int limit,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (this.gate)
        {
            IReadOnlyList<WorkflowInstanceRecord> claimable = [.. this.instances.Values
                .Where(record => record.Status is InstanceStatus.Running or InstanceStatus.Suspended)
                .Where(record => record.OwnerNodeId is null || record.LeaseExpiresAt <= asOf)
                .OrderBy(record => record.CreatedAt)
                .Take(limit)
                .Select(Copy)];

            return Task.FromResult(claimable);
        }
    }
}
