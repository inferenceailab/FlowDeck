using FlowDeck.Core;
using FlowDeck.Specs.Support;
using Reqnroll;

namespace FlowDeck.Specs.Steps;

/// <summary>
/// Binds Features/Observability/InstanceCounters.feature.
/// </summary>
[Binding]
[Scope(Feature = "Instance outcome counters")]
public sealed class InstanceCounterSteps(EngineContext world)
{
    private const string Secret = "sk-live-do-not-emit-this";

    [Given("a definition \"(.*)\" version (.*) that completes")]
    public void GivenACompletingDefinition(string id, int version) =>
        world.Declare(id, version, builder =>
            builder.AddStep("work", () => new SpecSteps.Recording(world.Log, "work")));

    [Given("a definition that completes, one that fails and one that is cancelled")]
    public void GivenThreeDefinitions()
    {
        world.Declare("completes", 1, builder =>
            builder.AddStep("work", () => new SpecSteps.Recording(world.Log, "work")));

        world.Declare("fails", 1, builder =>
            builder.AddStep("charge", () => new SpecSteps.Throwing(world.Log, "charge")));

        // Suspends, so there is a live instance for the cancel to act on.
        // Cancelling a terminal instance is refused (ADR-0008).
        world.Declare("cancels", 1, builder =>
            builder.AddStep("wait", () => new SpecSteps.Suspending(world.Log, "wait")));
    }

    [Given("a definition whose rollback undoes a step")]
    public void GivenARollback() =>
        world.Declare("order-fulfilment", 1, builder => builder
            .AddStep("reserve", () => new SpecSteps.Recording(world.Log, "reserve"))
                .WithCompensation(() => new SpecSteps.Recording(world.Log, "undo-reserve"))
            .AddStep("ship", () => new SpecSteps.Throwing(world.Log, "ship")));

    [Given("a definition whose compensating action throws")]
    public void GivenAFailingRollback() =>
        world.Declare("order-fulfilment", 1, builder => builder
            .AddStep("reserve", () => new SpecSteps.Recording(world.Log, "reserve"))
                .WithCompensation(() => new SpecSteps.Throwing(world.Log, "undo-reserve"))
            .AddStep("ship", () => new SpecSteps.Throwing(world.Log, "ship")));

    [Given("a definition whose step writes a secret into workflow data")]
    public void GivenAStepWritingASecret() =>
        world.Declare("order-fulfilment", 1, builder =>
            builder.AddStep("capture", () => new SpecSteps.Writing("api-key", Secret)));

    [When("three instances are started")]
    public async Task WhenThreeAreStarted()
    {
        var engine = world.Engine();

        for (var i = 0; i < 3; i++)
        {
            await engine.StartAsync("order-fulfilment", 3);
        }
    }

    [When("an instance is started")]
    public async Task WhenAnInstanceIsStarted() =>
        world.Instance = await world.Engine().StartAsync("order-fulfilment", 1);

    [When("an instance fails")]
    public async Task WhenAnInstanceFails() =>
        world.Instance = await world.Engine().StartAsync("order-fulfilment", 1);

    [When("each reaches its terminal state")]
    public async Task WhenEachReachesTerminal()
    {
        var engine = world.Engine();

        await engine.StartAsync("completes", 1);
        await engine.StartAsync("fails", 1);

        var suspended = await engine.StartAsync("cancels", 1);
        await engine.CancelAsync(suspended.Id);
    }

    [Then("the started counter reports three")]
    public void ThenStartedIsThree() => Assert.Equal(3, world.Metrics.Total("started"));

    [Then("every measurement is tagged with the definition id and version")]
    public void ThenMeasurementsAreTagged() =>
        Assert.All(world.Metrics.All, measurement =>
        {
            Assert.Equal("order-fulfilment", measurement.Tags["definition.id"]);
            Assert.Equal(3, measurement.Tags["definition.version"]);
        });

    [Then("each outcome counter reports one")]
    public void ThenEachOutcomeIsOne()
    {
        Assert.Equal(1, world.Metrics.Total("completed"));
        Assert.Equal(1, world.Metrics.Total("failed"));
        Assert.Equal(1, world.Metrics.Total("cancelled"));
    }

    [Then("no counter reports an outcome that did not happen")]
    public void ThenNothingElseIsCounted()
    {
        // None of these three declares a compensating action, so nothing rolled
        // back. Without this the mapping could count every failure twice and
        // the assertions above would still pass.
        Assert.Equal(0, world.Metrics.Total("compensated"));

        // Three starts and three settlements, and no other *lifecycle*
        // measurement. Scoped to that family rather than to everything the
        // engine emits, because step durations and retry counters now share
        // the meter and counting them here would make this assertion break
        // every time an unrelated instrument is added (#198, #199).
        Assert.Equal(
            6,
            world.Metrics.All.Count(measurement =>
                measurement.Instrument.StartsWith("flowdeck.instances.", StringComparison.Ordinal)));
    }

    [Then("the compensated counter reports one")]
    public void ThenCompensatedIsOne() => Assert.Equal(1, world.Metrics.Total("compensated"));

    [Then("the failed counter reports nothing")]
    public void ThenFailedIsZero()
    {
        // An instance that rolled back is not also a plain failure. Counting it
        // in both would double every incident and make the failure rate a
        // number nobody can act on.
        Assert.Equal(0, world.Metrics.Total("failed"));
        Assert.Equal(InstanceStatus.Compensated, world.Instance!.Status);
    }

    [Then("the compensated counter reports one, tagged as a failed rollback")]
    public void ThenPartialRollbackIsTagged()
    {
        var measurement = world.Metrics.Of("compensated").Single();

        // The outcome that always needs a human (ADR-0021). Folded into the
        // plain compensated count it would be invisible; given its own counter
        // it would make "how many rolled back" a sum nobody remembers to do.
        Assert.Equal(nameof(InstanceStatus.CompensationFailed), measurement.Tags["outcome"]);
    }

    [Then("no tag on any measurement contains that secret")]
    public void ThenNoTagLeaksTheSecret()
    {
        Assert.NotEmpty(world.Metrics.All);

        // Tags are cardinality as well as disclosure: a value that varied per
        // instance would give a time-series backend one series per run
        // (ADR-0025 decisions 2 and 3).
        Assert.All(world.Metrics.All, measurement =>
            Assert.All(measurement.Tags, tag =>
            {
                Assert.DoesNotContain(Secret, tag.Key, StringComparison.Ordinal);
                Assert.DoesNotContain(Secret, tag.Value?.ToString() ?? string.Empty, StringComparison.Ordinal);
            }));
    }
}
