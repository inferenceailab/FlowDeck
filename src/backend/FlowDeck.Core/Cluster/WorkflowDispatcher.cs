using FlowDeck.Core.Persistence;

namespace FlowDeck.Core.Cluster;

/// <summary>
/// Picks up work no node is holding and runs it.
/// </summary>
/// <remarks>
/// One of these runs on every node, and they are all the same — no leader, no
/// election, no failover path (ADR-0023 decision 4).
///
/// <para>
/// This is <b>recovery, not load balancing</b>. An instance started through
/// <see cref="WorkflowEngine.StartAsync(string, int, CancellationToken)"/> runs
/// inline on the node that received the request and stays there. The dispatcher
/// exists for work whose node died, and for suspended instances waiting to be
/// continued.
/// </para>
///
/// <para>
/// Deliberately free of any hosting dependency: it exposes a poll and a loop,
/// and the host decides how to run them. That keeps
/// <c>Microsoft.Extensions.Hosting</c> out of the engine assembly and lets a
/// scenario drive a single poll rather than racing a background thread.
/// </para>
/// </remarks>
public sealed class WorkflowDispatcher(
    WorkflowEngine engine,
    IWorkflowStore store,
    ClusterOptions options,
    TimeProvider? timeProvider = null,
    EngineMetrics? metrics = null)
{
    private readonly TimeProvider timeProvider = timeProvider ?? TimeProvider.System;

    // Same default as the engine's: shared, so a host that wires neither still
    // publishes one meter rather than two of the same name.
    private readonly EngineMetrics metrics = metrics ?? EngineMetrics.Default;
    private readonly InstanceClaims claims = new(store, options, timeProvider);

    /// <summary>How many instances a single poll will take on.</summary>
    /// <remarks>
    /// Bounded because every claimed instance is a lease this node must keep
    /// renewing. Taking everything abandoned at once would mean holding leases
    /// it has no capacity to honour, and a peer would take the work back mid-run.
    /// </remarks>
    public int BatchSize { get; init; } = 10;

    /// <summary>Instances this node has run since it started.</summary>
    public int Dispatched { get; private set; }

    /// <summary>
    /// Claims and runs one batch of claimable work.
    /// </summary>
    /// <returns>How many instances this poll ran.</returns>
    /// <remarks>
    /// Never throws for a workflow that failed. A dispatcher that died on the
    /// first bad instance would leave its node silently idle while still
    /// looking healthy — the worst failure a cluster member can have.
    /// </remarks>
    public async Task<int> PollOnceAsync(CancellationToken cancellationToken = default)
    {
        var claimable = await store
            .FindClaimableAsync(this.timeProvider.GetUtcNow(), this.BatchSize, cancellationToken)
            .ConfigureAwait(false);

        var ran = 0;

        foreach (var candidate in claimable)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var claimed = await this.claims.TryClaimAsync(candidate.Id, cancellationToken).ConfigureAwait(false);

            if (claimed is null)
            {
                // A peer got there first. Ordinary, not an error.
                continue;
            }

            await this.RunClaimedAsync(claimed, cancellationToken).ConfigureAwait(false);
            ran++;
        }

        return ran;
    }

    /// <summary>Polls this node failed to complete, since it started.</summary>
    /// <remarks>
    /// Surfaced rather than only logged, so a health check or a test can tell
    /// "idle because there is no work" from "idle because every poll is
    /// failing". The two look identical from outside.
    /// </remarks>
    public int FailedPolls { get; private set; }

    /// <summary>
    /// Polls until cancelled.
    /// </summary>
    /// <remarks>
    /// Survives a failing poll. The store being briefly unreachable is an
    /// ordinary event — a failover, a restart, a network blip — and a loop that
    /// exited on the first one would leave the node permanently idle while
    /// still reporting itself alive. That is the same failure as a dispatcher
    /// dying on a bad instance, one level up.
    /// </remarks>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(options.PollInterval, this.timeProvider);

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false);
                await this.PollOnceAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // The host is stopping. Not a failure.
                return;
            }
#pragma warning disable CA1031 // Deliberately broad: see below.
            catch (Exception)
#pragma warning restore CA1031
            {
                // Any store failure, not just FlowDeckException. A provider is
                // free to throw its own type - a SqlException, a socket error -
                // and this loop cannot enumerate them. Narrowing the catch
                // would mean the one exception nobody anticipated stops every
                // node's recovery until someone restarts them.
                this.FailedPolls++;
            }
        }
    }

    /// <summary>
    /// Hands back every lease this node holds.
    /// </summary>
    /// <remarks>
    /// For a graceful stop — a rolling deploy, an operator restarting a node.
    /// Without it a peer waits out the full lease before touching work this
    /// node has already abandoned, so recovery is as slow as the lease is long
    /// for the one case where it did not need to be.
    ///
    /// <para>
    /// Takes its own <see cref="CancellationToken"/> rather than the stopping
    /// one: the host has already signalled that token, so passing it would
    /// cancel the very releases this exists to perform.
    /// </para>
    ///
    /// <para>
    /// Best-effort. A node killed outright never reaches this, and the lease
    /// remains the backstop — which is why lapsing must keep working whether or
    /// not draining does.
    /// </para>
    /// </remarks>
    /// <returns>How many leases were released.</returns>
    public async Task<int> DrainAsync(CancellationToken cancellationToken = default)
    {
        // Everything this node holds, whatever its lease says: the point is to
        // hand back work in progress, and asOf is what decides claimability,
        // not ownership.
        var held = await store
            .FindClaimableAsync(DateTimeOffset.MaxValue, int.MaxValue, cancellationToken)
            .ConfigureAwait(false);

        var released = 0;

        foreach (var record in held.Where(record => record.OwnerNodeId == options.NodeId))
        {
            if (await this.claims.TryReleaseAsync(record.Id, cancellationToken).ConfigureAwait(false))
            {
                released++;
            }
        }

        return released;
    }

    private async Task RunClaimedAsync(WorkflowInstanceRecord claimed, CancellationToken cancellationToken)
    {
        try
        {
            if (claimed.Status == InstanceStatus.Running)
            {
                // Left Running by a node that died. Nothing else moves an
                // instance out of Running, so this is the sweep the workflow
                // guide has been promising since M2.
                claimed = await store
                    .SaveAsync(claimed with { Status = InstanceStatus.Suspended }, [], cancellationToken)
                    .ConfigureAwait(false);
            }

            await engine.ResumeAsync(claimed.Id, cancellationToken).ConfigureAwait(false);

            this.Dispatched++;

            // Counted after the run, so a recovery that threw is not reported
            // as one that worked. An operator alerting on this wants "work was
            // rescued", not "work was attempted".
            this.metrics.InstanceRecovered(options.NodeId);
        }
        catch (FlowDeckException)
        {
            // Cancellation is deliberately not caught here: OperationCanceledException
            // does not derive from FlowDeckException, so a stopping host still
            // unwinds rather than being mistaken for a workflow that failed.
            //
            // The instance failed, was claimed by someone else mid-run, or its
            // definition is not registered on this node. All of these are
            // things one instance did, not reasons to stop polling.
        }
        finally
        {
            // Released whatever happened, so a peer can pick the instance up
            // immediately rather than waiting out a lease this node is no
            // longer using.
            await this.claims.TryReleaseAsync(claimed.Id, cancellationToken).ConfigureAwait(false);
        }
    }
}
