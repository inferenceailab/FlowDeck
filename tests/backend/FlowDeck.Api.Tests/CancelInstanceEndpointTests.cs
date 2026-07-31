using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FlowDeck.Core;

namespace FlowDeck.Api.Tests;

/// <summary>
/// Issue #26 - Cancel an instance over HTTP.
///
/// Scenario: A running instance is cancelled
/// Scenario: Cancelling a completed instance is a conflict
/// </summary>
public class CancelInstanceEndpointTests
{
    private static async Task<Guid> StartAsync(HttpClient client, string definitionId)
    {
        using var response = await client.PostAsync($"/api/workflows/{definitionId}/instances", null);
        response.EnsureSuccessStatusCode();

        return (await response.Content.ReadFromJsonAsync<StartInstanceResponse>())!.InstanceId;
    }

    [Fact]
    public async Task A_suspended_instance_is_cancelled()
    {
        // Given a suspended instance
        using var factory = new FlowDeckApiFactory().With(new SuspendingWorkflow());
        using var client = factory.CreateClient();
        var id = await StartAsync(client, "suspending");

        // When I POST to its cancel action
        using var response = await client.PostAsync($"/api/instances/{id}/cancel", null);

        // Then the response status is 202 Accepted
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        // And the instance status becomes Cancelled
        var body = await response.Content.ReadFromJsonAsync<InstanceResponse>();
        Assert.Equal(InstanceStatus.Cancelled, body!.Status);

        using var reread = await client.GetAsync($"/api/instances/{id}");
        var persisted = await reread.Content.ReadFromJsonAsync<InstanceResponse>();
        Assert.Equal(InstanceStatus.Cancelled, persisted!.Status);
    }

    [Fact]
    public async Task Cancelling_a_completed_instance_is_a_conflict()
    {
        // Given a completed instance
        using var factory = new FlowDeckApiFactory().With(new SimpleWorkflow());
        using var client = factory.CreateClient();
        var id = await StartAsync(client, "simple");

        // When I POST to its cancel action
        using var response = await client.PostAsync($"/api/instances/{id}/cancel", null);

        // Then the response status is 409 Conflict
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Cancelling_twice_is_a_conflict_the_second_time()
    {
        // Silently accepting would overwrite the first cancellation timestamp
        // and make the audit trail lie about when work stopped.
        using var factory = new FlowDeckApiFactory().With(new SuspendingWorkflow());
        using var client = factory.CreateClient();
        var id = await StartAsync(client, "suspending");

        using var first = await client.PostAsync($"/api/instances/{id}/cancel", null);
        using var second = await client.PostAsync($"/api/instances/{id}/cancel", null);

        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task Cancelling_an_unknown_instance_returns_404()
    {
        using var factory = new FlowDeckApiFactory().With(new SimpleWorkflow());
        using var client = factory.CreateClient();

        using var response = await client.PostAsync($"/api/instances/{Guid.NewGuid()}/cancel", null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task A_cancelled_instance_keeps_the_step_it_stopped_at()
    {
        // An operator asking "where did this stop?" needs the answer to survive
        // cancellation, and the API must not drop it in projection.
        using var factory = new FlowDeckApiFactory().With(new SuspendingWorkflow());
        using var client = factory.CreateClient();
        var id = await StartAsync(client, "suspending");

        using var response = await client.PostAsync($"/api/instances/{id}/cancel", null);
        var body = await response.Content.ReadFromJsonAsync<InstanceResponse>();

        Assert.Equal("wait", body!.CurrentStepName);
        Assert.NotNull(body.CompletedAt);
    }

    [Fact]
    public async Task The_conflict_body_names_both_states()
    {
        // So a client can tell why it was refused without parsing prose, and a
        // dashboard can say "already completed" rather than "request failed".
        using var factory = new FlowDeckApiFactory().With(new SimpleWorkflow());
        using var client = factory.CreateClient();
        var id = await StartAsync(client, "simple");

        using var response = await client.PostAsync($"/api/instances/{id}/cancel", null);
        var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        var detail = problem.RootElement.GetProperty("detail").GetString()!;

        Assert.Contains("Completed", detail, StringComparison.Ordinal);
        Assert.Contains("Cancelled", detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_cancelled_instance_still_appears_in_listings()
    {
        // Cancel is not delete. #20's purge is what removes; an operator must
        // still be able to find what they stopped.
        using var factory = new FlowDeckApiFactory().With(new SuspendingWorkflow());
        using var client = factory.CreateClient();
        var id = await StartAsync(client, "suspending");

        using var cancelled = await client.PostAsync($"/api/instances/{id}/cancel", null);
        cancelled.EnsureSuccessStatusCode();

        using var listed = await client.GetAsync("/api/instances?status=Cancelled");
        var page = await listed.Content.ReadFromJsonAsync<InstancePage>();

        Assert.Equal(1, page!.Total);
        Assert.Equal(id, page.Items[0].Id);
    }
}
