using System.Net;
using System.Text.Json;
using FlowDeck.Core;
using FlowDeck.Specs.Support;
using Reqnroll;

namespace FlowDeck.Specs.Steps;

/// <summary>
/// Binds Features/Api/Definitions.feature.
/// </summary>
/// <remarks>
/// Description only. Nothing here executes a branch — that is #164.
/// </remarks>
[Binding]
[Scope(Feature = "A definition's shape over HTTP")]
public sealed class DefinitionsApiSteps(ApiContext api)
{
    private const string DefinitionId = "order-fulfilment";

    private static IStep Noop() => new SpecSteps.Recording([], "noop");

    private JsonElement Body => JsonDocument.Parse(api.Body).RootElement;

    private JsonElement Steps => this.Body.GetProperty("steps");

    private JsonElement Branches => this.Steps[0].GetProperty("branches");

    private JsonElement BranchNamed(string name) =>
        this.Branches.EnumerateArray().Single(branch =>
            string.Equals(branch.GetProperty("name").GetString(), name, StringComparison.Ordinal));

    private static string[] NamesIn(JsonElement array) =>
        [.. array.EnumerateArray().Select(item => item.GetProperty("name").GetString()!)];

    // --------------------------------------------------------------- given

    [Given("a registered definition with three sequential steps")]
    public void GivenThreeSequentialSteps()
    {
        // Not in alphabetical order, so "declaration order" is distinguishable
        // from whatever order a dictionary or a sort would produce.
        api.Declare(new SpecWorkflow(DefinitionId, 1, builder => builder
            .AddStep("reserve", Noop)
            .AddStep("charge", Noop)
            .AddStep("ship", Noop)));
    }

    [Given("a definition whose step retries three times and declares a compensating action")]
    public void GivenARetryingCompensatedStep()
    {
        // "ship" declares neither, so the scenario asserts both are read off the
        // step rather than reported the same way for every step.
        api.Declare(new SpecWorkflow(DefinitionId, 1, builder => builder
            .AddStep("charge", Noop, RetryPolicy.ExponentialBackoff(3))
                .WithCompensation(Noop)
            .AddStep("ship", Noop)));
    }

    [Given("a definition whose step declares branches {string} and {string}")]
    public void GivenAStepWithTwoBranches(string first, string second) =>
        api.Declare(new SpecWorkflow(DefinitionId, 1, builder => builder
            .AddStep("check-stock", Noop)
                .Branch(first, b => b.AddStep($"{first}-charge", Noop))
                .Branch(second, b => b.AddStep($"{second}-notify", Noop))));

    [Given("a definition declaring a branch on a condition beside one the step selects")]
    public void GivenAPredicateBranch() =>
        api.Declare(new SpecWorkflow(DefinitionId, 1, builder => builder
            .AddStep("price", Noop)
                .Branch("automatic", b => b.AddStep("auto-approve", Noop))
                .BranchWhen(
                    "manual-approval",
                    data => data.TryGet<int>("total", out var total) && total > 1000,
                    b => b.AddStep("approve", Noop))));

    [Given("a definition forking into two branches")]
    public void GivenAFork() =>
        api.Declare(new SpecWorkflow(DefinitionId, 1, builder => builder
            .AddStep("prepare", Noop)
                .Fork(
                    b => b.AddStep("email", Noop),
                    b => b.AddStep("invoice", Noop))
            .AddStep("confirm", Noop)));

    // ---------------------------------------------------------------- when

    [When("I GET that definition over HTTP")]
    public async Task WhenIGetThatDefinition() =>
        await api.SendAsync(client => client.GetAsync($"/api/workflows/{DefinitionId}"));

    [When("I GET a definition id that is not registered")]
    public async Task WhenIGetAnUnknownDefinition() =>
        await api.SendAsync(client => client.GetAsync("/api/workflows/does-not-exist"));

    // ---------------------------------------------------------------- then

    [Then("the response status is {int}")]
    public void ThenTheResponseStatusIs(int expected) =>
        Assert.Equal((HttpStatusCode)expected, api.Response!.StatusCode);

    [Then("the body reports that the definition was not found")]
    public void ThenTheBodyReportsDefinitionNotFound()
    {
        // Asserted because a 404 alone proves nothing here: a route that does
        // not exist is also a 404, so the status on its own would pass against
        // an endpoint that had never been mapped. The problem type is what
        // says the endpoint ran and the definition is what was missing.
        Assert.Equal(
            "application/problem+json",
            api.Response!.Content.Headers.ContentType?.MediaType);

        Assert.Contains(
            "definition-not-found",
            this.Body.GetProperty("type").GetString(),
            StringComparison.Ordinal);
    }

    [Then("the steps are returned in declaration order")]
    public void ThenTheStepsAreInDeclarationOrder() =>
        Assert.Equal(["reserve", "charge", "ship"], NamesIn(this.Steps));

    [Then("that step reports three attempts and that it is compensated")]
    public void ThenTheStepReportsItsPolicy()
    {
        var charge = this.Steps[0];

        Assert.Equal(3, charge.GetProperty("maxAttempts").GetInt32());
        Assert.True(charge.GetProperty("hasCompensation").GetBoolean());

        var ship = this.Steps[1];

        // One, not zero: a step that never retries still executes once, and a
        // client rendering "N attempts" should never have to special-case it.
        Assert.Equal(1, ship.GetProperty("maxAttempts").GetInt32());
        Assert.False(ship.GetProperty("hasCompensation").GetBoolean());
    }

    [Then("both branches are returned with their steps")]
    public void ThenBothBranchesAreReturned()
    {
        Assert.Equal(["in-stock", "backorder"], NamesIn(this.Branches));

        // The branch bodies, not just the labels. A branch reported as a name
        // with no steps would render as an edge leading nowhere.
        Assert.Equal(["in-stock-charge"], NamesIn(this.BranchNamed("in-stock").GetProperty("steps")));
        Assert.Equal(["backorder-notify"], NamesIn(this.BranchNamed("backorder").GetProperty("steps")));
    }

    [Then("neither is parallel")]
    public void ThenNeitherIsParallel() =>
        Assert.All(
            this.Branches.EnumerateArray(),
            branch => Assert.False(branch.GetProperty("isParallel").GetBoolean()));

    [Then("that branch is marked conditional")]
    public void ThenTheBranchIsConditional() =>
        Assert.True(this.BranchNamed("manual-approval").GetProperty("isConditional").GetBoolean());

    [Then("the step-decided branch is not")]
    public void ThenTheStepDecidedBranchIsNot() =>
        Assert.False(this.BranchNamed("automatic").GetProperty("isConditional").GetBoolean());

    [Then("no condition is described")]
    public void ThenNoConditionIsDescribed()
    {
        // A condition is a compiled delegate. Reporting anything about what it
        // tests would be a claim the wire cannot support, and the visual view
        // would then render it as fact (ADR-0024, #171).
        var properties = this.BranchNamed("manual-approval")
            .EnumerateObject()
            .Select(property => property.Name)
            .Order(StringComparer.Ordinal);

        Assert.Equal(["isConditional", "isParallel", "name", "steps"], properties);
    }

    [Then("both branches are marked parallel")]
    public void ThenBothBranchesAreParallel()
    {
        var branches = this.Branches.EnumerateArray().ToArray();

        Assert.Equal(2, branches.Length);
        Assert.All(branches, branch => Assert.True(branch.GetProperty("isParallel").GetBoolean()));

        // The step declared after the fork is a sibling of the forking step,
        // not a third arm. The join is implicit, and a caller drawing the graph
        // has to be able to tell the difference.
        Assert.Equal(["prepare", "confirm"], NamesIn(this.Steps));
    }
}
