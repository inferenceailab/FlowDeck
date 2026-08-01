using FlowDeck.Core;
using FlowDeck.Core.Persistence;
using FlowDeck.Specs.Support;
using Reqnroll;

namespace FlowDeck.Specs.Steps;

/// <summary>
/// Binds Features/Engine/VersionCoexistence.feature.
/// </summary>
/// <remarks>
/// Every step here names the version it ran through the step name it logs, so
/// "executed v1's steps" is an assertion about which shape ran rather than
/// about which one was registered.
/// </remarks>
[Binding]
[Scope(Feature = "Two definition versions execute side by side")]
public sealed class VersionCoexistenceSteps(EngineContext world)
{
    private WorkflowInstance? second;

    [Given("a suspended instance of \"(.*)\" v(.*)")]
    public async Task GivenASuspendedV1(string id, int version)
    {
        world.Declare(id, version, builder =>
            builder.AddStep("v1-wait", () => new SpecSteps.Suspending(world.Log, "v1-wait")));

        world.Instance = await world.Engine().StartAsync(id, version);
        world.Log.Clear();
    }

    [Given("\"(.*)\" v(.*) and v(.*) declare different steps")]
    public void GivenTwoVersions(string id, int first, int second)
    {
        world.Declare(id, first, builder =>
            builder.AddStep("v1-work", () => new SpecSteps.Recording(world.Log, "v1-work")));

        world.Declare(id, second, builder =>
            builder.AddStep("v2-work", () => new SpecSteps.Recording(world.Log, "v2-work")));
    }

    [Given("a crashed instance of \"(.*)\" v(.*) and a registered v(.*)")]
    public async Task GivenACrashedV1(string id, int version, int newer)
    {
        world.Declare(id, version, builder =>
            builder.AddStep("v1-wait", () => new SpecSteps.Suspending(world.Log, "v1-wait")));

        world.Declare(id, newer, builder =>
            builder.AddStep("v2-work", () => new SpecSteps.Recording(world.Log, "v2-work")));

        // Suspended rather than hand-written as Running: the state a recovery
        // starts from is taken from a real run, so this asserts against what
        // the engine actually leaves behind (the pattern #166 established).
        world.Instance = await world.Engine().StartAsync(id, version);
        world.Log.Clear();
    }

    [When("\"(.*)\" v(.*) is registered with different steps")]
    public void WhenANewerVersionIsRegistered(string id, int version) =>
        world.Declare(id, version, builder =>
            builder.AddStep("v2-work", () => new SpecSteps.Recording(world.Log, "v2-work")));

    [When("the v(.*) instance is resumed")]
    public async Task WhenResumed(int version)
    {
        _ = version;

        // A fresh engine, so the registry it resolves through is the one built
        // after v2 was declared. Resuming on the engine that started the
        // instance would prove nothing about a deployment having happened.
        await world.Engine().ResumeAsync(world.Instance!.Id);
    }

    [When("an instance of each is started")]
    public async Task WhenOneOfEachIsStarted()
    {
        var engine = world.Engine();

        world.Instance = await engine.StartAsync("orders", 1);
        this.second = await engine.StartAsync("orders", 2);
    }

    [When("an instance is started without naming a version")]
    public async Task WhenStartedWithoutAVersion()
    {
        // What the API does for a caller who names no version: resolve the
        // latest, then start that one (WorkflowEndpoints.StartAsync). Mirrored
        // rather than reimplemented, so this scenario is about which version
        // "latest" is rather than about HTTP.
        var latest = world.BuildRegistry().GetLatest("orders");

        world.Instance = await world.Engine().StartAsync(latest.Id, latest.Version);
    }

    [When("another node recovers it")]
    public async Task WhenAnotherNodeRecoversIt()
    {
        // A different engine over the same store, which is what a peer node is.
        var record = await world.Store.FindAsync(world.Instance!.Id);

        await world.Store.SaveAsync(record! with { Status = InstanceStatus.Suspended }, []);
        await world.RestartedHost().ResumeAsync(world.Instance.Id);
    }

    /// <summary>
    /// Binds both "executes" and "resumes", which assert the same thing.
    /// </summary>
    /// <remarks>
    /// The feature file says them differently because a reader cares which
    /// happened, and the assertion is identical either way: the step that ran
    /// came from the version the instance started under, and the newer
    /// version's step did not run at all.
    /// </remarks>
    [Then("it executes v(.*)'s steps")]
    [Then("it resumes v(.*)'s steps")]
    public void ThenItRanThatVersionsSteps(int version)
    {
        Assert.Contains($"v{version}-wait", world.Log);

        // The negative matters more than the positive. Resolving through a
        // registry that now holds v2 and running v2's step is the failure this
        // scenario exists to catch.
        Assert.DoesNotContain("v2-work", world.Log);
    }

    [Then("each executes its own version's steps")]
    public void ThenEachRunsItsOwn()
    {
        Assert.Equal(["v1-work", "v2-work"], world.Log);
        Assert.Equal(InstanceStatus.Completed, world.Instance!.Status);
        Assert.Equal(InstanceStatus.Completed, this.second!.Status);

        // And each instance records the version it ran, so history can be read
        // back against the right shape.
        Assert.Equal(1, world.Instance.DefinitionVersion);
        Assert.Equal(2, this.second.DefinitionVersion);
    }

    [Then("it runs v(.*)")]
    public void ThenItRunsVersion(int version)
    {
        Assert.Equal(version, world.Instance!.DefinitionVersion);
        Assert.Equal([$"v{version}-work"], world.Log);
    }
}
