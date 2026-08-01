using FlowDeck.Core.Persistence;

namespace FlowDeck.Core.Cluster;

/// <summary>
/// Claims, renews and releases instance leases on behalf of one node.
/// </summary>
/// <remarks>
/// Built entirely on <see cref="IWorkflowStore.SaveAsync"/> and its
/// <c>Revision</c> guard rather than on a new store method. Two nodes that read
/// the same instance and both write are already resolved by the concurrency
/// token the conformance suite enforces on every provider — so atomic claiming
/// costs no new provider surface and inherits a guarantee that is already
/// tested.
///
/// <para>
/// Expiry is judged against this node's own clock (ADR-0023 decision 7). There
/// is no portable way to ask a provider-agnostic store for a server timestamp,
/// so nodes with badly skewed clocks will disagree about what has lapsed.
/// </para>
/// </remarks>
public sealed class InstanceClaims(
    IWorkflowStore store,
    ClusterOptions options,
    TimeProvider? timeProvider = null)
{
    private readonly TimeProvider timeProvider = timeProvider ?? TimeProvider.System;

    /// <summary>This node's identity, as written onto a claimed instance.</summary>
    public string NodeId => options.NodeId;

    /// <summary>
    /// Takes the lease on an instance, if nobody live is holding it.
    /// </summary>
    /// <returns>
    /// The claimed record, or <see langword="null"/> if the instance is
    /// terminal, unknown, or held by another node whose lease has not lapsed.
    /// </returns>
    public async Task<WorkflowInstanceRecord?> TryClaimAsync(
        Guid instanceId,
        CancellationToken cancellationToken = default)
    {
        var record = await store.FindAsync(instanceId, cancellationToken).ConfigureAwait(false);

        if (record is null || !this.IsClaimable(record))
        {
            return null;
        }

        try
        {
            return await store
                .SaveAsync(this.Claimed(record), [], cancellationToken)
                .ConfigureAwait(false);
        }
        catch (WorkflowStoreConcurrencyException)
        {
            // Another node wrote between the read above and this save. Losing
            // the race is an ordinary outcome, not an error: the winner is
            // running the instance and this node moves on to other work.
            return null;
        }
    }

    /// <summary>
    /// Extends this node's lease on an instance it already holds.
    /// </summary>
    /// <returns>
    /// The renewed record, or <see langword="null"/> if this node no longer
    /// holds the lease — which is how a node discovers it has been superseded.
    /// </returns>
    public async Task<WorkflowInstanceRecord?> TryRenewAsync(
        Guid instanceId,
        CancellationToken cancellationToken = default)
    {
        var record = await store.FindAsync(instanceId, cancellationToken).ConfigureAwait(false);

        if (record?.OwnerNodeId != options.NodeId)
        {
            return null;
        }

        try
        {
            return await store
                .SaveAsync(this.Claimed(record), [], cancellationToken)
                .ConfigureAwait(false);
        }
        catch (WorkflowStoreConcurrencyException)
        {
            return null;
        }
    }

    /// <summary>
    /// Gives up this node's lease, so a peer can take the instance at once.
    /// </summary>
    /// <remarks>
    /// Only ever clears a lease this node holds. Releasing another node's claim
    /// would hand its in-flight work to a third node while it was still
    /// running.
    /// </remarks>
    public async Task<bool> TryReleaseAsync(
        Guid instanceId,
        CancellationToken cancellationToken = default)
    {
        var record = await store.FindAsync(instanceId, cancellationToken).ConfigureAwait(false);

        if (record?.OwnerNodeId != options.NodeId)
        {
            return false;
        }

        try
        {
            await store
                .SaveAsync(record with { OwnerNodeId = null, LeaseExpiresAt = null }, [], cancellationToken)
                .ConfigureAwait(false);

            return true;
        }
        catch (WorkflowStoreConcurrencyException)
        {
            return false;
        }
    }

    /// <summary>
    /// Whether this node could take the instance right now.
    /// </summary>
    private bool IsClaimable(WorkflowInstanceRecord record)
    {
        if (record.Status is InstanceStatus.Completed or InstanceStatus.Failed
            or InstanceStatus.Cancelled or InstanceStatus.Compensated
            or InstanceStatus.CompensationFailed)
        {
            // Terminal states are final (ADR-0008). Claiming one would let a
            // node "recover" work that finished.
            return false;
        }

        if (record.OwnerNodeId is null)
        {
            return true;
        }

        // Re-claiming an instance this node already holds is a renewal, not a
        // conflict - a node that crashed mid-run and came back under the same
        // id should not be locked out of its own work.
        if (record.OwnerNodeId == options.NodeId)
        {
            return true;
        }

        // Someone else holds it. Only a lapsed lease makes it available.
        return record.LeaseExpiresAt is null
            || record.LeaseExpiresAt <= this.timeProvider.GetUtcNow();
    }

    private WorkflowInstanceRecord Claimed(WorkflowInstanceRecord record) => record with
    {
        OwnerNodeId = options.NodeId,
        LeaseExpiresAt = this.timeProvider.GetUtcNow() + options.LeaseDuration,
    };
}
