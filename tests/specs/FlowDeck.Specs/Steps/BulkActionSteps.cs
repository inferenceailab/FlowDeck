using System.Text.Json;
using FlowDeck.Core;
using FlowDeck.Specs.Support;
using Reqnroll;

namespace FlowDeck.Specs.Steps;

/// <summary>
/// Binds Features/Api/BulkActions.feature.
/// </summary>
[Binding]
[Scope(Feature = "Bulk operator actions")]
public sealed class BulkActionSteps(ApiContext api)
{
    private readonly List<string> log = [];
    private readonly List<Guid> suspended = [];
    private readonly List<Guid> completed = [];

    private JsonElement report;

    private void DeclareSuspending(string id) =>
        api.Declare(new SpecWorkflow(id, 1, builder =>
            builder.AddStep($"{id}-wait", () => new SpecSteps.Suspending(this.log, $"{id}-wait"))));

    [Given("five suspended instances of \"(.*)\"")]
    public async Task GivenFiveSuspended(string id)
    {
        this.DeclareSuspending(id);

        for (var i = 0; i < 5; i++)
        {
            this.suspended.Add((await api.Engine.StartAsync(id, 1)).Id);
        }
    }

    [Given("four suspended instances and one already completed")]
    public async Task GivenFourSuspendedAndOneCompleted()
    {
        this.DeclareSuspending("orders");

        // A second version of the same definition, so it matches the same
        // definitionId filter. The refusal has to come from the engine on an
        // instance the bulk action genuinely selected - one the filter excluded
        // would test nothing.
        api.Declare(new SpecWorkflow("orders", 2, builder =>
            builder.AddStep("orders-work", () => new SpecSteps.Recording(this.log, "orders-work"))));

        for (var i = 0; i < 4; i++)
        {
            this.suspended.Add((await api.Engine.StartAsync("orders", 1)).Id);
        }

        var finished = await api.Engine.StartAsync("orders", 2);

        this.completed.Add(finished.Id);
    }

    [Given("three failed instances of \"(.*)\"")]
    public async Task GivenThreeFailed(string id)
    {
        api.Declare(new SpecWorkflow(id, 1, builder =>
            builder.AddStep($"{id}-charge", () => new SpecSteps.Throwing(this.log, $"{id}-charge"))));

        for (var i = 0; i < 3; i++)
        {
            this.completed.Add((await api.Engine.StartAsync(id, 1)).Id);
        }
    }

    [Given("suspended instances of \"(.*)\" and of \"(.*)\"")]
    public async Task GivenTwoDefinitions(string first, string second)
    {
        this.DeclareSuspending(first);
        this.DeclareSuspending(second);

        this.suspended.Add((await api.Engine.StartAsync(first, 1)).Id);
        this.completed.Add((await api.Engine.StartAsync(second, 1)).Id);
    }

    [Given("more suspended instances than the page cap allows")]
    public async Task GivenMoreThanTheCap()
    {
        this.DeclareSuspending("orders");

        for (var i = 0; i < 205; i++)
        {
            this.suspended.Add((await api.Engine.StartAsync("orders", 1)).Id);
        }
    }

    [When("I bulk cancel instances of \"(.*)\"")]
    public async Task WhenIBulkCancel(string id) => await this.PostAsync("cancel", id);

    [When("I bulk retry instances of \"(.*)\"")]
    public async Task WhenIBulkRetry(string id) => await this.PostAsync("retry", id);

    private async Task PostAsync(string action, string definitionId)
    {
        await api.SendAsync(client =>
            client.PostAsync($"/api/instances/bulk/{action}?definitionId={definitionId}", content: null));

        this.report = JsonDocument.Parse(api.Body).RootElement.Clone();
    }

    private JsonElement[] Results() => [.. this.report.GetProperty("results").EnumerateArray()];

    [Then("all five are Cancelled")]
    public async Task ThenAllFiveCancelled() =>
        Assert.All(
            await Task.WhenAll(this.suspended.Select(id => api.Engine.GetInstanceAsync(id))),
            instance => Assert.Equal(InstanceStatus.Cancelled, instance.Status));

    [Then("the report says five succeeded and none failed")]
    public void ThenFiveSucceeded()
    {
        Assert.Equal(5, this.report.GetProperty("succeeded").GetInt32());
        Assert.Equal(0, this.report.GetProperty("failed").GetInt32());
        Assert.False(this.report.GetProperty("truncated").GetBoolean());
    }

    [Then("the four suspended ones are Cancelled")]
    public async Task ThenTheFourAreCancelled() =>
        Assert.All(
            await Task.WhenAll(this.suspended.Select(id => api.Engine.GetInstanceAsync(id))),
            instance => Assert.Equal(InstanceStatus.Cancelled, instance.Status));

    [Then("the report names the one that was refused, and why")]
    public void ThenTheRefusalIsNamed()
    {
        Assert.Equal(4, this.report.GetProperty("succeeded").GetInt32());
        Assert.Equal(1, this.report.GetProperty("failed").GetInt32());

        var refused = this.Results().Single(result => !result.GetProperty("succeeded").GetBoolean());

        // The id and the engine's own message. "One failed" alone would leave
        // an operator diffing the list by hand to find which.
        Assert.Equal(this.completed[0], refused.GetProperty("instanceId").GetGuid());
        Assert.Contains("cannot move", refused.GetProperty("error").GetString()!, StringComparison.Ordinal);
    }

    [Then("three new instances were started")]
    public void ThenThreeRetriesStarted() =>
        Assert.Equal(3, this.report.GetProperty("succeeded").GetInt32());

    [Then("each result links its new instance to the original")]
    public async Task ThenEachRetryLinksBack()
    {
        foreach (var result in this.Results())
        {
            var started = result.GetProperty("newInstanceId").GetGuid();
            var original = result.GetProperty("instanceId").GetGuid();

            // The link is what makes a bulk retry auditable at all: fifty new
            // ids with nothing tying them to what they replaced would be worse
            // than the failures they came from.
            Assert.Equal(original, (await api.Engine.GetInstanceAsync(started)).RetriedFromInstanceId);
        }
    }

    [Then("only the \"(.*)\" instances are Cancelled")]
    public async Task ThenOnlyThatDefinition(string id)
    {
        _ = id;

        Assert.Equal(
            InstanceStatus.Cancelled,
            (await api.Engine.GetInstanceAsync(this.suspended[0])).Status);

        // The other definition's instance is untouched. A filter the endpoint
        // ignored would cancel the lot and still report success.
        Assert.Equal(
            InstanceStatus.Suspended,
            (await api.Engine.GetInstanceAsync(this.completed[0])).Status);
    }

    [Then("no more than the cap were attempted")]
    public void ThenTheCapHeld() =>

        // An unbounded bulk action is a denial-of-service vector behind a
        // button.
        Assert.Equal(200, this.report.GetProperty("attempted").GetInt32());

    [Then("the report says the set was truncated")]
    public void ThenTruncationIsStated() =>

        // Stated, not inferred. An operator who thinks they cancelled
        // everything and did not is worse off than one who was told.
        Assert.True(this.report.GetProperty("truncated").GetBoolean());
}
