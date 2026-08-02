using FlowDeck.Core;
using FlowDeck.Specs.Support;
using Reqnroll;

namespace FlowDeck.Specs.Steps;

/// <summary>
/// Binds Features/Observability/RetryMetrics.feature.
/// </summary>
[Binding]
[Scope(Feature = "Retry and compensation counters")]
public sealed class RetryMetricsSteps(EngineContext world)
{
    private const string Secret = "sk-live-9f2b7c41-canary-must-not-escape";

    private static RetryPolicy Twice => RetryPolicy.FixedDelay(3, TimeSpan.Zero);

    [Given("a step that fails twice and then succeeds")]
    public void GivenAStepThatFailsTwice() =>
        world.Declare("orders", 1, builder => builder
            .AddStep("charge", () => new FailsThenSucceeds(world.Log, "charge", 2), Twice)

            // A second step that never fails, so "a workflow that never
            // retried reports nothing" is asserted on a step that genuinely
            // ran rather than on one that does not exist.
            .AddStep("ship", () => new SpecSteps.Recording(world.Log, "ship")));

    [Given("two steps that each retry once")]
    public void GivenTwoRetryingSteps() =>
        world.Declare("orders", 1, builder => builder
            .AddStep("reserve", () => new FailsThenSucceeds(world.Log, "reserve", 1), Twice)
            .AddStep("charge", () => new FailsThenSucceeds(world.Log, "charge", 1), Twice));

    [Given("an instance whose rollback undoes two steps")]
    public void GivenARollbackOfTwo() =>
        world.Declare("orders", 1, builder => builder
            .AddStep("reserve", () => new SpecSteps.Recording(world.Log, "reserve"))
                .WithCompensation(() => new SpecSteps.Recording(world.Log, "undo-reserve"))
            .AddStep("charge", () => new SpecSteps.Recording(world.Log, "charge"))
                .WithCompensation(() => new SpecSteps.Recording(world.Log, "undo-charge"))
            .AddStep("ship", () => new SpecSteps.Throwing(world.Log, "ship")));

    [Given("an instance where one compensating action throws and one succeeds")]
    public void GivenOneFailingUndo() =>
        world.Declare("orders", 1, builder => builder
            .AddStep("reserve", () => new SpecSteps.Recording(world.Log, "reserve"))
                .WithCompensation(() => new SpecSteps.Throwing(world.Log, "undo-reserve"))
            .AddStep("charge", () => new SpecSteps.Recording(world.Log, "charge"))
                .WithCompensation(() => new SpecSteps.Recording(world.Log, "undo-charge"))
            .AddStep("ship", () => new SpecSteps.Throwing(world.Log, "ship")));

    [Given("a definition whose step writes a secret and then retries")]
    public void GivenASecretAndARetry() =>
        world.Declare("orders", 1, builder => builder
            .AddStep("capture", () => new SpecSteps.Writing("api-key", Secret))
            .AddStep("charge", () => new FailsThenSucceeds(world.Log, "charge", 1), Twice));

    [When("an instance is started")]
    public async Task WhenStarted() => world.Instance = await world.Engine().StartAsync("orders", 1);

    [When("it fails")]
    public async Task WhenItFails() => world.Instance = await world.Engine().StartAsync("orders", 1);

    private IReadOnlyList<Measurement> Retries() => world.Metrics.Instrument("flowdeck.steps.retried");

    private IReadOnlyList<Measurement> Compensations() => world.Metrics.Instrument("flowdeck.compensations");

    [Then("the retry counter reports two")]
    public void ThenTwoRetries() =>

        // Two, not three. The third attempt succeeded, and counting it would
        // make a step that recovered look the same as one still failing.
        Assert.Equal(2, this.Retries().Sum(measurement => measurement.Value));

    [Then("a workflow that never retried reports nothing")]
    public void ThenNoRetriesForTheOtherStep() =>
        Assert.DoesNotContain(this.Retries(), m => Equals(m.Tags["step.name"], "ship"));

    [Then("each step's retries are counted under its own name")]
    public void ThenRetriesAreTaggedByStep()
    {
        // Which step is the part an operator cannot get anywhere else without
        // reading history per instance.
        Assert.Equal(1, this.Retries().Where(m => Equals(m.Tags["step.name"], "reserve")).Sum(m => m.Value));
        Assert.Equal(1, this.Retries().Where(m => Equals(m.Tags["step.name"], "charge")).Sum(m => m.Value));
    }

    [Then("two compensating actions are counted, both as undone")]
    public void ThenTwoUndone()
    {
        Assert.Equal(2, this.Compensations().Sum(m => m.Value));
        Assert.All(this.Compensations(), m => Assert.Equal("undone", m.Tags["outcome"]));
    }

    [Then("one action is counted as undone and one as failed")]
    public void ThenOneOfEach()
    {
        // The difference between "one undo failed" and "nine did", which the
        // per-instance CompensationFailed counter cannot express.
        Assert.Equal(1, this.Compensations().Where(m => Equals(m.Tags["outcome"], "undone")).Sum(m => m.Value));
        Assert.Equal(1, this.Compensations().Where(m => Equals(m.Tags["outcome"], "failed")).Sum(m => m.Value));

        Assert.Equal(
            "reserve",
            this.Compensations().Single(m => Equals(m.Tags["outcome"], "failed")).Tags["step.name"]);
    }

    [Then("no tag on any measurement contains that secret")]
    public void ThenNothingLeaks()
    {
        Assert.NotEmpty(this.Retries());

        Assert.All(world.Metrics.All, measurement =>
            Assert.All(measurement.Tags, tag =>
            {
                Assert.DoesNotContain(Secret, tag.Key, StringComparison.Ordinal);
                Assert.DoesNotContain(Secret, tag.Value?.ToString() ?? string.Empty, StringComparison.Ordinal);
            }));
    }

    /// <summary>Fails a fixed number of times, then advances.</summary>
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
