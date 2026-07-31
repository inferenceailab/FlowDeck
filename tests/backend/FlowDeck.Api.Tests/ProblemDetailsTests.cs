using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace FlowDeck.Api.Tests;

/// <summary>
/// Issue #27 - Return RFC 9457 problem details for all errors.
///
/// Scenario: Validation failure returns problem details
/// </summary>
public class ProblemDetailsTests
{
    private static async Task<JsonElement> ProblemFor(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    [Fact]
    public async Task A_validation_failure_returns_problem_details()
    {
        // Given a typed workflow
        using var factory = new FlowDeckApiFactory().With(new TypedWorkflow([]));
        using var client = factory.CreateClient();

        // When I POST an instance start request with an invalid body
        using var content = new StringContent("{ not json", Encoding.UTF8, "application/json");
        using var response = await client.PostAsync("/api/workflows/typed/instances", content);

        // Then the response status is 400 Bad Request
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        // And the content type is application/problem+json
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        // And the body contains type, title, status and detail
        var problem = await ProblemFor(response);

        Assert.Equal(ProblemTypes.MalformedRequest, problem.GetProperty("type").GetString());
        Assert.False(string.IsNullOrWhiteSpace(problem.GetProperty("title").GetString()));
        Assert.Equal(400, problem.GetProperty("status").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(problem.GetProperty("detail").GetString()));
    }

    [Fact]
    public async Task Every_mapped_failure_carries_a_stable_type_uri()
    {
        // The field a client branches on. Status is too coarse - three problems
        // map to 409 - and nobody should be parsing prose out of detail.
        using var factory = new FlowDeckApiFactory()
            .With(new SimpleWorkflow())
            .With(new TypedWorkflow([]));
        using var client = factory.CreateClient();

        using var started = await client.PostAsync("/api/workflows/simple/instances", null);
        var id = (await started.Content.ReadFromJsonAsync<StartInstanceResponse>())!.InstanceId;

        var cases = new (string Description, Func<Task<HttpResponseMessage>> Act, string ExpectedType)[]
        {
            ("unknown definition",
                () => client.PostAsync("/api/workflows/nope/instances", null),
                ProblemTypes.DefinitionNotFound),

            ("unknown instance",
                () => client.GetAsync($"/api/instances/{Guid.NewGuid()}"),
                ProblemTypes.InstanceNotFound),

            ("cancel a completed instance",
                () => client.PostAsync($"/api/instances/{id}/cancel", null),
                ProblemTypes.InvalidStateTransition),

            ("typed workflow, no body",
                () => client.PostAsync("/api/workflows/typed/instances", null),
                ProblemTypes.InvalidInput),
        };

        foreach (var (description, act, expected) in cases)
        {
            using var response = await act();
            var problem = await ProblemFor(response);

            Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
            Assert.Equal(expected, problem.GetProperty("type").GetString());
        }
    }

    [Fact]
    public async Task Distinct_problems_that_share_a_status_have_distinct_types()
    {
        // The reason type exists. If both 409s carried the same type, a client
        // could not tell a retryable conflict from a permanent one.
        Assert.NotEqual(ProblemTypes.InvalidStateTransition, ProblemTypes.ConcurrentModification);
        Assert.NotEqual(ProblemTypes.InvalidStateTransition, ProblemTypes.DuplicateInstance);
        Assert.NotEqual(ProblemTypes.ConcurrentModification, ProblemTypes.DuplicateInstance);

        await Task.CompletedTask;
    }

    [Fact]
    public async Task A_problem_carries_a_traceId_for_correlation()
    {
        // Without it, "it returned a 500" is unactionable: nobody can find the
        // matching server-side log line.
        using var factory = new FlowDeckApiFactory().With(new SimpleWorkflow());
        using var client = factory.CreateClient();

        using var response = await client.GetAsync($"/api/instances/{Guid.NewGuid()}");
        var problem = await ProblemFor(response);

        Assert.True(problem.TryGetProperty("traceId", out var traceId));
        Assert.False(string.IsNullOrWhiteSpace(traceId.GetString()));
    }

    [Fact]
    public async Task A_problem_names_the_request_it_is_about()
    {
        using var factory = new FlowDeckApiFactory().With(new SimpleWorkflow());
        using var client = factory.CreateClient();

        var unknown = Guid.NewGuid();
        using var response = await client.GetAsync($"/api/instances/{unknown}");
        var problem = await ProblemFor(response);

        var instance = problem.GetProperty("instance").GetString()!;

        Assert.Contains("GET", instance, StringComparison.Ordinal);
        Assert.Contains(unknown.ToString(), instance, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_framework_produced_404_is_also_problem_details()
    {
        // A routing 404 bypasses the exception handler entirely. Without
        // AddProblemDetails it would return an empty body, so a client would
        // get JSON for some errors and nothing for others.
        using var factory = new FlowDeckApiFactory().With(new SimpleWorkflow());
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/no-such-endpoint");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        var problem = await ProblemFor(response);
        Assert.True(problem.TryGetProperty("traceId", out _));
    }

    [Fact]
    public async Task No_problem_body_exposes_a_stack_trace()
    {
        // Problem details include the exception when one is present. If that
        // ever surfaced in the response body it would publish internals to any
        // caller.
        using var factory = new FlowDeckApiFactory().With(new SimpleWorkflow());
        using var client = factory.CreateClient();

        using var response = await client.PostAsync("/api/workflows/nope/instances", null);
        var body = await response.Content.ReadAsStringAsync();

        Assert.DoesNotContain("stackTrace", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("at FlowDeck", body, StringComparison.Ordinal);
        Assert.DoesNotContain("StackTrace", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Type_uris_point_at_documentation_that_exists()
    {
        // RFC 9457 does not require type to resolve, but a type that points at
        // a page nobody wrote is worse than a bare identifier - it promises
        // help that is not there.
        var docs = Path.Combine(FindRepositoryRoot(), "docs", "api-errors.md");

        Assert.True(File.Exists(docs), $"expected error documentation at {docs}");

        var content = await File.ReadAllTextAsync(docs);

        foreach (var anchor in new[]
        {
            "definition-not-found", "instance-not-found", "invalid-state-transition",
            "invalid-input", "malformed-request", "concurrent-modification",
            "duplicate-instance", "invalid-definition",
        })
        {
            Assert.Contains($"### {anchor}", content, StringComparison.Ordinal);
        }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "FlowDeck.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
