using System.Net;
using System.Text.Json;
using FlowDeck.Core;
using FlowDeck.Core.Persistence;
using FlowDeck.Specs.Support;
using Reqnroll;

namespace FlowDeck.Specs.Steps;

/// <summary>
/// Binds Features/Api/Retry.feature.
/// </summary>
[Binding]
[Scope(Feature = "Retrying a finished instance")]
public sealed class RetryApiSteps(ApiContext api)
{
    private readonly List<string> log = [];

    private IReadOnlyList<StepHistoryEntry> originalHistory = [];
    private Guid retried;

    private void DeclareFailing(string id, int version) =>
        api.Declare(new SpecWorkflow(id, version, builder => builder
            .AddStep($"v{version}-reserve", () => new SpecSteps.Recording(this.log, $"v{version}-reserve"))
            .AddStep($"v{version}-charge", () => new SpecSteps.Throwing(this.log, $"v{version}-charge"))));

    [Given("a failed instance of \"(.*)\" v(.*)")]
    public async Task GivenAFailedInstance(string id, int version)
    {
        this.DeclareFailing(id, version);

        api.InstanceId = (await api.Engine.StartAsync(id, version)).Id;
        this.originalHistory = await api.Engine.GetHistoryAsync(api.InstanceId);
        this.log.Clear();
    }

    [Given("a failed instance of \"(.*)\" v(.*) and a registered v(.*)")]
    public async Task GivenAFailedInstanceAndANewerVersion(string id, int version, int newer)
    {
        this.DeclareFailing(id, version);
        this.DeclareFailing(id, newer);

        api.InstanceId = (await api.Engine.StartAsync(id, version)).Id;
        this.log.Clear();
    }

    [Given("a failed instance started with input")]
    public async Task GivenAFailedInstanceWithInput()
    {
        api.Declare(new SpecWorkflow<OrderRequest>("orders", 1, builder => builder
            .AddStep("read", () => new SpecSteps.ReadingInput<OrderRequest>(
                order => this.log.Add($"read:{order?.Id}")))
            .AddStep("charge", () => new SpecSteps.Throwing(this.log, "charge"))));

        api.InstanceId = (await api.Engine.StartAsync("orders", 1, new OrderRequest(42))).Id;
        this.log.Clear();
    }

    [Given("a suspended instance")]
    public async Task GivenASuspendedInstance()
    {
        api.Declare(new SpecWorkflow("orders", 1, builder =>
            builder.AddStep("wait", () => new SpecSteps.Suspending(this.log, "wait"))));

        api.InstanceId = (await api.Engine.StartAsync("orders", 1)).Id;
    }

    [When("I POST to its retry endpoint")]
    public async Task WhenIRetry()
    {
        await api.SendAsync(client =>
            client.PostAsync($"/api/instances/{api.InstanceId}/retry", content: null));

        if (api.Response!.IsSuccessStatusCode)
        {
            using var document = JsonDocument.Parse(api.Body);
            this.retried = document.RootElement.GetProperty("id").GetGuid();
        }
    }

    [Then("the response status is (.*)")]
    public void ThenTheStatusIs(int expected) =>
        Assert.Equal(expected, (int)api.Response!.StatusCode);

    [Then("a different instance id comes back")]
    public void ThenANewIdComesBack()
    {
        Assert.Equal(HttpStatusCode.Accepted, api.Response!.StatusCode);

        // The cost of leaving terminal states final, and the response is where
        // it has to be visible. Returning the original would hide it.
        Assert.NotEqual(api.InstanceId, this.retried);
    }

    [Then("the new instance records which one it was retried from")]
    public async Task ThenItRecordsItsOrigin()
    {
        var instance = await api.Engine.GetInstanceAsync(this.retried);

        // What makes the id change bearable: an operator following an alert to
        // the failed instance can find what was done about it.
        Assert.Equal(api.InstanceId, instance.RetriedFromInstanceId);
    }

    [Then("the original is still Failed")]
    public async Task ThenTheOriginalIsUntouched()
    {
        var original = await api.Engine.GetInstanceAsync(api.InstanceId);

        Assert.Equal(InstanceStatus.Failed, original.Status);
        Assert.Null(original.RetriedFromInstanceId);
    }

    [Then("the original's history is unchanged")]
    public async Task ThenTheOriginalHistoryIsUnchanged()
    {
        var history = await api.Engine.GetHistoryAsync(api.InstanceId);

        // Not merely "still there": the same entries. A retry that appended to
        // the original's history would rewrite the record of what happened.
        Assert.Equal(
            this.originalHistory.Select(entry => entry.StepName),
            history.Select(entry => entry.StepName));
    }

    [Then("the new instance runs v(.*)")]
    public async Task ThenItRunsThatVersion(int version)
    {
        var instance = await api.Engine.GetInstanceAsync(this.retried);

        // From the original, not from the registry's latest. Silently
        // upgrading a retry to v2 would be a different workflow wearing the
        // same button.
        Assert.Equal(version, instance.DefinitionVersion);
        Assert.Contains($"v{version}-reserve", this.log);
    }

    [Then("every step ran again")]
    public void ThenEveryStepRanAgain() => Assert.Contains("charge", this.log);

    [Then("the new instance received the same input")]
    public void ThenTheInputCameThrough() =>

        // From the original's stored input. A retry that lost it would run a
        // different workflow and report success for it.
        Assert.Contains("read:42", this.log);
}
