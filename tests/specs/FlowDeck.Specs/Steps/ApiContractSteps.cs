using System.Text;
using System.Text.Json;
using FlowDeck.Core;
using FlowDeck.Core.Persistence;
using FlowDeck.Specs.Support;
using Reqnroll;

namespace FlowDeck.Specs.Steps;

/// <summary>
/// Binds Features/Api/ApiContract.feature.
/// </summary>
[Binding]
public sealed class ApiContractSteps(ApiContext api)
{
    [Given("the persistence store is reachable")]
    public void GivenTheStoreIsReachable()
    {
        // The default in-memory store already is. Stated so the scenario reads
        // as a pair with the unreachable case rather than looking like it
        // forgot an arrangement.
        api.Declare(new SpecWorkflow("health", 1, builder => builder.AddStep("work", () => new Noop())));
    }

    [Given("the persistence store is unreachable")]
    public void GivenTheStoreIsUnreachable()
    {
        api.Declare(new SpecWorkflow("health", 1, builder => builder.AddStep("work", () => new Noop())));
        api.UseStore(new UnreachableStore());
    }

    [When("I POST an instance start request with an invalid body")]
    public async Task WhenIPostAnInvalidBody()
    {
        // A definition that *declares* input. The endpoint only reads the body
        // when there is an input type to bind it to, so posting nonsense to a
        // no-input workflow is a 202 - correctly, since there is nothing to
        // parse. Using such a workflow here would have made the scenario assert
        // 400 against a request that was never going to produce one.
        api.Declare(new SpecWorkflow<OrderRequest>(
            "problem", 1, builder => builder.AddStep("work", () => new Noop())));

        await api.SendAsync(client => client.PostAsync(
            "/api/workflows/problem/instances",
            new StringContent("{ not json", Encoding.UTF8, "application/json")));
    }

    // A regex: a Cucumber Expression cannot parse two adjacent placeholders
    // separated by a slash, which is exactly the shape of a media type.
    [Then(@"^the content type is (\S+)$")]
    public void ThenTheContentTypeIs(string mediaType) =>
        Assert.Equal(mediaType, api.Response!.Content.Headers.ContentType?.MediaType);

    [Then("the body contains type, title, status and detail")]
    public void ThenTheBodyIsProblemDetails()
    {
        var body = JsonDocument.Parse(api.Body).RootElement;

        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("type").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("title").GetString()));
        Assert.Equal(400, body.GetProperty("status").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("detail").GetString()));
    }

    [Then("the document lists every public endpoint")]
    public void ThenTheDocumentListsEveryEndpoint()
    {
        var paths = JsonDocument.Parse(api.Body).RootElement.GetProperty("paths");

        // Named explicitly rather than counted. A count passes while the
        // document describes a different set of endpoints than it used to.
        string[] expected =
        [
            "/api/workflows",
            "/api/workflows/{definitionId}/instances",
            "/api/instances",
            "/api/instances/{instanceId}",
            "/api/instances/{instanceId}/cancel",
            "/api/instances/{instanceId}/history",
        ];

        var documented = paths.EnumerateObject().Select(path => path.Name).ToArray();

        Assert.All(expected, path => Assert.Contains(path, documented, StringComparer.Ordinal));
    }

    private sealed class Noop : IStep
    {
        public ValueTask<Outcome> ExecuteAsync(IStepContext context, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Outcome.Next);
    }

    /// <summary>A store that cannot be reached, for the readiness scenario.</summary>
    private sealed class UnreachableStore : IWorkflowStore
    {
        private static InvalidOperationException Down() => new("the database is unreachable");

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
            Guid instanceId,
            CancellationToken cancellationToken = default) =>
            throw Down();

        public Task<IReadOnlyList<WorkflowInstanceRecord>> ListAsync(
            InstanceFilter filter,
            CancellationToken cancellationToken = default) =>
            throw Down();

        public Task<int> CountAsync(InstanceFilter filter, CancellationToken cancellationToken = default) =>
            throw Down();

        public Task<int> PurgeAsync(DateTimeOffset completedBefore, CancellationToken cancellationToken = default) =>
            throw Down();
    }
}
