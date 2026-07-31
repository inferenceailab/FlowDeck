using FlowDeck.Core.Persistence;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace FlowDeck.Api;

/// <summary>
/// Reports whether the workflow store is reachable.
/// </summary>
/// <remarks>
/// Readiness, not liveness. A node whose store is unreachable is running
/// correctly but cannot do its job, so it should be taken out of rotation - not
/// restarted. Restarting it would achieve nothing except losing whatever it was
/// mid-way through.
///
/// <para>
/// The check is a cheap read that touches the store the way a real request
/// would. Counting is chosen over a connection ping because a ping can succeed
/// against a database whose schema is missing, which is exactly the state a
/// half-finished deployment leaves behind.
/// </para>
/// </remarks>
public sealed class WorkflowStoreHealthCheck(IWorkflowStore store) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await store.CountAsync(new InstanceFilter(), cancellationToken).ConfigureAwait(false);

            return HealthCheckResult.Healthy("Workflow store is reachable.");
        }
        catch (OperationCanceledException)
        {
            // Shutdown, not a store fault. Reporting unhealthy here would make
            // every graceful stop look like an outage in the monitoring.
            throw;
        }
        catch (Exception ex)
        {
            // Deliberately broad: any failure to reach the store means this node
            // cannot serve, whatever the provider chose to throw.
            return HealthCheckResult.Unhealthy("Workflow store is unreachable.", ex);
        }
    }
}
