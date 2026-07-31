using System.Net;
using System.Net.Http.Json;
using FlowDeck.Core;

namespace FlowDeck.Api.Tests;

/// <summary>
/// Issue #122 - Show compensation over HTTP.
///
/// Scenario: The new statuses reach the API
/// Scenario: Compensating actions appear in the timeline
/// Scenario: The list can be filtered to the new statuses
/// </summary>
public class CompensationEndpointTests
{
    private sealed class Noop : IStep
    {
        public ValueTask<Outcome> ExecuteAsync(IStepContext context, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Outcome.Next);
    }

    private sealed class Throws(string message) : IStep
    {
        public ValueTask<Outcome> ExecuteAsync(IStepContext context, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException(message);
    }

    /// <summary>Charges, fails to ship, and refunds successfully.</summary>
    private sealed class RollsBack : IWorkflowDefinition
    {
        public string Id => "rolls-back";

        public int Version => 1;

        public void Build(IWorkflowBuilder builder) => builder
            .AddStep("charge", () => new Noop()).WithCompensation(() => new Noop())
            .AddStep("ship", () => new Throws("no carrier"));
    }

    /// <summary>Same, but the refund fails too.</summary>
    private sealed class RollbackFails : IWorkflowDefinition
    {
        public string Id => "rollback-fails";

        public int Version => 1;

        public void Build(IWorkflowBuilder builder) => builder
            .AddStep("charge", () => new Noop())
                .WithCompensation(() => new Throws("gateway unreachable"))
            .AddStep("ship", () => new Throws("no carrier"));
    }

    private static async Task<Guid> StartAsync(HttpClient client, string definitionId)
    {
        using var response = await client.PostAsync($"/api/workflows/{definitionId}/instances", null);
        response.EnsureSuccessStatusCode();

        return (await response.Content.ReadFromJsonAsync<StartInstanceResponse>())!.InstanceId;
    }

    [Fact]
    public async Task A_rolled_back_instance_serialises_as_Compensated()
    {
        // By name, not ordinal. A client reading 5 would have to guess.
        using var factory = new FlowDeckApiFactory().With(new RollsBack());
        using var client = factory.CreateClient();
        var id = await StartAsync(client, "rolls-back");

        using var response = await client.GetAsync($"/api/instances/{id}");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Contains("\"status\":\"Compensated\"", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_partial_rollback_serialises_as_CompensationFailed()
    {
        using var factory = new FlowDeckApiFactory().With(new RollbackFails());
        using var client = factory.CreateClient();
        var id = await StartAsync(client, "rollback-fails");

        using var response = await client.GetAsync($"/api/instances/{id}");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Contains("\"status\":\"CompensationFailed\"", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_original_failure_is_still_reported_after_a_rollback()
    {
        // The rollback is what the engine did about it; the failure is why. An
        // operator opening a compensated instance still needs the cause.
        using var factory = new FlowDeckApiFactory().With(new RollsBack());
        using var client = factory.CreateClient();
        var id = await StartAsync(client, "rolls-back");

        using var response = await client.GetAsync($"/api/instances/{id}");
        var instance = await response.Content.ReadFromJsonAsync<InstanceResponse>();

        Assert.Equal("ship", instance!.FailedStepName);
        Assert.Equal("no carrier", instance.ErrorMessage);
    }

    [Fact]
    public async Task Compensating_actions_appear_in_the_history()
    {
        using var factory = new FlowDeckApiFactory().With(new RollsBack());
        using var client = factory.CreateClient();
        var id = await StartAsync(client, "rolls-back");

        using var response = await client.GetAsync($"/api/instances/{id}/history");
        var history = await response.Content.ReadFromJsonAsync<StepHistoryResponse[]>();

        Assert.NotNull(history);
        Assert.Equal(["charge", "ship", "compensate:charge"], history.Select(entry => entry.StepName));

        // The rollback ran and succeeded, so an operator can see it happened
        // rather than assuming it from the status alone.
        Assert.Equal(StepStatus.Success, history[2].Status);
    }

    [Fact]
    public async Task A_failed_compensating_action_reports_its_own_error()
    {
        using var factory = new FlowDeckApiFactory().With(new RollbackFails());
        using var client = factory.CreateClient();
        var id = await StartAsync(client, "rollback-fails");

        using var response = await client.GetAsync($"/api/instances/{id}/history");
        var history = await response.Content.ReadFromJsonAsync<StepHistoryResponse[]>();

        var rollback = history!.Single(entry => entry.StepName == "compensate:charge");

        Assert.Equal(StepStatus.Failed, rollback.Status);
        Assert.Equal("gateway unreachable", rollback.ErrorMessage);
    }

    [Fact]
    public async Task The_list_can_be_filtered_to_Compensated()
    {
        using var factory = new FlowDeckApiFactory().With(new RollsBack()).With(new RollbackFails());
        using var client = factory.CreateClient();

        await StartAsync(client, "rolls-back");
        await StartAsync(client, "rollback-fails");

        using var response = await client.GetAsync("/api/instances?status=Compensated");
        var page = await response.Content.ReadFromJsonAsync<InstancePage>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Single(page!.Items);
        Assert.Equal("rolls-back", page.Items[0].DefinitionId);
    }

    [Fact]
    public async Task The_list_can_be_filtered_to_CompensationFailed()
    {
        using var factory = new FlowDeckApiFactory().With(new RollsBack()).With(new RollbackFails());
        using var client = factory.CreateClient();

        await StartAsync(client, "rolls-back");
        await StartAsync(client, "rollback-fails");

        using var response = await client.GetAsync("/api/instances?status=CompensationFailed");
        var page = await response.Content.ReadFromJsonAsync<InstancePage>();

        Assert.Single(page!.Items);
        Assert.Equal("rollback-fails", page.Items[0].DefinitionId);
    }

    [Fact]
    public async Task Cancelling_a_compensated_instance_is_refused()
    {
        // Terminal, like every other finished state (ADR-0008).
        using var factory = new FlowDeckApiFactory().With(new RollsBack());
        using var client = factory.CreateClient();
        var id = await StartAsync(client, "rolls-back");

        using var response = await client.PostAsync($"/api/instances/{id}/cancel", null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }
}
