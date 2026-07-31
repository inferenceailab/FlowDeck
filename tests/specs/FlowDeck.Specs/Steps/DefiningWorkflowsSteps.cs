using FlowDeck.Core;
using FlowDeck.Core.Persistence;
using FlowDeck.Specs.Support;
using Reqnroll;

namespace FlowDeck.Specs.Steps;

/// <summary>
/// Binds Features/Engine/DefiningWorkflows.feature.
/// </summary>
[Binding]
public sealed class DefiningWorkflowsSteps(EngineContext world)
{
    [Given("a class implementing IWorkflowDefinition with id {string} and version {int}")]
    public void GivenADefinitionWithIdAndVersion(string id, int version) =>
        world.Declare(id, version, builder =>
            builder.AddStep("work", () => new SpecSteps.Recording(world.Log, "work")));

    [When("the definition is registered with the engine")]
    public void WhenTheDefinitionIsRegistered() =>
        world.CapturingError(() => world.BuildRegistry());

    [Then("the registry returns it for id {string} version {int}")]
    public void ThenTheRegistryReturnsIt(string id, int version)
    {
        var definition = world.BuildRegistry().Get(id, version);

        Assert.Equal(id, definition.Id);
        Assert.Equal(version, definition.Version);
    }

    [Given("a definition {string} version {int} is already registered")]
    public void GivenADefinitionIsAlreadyRegistered(string id, int version) =>
        this.GivenADefinitionWithIdAndVersion(id, version);

    [When("a second definition with the same id and version is registered")]
    public void WhenASecondDefinitionWithTheSameIdIsRegistered()
    {
        // Registered against the real registry rather than declared: the
        // scenario is about the registry rejecting a duplicate, and Declare
        // replaces by id, which would hide exactly the thing being asserted.
        var registry = world.BuildRegistry();
        var existing = world.Only;

        world.CapturingError(() => registry.Register(
            new SpecWorkflow(existing.Id, existing.Version, builder =>
                builder.AddStep("other", () => new SpecSteps.Recording(world.Log, "other")))));
    }

    [Then("registration fails with a DuplicateDefinitionException")]
    public void ThenRegistrationFailsWithDuplicateDefinition() =>
        Assert.IsType<DuplicateDefinitionException>(world.Error);

    [Given("no definition registered with id {string}")]
    public void GivenNoDefinitionRegisteredWithId(string id) =>
        Assert.False(world.IsDeclared(id));

    [When("an instance of {string} is started")]
    public async Task WhenAnInstanceOfIsStarted(string id) =>
        await world.CapturingErrorAsync(async () =>
            world.Instance = await world.Engine().StartAsync(id, 1));

    [Then("a DefinitionNotFoundException is thrown")]
    public void ThenADefinitionNotFoundExceptionIsThrown() =>
        Assert.IsType<DefinitionNotFoundException>(world.Error);

    [Then("no instance is created")]
    public async Task ThenNoInstanceIsCreated()
    {
        Assert.Null(world.Instance);

        // Through the store rather than the local field: a rejected start that
        // had already written a record would satisfy the assertion above while
        // leaving a half-built instance an operator would later find.
        Assert.Empty(await world.Store.ListAsync(new InstanceFilter()));
    }

    [Given("a registered definition containing exactly one step")]
    public void GivenARegisteredDefinitionWithOneStep() =>
        world.Declare("one-step", 1, builder =>
            builder.AddStep("only", () => new SpecSteps.Recording(world.Log, "only")));

    [When("an instance is started")]
    public async Task WhenAnInstanceIsStarted()
    {
        var declaration = world.Only;

        await world.CapturingErrorAsync(async () =>
            world.Instance = await world.Engine().StartAsync(declaration.Id, declaration.Version));
    }

    [Then("the step executes exactly once")]
    public void ThenTheStepExecutesExactlyOnce() => Assert.Single(world.Log);

    [Then("the instance status becomes Completed")]
    public void ThenTheInstanceStatusBecomesCompleted() =>
        Assert.Equal(InstanceStatus.Completed, world.Instance!.Status);

    [Given("a definition declaring steps A, B and C in that order")]
    public void GivenADefinitionDeclaringABC() =>
        world.Declare("abc", 1, builder => builder
            .AddStep("A", () => new SpecSteps.Recording(world.Log, "A"))
            .AddStep("B", () => new SpecSteps.Recording(world.Log, "B"))
            .AddStep("C", () => new SpecSteps.Recording(world.Log, "C")));

    [Then("the execution log records A then B then C")]
    public void ThenTheLogRecordsABC() => Assert.Equal(["A", "B", "C"], world.Log);

    [Given("a definition declaring steps A, B and C")]
    public void GivenADefinitionDeclaringABCUnordered() => this.GivenADefinitionDeclaringABC();

    [Given("step B throws an exception")]
    public void GivenStepBThrows() =>
        world.Declare("abc", 1, builder => builder
            .AddStep("A", () => new SpecSteps.Recording(world.Log, "A"))
            .AddStep("B", () => new SpecSteps.Throwing(world.Log, "B"))
            .AddStep("C", () => new SpecSteps.Recording(world.Log, "C")));

    [Then("step C is never executed")]
    public void ThenStepCIsNeverExecuted() => Assert.DoesNotContain("C", world.Log);

    [Then("the instance status becomes Failed")]
    public void ThenTheInstanceStatusBecomesFailed() =>
        Assert.Equal(InstanceStatus.Failed, world.Instance!.Status);
}
