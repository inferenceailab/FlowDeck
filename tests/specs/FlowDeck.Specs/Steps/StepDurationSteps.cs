using System.Globalization;
using FlowDeck.Core;
using FlowDeck.Specs.Support;
using Microsoft.Extensions.DependencyInjection;
using Reqnroll;

namespace FlowDeck.Specs.Steps;

/// <summary>
/// Binds Features/Observability/StepDuration.feature.
/// </summary>
/// <remarks>
/// Half engine, half scrape endpoint. The histogram is the first instrument
/// FlowDeck emits that is not a counter, so the rendering is as much the story
/// as the measurement is.
/// </remarks>
[Binding]
[Scope(Feature = "Step duration")]
public sealed class StepDurationSteps(EngineContext world, ApiContext api)
{
    private const string Secret = "sk-live-9f2b7c41-canary-must-not-escape";
    private const string Instrument = "flowdeck.steps.duration";

    [Given("a definition with steps \"(.*)\" and \"(.*)\"")]
    public void GivenTwoSteps(string first, string second) =>
        world.Declare("orders", 1, builder => builder
            .AddStep(first, () => new SpecSteps.Recording(world.Log, first))
            .AddStep(second, () => new SpecSteps.Recording(world.Log, second)));

    [Given("a step that fails twice and then succeeds")]
    public void GivenARetryingStep() =>
        world.Declare("orders", 1, builder => builder
            .AddStep(
                "charge",
                () => new FailsThenSucceeds(world.Log, "charge", 2),
                RetryPolicy.FixedDelay(3, TimeSpan.Zero)));

    [Given("a definition whose step writes a secret")]
    public void GivenASecret() =>
        world.Declare("orders", 1, builder =>
            builder.AddStep("capture", () => new SpecSteps.Writing("api-key", Secret)));

    [Given("a host that has run an instance")]
    public async Task GivenAHostThatHasRun()
    {
        api.Declare(new SpecWorkflow("orders", 1, builder =>
            builder.AddStep("work", () => new SpecSteps.Recording([], "work"))));

        api.InstanceId = (await api.Engine.StartAsync("orders", 1)).Id;
    }

    [Given("a host where a step took a minute")]
    public async Task GivenASlowStep()
    {
        await this.GivenAHostThatHasRun();

        // Recorded directly rather than by running a slow step, because the
        // subject here is the *rendering* of an observation above every edge -
        // and a scenario cannot honestly take a minute to assert it.
        api.Services
            .GetRequiredService<EngineMetrics>()
            .StepFinished(await api.Engine.GetInstanceAsync(api.InstanceId), "slow", StepStatus.Success, 60);
    }

    [When("an instance is started")]
    public async Task WhenStarted() => world.Instance = await world.Engine().StartAsync("orders", 1);

    [When(@"I GET \/metrics")]
    public async Task WhenIGetMetrics() => await api.SendAsync(client => client.GetAsync("/metrics"));

    private IReadOnlyList<Measurement> Durations() => world.Metrics.Instrument(Instrument);

    [Then("each step's duration is recorded under its own name")]
    public void ThenEachStepIsRecorded()
    {
        Assert.Equal(
            ["reserve", "charge"],
            this.Durations().Select(measurement => measurement.Tags["step.name"]));

        Assert.All(this.Durations(), m => Assert.Equal(nameof(StepStatus.Success), m.Tags["outcome"]));
    }

    [Then("three durations are recorded for it")]
    public void ThenThreeDurations() =>

        // Per execution, not per step. Averaging three attempts into one
        // observation would hide the thing worth seeing - that it took three
        // goes.
        Assert.Equal(3, this.Durations().Count);

    [Then("the failed attempts are tagged separately from the successful one")]
    public void ThenOutcomesAreSeparate()
    {
        // A step that fails fast and a step that succeeds slowly are different
        // problems, and their durations must not share a series.
        Assert.Equal(2, this.Durations().Count(m => Equals(m.Tags["outcome"], nameof(StepStatus.Failed))));
        Assert.Equal(1, this.Durations().Count(m => Equals(m.Tags["outcome"], nameof(StepStatus.Success))));
    }

    [Then("the duration is declared as a histogram measured in seconds")]
    public void ThenItIsAHistogram()
    {
        Assert.Contains("# TYPE flowdeck_steps_duration_seconds histogram", api.Body, StringComparison.Ordinal);
        Assert.Contains("# HELP flowdeck_steps_duration_seconds ", api.Body, StringComparison.Ordinal);
    }

    [Then("it reports cumulative buckets, a sum and a count")]
    public void ThenItReportsTheThreeParts()
    {
        Assert.Contains("flowdeck_steps_duration_seconds_sum{", api.Body, StringComparison.Ordinal);
        Assert.Contains("flowdeck_steps_duration_seconds_count{", api.Body, StringComparison.Ordinal);

        // Cumulative: each edge holds everything at or below it, so the counts
        // never decrease as the edges grow. Per-bucket counts would render
        // without error and chart wrongly.
        var counts = BucketCounts();

        Assert.NotEmpty(counts);

        for (var i = 1; i < counts.Count; i++)
        {
            Assert.True(
                counts[i] >= counts[i - 1],
                $"bucket {i} fell to {counts[i]} from {counts[i - 1]}");
        }
    }

    [Then("every configured bucket edge appears")]
    public void ThenEveryEdgeAppears() =>
        Assert.All(
            EngineMetrics.BucketBoundaries,
            edge => Assert.Contains(
                $"le=\"{edge.ToString("0.######", CultureInfo.InvariantCulture)}\"",
                api.Body,
                StringComparison.Ordinal));

    // A regex rather than a Cucumber Expression: "+" is a quantifier there, and
    // escaping it is not something that syntax supports.
    [Then(@"^the last one is \+Inf, carrying the total count$")]
    public void ThenInfCarriesTheTotal()
    {
        // Required by the format: a histogram without +Inf is rejected by the
        // collector rather than merely losing its last bucket.
        Assert.Contains("le=\"+Inf\"", api.Body, StringComparison.Ordinal);

        var infinite = Line("_bucket", "le=\"+Inf\"");
        var total = Line("_count", null);

        Assert.Equal(total, infinite);
    }

    [Then("no bucket edge holds it")]
    public void ThenNoEdgeHoldsIt()
    {
        // Every edge is below a minute, so the slow observation falls outside
        // all of them. Its series' largest finite bucket therefore counts one
        // fewer than the total.
        var slow = Series("step_name=\"slow\"");

        var largest = slow
            .Where(line => line.Contains("_bucket", StringComparison.Ordinal))
            .Where(line => !line.Contains("+Inf", StringComparison.Ordinal))
            .Select(Value)
            .LastOrDefault();

        Assert.Equal(0, largest);
    }

    [Then("the total count includes it")]
    public void ThenTheTotalIncludesIt()
    {
        // This is what distinguishes +Inf from "the last cumulative bucket".
        // With every observation inside the edges the two are equal, and a
        // renderer that confused them would go unnoticed.
        var slow = Series("step_name=\"slow\"");

        Assert.Equal(1, Value(slow.Single(line => line.Contains("+Inf", StringComparison.Ordinal))));
        Assert.Equal(1, Value(slow.Single(line => line.Contains("_count", StringComparison.Ordinal))));
    }

    /// <summary>The rendered lines of one series.</summary>
    private string[] Series(string label) =>
    [
        .. api.Body
            .Split('\n')
            .Where(line => line.StartsWith("flowdeck_steps_duration_seconds", StringComparison.Ordinal))
            .Where(line => line.Contains(label, StringComparison.Ordinal)),
    ];

    private static long Value(string line) =>
        long.Parse(line[(line.LastIndexOf(' ') + 1)..].Trim(), CultureInfo.InvariantCulture);

    [Then("no tag on any measurement contains that secret")]
    public void ThenNothingLeaks()
    {
        Assert.NotEmpty(this.Durations());

        Assert.All(world.Metrics.All, measurement =>
            Assert.All(measurement.Tags, tag =>
            {
                Assert.DoesNotContain(Secret, tag.Key, StringComparison.Ordinal);
                Assert.DoesNotContain(Secret, tag.Value?.ToString() ?? string.Empty, StringComparison.Ordinal);
            }));
    }

    /// <summary>The bucket counts of the first rendered series, in edge order.</summary>
    private List<long> BucketCounts() =>
    [
        .. api.Body
            .Split('\n')
            .Where(line => line.StartsWith("flowdeck_steps_duration_seconds_bucket", StringComparison.Ordinal))
            .Where(line => !line.Contains("+Inf", StringComparison.Ordinal))
            .Select(line => long.Parse(line[(line.LastIndexOf(' ') + 1)..].Trim(), CultureInfo.InvariantCulture)),
    ];

    /// <summary>The value of the first line with a suffix and optional label.</summary>
    private long Line(string suffix, string? containing)
    {
        var match = api.Body
            .Split('\n')
            .First(line =>
                line.StartsWith($"flowdeck_steps_duration_seconds{suffix}", StringComparison.Ordinal)
                && (containing is null || line.Contains(containing, StringComparison.Ordinal)));

        return long.Parse(match[(match.LastIndexOf(' ') + 1)..].Trim(), CultureInfo.InvariantCulture);
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
