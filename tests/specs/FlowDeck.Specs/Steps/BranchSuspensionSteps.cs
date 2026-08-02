using FlowDeck.Core;
using FlowDeck.Specs.Support;
using Reqnroll;

namespace FlowDeck.Specs.Steps;

/// <summary>
/// Binds Features/Graph/BranchSuspension.feature.
/// </summary>
[Binding]
[Scope(Feature = "Suspending inside a branch")]
public sealed class BranchSuspensionSteps(EngineContext world)
{
    private void DeclareFork(string secondBranchBehaviour) =>
        world.Declare("forked", 1, builder => builder
            .AddStep("split", () => new SpecSteps.Recording(world.Log, "split"))
            .Fork(
                left => left.AddStep("park", () => new SpecSteps.Suspending(world.Log, "park")),
                right => right
                    .AddStep("sibling-one", () => new SpecSteps.Recording(world.Log, "sibling-one"))
                    .AddStep("sibling-two", () => secondBranchBehaviour == "fails"
                        ? new SpecSteps.Throwing(world.Log, "sibling-two")
                        : new SpecSteps.Recording(world.Log, "sibling-two"))));

    [Given("a fork whose first branch suspends and whose second completes")]
    public void GivenAForkThatParksAndCompletes() => this.DeclareFork("completes");

    [Given("a fork whose first branch suspends and whose second fails")]
    public void GivenAForkThatParksAndFails() => this.DeclareFork("fails");

    [Given("a suspended forked instance whose sibling finished")]
    public async Task GivenASuspendedFork()
    {
        this.DeclareFork("completes");

        world.Instance = await world.Engine().StartAsync("forked", 1);

        Assert.Equal(InstanceStatus.Suspended, world.Instance.Status);

        // Cleared so the Then can assert on what the *resume* ran, rather than
        // on everything the instance has ever done.
        world.Log.Clear();
    }

    [When("an instance is started")]
    public async Task WhenAnInstanceIsStarted() =>
        world.Instance = await world.Engine().StartAsync("forked", 1);

    [When("it is resumed")]
    public async Task WhenItIsResumed() =>
        world.Instance = await world.RestartedHost().ResumeAsync(world.Instance!.Id);

    [Then("the instance is Suspended")]
    public void ThenItIsSuspended() => Assert.Equal(InstanceStatus.Suspended, world.Instance!.Status);

    [Then("it is not Failed")]
    public void ThenItIsNotFailed()
    {
        // The behaviour this story replaces: a suspend inside a branch used to
        // raise NotSupportedException and fail the instance.
        Assert.NotEqual(InstanceStatus.Failed, world.Instance!.Status);
        Assert.Null(world.Instance.FailedStepName);
    }

    [Then("the sibling branch's steps all ran")]
    public void ThenTheSiblingCompleted() =>

        // Both of them. A sibling abandoned partway would be the failure this
        // rule exists to prevent - its side effects happen either way, and only
        // the record of them would be lost.
        Assert.Equal(["sibling-one", "sibling-two"], world.Log.Where(entry => entry.StartsWith("sibling", StringComparison.Ordinal)));

    [Then("the parked step runs again")]
    public void ThenTheParkedStepRuns() =>

        // Re-entered, because a suspending step stays the position rather than
        // being stepped past - the same rule the top-level sequence follows.
        Assert.Contains("park", world.Log);

    [Then("the sibling's steps do not run again")]
    public void ThenTheSiblingDoesNotRepeat()
    {
        // The arm that finished left no active node behind, so recovery has
        // nothing to resume there. Re-running it would be exactly what NFR-1
        // forbids.
        Assert.DoesNotContain("sibling-one", world.Log);
        Assert.DoesNotContain("sibling-two", world.Log);

        // And the step that opened the fork is not re-run either.
        Assert.DoesNotContain("split", world.Log);
    }

    [Then("the instance is Failed rather than Suspended")]
    public void ThenFailureWins()
    {
        // Failure outranks suspension at the join. An instance reported as
        // Suspended would invite an operator to resume something that has
        // already broken and been rolled back.
        Assert.Equal(InstanceStatus.Failed, world.Instance!.Status);
        Assert.Equal("sibling-two", world.Instance.FailedStepName);
    }
}
