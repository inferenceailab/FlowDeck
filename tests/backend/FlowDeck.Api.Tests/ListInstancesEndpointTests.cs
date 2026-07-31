using System.Net;
using System.Net.Http.Json;
using FlowDeck.Core;

namespace FlowDeck.Api.Tests;

/// <summary>
/// Issue #25 - List instances with paging and filtering.
///
/// Scenario: Results are paged
/// Scenario: Results can be filtered by status
/// </summary>
public class ListInstancesEndpointTests
{
    private sealed class ThrowingStep : IStep
    {
        public ValueTask<Outcome> ExecuteAsync(IStepContext context, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("boom");
    }

    private sealed class FailingWorkflow : IWorkflowDefinition
    {
        public string Id => "failing";

        public int Version => 1;

        public void Build(IWorkflowBuilder builder) => builder.AddStep("only", () => new ThrowingStep());
    }

    private static async Task StartManyAsync(HttpClient client, string definitionId, int count)
    {
        for (var i = 0; i < count; i++)
        {
            using var response = await client.PostAsync($"/api/workflows/{definitionId}/instances", null);
            response.EnsureSuccessStatusCode();
        }
    }

    [Fact]
    public async Task Results_are_paged()
    {
        // Given 150 existing instances
        using var factory = new FlowDeckApiFactory().With(new SimpleWorkflow());
        using var client = factory.CreateClient();
        await StartManyAsync(client, "simple", 150);

        // When I GET a page of 50
        using var response = await client.GetAsync("/api/instances?page=1&pageSize=50");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var page = await response.Content.ReadFromJsonAsync<InstancePage>();

        // Then exactly 50 instances are returned
        Assert.Equal(50, page!.Items.Count);

        // And the body reports a total count of 150
        Assert.Equal(150, page.Total);
        Assert.Equal(1, page.Page);
        Assert.Equal(50, page.PageSize);
    }

    [Fact]
    public async Task Results_can_be_filtered_by_status()
    {
        // Given instances with mixed statuses
        using var factory = new FlowDeckApiFactory()
            .With(new SimpleWorkflow())
            .With(new FailingWorkflow());
        using var client = factory.CreateClient();

        await StartManyAsync(client, "simple", 3);
        await StartManyAsync(client, "failing", 2);

        // When I filter by Failed
        using var response = await client.GetAsync("/api/instances?status=Failed");

        var page = await response.Content.ReadFromJsonAsync<InstancePage>();

        // Then only failed instances are returned
        Assert.Equal(2, page!.Total);
        Assert.All(page.Items, item => Assert.Equal(InstanceStatus.Failed, item.Status));
    }

    [Fact]
    public async Task Paging_walks_the_whole_set_without_gaps_or_repeats()
    {
        // A paged list that drops or duplicates a row is worse than no paging:
        // the dashboard silently lies about what ran.
        using var factory = new FlowDeckApiFactory().With(new SimpleWorkflow());
        using var client = factory.CreateClient();
        await StartManyAsync(client, "simple", 25);

        var seen = new List<Guid>();

        for (var page = 1; page <= 3; page++)
        {
            using var response = await client.GetAsync($"/api/instances?page={page}&pageSize=10");
            var body = await response.Content.ReadFromJsonAsync<InstancePage>();
            seen.AddRange(body!.Items.Select(item => item.Id));
        }

        Assert.Equal(25, seen.Count);
        Assert.Equal(25, seen.Distinct().Count());
    }

    [Fact]
    public async Task The_total_ignores_paging_but_honours_the_filter()
    {
        using var factory = new FlowDeckApiFactory()
            .With(new SimpleWorkflow())
            .With(new FailingWorkflow());
        using var client = factory.CreateClient();

        await StartManyAsync(client, "simple", 10);
        await StartManyAsync(client, "failing", 4);

        using var response = await client.GetAsync("/api/instances?status=Failed&page=1&pageSize=2");
        var page = await response.Content.ReadFromJsonAsync<InstancePage>();

        Assert.Equal(2, page!.Items.Count);
        Assert.Equal(4, page.Total);
    }

    [Fact]
    public async Task An_oversized_page_request_is_clamped_not_rejected()
    {
        // An unbounded pageSize lets one request pull the whole table - a
        // denial-of-service vector long before it is a slow dashboard.
        using var factory = new FlowDeckApiFactory().With(new SimpleWorkflow());
        using var client = factory.CreateClient();
        await StartManyAsync(client, "simple", 5);

        using var response = await client.GetAsync("/api/instances?pageSize=100000");
        var page = await response.Content.ReadFromJsonAsync<InstancePage>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(InstanceEndpoints.MaxPageSize, page!.PageSize);
    }

    [Fact]
    public async Task A_nonsensical_page_number_is_clamped_to_the_first_page()
    {
        using var factory = new FlowDeckApiFactory().With(new SimpleWorkflow());
        using var client = factory.CreateClient();
        await StartManyAsync(client, "simple", 3);

        using var response = await client.GetAsync("/api/instances?page=0&pageSize=2");
        var page = await response.Content.ReadFromJsonAsync<InstancePage>();

        Assert.Equal(1, page!.Page);
        Assert.Equal(2, page.Items.Count);
    }

    [Fact]
    public async Task Instances_are_listed_newest_first()
    {
        using var factory = new FlowDeckApiFactory().With(new SimpleWorkflow());
        using var client = factory.CreateClient();
        await StartManyAsync(client, "simple", 5);

        using var response = await client.GetAsync("/api/instances");
        var page = await response.Content.ReadFromJsonAsync<InstancePage>();

        var timestamps = page!.Items.Select(item => item.CreatedAt).ToArray();

        Assert.Equal(timestamps.OrderByDescending(value => value), timestamps);
    }

    [Fact]
    public async Task Filtering_by_definition_id_narrows_the_list()
    {
        using var factory = new FlowDeckApiFactory()
            .With(new SimpleWorkflow("alpha"))
            .With(new SimpleWorkflow("beta"));
        using var client = factory.CreateClient();

        await StartManyAsync(client, "alpha", 3);
        await StartManyAsync(client, "beta", 1);

        using var response = await client.GetAsync("/api/instances?definitionId=alpha");
        var page = await response.Content.ReadFromJsonAsync<InstancePage>();

        Assert.Equal(3, page!.Total);
        Assert.All(page.Items, item => Assert.Equal("alpha", item.DefinitionId));
    }

    [Fact]
    public async Task An_empty_store_returns_an_empty_page_not_an_error()
    {
        using var factory = new FlowDeckApiFactory().With(new SimpleWorkflow());
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/instances");
        var page = await response.Content.ReadFromJsonAsync<InstancePage>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(page!.Items);
        Assert.Equal(0, page.Total);
    }

    [Fact]
    public async Task A_page_beyond_the_end_is_empty_but_still_reports_the_total()
    {
        // So a client that over-pages can tell it has gone too far rather than
        // concluding the data vanished.
        using var factory = new FlowDeckApiFactory().With(new SimpleWorkflow());
        using var client = factory.CreateClient();
        await StartManyAsync(client, "simple", 3);

        using var response = await client.GetAsync("/api/instances?page=99&pageSize=10");
        var page = await response.Content.ReadFromJsonAsync<InstancePage>();

        Assert.Empty(page!.Items);
        Assert.Equal(3, page.Total);
    }

    [Fact]
    public async Task An_unrecognised_status_value_returns_400()
    {
        using var factory = new FlowDeckApiFactory().With(new SimpleWorkflow());
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/instances?status=NotAStatus");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
