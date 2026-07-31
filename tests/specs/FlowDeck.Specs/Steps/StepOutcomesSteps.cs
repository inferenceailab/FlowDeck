using FlowDeck.Core;
using FlowDeck.Specs.Support;
using Reqnroll;

namespace FlowDeck.Specs.Steps;

/// <summary>
/// Binds Features/Engine/StepOutcomes.feature.
/// </summary>
[Binding]
public sealed class StepOutcomesSteps(EngineContext world)
{
    [Given("a step implementing IStep that returns Outcome.Next")]
    public void GivenAStepReturningNext() =>
        world.Declare("outcome", 1, builder => builder
            .AddStep("subject", () => new SpecSteps.Recording(world.Log, "subject"))
            .AddStep("after", () => new SpecSteps.Recording(world.Log, "after")));

    [Given("a step returning Outcome.Suspend")]
    public void GivenAStepReturningSuspend() =>
        world.Declare("outcome", 1, builder => builder
            .AddStep("subject", () => new SpecSteps.Suspending(world.Log, "subject"))
            .AddStep("after", () => new SpecSteps.Recording(world.Log, "after")));

    [Given("a step that throws InvalidOperationException")]
    public void GivenAStepThatThrows() =>
        world.Declare("outcome", 1, builder => builder
            .AddStep("subject", () => new SpecSteps.Throwing(world.Log, "subject")));

    [When("the engine executes the step")]
    public async Task WhenTheEngineExecutesTheStep() =>
        world.Instance = await world.Engine().StartAsync("outcome", 1);

    [When("the instance executes that step")]
    public async Task WhenTheInstanceExecutesThatStep() =>
        world.Instance = await world.Engine().StartAsync("outcome", 1);

    [Then("the step result is Success")]
    public async Task ThenTheStepResultIsSuccess()
    {
        // Read from history rather than inferred from the instance status: the
        // scenario is about the step's own result, and an instance that
        // completed does not prove which step reported what.
        var history = await world.Engine().GetHistoryAsync(world.Instance!.Id);

        Assert.Equal(StepStatus.Success, history.First(entry => entry.StepName == "subject").Status);
    }

    [Then("the workflow advances past that step")]
    public void ThenTheWorkflowAdvancesPastThatStep() => Assert.Contains("after", world.Log);

    [Then("the instance remains at the same step")]
    public async Task ThenTheInstanceRemainsAtTheSameStep()
    {
        Assert.Equal("subject", world.Instance!.CurrentStepName);
        Assert.DoesNotContain("after", world.Log);

        // The name alone does not prove this. CurrentStepName is assigned
        // before the step executes and is not moved by advancing, so an engine
        // that suspended *and* incremented the position would satisfy both
        // assertions above while having skipped the step.
        //
        // A mutation doing exactly that passed this scenario until this line
        // was added. What it breaks is NFR-1: resuming would step over work
        // that never completed.
        Assert.Equal(0, world.Instance.CurrentStepIndex);

        // Demonstrated rather than inferred: resuming re-enters "subject".
        await world.Engine().ResumeAsync(world.Instance.Id);

        Assert.Equal(["subject", "subject"], world.Log);
    }

    [Then("the instance status is Suspended")]
    public void ThenTheInstanceStatusIsSuspended() =>
        Assert.Equal(InstanceStatus.Suspended, world.Instance!.Status);

    [Then("the recorded error message contains {string}")]
    public void ThenTheRecordedErrorMessageContains(string expected)
    {
        // ErrorType rather than ErrorMessage: the type name is what survives a
        // reload, and the scenario names an exception type.
        Assert.Equal(expected, world.Instance!.ErrorType);
    }

    [Then("the failing step name is recorded")]
    public void ThenTheFailingStepNameIsRecorded() =>
        Assert.Equal("subject", world.Instance!.FailedStepName);
}
