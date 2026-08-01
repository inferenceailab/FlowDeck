using FlowDeck.Core;
using FlowDeck.Specs.Support;
using Reqnroll;

namespace FlowDeck.Specs.Steps;

/// <summary>
/// Binds Features/Engine/Retirement.feature.
/// </summary>
[Binding]
[Scope(Feature = "Retiring a definition version")]
public sealed class RetirementSteps(EngineContext world)
{
    private WorkflowEngine? retiredOn;
    private Guid completedInstance;

    /// <summary>
    /// The engine the When acted on.
    /// </summary>
    /// <remarks>
    /// An asserting accessor rather than a null-forgiving operator at each use.
    /// Every Then here has to act on the <i>same</i> engine: EngineContext
    /// builds a fresh registry per call, so a second one would be asserted
    /// against a registry that never saw the removal.
    /// </remarks>
    private WorkflowEngine Engine =>
        this.retiredOn ?? throw new InvalidOperationException("No step retired anything.");

    private void Declare(string id, int version, string step) =>
        world.Declare(id, version, builder =>
            builder.AddStep(step, () => new SpecSteps.Recording(world.Log, step)));

    [Given("a definition \"(.*)\" v(.*) with no live instances")]
    public void GivenAnUnusedDefinition(string id, int version) => this.Declare(id, version, "work");

    [Given("a suspended instance of \"(.*)\" v(.*)")]
    public async Task GivenASuspendedInstance(string id, int version)
    {
        world.Declare(id, version, builder =>
            builder.AddStep("wait", () => new SpecSteps.Suspending(world.Log, "wait")));

        world.Instance = await world.Engine().StartAsync(id, version);
    }

    [Given("a completed instance of \"(.*)\" v(.*)")]
    public async Task GivenACompletedInstance(string id, int version)
    {
        this.Declare(id, version, "work");

        var instance = await world.Engine().StartAsync(id, version);

        this.completedInstance = instance.Id;
    }

    [Given("\"(.*)\" v(.*) and v(.*) are registered")]
    public void GivenTwoVersions(string id, int first, int second)
    {
        this.Declare(id, first, "v1-work");
        this.Declare(id, second, "v2-work");
    }

    [Given("\"(.*)\" v(.*) is registered")]
    public void GivenOneVersion(string id, int version) => this.Declare(id, version, "work");

    [When("I retire \"(.*)\" v(.*)")]
    public async Task WhenIRetire(string id, int version)
    {
        // Held, because every Then below acts on the same engine. A second
        // engine would build a second registry, and the removal would be
        // asserted against something that never saw it.
        this.retiredOn = world.Engine();

        await world.CapturingErrorAsync(() => this.Engine.RetireAsync(id, version));
    }

    [Then("it is no longer registered")]
    public void ThenItIsGone() =>

        // Through the public surface rather than by peering at the registry:
        // what an operator cares about is that nothing can run on it again,
        // which is what the next step asserts.
        Assert.Null(world.Error);

    [Then("starting an instance of \"(.*)\" v(.*) is refused")]
    public async Task ThenStartingIsRefused(string id, int version) =>
        await Assert.ThrowsAsync<DefinitionNotFoundException>(() => this.Engine.StartAsync(id, version));

    [Then("the call fails, naming how many instances still hold it")]
    public void ThenItFailsWithACount()
    {
        var error = Assert.IsType<DefinitionInUseException>(world.Error);

        // The number, not only the refusal. "Refused" alone leaves an operator
        // with no next step; the count says whether to wait or to go and cancel
        // something.
        Assert.Equal(1, error.ActiveInstances);
        Assert.Contains("1 instance(s)", error.Message, StringComparison.Ordinal);
    }

    [Then("\"(.*)\" v(.*) is still registered")]
    public async Task ThenItIsStillRegistered(string id, int version)
    {
        // Proved by using it. A refused retirement that had removed the
        // definition anyway would pass any check that only read a flag.
        var instance = await this.Engine.StartAsync(id, version);

        Assert.Equal(InstanceStatus.Suspended, instance.Status);
    }

    [Then("that instance is still readable")]
    public async Task ThenTheInstanceIsStillReadable()
    {
        // Retiring a definition is not a reason to lose the record of what ran
        // under it. History outlives the definition deliberately.
        var found = await this.Engine.FindInstanceAsync(this.completedInstance);

        Assert.NotNull(found);
        Assert.Equal(InstanceStatus.Completed, found.Status);
    }

    [Then("\"(.*)\" v(.*) still starts instances")]
    public async Task ThenTheOtherVersionStillWorks(string id, int version)
    {
        var instance = await this.Engine.StartAsync(id, version);

        Assert.Equal(InstanceStatus.Completed, instance.Status);
        Assert.Contains("v2-work", world.Log);
    }

    [Then("the call fails saying no such definition is registered")]
    public void ThenItFailsAsNotFound() =>

        // Not a silent no-op. A typo in a retirement command would otherwise
        // read as a completed cleanup.
        Assert.IsType<DefinitionNotFoundException>(world.Error);
}
