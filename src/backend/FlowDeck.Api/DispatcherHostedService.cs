using FlowDeck.Core.Cluster;

namespace FlowDeck.Api;

/// <summary>
/// Runs this node's dispatcher for as long as the host is up.
/// </summary>
/// <remarks>
/// Deliberately thin. <see cref="WorkflowDispatcher"/> owns the polling and the
/// error handling and has no hosting dependency of its own, so a scenario can
/// drive a single poll rather than racing a background thread — and the engine
/// assembly stays free of <c>Microsoft.Extensions.Hosting</c>.
///
/// <para>
/// This class exists only to bind that loop to the host's lifetime.
/// </para>
/// </remarks>
internal sealed class DispatcherHostedService(
    WorkflowDispatcher dispatcher,
    ILogger<DispatcherHostedService> logger,
    ClusterOptions options) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation(
            "FlowDeck node {NodeId} polling every {PollInterval} with a {LeaseDuration} lease.",
            options.NodeId,
            options.PollInterval,
            options.LeaseDuration);

        await dispatcher.RunAsync(stoppingToken).ConfigureAwait(false);

        logger.LogInformation("FlowDeck node {NodeId} stopped polling.", options.NodeId);
    }

    /// <summary>
    /// Hands leases back before the process goes.
    /// </summary>
    /// <remarks>
    /// Runs after <c>ExecuteAsync</c> has unwound, within the host's shutdown
    /// timeout. Without it a peer waits out the full lease before touching work
    /// this node has already stopped doing.
    ///
    /// <para>
    /// <paramref name="cancellationToken"/> is the host's shutdown token, and
    /// it is deliberately <b>not</b> forwarded: it is already signalled by the
    /// time this runs, and passing it would cancel the releases. If shutdown
    /// runs out of time, the lease lapsing is the backstop.
    /// </para>
    /// </remarks>
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        // The loop stops first, so nothing claims new work while leases are
        // being handed back.
        await base.StopAsync(cancellationToken).ConfigureAwait(false);

        var released = 0;
        Exception? failure = null;

        try
        {
            released = await dispatcher.DrainAsync(CancellationToken.None).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Deliberately broad: see below.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            // Any failure, not just FlowDeckException. A store that is already
            // gone is the common case at shutdown, and it throws whatever its
            // provider throws — a SqlException, a socket error. Draining is an
            // optimisation; failing at it must never fail a shutdown, because
            // the lease lapsing is the backstop either way.
            failure = ex;
        }

        this.Report(released, failure);
    }

    /// <summary>
    /// Logs the drain result, and never throws doing it.
    /// </summary>
    /// <remarks>
    /// By this point the host may already have disposed its logging
    /// infrastructure — which it does, and which turned a clean shutdown into
    /// an <c>ObjectDisposedException</c> the first time this method logged
    /// directly. A lost log line at shutdown is acceptable; a crash is not.
    /// </remarks>
    private void Report(int released, Exception? failure)
    {
        try
        {
            if (failure is null)
            {
                logger.LogInformation(
                    "FlowDeck node {NodeId} released {Released} lease(s) on shutdown.",
                    options.NodeId,
                    released);
            }
            else
            {
                logger.LogWarning(
                    failure,
                    "FlowDeck node {NodeId} could not release its leases; they will lapse instead.",
                    options.NodeId);
            }
        }
#pragma warning disable CA1031 // See the remarks above.
        catch (Exception)
#pragma warning restore CA1031
        {
            // Nowhere left to report it to.
        }
    }
}
