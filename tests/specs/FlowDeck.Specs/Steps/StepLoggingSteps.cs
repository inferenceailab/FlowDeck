using FlowDeck.Core;
using FlowDeck.Specs.Support;
using Microsoft.Extensions.Logging;
using Reqnroll;

namespace FlowDeck.Specs.Steps;

/// <summary>
/// Binds Features/Observability/StepLogging.feature.
/// </summary>
[Binding]
[Scope(Feature = "Step execution logging")]
public sealed class StepLoggingSteps(EngineContext world)
{
    [Given("a definition with steps \"(.*)\" and \"(.*)\"")]
    public void GivenTwoSteps(string first, string second) =>
        world.Declare("order-fulfilment", 1, builder => builder
            .AddStep(first, () => new SpecSteps.Recording(world.Log, first))
            .AddStep(second, () => new SpecSteps.Recording(world.Log, second)));

    [Given("a step that fails twice and then succeeds")]
    public void GivenAStepThatFailsTwice() =>
        world.Declare("order-fulfilment", 1, builder => builder
            .AddStep(
                "charge",
                () => new FailsThenSucceeds(world.Log, "charge", failures: 2),

                // A real delay, not zero. The entry has to carry how long the
                // engine intends to wait, and a zero would pass an assertion
                // that the field merely exists.
                RetryPolicy.FixedDelay(3, TimeSpan.FromMilliseconds(20))));

    [Given("an instance whose rollback undoes two steps")]
    public void GivenARollbackOfTwoSteps() =>
        world.Declare("order-fulfilment", 1, builder => builder
            .AddStep("reserve", () => new SpecSteps.Recording(world.Log, "reserve"))
                .WithCompensation(() => new SpecSteps.Recording(world.Log, "undo-reserve"))
            .AddStep("charge", () => new SpecSteps.Recording(world.Log, "charge"))
                .WithCompensation(() => new SpecSteps.Recording(world.Log, "undo-charge"))
            .AddStep("ship", () => new SpecSteps.Throwing(world.Log, "ship")));

    [Given("an instance whose compensating action throws")]
    public void GivenAFailingCompensation() =>
        world.Declare("order-fulfilment", 1, builder => builder
            .AddStep("reserve", () => new SpecSteps.Recording(world.Log, "reserve"))
                .WithCompensation(() => new SpecSteps.Throwing(world.Log, "undo-reserve"))
            .AddStep("ship", () => new SpecSteps.Throwing(world.Log, "ship")));

    [Given("a definition that forks into steps \"(.*)\" and \"(.*)\"")]
    public void GivenAFork(string left, string right) =>
        world.Declare("order-fulfilment", 1, builder => builder
            .AddStep("split", () => new SpecSteps.Recording(world.Log, "split"))
            .Fork(
                a => a.AddStep(left, () => new SpecSteps.Recording(world.Log, left)),
                b => b.AddStep(right, () => new SpecSteps.Recording(world.Log, right))));

    [When("an instance is started")]
    public async Task WhenAnInstanceIsStarted() =>
        world.Instance = await world.Engine().StartAsync("order-fulfilment", 1);

    [When("it fails")]
    public async Task WhenItFails() =>
        world.Instance = await world.Engine().StartAsync("order-fulfilment", 1);

    [Then("each step logs that it started and how it finished")]
    public void ThenEachStepLogsBothEnds()
    {
        Assert.Equal(["reserve", "charge"], Names("StepStarted"));
        Assert.Equal(["reserve", "charge"], Names("StepFinished"));

        // Finished as what, not merely that it finished. An entry that omitted
        // the outcome would leave a failure and a success looking identical.
        Assert.All(
            world.Logger.Named("StepFinished"),
            entry => Assert.Equal(StepStatus.Success, entry.Field("Status")));
    }

    [Then("the outcome entry carries how long the step took")]
    public void ThenTheOutcomeCarriesDuration() =>
        Assert.All(
            world.Logger.Named("StepFinished"),
            entry => Assert.IsType<double>(entry.Field("ElapsedMs")));

    [Then("each attempt is logged with its own attempt number")]
    public void ThenAttemptsAreNumbered()
    {
        // 1, 2, 3 rather than three entries that all claim to be the first.
        // Three identical rows read as a rendering bug, which is the same
        // reason history carries an attempt number (#107).
        Assert.Equal(
            [1, 2, 3],
            world.Logger.Named("StepFinished").Select(entry => entry.Field("Attempt")).ToArray());
    }

    [Then("an entry between the attempts says a retry is scheduled, with the delay")]
    public void ThenRetriesAreAnnounced()
    {
        var retries = world.Logger.Named("StepRetrying");

        // Two, not three: the third attempt succeeded, so nothing was scheduled
        // after it. An off-by-one here would announce a retry that never came.
        Assert.Equal(2, retries.Count);

        Assert.All(retries, entry =>
        {
            Assert.Equal("charge", entry.Field("StepName"));
            Assert.Equal(20d, entry.Field("DelayMs"));

            // Loud enough to notice. A retry is the engine telling an operator
            // something is wrong that it is coping with for now.
            Assert.Equal(LogLevel.Warning, entry.Level);
        });
    }

    [Then("each compensating action is logged as a rollback rather than as a step")]
    public void ThenRollbacksAreDistinct()
    {
        Assert.Equal(2, world.Logger.Named("StepRolledBack").Count);

        // And not as forward progress. The engine names rollback history
        // entries "compensate:x" (ADR-0021), and an operator filtering the
        // forward events should not have to know that convention.
        Assert.All(
            world.Logger.Named("StepFinished"),
            entry => Assert.DoesNotContain(
                "compensate:",
                (string)entry.Field("StepName")!,
                StringComparison.Ordinal));
    }

    [Then("the rolled back step names are the ones that ran")]
    public void ThenRolledBackNamesAreTheStepsThemselves()
    {
        // Most recently completed first, and without the wire prefix.
        Assert.Equal(["charge", "reserve"], Names("StepRolledBack"));
    }

    [Then("the failed rollback is logged as an error naming the step")]
    public void ThenTheFailedRollbackIsAnError()
    {
        var entry = world.Logger.Named("RollbackFailed").Single();

        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Equal("reserve", entry.Field("StepName"));
        Assert.Equal(nameof(InvalidOperationException), entry.Field("ErrorType"));
    }

    [Then("each branch step's entries carry the branch it ran on")]
    public void ThenBranchStepsCarryTheirBranch()
    {
        Assert.Equal("branch-1", Branch("left"));
        Assert.Equal("branch-2", Branch("right"));

        object? Branch(string stepName) => world.Logger
            .Named("StepFinished")
            .Single(entry => Equals(entry.Field("StepName"), stepName))
            .Field("Branch");
    }

    [Then("a step on the top-level sequence carries no branch")]
    public void ThenTopLevelStepsCarryNoBranch()
    {
        // Absent, not empty. A step that never forked has no branch to name,
        // and an empty string is a value a reader then has to interpret.
        var split = world.Logger
            .Named("StepFinished")
            .Single(entry => Equals(entry.Field("StepName"), "split"));

        Assert.Null(split.Field("Branch"));
    }

    private string[] Names(string eventName) =>
        [.. world.Logger.Named(eventName).Select(entry => (string)entry.Field("StepName")!)];

    /// <summary>Fails a fixed number of times, then advances.</summary>
    /// <remarks>
    /// Counts from the log rather than a field, because the engine builds the
    /// step afresh for every execution - a field would reset each time and the
    /// step would fail forever.
    /// </remarks>
    private sealed class FailsThenSucceeds(List<string> log, string name, int failures) : IStep
    {
        public ValueTask<Outcome> ExecuteAsync(IStepContext context, CancellationToken cancellationToken = default)
        {
            var previous = log.Count(entry => entry == name);
            log.Add(name);

            return previous < failures
                ? throw new InvalidOperationException($"{name} transient {previous + 1}")
                : ValueTask.FromResult(Outcome.Next);
        }
    }
}
