using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FlowDeck.Core;

namespace FlowDeck.Api.Tests;

/// <summary>
/// Issue #24 - Query a single instance over HTTP.
///
/// Scenario: Known instance returns its state
/// Scenario: Unknown instance returns 404
/// </summary>
public class GetInstanceEndpointTests
{
    private sealed class ThrowingStep : IStep
    {
        public ValueTask<Outcome> ExecuteAsync(IStepContext context, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("card declined");
    }

    private sealed class FailingWorkflow : IWorkflowDefinition
    {
        public string Id => "failing";

        public int Version => 1;

        public void Build(IWorkflowBuilder builder) => builder.AddStep("charge", () => new ThrowingStep());
    }

    [Fact]
    public async Task A_known_instance_returns_its_state()
    {
        // Given an existing instance
        using var factory = new FlowDeckApiFactory().With(new SuspendingWorkflow());
        using var client = factory.CreateClient();

        var started = await client.PostAsync("/api/workflows/suspending/instances", null);
        var id = (await started.Content.ReadFromJsonAsync<StartInstanceResponse>())!.InstanceId;
        started.Dispose();

        // When I GET the instance
        using var response = await client.GetAsync($"/api/instances/{id}");

        // Then the response status is 200 OK
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // And the body contains status, current step and timestamps
        var body = await response.Content.ReadFromJsonAsync<InstanceResponse>();

        Assert.NotNull(body);
        Assert.Equal(id, body!.Id);
        Assert.Equal(InstanceStatus.Suspended, body.Status);
        Assert.Equal("wait", body.CurrentStepName);
        Assert.NotEqual(default, body.CreatedAt);
        Assert.Null(body.CompletedAt);
    }

    [Fact]
    public async Task An_unknown_instance_returns_404()
    {
        using var factory = new FlowDeckApiFactory().With(new SimpleWorkflow());
        using var client = factory.CreateClient();

        using var response = await client.GetAsync($"/api/instances/{Guid.Empty}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task The_Location_header_from_starting_an_instance_actually_resolves()
    {
        // #23 promises a Location. A Location that 404s is worse than none, and
        // nothing had followed it.
        using var factory = new FlowDeckApiFactory().With(new SimpleWorkflow());
        using var client = factory.CreateClient();

        using var started = await client.PostAsync("/api/workflows/simple/instances", null);
        var location = started.Headers.Location!;

        using var followed = await client.GetAsync(location);

        Assert.Equal(HttpStatusCode.OK, followed.StatusCode);
    }

    [Fact]
    public async Task A_completed_instance_reports_its_completion_time_and_no_current_step()
    {
        using var factory = new FlowDeckApiFactory().With(new SimpleWorkflow());
        using var client = factory.CreateClient();

        using var started = await client.PostAsync("/api/workflows/simple/instances", null);
        var id = (await started.Content.ReadFromJsonAsync<StartInstanceResponse>())!.InstanceId;

        using var response = await client.GetAsync($"/api/instances/{id}");
        var body = await response.Content.ReadFromJsonAsync<InstanceResponse>();

        Assert.Equal(InstanceStatus.Completed, body!.Status);
        Assert.Null(body.CurrentStepName);
        Assert.NotNull(body.CompletedAt);
    }

    [Fact]
    public async Task A_failed_instance_reports_the_failing_step_and_error_text()
    {
        using var factory = new FlowDeckApiFactory().With(new FailingWorkflow());
        using var client = factory.CreateClient();

        using var started = await client.PostAsync("/api/workflows/failing/instances", null);
        var id = (await started.Content.ReadFromJsonAsync<StartInstanceResponse>())!.InstanceId;

        using var response = await client.GetAsync($"/api/instances/{id}");
        var body = await response.Content.ReadFromJsonAsync<InstanceResponse>();

        Assert.Equal(InstanceStatus.Failed, body!.Status);
        Assert.Equal("charge", body.FailedStepName);
        Assert.Equal("InvalidOperationException", body.ErrorType);
        Assert.Equal("card declined", body.ErrorMessage);
    }

    [Fact]
    public async Task No_stack_trace_or_internal_type_is_exposed()
    {
        // The engine holds a live Exception. Serialising WorkflowInstance
        // directly would put stack traces and internal namespaces on the wire.
        using var factory = new FlowDeckApiFactory().With(new FailingWorkflow());
        using var client = factory.CreateClient();

        using var started = await client.PostAsync("/api/workflows/failing/instances", null);
        var id = (await started.Content.ReadFromJsonAsync<StartInstanceResponse>())!.InstanceId;

        using var response = await client.GetAsync($"/api/instances/{id}");
        var json = await response.Content.ReadAsStringAsync();

        Assert.DoesNotContain("stackTrace", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("FlowDeck.Core", json, StringComparison.Ordinal);
        Assert.DoesNotContain("at FlowDeck", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_non_guid_id_does_not_reach_the_handler()
    {
        using var factory = new FlowDeckApiFactory().With(new SimpleWorkflow());
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/instances/not-a-guid");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Timestamps_are_serialised_as_UTC()
    {
        // Mixed offsets across nodes would make instances impossible to order
        // in a dashboard.
        using var factory = new FlowDeckApiFactory().With(new SimpleWorkflow());
        using var client = factory.CreateClient();

        using var started = await client.PostAsync("/api/workflows/simple/instances", null);
        var id = (await started.Content.ReadFromJsonAsync<StartInstanceResponse>())!.InstanceId;

        using var response = await client.GetAsync($"/api/instances/{id}");
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        var createdAt = json.RootElement.GetProperty("createdAt").GetString()!;

        Assert.True(
            createdAt.EndsWith("+00:00", StringComparison.Ordinal) || createdAt.EndsWith('Z'),
            $"expected a UTC offset, got {createdAt}");
    }
}
