using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FlowDeck.Core;

namespace FlowDeck.Api.Tests;

/// <summary>
/// Issue #23 - Start a workflow instance over HTTP.
///
/// Scenario: Starting a known definition returns 202
/// Scenario: Starting an unknown definition returns 404
/// </summary>
public class StartInstanceEndpointTests
{
    [Fact]
    public async Task Starting_a_known_definition_returns_202_with_id_and_location()
    {
        // Given a registered definition
        using var factory = new FlowDeckApiFactory().With(new SimpleWorkflow("order-fulfilment"));
        using var client = factory.CreateClient();

        // When I POST to its instances collection
        using var response = await client.PostAsync(
            "/api/workflows/order-fulfilment/instances",
            content: null);

        // Then the response status is 202 Accepted
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        // And the body contains the new instance id
        var body = await response.Content.ReadFromJsonAsync<StartInstanceResponse>();
        Assert.NotNull(body);
        Assert.NotEqual(Guid.Empty, body.InstanceId);

        // And the Location header points at the instance resource
        Assert.Equal($"/api/instances/{body.InstanceId}", response.Headers.Location?.ToString());
    }

    [Fact]
    public async Task Starting_an_unknown_definition_returns_404()
    {
        using var factory = new FlowDeckApiFactory().With(new SimpleWorkflow());
        using var client = factory.CreateClient();

        using var response = await client.PostAsync("/api/workflows/does-not-exist/instances", content: null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task A_failure_response_is_problem_details_not_a_stack_trace()
    {
        // A 404 that returns an HTML error page or a stack trace is unusable to
        // a client and leaks internals. #27 formalises the full contract; the
        // content type has to be right from the first endpoint.
        using var factory = new FlowDeckApiFactory().With(new SimpleWorkflow());
        using var client = factory.CreateClient();

        using var response = await client.PostAsync("/api/workflows/nope/instances", content: null);

        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(404, problem.RootElement.GetProperty("status").GetInt32());
        Assert.Contains("nope", problem.RootElement.GetProperty("detail").GetString()!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_returned_status_reflects_where_the_instance_actually_got_to()
    {
        // 202 does not mean "queued and untouched". A short workflow may already
        // be Completed; a parking one Suspended. Reporting Running regardless
        // would make the field useless.
        using var factory = new FlowDeckApiFactory()
            .With(new SimpleWorkflow())
            .With(new SuspendingWorkflow());
        using var client = factory.CreateClient();

        var completed = await client.PostAsync("/api/workflows/simple/instances", null);
        var suspended = await client.PostAsync("/api/workflows/suspending/instances", null);

        Assert.Equal(
            InstanceStatus.Completed,
            (await completed.Content.ReadFromJsonAsync<StartInstanceResponse>())!.Status);

        Assert.Equal(
            InstanceStatus.Suspended,
            (await suspended.Content.ReadFromJsonAsync<StartInstanceResponse>())!.Status);

        completed.Dispose();
        suspended.Dispose();
    }

    [Fact]
    public async Task Typed_input_is_read_from_the_request_body()
    {
        var seen = new List<int>();
        using var factory = new FlowDeckApiFactory().With(new TypedWorkflow(seen));
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync(
            "/api/workflows/typed/instances",
            new OrderRequest(7));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal([7], seen);
    }

    [Fact]
    public async Task A_typed_workflow_started_with_no_body_returns_400()
    {
        // The engine already refuses this (ADR-0006). The API must surface it
        // as the caller's mistake rather than a server error.
        using var factory = new FlowDeckApiFactory().With(new TypedWorkflow([]));
        using var client = factory.CreateClient();

        using var response = await client.PostAsync("/api/workflows/typed/instances", content: null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task An_explicit_version_selects_that_version()
    {
        using var factory = new FlowDeckApiFactory()
            .With(new SimpleWorkflow("versioned", 1))
            .With(new SimpleWorkflow("versioned", 2));
        using var client = factory.CreateClient();

        using var response = await client.PostAsync("/api/workflows/versioned/instances?version=1", null);
        var body = await response.Content.ReadFromJsonAsync<StartInstanceResponse>();

        var instance = await factory.Engine.GetInstanceAsync(body!.InstanceId);
        Assert.Equal(1, instance.DefinitionVersion);
    }

    [Fact]
    public async Task Omitting_the_version_selects_the_latest()
    {
        // A caller starting a workflow usually wants "the current one".
        // Requiring an explicit version would make every client redeploy on
        // each version bump.
        using var factory = new FlowDeckApiFactory()
            .With(new SimpleWorkflow("versioned", 1))
            .With(new SimpleWorkflow("versioned", 2));
        using var client = factory.CreateClient();

        using var response = await client.PostAsync("/api/workflows/versioned/instances", null);
        var body = await response.Content.ReadFromJsonAsync<StartInstanceResponse>();

        var instance = await factory.Engine.GetInstanceAsync(body!.InstanceId);
        Assert.Equal(2, instance.DefinitionVersion);
    }

    [Fact]
    public async Task An_unknown_version_of_a_known_definition_returns_404()
    {
        using var factory = new FlowDeckApiFactory().With(new SimpleWorkflow("versioned", 1));
        using var client = factory.CreateClient();

        using var response = await client.PostAsync("/api/workflows/versioned/instances?version=99", null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task A_malformed_body_for_a_typed_workflow_returns_400()
    {
        using var factory = new FlowDeckApiFactory().With(new TypedWorkflow([]));
        using var client = factory.CreateClient();

        using var content = new StringContent("{ this is not json", Encoding.UTF8, "application/json");
        using var response = await client.PostAsync("/api/workflows/typed/instances", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
