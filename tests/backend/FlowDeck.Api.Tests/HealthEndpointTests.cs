using System.Net;
using FlowDeck.Core.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace FlowDeck.Api.Tests;

/// <summary>
/// Issue #29 - Expose health and readiness endpoints.
///
/// Scenario: Healthy service reports ready
/// Scenario: Unreachable store reports not ready
/// </summary>
public class HealthEndpointTests
{
    /// <summary>
    /// A store that fails every call, standing in for an unreachable database.
    /// </summary>
    private sealed class UnreachableStore : IWorkflowStore
    {
        private static Exception Down() => new InvalidOperationException("connection refused");

        public Task CreateAsync(WorkflowInstanceRecord record, CancellationToken cancellationToken = default) =>
            throw Down();

        public Task<WorkflowInstanceRecord?> FindAsync(Guid instanceId, CancellationToken cancellationToken = default) =>
            throw Down();

        public Task<WorkflowInstanceRecord> SaveAsync(
            WorkflowInstanceRecord record,
            IReadOnlyList<StepHistoryEntry> history,
            CancellationToken cancellationToken = default) =>
            throw Down();

        public Task<IReadOnlyList<StepHistoryEntry>> GetHistoryAsync(
            Guid instanceId, CancellationToken cancellationToken = default) =>
            throw Down();

        public Task<IReadOnlyList<WorkflowInstanceRecord>> ListAsync(
            InstanceFilter filter, CancellationToken cancellationToken = default) =>
            throw Down();

        public Task<int> CountAsync(InstanceFilter filter, CancellationToken cancellationToken = default) =>
            throw Down();

        public Task<int> PurgeAsync(DateTimeOffset completedBefore, CancellationToken cancellationToken = default) =>
            throw Down();

        public Task<IReadOnlyList<WorkflowInstanceRecord>> FindClaimableAsync(
            DateTimeOffset asOf,
            int limit,
            CancellationToken cancellationToken = default) =>
            throw Down();
    }

    private static FlowDeckApiFactory BrokenStore() =>
        new FlowDeckApiFactory().WithStore(new UnreachableStore());

    [Fact]
    public async Task A_healthy_service_reports_ready()
    {
        // Given the persistence store is reachable
        using var factory = new FlowDeckApiFactory().With(new SimpleWorkflow());
        using var client = factory.CreateClient();

        // When I GET /health/ready
        using var response = await client.GetAsync("/health/ready");

        // Then the response status is 200 OK
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task An_unreachable_store_reports_not_ready()
    {
        // Given the persistence store is unreachable
        using var factory = BrokenStore();
        using var client = factory.CreateClient();

        // When I GET /health/ready
        using var response = await client.GetAsync("/health/ready");

        // Then the response status is 503 Service Unavailable
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task Liveness_stays_healthy_even_when_the_store_is_down()
    {
        // The distinction that matters. A node whose database is down is
        // running correctly and cannot serve - it should leave rotation, not be
        // restarted. Coupling liveness to the store would produce a restart
        // loop across every node during a database outage, which makes recovery
        // harder rather than easier.
        using var factory = BrokenStore();
        using var client = factory.CreateClient();

        using var live = await client.GetAsync("/health/live");
        using var ready = await client.GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
    }

    [Fact]
    public async Task Liveness_is_healthy_on_a_working_node()
    {
        using var factory = new FlowDeckApiFactory().With(new SimpleWorkflow());
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/health/live");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task The_readiness_response_does_not_leak_connection_details()
    {
        // A probe endpoint is usually unauthenticated. Its body must not
        // publish exception text that names hosts, credentials or paths.
        using var factory = BrokenStore();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/health/ready");
        var body = await response.Content.ReadAsStringAsync();

        Assert.DoesNotContain("connection refused", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("InvalidOperationException", body, StringComparison.Ordinal);
        Assert.DoesNotContain("at FlowDeck", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Health_endpoints_are_reachable_before_any_workflow_is_registered()
    {
        // A node that has not yet had definitions registered is still a node an
        // orchestrator needs to probe.
        using var factory = new FlowDeckApiFactory();
        using var client = factory.CreateClient();

        using var live = await client.GetAsync("/health/live");
        using var ready = await client.GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
    }
}