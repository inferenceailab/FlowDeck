using System.Net;
using System.Net.Http.Json;
using FlowDeck.Core;

namespace FlowDeck.Api.Tests;

/// <summary>
/// Issue #92 - Expose execution history over HTTP.
/// </summary>
/// <remarks>
/// Blocks #33: the step timeline has no data source without this.
/// </remarks>
public class InstanceHistoryEndpointTests
{
    private sealed class ThrowingStep : IStep
    {
        public ValueTask<Outcome> ExecuteAsync(IStepContext context, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("card declined");
    }

    private sealed class ThreeStepWorkflow(Func<IStep> middle) : IWorkflowDefinition
    {
        public string Id => "three-step";

        public int Version => 1;

        public void Build(IWorkflowBuilder builder)
        {
            builder.AddStep("validate", () => new NoopStep());
            builder.AddStep("charge", middle);
            builder.AddStep("ship", () => new NoopStep());
        }
    }

    private static async Task<Guid> StartAsync(HttpClient client, string definitionId)
    {
        using var response = await client.PostAsync($"/api/workflows/{definitionId}/instances", null);
        response.EnsureSuccessStatusCode();

        return (await response.Content.ReadFromJsonAsync<StartInstanceResponse>())!.InstanceId;
    }

    [Fact]
    public async Task History_is_returned_in_execution_order()
    {
        using var factory = new FlowDeckApiFactory().With(new ThreeStepWorkflow(() => new NoopStep()));
        using var client = factory.CreateClient();
        var id = await StartAsync(client, "three-step");

        using var response = await client.GetAsync($"/api/instances/{id}/history");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var history = await response.Content.ReadFromJsonAsync<StepHistoryResponse[]>();

        Assert.Equal(["validate", "charge", "ship"], history!.Select(entry => entry.StepName));
        Assert.Equal([1, 2, 3], history.Select(entry => entry.Sequence));
    }

    [Fact]
    public async Task Each_entry_carries_what_a_timeline_needs()
    {
        using var factory = new FlowDeckApiFactory().With(new ThreeStepWorkflow(() => new NoopStep()));
        using var client = factory.CreateClient();
        var id = await StartAsync(client, "three-step");

        using var response = await client.GetAsync($"/api/instances/{id}/history");
        var history = await response.Content.ReadFromJsonAsync<StepHistoryResponse[]>();

        Assert.All(history!, entry =>
        {
            Assert.False(string.IsNullOrWhiteSpace(entry.StepName));
            Assert.NotEqual(default, entry.StartedAt);
            Assert.True(entry.CompletedAt >= entry.StartedAt);
            Assert.True(entry.DurationMs >= 0);
            Assert.Equal(StepStatus.Success, entry.Status);
        });
    }

    [Fact]
    public async Task A_failed_step_reports_its_error()
    {
        // The detail view's job is saying where and why a run failed, not that
        // it did. Without this the timeline can only show a red marker.
        using var factory = new FlowDeckApiFactory().With(new ThreeStepWorkflow(() => new ThrowingStep()));
        using var client = factory.CreateClient();
        var id = await StartAsync(client, "three-step");

        using var response = await client.GetAsync($"/api/instances/{id}/history");
        var history = await response.Content.ReadFromJsonAsync<StepHistoryResponse[]>();

        Assert.Equal(["validate", "charge"], history!.Select(entry => entry.StepName));

        var failed = history[1];
        Assert.Equal(StepStatus.Failed, failed.Status);
        Assert.Equal("InvalidOperationException", failed.ErrorType);
        Assert.Equal("card declined", failed.ErrorMessage);
    }

    [Fact]
    public async Task An_unknown_instance_returns_an_empty_array_not_404()
    {
        // History removed by retention (#20) is not a client error. A 404 would
        // make a purged instance look like a mistake by the caller.
        using var factory = new FlowDeckApiFactory().With(new SimpleWorkflow());
        using var client = factory.CreateClient();

        using var response = await client.GetAsync($"/api/instances/{Guid.NewGuid()}/history");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty((await response.Content.ReadFromJsonAsync<StepHistoryResponse[]>())!);
    }

    [Fact]
    public async Task A_suspended_instance_reports_the_steps_that_ran_so_far()
    {
        using var factory = new FlowDeckApiFactory().With(new SuspendingWorkflow());
        using var client = factory.CreateClient();
        var id = await StartAsync(client, "suspending");

        using var response = await client.GetAsync($"/api/instances/{id}/history");
        var history = await response.Content.ReadFromJsonAsync<StepHistoryResponse[]>();

        Assert.Single(history!);
        Assert.Equal("wait", history![0].StepName);
    }

    [Fact]
    public async Task No_stack_trace_or_internal_type_is_exposed()
    {
        using var factory = new FlowDeckApiFactory().With(new ThreeStepWorkflow(() => new ThrowingStep()));
        using var client = factory.CreateClient();
        var id = await StartAsync(client, "three-step");

        using var response = await client.GetAsync($"/api/instances/{id}/history");
        var json = await response.Content.ReadAsStringAsync();

        Assert.DoesNotContain("stackTrace", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("FlowDeck.Core", json, StringComparison.Ordinal);
        Assert.DoesNotContain("at FlowDeck", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Duration_is_computed_server_side()
    {
        // So every client agrees on it rather than each subtracting timestamps
        // and rounding differently.
        using var factory = new FlowDeckApiFactory().With(new ThreeStepWorkflow(() => new NoopStep()));
        using var client = factory.CreateClient();
        var id = await StartAsync(client, "three-step");

        using var response = await client.GetAsync($"/api/instances/{id}/history");
        var history = await response.Content.ReadFromJsonAsync<StepHistoryResponse[]>();

        Assert.All(history!, entry =>
            Assert.Equal((entry.CompletedAt - entry.StartedAt).TotalMilliseconds, entry.DurationMs, 3));
    }
}
