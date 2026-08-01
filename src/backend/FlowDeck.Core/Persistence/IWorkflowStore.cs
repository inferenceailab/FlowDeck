namespace FlowDeck.Core.Persistence;

/// <summary>
/// Durable storage for workflow instances and their execution history.
/// </summary>
/// <remarks>
/// Implements the model chosen in ADR-0013: the instance record is the
/// authoritative checkpoint, and history is an append-only log written
/// alongside it. Recovery reads the record; it never replays history.
///
/// <para>
/// <b>Atomicity is part of the contract.</b>
/// <see cref="SaveAsync"/> must persist the state change and its history
/// entries together or not at all. A provider that cannot transact across both
/// is not conformant: a crash between the two writes would leave an instance
/// whose state disagrees with its own history.
/// </para>
///
/// <para>
/// Implementations must pass <c>WorkflowStoreConformanceTests</c>. That suite
/// is the contract; this interface is only its signature.
/// </para>
/// </remarks>
public interface IWorkflowStore
{
    /// <summary>
    /// Stores a newly created instance.
    /// </summary>
    /// <exception cref="DuplicateInstanceException">The id is already stored.</exception>
    Task CreateAsync(WorkflowInstanceRecord record, CancellationToken cancellationToken = default);

    /// <summary>
    /// Loads an instance, or returns <see langword="null"/> if it is unknown.
    /// </summary>
    /// <remarks>
    /// Returns null rather than throwing: a caller polling for an instance that
    /// may have been purged (#20) is not in an exceptional situation.
    /// </remarks>
    Task<WorkflowInstanceRecord?> FindAsync(Guid instanceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes a checkpoint and appends history, atomically.
    /// </summary>
    /// <param name="record">
    /// The new state. Its <see cref="WorkflowInstanceRecord.Revision"/> must
    /// match the stored revision.
    /// </param>
    /// <param name="history">
    /// Entries to append. May be empty. <see cref="StepHistoryEntry.Sequence"/>
    /// is assigned by the store, so callers need not track it.
    /// </param>
    /// <returns>The saved record, with its revision incremented.</returns>
    /// <exception cref="WorkflowStoreConcurrencyException">
    /// The stored revision differs. Neither the state nor the history is
    /// modified.
    /// </exception>
    /// <exception cref="InstanceNotFoundException">The instance is unknown.</exception>
    Task<WorkflowInstanceRecord> SaveAsync(
        WorkflowInstanceRecord record,
        IReadOnlyList<StepHistoryEntry> history,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads an instance's history in execution order. Empty if unknown.
    /// </summary>
    Task<IReadOnlyList<StepHistoryEntry>> GetHistoryAsync(
        Guid instanceId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists instances, most recently created first.
    /// </summary>
    Task<IReadOnlyList<WorkflowInstanceRecord>> ListAsync(
        InstanceFilter filter,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Counts instances matching a filter, ignoring its paging.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="ListAsync"/> because #25 must report a total
    /// alongside a page, and counting by fetching everything would defeat the
    /// paging.
    /// </remarks>
    Task<int> CountAsync(InstanceFilter filter, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes terminal instances that reached their final state before
    /// <paramref name="completedBefore"/>, together with their history.
    /// </summary>
    /// <remarks>
    /// Only terminal instances are eligible. An in-flight instance is never
    /// removed no matter how old it is: age is not evidence that work is
    /// finished, and deleting a suspended instance would destroy work that is
    /// merely waiting.
    /// </remarks>
    /// <returns>How many instances were removed.</returns>
    Task<int> PurgeAsync(DateTimeOffset completedBefore, CancellationToken cancellationToken = default);

    /// <summary>
    /// Instances no node is holding, oldest first.
    /// </summary>
    /// <param name="asOf">
    /// The instant to judge lease expiry against — the caller's clock, not the
    /// store's. There is no portable server timestamp across the providers this
    /// contract covers, so the node asking decides what has lapsed (ADR-0023).
    /// </param>
    /// <param name="limit">
    /// Most to return. A poll takes a batch rather than the world: a node that
    /// pulled every abandoned instance at once would hold leases it has no
    /// capacity to renew.
    /// </param>
    /// <remarks>
    /// An instance is claimable when it is <b>not terminal</b> and either has no
    /// owner or holds a lease that lapsed at or before <paramref name="asOf"/>.
    ///
    /// <para>
    /// A separate method rather than a flag on <see cref="InstanceFilter"/>:
    /// this is the query every node runs on a timer, it wants its own index,
    /// and <see cref="InstanceFilter"/> exists to serve the dashboard.
    /// </para>
    ///
    /// <para>
    /// Oldest first, so work abandoned longest ago is recovered first rather
    /// than starved by a steady arrival of newer instances.
    /// </para>
    /// </remarks>
    Task<IReadOnlyList<WorkflowInstanceRecord>> FindClaimableAsync(
        DateTimeOffset asOf,
        int limit,
        CancellationToken cancellationToken = default);
}
