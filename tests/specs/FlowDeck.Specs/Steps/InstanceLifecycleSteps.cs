using FlowDeck.Core;
using FlowDeck.Specs.Support;
using Reqnroll;

namespace FlowDeck.Specs.Steps;

/// <summary>
/// Binds Features/Engine/InstanceLifecycle.feature.
/// </summary>
/// <remarks>
/// Scoped to @M1. "Then the instance status becomes Cancelled" appears
/// verbatim in both issue #12 and issue #26, so the engine and API features
/// share step text - and an unscoped binding for it is ambiguous rather than
/// convenient. Scoping keeps both feature files faithful to the issue that
/// asked for them.
/// </remarks>
[Binding]
[Scope(Tag = "M1")]
public sealed class InstanceLifecycleSteps(EngineContext world)
{
    private readonly List<Guid> started = [];

    [Given("a registered definition")]
    public void GivenARegisteredDefinition() =>
        world.Declare("lifecycle", 1, builder =>
            builder.AddStep("work", () => new SpecSteps.Recording(world.Log, "work")));

    [When("two instances are started")]
    public async Task WhenTwoInstancesAreStarted()
    {
        var engine = world.Engine();

        this.started.Add((await engine.StartAsync("lifecycle", 1)).Id);
        this.started.Add((await engine.StartAsync("lifecycle", 1)).Id);
    }

    [Then("each returns a distinct non-empty instance id")]
    public void ThenEachReturnsADistinctId()
    {
        Assert.DoesNotContain(Guid.Empty, this.started);
        Assert.Equal(this.started.Count, this.started.Distinct().Count());
    }

    [Given("an instance that runs to completion")]
    public async Task GivenAnInstanceThatCompletes()
    {
        world.Declare("lifecycle", 1, builder =>
            builder.AddStep("work", () => new SpecSteps.Recording(world.Log, "work")));

        world.Instance = await world.Engine().StartAsync("lifecycle", 1);
    }

    [Given("a suspended instance")]
    public async Task GivenASuspendedInstance()
    {
        world.Declare("lifecycle", 1, builder =>
            builder.AddStep("wait", () => new SpecSteps.Suspending(world.Log, "wait")));

        world.Instance = await world.Engine().StartAsync("lifecycle", 1);

        Assert.Equal(InstanceStatus.Suspended, world.Instance.Status);
    }

    [Given("a completed instance")]
    public async Task GivenACompletedInstance() => await this.GivenAnInstanceThatCompletes();

    [Given("a running instance suspended at step B")]
    public async Task GivenAnInstanceSuspendedAtB()
    {
        world.Declare("lifecycle", 1, builder => builder
            .AddStep("A", () => new SpecSteps.Recording(world.Log, "A"))
            .AddStep("B", () => new SpecSteps.Suspending(world.Log, "B")));

        world.Instance = await world.Engine().StartAsync("lifecycle", 1);
    }

    [When("I query the instance")]
    public async Task WhenIQueryTheInstance()
    {
        // Reloaded from the store rather than reusing the returned object. An
        // assertion against the in-process instance would pass even if nothing
        // had been persisted, which is most of what these scenarios are about.
        world.Instance = await world.Engine().GetInstanceAsync(world.Instance!.Id);
    }

    [Then("CreatedAt is set")]
    public void ThenCreatedAtIsSet() => Assert.NotEqual(default, world.Instance!.CreatedAt);

    [Then("CompletedAt is set")]
    public void ThenCompletedAtIsSet() => Assert.NotNull(world.Instance!.CompletedAt);

    [Then("CompletedAt is greater than or equal to CreatedAt")]
    public void ThenCompletedAtIsAtOrAfterCreatedAt() =>
        Assert.True(world.Instance!.CompletedAt >= world.Instance.CreatedAt);

    [Then("CompletedAt is null")]
    public void ThenCompletedAtIsNull() => Assert.Null(world.Instance!.CompletedAt);

    [Then("the status is Suspended")]
    public void ThenTheStatusIsSuspended() =>
        Assert.Equal(InstanceStatus.Suspended, world.Instance!.Status);

    [Then("the current step name is {string}")]
    public void ThenTheCurrentStepNameIs(string expected) =>
        Assert.Equal(expected, world.Instance!.CurrentStepName);

    [When("I cancel it")]
    public async Task WhenICancelIt() =>
        await world.CapturingErrorAsync(async () =>
            world.Instance = await world.Engine().CancelAsync(world.Instance!.Id));

    [Then("the instance status becomes Cancelled")]
    public void ThenTheInstanceStatusBecomesCancelled() =>
        Assert.Equal(InstanceStatus.Cancelled, world.Instance!.Status);

    [Then("no further steps execute")]
    public void ThenNoFurtherStepsExecute()
    {
        // The suspending step ran once before the cancel. Cancelling must not
        // have re-entered it, which a naive implementation that resumed before
        // stopping would do.
        Assert.Single(world.Log);
    }

    [Then("the call fails with an InvalidStateTransitionException")]
    public void ThenTheCallFailsWithInvalidStateTransition() =>
        Assert.IsType<InvalidStateTransitionException>(world.Error);
}
