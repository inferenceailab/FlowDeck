using System.Text.Json;
using FlowDeck.Core;
using FlowDeck.Specs.Support;
using Reqnroll;

namespace FlowDeck.Specs.Steps;

/// <summary>
/// Binds Features/Api/CancelAndRollBack.feature.
/// </summary>
[Binding]
[Scope(Feature = "Cancelling and rolling back")]
public sealed class CancelAndRollBackSteps(ApiContext api)
{
    private readonly List<string> log = [];

    private void DeclareCompensating(bool undoThrows)
    {
        // Two steps that completed and declare undos, then one that parks. The
        // instance is therefore suspended with real work behind it, which is
        // the case an operator is deciding about.
        api.Declare(new SpecWorkflow("orders", 1, builder => builder
            .AddStep("reserve", () => new SpecSteps.Recording(this.log, "reserve"))
                .WithCompensation(() => undoThrows
                    ? new SpecSteps.Throwing(this.log, "undo-reserve")
                    : new SpecSteps.Recording(this.log, "undo-reserve"))
            .AddStep("charge", () => new SpecSteps.Recording(this.log, "charge"))
                .WithCompensation(() => new SpecSteps.Recording(this.log, "undo-charge"))
            .AddStep("wait", () => new SpecSteps.Suspending(this.log, "wait"))));
    }

    [Given("a suspended instance whose earlier steps declare compensating actions")]
    public async Task GivenASuspendedInstanceWithUndos()
    {
        this.DeclareCompensating(undoThrows: false);

        api.InstanceId = (await api.Engine.StartAsync("orders", 1)).Id;
    }

    [Given("a suspended instance whose compensating action throws")]
    public async Task GivenAFailingUndo()
    {
        this.DeclareCompensating(undoThrows: true);

        api.InstanceId = (await api.Engine.StartAsync("orders", 1)).Id;
    }

    [Given("a suspended instance whose steps declare no compensating actions")]
    public async Task GivenNoUndos()
    {
        api.Declare(new SpecWorkflow("orders", 1, builder => builder
            .AddStep("reserve", () => new SpecSteps.Recording(this.log, "reserve"))
            .AddStep("wait", () => new SpecSteps.Suspending(this.log, "wait"))));

        api.InstanceId = (await api.Engine.StartAsync("orders", 1)).Id;
    }

    [Given("a completed instance")]
    public async Task GivenACompletedInstance()
    {
        api.Declare(new SpecWorkflow("orders", 1, builder =>
            builder.AddStep("work", () => new SpecSteps.Recording(this.log, "work"))));

        api.InstanceId = (await api.Engine.StartAsync("orders", 1)).Id;
    }

    [When("I POST to its cancel-and-roll-back endpoint")]
    public async Task WhenICancelAndRollBack() =>
        await api.SendAsync(client =>
            client.PostAsync($"/api/instances/{api.InstanceId}/cancel-and-roll-back", content: null));

    [When("I POST to its cancel endpoint")]
    public async Task WhenICancel() =>
        await api.SendAsync(client =>
            client.PostAsync($"/api/instances/{api.InstanceId}/cancel", content: null));

    [Then("the compensating actions ran, most recently completed first")]
    public void ThenUndosRanInReverse() =>

        // Reverse completion order, the same rule a failure follows
        // (ADR-0021). The step that parked declared no undo, so it is absent.
        Assert.Equal(
            ["undo-charge", "undo-reserve"],
            this.log.Where(entry => entry.StartsWith("undo-", StringComparison.Ordinal)));

    [Then("no compensating action ran")]
    public void ThenNothingWasUndone() =>

        // The behaviour ADR-0021 chose and #124 has carried since: an operator
        // cancelling to fix forward keeps the work they meant to keep.
        Assert.DoesNotContain(this.log, entry => entry.StartsWith("undo-", StringComparison.Ordinal));

    [Then("the instance reports (.*)")]
    public void ThenItReports(string status)
    {
        using var document = JsonDocument.Parse(api.Body);

        Assert.Equal(status, document.RootElement.GetProperty("status").GetString());
    }

    [Then("the response status is (.*)")]
    public void ThenTheStatusIs(int expected) =>
        Assert.Equal(expected, (int)api.Response!.StatusCode);
}
