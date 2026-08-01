using FlowDeck.Core;
using FlowDeck.Specs.Support;
using Reqnroll;

namespace FlowDeck.Specs.Steps;

/// <summary>
/// Binds Features/Resilience/Compensation.feature.
/// </summary>
[Binding]
[Scope(Tag = "M5")]
public sealed class CompensationSteps(EngineContext world)
{
    private IReadOnlyList<StepDeclaration>? compiled;

    /// <summary>Compiles a definition the way the engine does.</summary>
    private static IReadOnlyList<StepDeclaration> Compile(Action<IWorkflowBuilder> declare)
    {
        var builder = new WorkflowBuilder("compensating");
        declare(builder);
        return builder.Build();
    }

    // ------------------------------------------------------- declaring (#118)

    [Given("a workflow declaring a step with WithCompensation")]
    public void GivenAWorkflowDeclaringCompensation() =>
        world.Declare("compensating", 1, builder => builder
            .AddStep("charge", () => new SpecSteps.Recording(world.Log, "charge"))
                .WithCompensation(() => new SpecSteps.Recording(world.Log, "refund")));

    [Given("a workflow declaring two steps, the first with a compensating action")]
    public void GivenTwoStepsFirstWithCompensation() =>
        world.Declare("compensating", 1, builder => builder
            .AddStep("charge", () => new SpecSteps.Recording(world.Log, "charge"))
                .WithCompensation(() => new SpecSteps.Recording(world.Log, "refund"))
            .AddStep("ship", () => new SpecSteps.Recording(world.Log, "ship")));

    [Given("a workflow calling WithCompensation before AddStep")]
    public void GivenCompensationBeforeAnyStep() =>
        world.Declare("compensating", 1, builder => builder
            .WithCompensation(() => new SpecSteps.Recording(world.Log, "refund"))
            .AddStep("charge", () => new SpecSteps.Recording(world.Log, "charge")));

    [When("the definition is compiled")]
    public void WhenTheDefinitionIsCompiled() =>
        this.compiled = Compile(world.Only.Build);

    [Then("that step carries its compensating action")]
    public async Task ThenThatStepCarriesItsAction()
    {
        var compensation = this.compiled![0].Compensation;

        Assert.NotNull(compensation);

        // Executed, not merely constructed. Calling the factory alone proves
        // only that it returns something - an earlier version of this step did
        // exactly that and asserted on a log the step had never written to.
        await compensation().ExecuteAsync(new StepContext(Guid.NewGuid(), "charge", new WorkflowData()));

        Assert.Contains("refund", world.Log);

        // A factory, so two instances rolling back at once cannot share state.
        Assert.NotSame(compensation(), compensation());
    }

    [Then("only the first step carries one")]
    public void ThenOnlyTheFirstStepCarriesOne()
    {
        Assert.NotNull(this.compiled![0].Compensation);
        Assert.Null(this.compiled[1].Compensation);
    }

    [When("a compensating instance is started")]
    public async Task WhenACompensatingInstanceIsStarted() =>
        await world.CapturingErrorAsync(async () =>
            world.Instance = await world.Engine().StartAsync("compensating", 1));

    [Then("InvalidWorkflowDefinitionException is raised")]
    public void ThenInvalidWorkflowDefinitionIsRaised() =>
        Assert.IsType<InvalidWorkflowDefinitionException>(world.Error);

    // -------------------------------------------------------- rollback (#119)

    [Given("a workflow whose first step has a compensating action")]
    public void GivenAWorkflowWhoseFirstStepHasCompensation() => world.Captured["shape"] = "first-compensated";

    [Given("whose second step throws")]
    public void GivenWhoseSecondStepThrows() =>
        world.Declare("compensating", 1, builder => builder
            .AddStep("charge", () => new SpecSteps.Recording(world.Log, "charge"))
                .WithCompensation(() => new SpecSteps.Recording(world.Log, "refund"))
            .AddStep("ship", () => new SpecSteps.Throwing(world.Log, "ship")));

    [Given("three steps with compensating actions")]
    public void GivenThreeStepsWithCompensation() => this.DeclareThree(secondUndoThrows: false);

    [Given("the second action to run throws")]
    public void GivenTheSecondActionThrows() => this.DeclareThree(secondUndoThrows: true);

    [Given("the third step throws")]
    public void GivenTheThirdStepThrows()
    {
        // The three-step declarations above already make C throw. Stated in the
        // feature because the scenario reads as a sentence, and re-declaring
        // here would undo whichever variant the previous Given chose.
    }

    private void DeclareThree(bool secondUndoThrows) =>
        world.Declare("compensating", 1, builder => builder
            .AddStep("a", () => new SpecSteps.Recording(world.Log, "a"))
                .WithCompensation(() => new SpecSteps.Recording(world.Log, "undo-a"))
            .AddStep("b", () => new SpecSteps.Recording(world.Log, "b"))
                .WithCompensation(() => secondUndoThrows
                    ? new SpecSteps.Throwing(world.Log, "undo-b")
                    : new SpecSteps.Recording(world.Log, "undo-b"))
            .AddStep("c", () => new SpecSteps.Throwing(world.Log, "c"))
                .WithCompensation(() => new SpecSteps.Recording(world.Log, "undo-c")));

    [Given("a workflow where only the second of three steps declares one")]
    public void GivenOnlyTheSecondDeclaresCompensation() =>
        world.Declare("compensating", 1, builder => builder
            .AddStep("a", () => new SpecSteps.Recording(world.Log, "a"))
            .AddStep("b", () => new SpecSteps.Recording(world.Log, "b"))
                .WithCompensation(() => new SpecSteps.Recording(world.Log, "undo-b"))
            .AddStep("c", () => new SpecSteps.Throwing(world.Log, "c")));

    [Given("a step that exhausted its retries and declares a compensating action")]
    public void GivenAStepThatExhaustedItsRetries() =>
        world.Declare("compensating", 1, builder => builder
            .AddStep(
                "charge",
                () => new SpecSteps.Throwing(world.Log, "charge"),
                RetryPolicy.FixedDelay(3, TimeSpan.Zero))
                .WithCompensation(() => new SpecSteps.Recording(world.Log, "refund")));

    [Then("the compensating action runs")]
    public void ThenTheCompensatingActionRuns() => Assert.Equal(["charge", "ship", "refund"], world.Log);

    [Then("the compensating actions run in reverse declaration order")]
    public void ThenActionsRunInReverseOrder() =>
        Assert.Equal(["a", "b", "c", "undo-c", "undo-b", "undo-a"], world.Log);

    [Then("only that action runs, and the others are not treated as failures")]
    public void ThenOnlyThatActionRuns()
    {
        Assert.Equal(["a", "b", "c", "undo-b"], world.Log);

        // Skipped, not failed. A step with nothing to undo counting as a
        // rollback failure would make every partial rollback look broken.
        Assert.Equal(InstanceStatus.Compensated, world.Instance!.Status);
    }

    [Then("that action runs exactly once")]
    public void ThenThatActionRunsExactlyOnce()
    {
        // Three attempts, one refund. Not zero, because a step that never
        // reported success may still have had an effect; not three, because
        // #108 requires the attempts to be idempotent, so they shared one.
        Assert.Equal(["charge", "charge", "charge", "refund"], world.Log);
    }

    // -------------------------------------------------------- statuses (#120)

    [Given("a workflow that fails and one compensating action that throws")]
    public void GivenACompensatingActionThatThrows() =>
        world.Declare("compensating", 1, builder => builder
            .AddStep("charge", () => new SpecSteps.Recording(world.Log, "charge"))
                .WithCompensation(() => new SpecSteps.Throwing(world.Log, "refund"))
            .AddStep("ship", () => new SpecSteps.Throwing(world.Log, "ship")));

    [Given("a workflow with no compensating actions whose step throws")]
    public void GivenNoCompensatingActions() =>
        world.Declare("compensating", 1, builder => builder
            .AddStep("a", () => new SpecSteps.Recording(world.Log, "a"))
            .AddStep("b", () => new SpecSteps.Throwing(world.Log, "b")));

    [Then("the compensating instance status is {word}")]
    public void ThenTheCompensatingInstanceStatusIs(string expected) =>
        Assert.Equal(Enum.Parse<InstanceStatus>(expected), world.Instance!.Status);

    [Given("a Compensated instance")]
    public async Task GivenACompensatedInstance()
    {
        this.GivenWhoseSecondStepThrows();

        world.Instance = await world.Engine().StartAsync("compensating", 1);

        Assert.Equal(InstanceStatus.Compensated, world.Instance.Status);
    }

    [When("it is cancelled")]
    public async Task WhenItIsCancelled() =>
        await world.CapturingErrorAsync(async () => await world.Engine().CancelAsync(world.Instance!.Id));

    [Then("InvalidStateTransitionException is raised for the rollback")]
    public void ThenInvalidStateTransitionIsRaised() =>
        Assert.IsType<InvalidStateTransitionException>(world.Error);

    // ------------------------------------------------ failing rollback (#121)

    [Then("all three actions are attempted")]
    public void ThenAllThreeActionsAreAttempted()
    {
        // Continues past the failure. Refusing to undo a because undoing b
        // failed adds a second unresolved side effect to the first.
        Assert.Equal(["a", "b", "c", "undo-c", "undo-b", "undo-a"], world.Log);
        Assert.Equal(InstanceStatus.CompensationFailed, world.Instance!.Status);
    }

    [Given("two compensating actions that both throw")]
    public void GivenTwoActionsThatBothThrow() =>
        world.Declare("compensating", 1, builder => builder
            .AddStep("a", () => new SpecSteps.Recording(world.Log, "a"))
                .WithCompensation(() => new SpecSteps.Throwing(world.Log, "undo-a"))
            .AddStep("b", () => new SpecSteps.Throwing(world.Log, "b"))
                .WithCompensation(() => new SpecSteps.Throwing(world.Log, "undo-b")));

    [Then("history records both, each with its own error")]
    public async Task ThenHistoryRecordsBoth()
    {
        var rollback = (await world.Engine().GetHistoryAsync(world.Instance!.Id))
            .Where(entry => entry.StepName.StartsWith("compensate:", StringComparison.Ordinal))
            .ToArray();

        Assert.Equal(["compensate:b", "compensate:a"], rollback.Select(entry => entry.StepName));
        Assert.All(rollback, entry => Assert.Equal(StepStatus.Failed, entry.Status));

        // Each carries its own message, not a copy of the first. "Two failed"
        // is only useful if the failures can differ.
        Assert.Equal(["undo-b failed", "undo-a failed"], rollback.Select(entry => entry.ErrorMessage));
    }

    [Given("a workflow whose step failed with {string}")]
    public void GivenAWorkflowWhoseStepFailedWith(string message) => world.Captured["original"] = message;

    [Given("whose compensating action fails with something else")]
    public void GivenACompensatingActionFailingWithSomethingElse()
    {
        var original = (string)world.Captured["original"]!;

        world.Declare("compensating", 1, builder => builder
            .AddStep("charge", () => new SpecSteps.Recording(world.Log, "charge"))
                .WithCompensation(() => new SpecSteps.Throwing(
                    world.Log, "refund", new InvalidOperationException("gateway unreachable")))
            .AddStep("ship", () => new SpecSteps.Throwing(
                world.Log, "ship", new InvalidOperationException(original))));
    }

    [Then("the instance still reports the original failure")]
    public void ThenTheInstanceStillReportsTheOriginalFailure()
    {
        Assert.Equal("ship", world.Instance!.FailedStepName);
        Assert.Equal((string)world.Captured["original"]!, world.Instance.ErrorMessage);
        Assert.Equal(InstanceStatus.CompensationFailed, world.Instance.Status);
    }
}
