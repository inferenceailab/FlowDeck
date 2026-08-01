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
}
