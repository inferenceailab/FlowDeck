using System.Text.Json;
using FlowDeck.Core;
using FlowDeck.Specs.Support;
using Reqnroll;

namespace FlowDeck.Specs.Steps;

/// <summary>
/// Binds Features/Api/VersionUsage.feature.
/// </summary>
[Binding]
[Scope(Feature = "Reporting which definition versions are in use")]
public sealed class VersionUsageSteps(ApiContext api)
{
    [Given("\"(.*)\" v(.*) with a suspended instance and v(.*) with none")]
    public async Task GivenOneHeldOneFree(string id, int held, int free)
    {
        api.Declare(new SpecWorkflow(id, held, builder =>
            builder.AddStep("wait", () => new SpecSteps.Suspending([], "wait"))));

        api.Declare(new SpecWorkflow(id, free, builder =>
            builder.AddStep("work", () => new SpecSteps.Recording([], "work"))));

        await api.Engine.StartAsync(id, held);
    }

    [Given("\"(.*)\" v(.*) whose only instance completed")]
    public async Task GivenACompletedInstance(string id, int version)
    {
        api.Declare(new SpecWorkflow(id, version, builder =>
            builder.AddStep("work", () => new SpecSteps.Recording([], "work"))));

        await api.Engine.StartAsync(id, version);
    }

    [When("I GET the workflow definitions")]
    public async Task WhenIGetDefinitions() =>
        await api.SendAsync(client => client.GetAsync("/api/workflows"));

    [Then("v(.*) reports one live instance")]
    public void ThenThatVersionReportsOne(int version) => Assert.Equal(1, ActiveFor(version));

    [Then("v(.*) reports none")]
    public void ThenThatVersionReportsNone(int version) =>

        // Zero, not absent. The store omits versions nothing is running because
        // it cannot know what is registered; the registry turns that absence
        // into a zero, and a client should not have to.
        Assert.Equal(0, ActiveFor(version));

    [Then("the version reporting none can be retired")]
    public async Task ThenTheFreeVersionRetires()
    {
        var retired = await api.Engine.RetireAsync("orders", 2);

        Assert.Equal(0, retired);
    }

    [Then("the version reporting one cannot")]
    public async Task ThenTheHeldVersionDoesNot()
    {
        var error = await Assert.ThrowsAsync<DefinitionInUseException>(
            () => api.Engine.RetireAsync("orders", 1));

        // The screen and the engine agree, which is the point of the scenario.
        // A count that disagreed with what retirement allows would be worse
        // than showing nothing.
        Assert.Equal(ActiveFor(1), error.ActiveInstances);
    }

    private int ActiveFor(int version)
    {
        using var document = JsonDocument.Parse(api.Body);

        return document.RootElement
            .EnumerateArray()
            .Single(definition => definition.GetProperty("version").GetInt32() == version)
            .GetProperty("activeInstances")
            .GetInt32();
    }
}
