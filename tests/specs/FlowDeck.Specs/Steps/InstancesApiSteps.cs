using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using FlowDeck.Core;
using FlowDeck.Specs.Support;
using Reqnroll;

namespace FlowDeck.Specs.Steps;

/// <summary>
/// Binds Features/Api/Instances.feature.
/// </summary>
[Binding]
[Scope(Tag = "M3")]
public sealed class InstancesApiSteps(ApiContext api)
{
    private static JsonElement Json(string body) => JsonDocument.Parse(body).RootElement;

    [Given("a registered definition {string}")]
    public void GivenARegisteredDefinition(string id) =>
        api.Declare(new SpecWorkflow(id, 1, builder => builder.AddStep("work", () => new Noop())));

    [Given("two registered definitions")]
    public void GivenTwoRegisteredDefinitions()
    {
        api.Declare(new SpecWorkflow("first", 1, builder => builder.AddStep("work", () => new Noop())));
        api.Declare(new SpecWorkflow("second", 2, builder => builder.AddStep("work", () => new Noop())));
    }

    [Given("an existing instance")]
    public async Task GivenAnExistingInstance()
    {
        api.Declare(new SpecWorkflow("existing", 1, builder => builder.AddStep("work", () => new Noop())));

        api.InstanceId = (await api.Engine.StartAsync("existing", 1)).Id;
    }

    [Given("a suspended instance exists")]
    public async Task GivenASuspendedInstanceExists()
    {
        api.Declare(new SpecWorkflow("suspending", 1, builder => builder.AddStep("wait", () => new Parks())));

        api.InstanceId = (await api.Engine.StartAsync("suspending", 1)).Id;
    }

    [Given("a completed instance exists")]
    public async Task GivenACompletedInstanceExists() => await this.GivenAnExistingInstance();

    [Given("{int} existing instances")]
    public async Task GivenExistingInstances(int count)
    {
        api.Declare(new SpecWorkflow("many", 1, builder => builder.AddStep("work", () => new Noop())));

        for (var i = 0; i < count; i++)
        {
            await api.Engine.StartAsync("many", 1);
        }
    }

    [Given("instances with mixed statuses")]
    public async Task GivenInstancesWithMixedStatuses()
    {
        api.Declare(new SpecWorkflow("ok", 1, builder => builder.AddStep("work", () => new Noop())));
        api.Declare(new SpecWorkflow("bad", 1, builder => builder.AddStep("work", () => new Explodes())));

        await api.Engine.StartAsync("ok", 1);
        await api.Engine.StartAsync("bad", 1);
        await api.Engine.StartAsync("ok", 1);
    }

    [When(@"^I POST /api/workflows/(\S+)/instances with a valid body$")]
    public async Task WhenIPostAValidStart(string definitionId) =>
        await api.SendAsync(client => client.PostAsync(
            $"/api/workflows/{definitionId}/instances",
            new StringContent("{}", Encoding.UTF8, "application/json")));

    [When(@"^I POST /api/workflows/(\S+)/instances$")]
    public async Task WhenIPostAStart(string definitionId) =>
        await api.SendAsync(client => client.PostAsync($"/api/workflows/{definitionId}/instances", null));

    [When("I GET the instance by id")]
    public async Task WhenIGetTheInstanceById() =>
        await api.SendAsync(client => client.GetAsync($"/api/instances/{api.InstanceId}"));

    [When(@"^I GET (/\S+)$")]
    public async Task WhenIGet(string path) => await api.SendAsync(client => client.GetAsync(path));

    [When("I POST the cancel endpoint for that instance")]
    public async Task WhenIPostCancel() =>
        await api.SendAsync(client => client.PostAsync($"/api/instances/{api.InstanceId}/cancel", null));

    [Then("the response status is {int}")]
    public void ThenTheResponseStatusIs(int expected) =>
        Assert.Equal((HttpStatusCode)expected, api.Response!.StatusCode);

    [Then("the body contains the new instance id")]
    public void ThenTheBodyContainsTheNewInstanceId()
    {
        var id = Json(api.Body).GetProperty("instanceId").GetGuid();

        Assert.NotEqual(Guid.Empty, id);
        api.InstanceId = id;
    }

    [Then("the Location header points at the instance resource")]
    public void ThenTheLocationHeaderPointsAtTheInstance()
    {
        var location = api.Response!.Headers.Location;

        Assert.NotNull(location);

        // Contains the id, so a client can follow it. A Location that pointed
        // at the collection would satisfy "a Location header is present" and be
        // useless to the caller who just started something.
        Assert.Contains(api.InstanceId.ToString(), location.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Then("the body contains status, current step and timestamps")]
    public void ThenTheBodyContainsInstanceState()
    {
        var body = Json(api.Body);

        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("status").GetString()));
        Assert.True(body.TryGetProperty("currentStepName", out _));
        Assert.NotEqual(default, body.GetProperty("createdAt").GetDateTimeOffset());
    }

    [Then("exactly {int} instances are returned")]
    public void ThenExactlyInstancesAreReturned(int expected) =>
        Assert.Equal(expected, Json(api.Body).GetProperty("items").GetArrayLength());

    [Then("the body reports a total count of {int}")]
    public void ThenTheBodyReportsATotalCountOf(int expected) =>
        Assert.Equal(expected, Json(api.Body).GetProperty("total").GetInt32());

    [Then("only failed instances are returned")]
    public void ThenOnlyFailedInstancesAreReturned()
    {
        var items = Json(api.Body).GetProperty("items").EnumerateArray().ToArray();

        // Non-empty as well as uniform: an empty page satisfies "only failed
        // instances" vacuously, and the Given created one.
        Assert.NotEmpty(items);
        Assert.All(items, item => Assert.Equal("Failed", item.GetProperty("status").GetString()));
    }

    [Then("the instance status becomes Cancelled")]
    public async Task ThenTheInstanceStatusBecomesCancelled()
    {
        // Read back over HTTP rather than through the engine: the scenario is
        // about what a client sees after cancelling.
        using var response = await api.Client.GetAsync($"/api/instances/{api.InstanceId}");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal("Cancelled", Json(body).GetProperty("status").GetString());
    }

    [Then("both definitions are returned with their ids and versions")]
    public void ThenBothDefinitionsAreReturned()
    {
        var items = Json(api.Body).EnumerateArray().ToArray();

        Assert.Equal(2, items.Length);

        Assert.Equal(
            ["first:1", "second:2"],
            items
                .Select(item =>
                    $"{item.GetProperty("id").GetString()}:{item.GetProperty("version").GetInt32().ToString(CultureInfo.InvariantCulture)}")
                .Order(StringComparer.Ordinal));
    }

    private sealed class Noop : IStep
    {
        public ValueTask<Outcome> ExecuteAsync(IStepContext context, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Outcome.Next);
    }

    private sealed class Parks : IStep
    {
        public ValueTask<Outcome> ExecuteAsync(IStepContext context, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Outcome.Suspend);
    }

    private sealed class Explodes : IStep
    {
        public ValueTask<Outcome> ExecuteAsync(IStepContext context, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("boom");
    }
}
