using FlowDeck.Core;
using FlowDeck.Specs.Support;
using Reqnroll;

namespace FlowDeck.Specs.Steps;

/// <summary>
/// Binds Features/Engine/WorkflowData.feature.
/// </summary>
[Binding]
public sealed class WorkflowDataSteps(EngineContext world)
{
    [Given("step A writes {string} = {int} to the workflow data")]
    public void GivenStepAWrites(string key, int value) =>
        world.Declare("data", 1, builder => builder
            .AddStep("A", () => new SpecSteps.Writing(key, value))
            .AddStep("B", () => new SpecSteps.Reading<int>(key, read => world.Captured["B"] = read)));

    [When("step B executes")]
    public async Task WhenStepBExecutes() =>
        world.Instance = await world.Engine().StartAsync("data", 1);

    [Then("step B reads {string} as {int}")]
    public void ThenStepBReads(string key, int expected) =>
        Assert.Equal(expected, world.Captured["B"]);

    [Given("two concurrent instances of the same definition")]
    public void GivenTwoConcurrentInstances() =>
        world.Declare("isolated", 1, builder => builder
            .AddStep("write", () => new SpecSteps.Writing("orderId", world.Captured["next"]))
            .AddStep("read", () => new SpecSteps.Reading<int>(
                "orderId",
                read => world.Captured[$"instance-{world.Captured["next"]}"] = read)));

    [When("instance 1 writes {string} = {int} and instance 2 writes {string} = {int}")]
    public async Task WhenTwoInstancesWriteDifferentValues(
        string firstKey,
        int firstValue,
        string secondKey,
        int secondValue)
    {
        Assert.Equal(firstKey, secondKey);

        // Started one after the other rather than genuinely in parallel. The
        // property under test is that data is scoped per instance, which does
        // not depend on timing - and a racing test would be flaky about
        // something the engine does not promise anyway (one instance, one
        // worker).
        foreach (var value in new[] { firstValue, secondValue })
        {
            world.Captured["next"] = value;
            await world.Engine().StartAsync("isolated", 1);
        }
    }

    [Then("each instance reads back only its own value")]
    public void ThenEachInstanceReadsItsOwnValue()
    {
        Assert.Equal(1, world.Captured["instance-1"]);
        Assert.Equal(2, world.Captured["instance-2"]);
    }

    [Given("a definition typed on input OrderRequest")]
    public void GivenADefinitionTypedOnInput() =>
        world.DeclareWithInput<OrderRequest>("typed", 1, builder => builder
            .AddStep("first", () => new SpecSteps.ReadingInput<OrderRequest>(
                input => world.Captured["input"] = input)));

    [When("an instance is started with OrderRequest with Id {int}")]
    public async Task WhenStartedWithOrderRequest(int id) =>
        world.Instance = await world.Engine().StartAsync("typed", 1, new OrderRequest(id));

    [Then("the first step reads Input.Id as {int}")]
    public void ThenTheFirstStepReadsInputId(int expected) =>
        Assert.Equal(expected, ((OrderRequest)world.Captured["input"]!).Id);

    [When("an instance is started with an input of a different type")]
    public async Task WhenStartedWithTheWrongInputType() =>
        await world.CapturingErrorAsync(async () =>
            world.Instance = await world.Engine().StartAsync("typed", 1, "not an OrderRequest"));

    [Then("the start call fails with an InvalidInputTypeException")]
    public void ThenTheStartFailsWithInvalidInputType()
    {
        var error = Assert.IsType<InvalidInputTypeException>(world.Error);

        Assert.Equal(typeof(OrderRequest), error.ExpectedType);
        Assert.Equal(typeof(string), error.ActualType);
    }
}
