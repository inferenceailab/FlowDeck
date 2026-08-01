using System.Globalization;
using System.Text.RegularExpressions;
using FlowDeck.Core;
using FlowDeck.Specs.Support;
using Reqnroll;

namespace FlowDeck.Specs.Steps;

/// <summary>
/// Binds Features/Observability/Documentation.feature.
/// </summary>
/// <remarks>
/// Two halves that belong together. The prose is asserted against the file,
/// following #108, #123, #150 and #167 - and the last two scenarios assert the
/// claim the prose makes, because a data boundary nothing tests is a comment.
/// </remarks>
[Binding]
[Scope(Feature = "What FlowDeck emits is documented and bounded")]
public sealed partial class ObservabilityDocumentationSteps(EngineContext world)
{
    /// <summary>
    /// The value that must not escape.
    /// </summary>
    /// <remarks>
    /// Distinctive enough that a substring search cannot match it by accident,
    /// and shaped like the thing this rule exists for.
    /// </remarks>
    private const string Secret = "sk-live-9f2b7c41-canary-must-not-escape";

    private string guide = string.Empty;

    private static string ReadGuide()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "docs")))
        {
            directory = directory.Parent;
        }

        var text = directory is null
            ? throw new InvalidOperationException("Could not locate the docs directory.")
            : File.ReadAllText(Path.Combine(directory.FullName, "docs", "guides", "observing-flowdeck.md"));

        // Whitespace collapsed, so an assertion about a sentence is about the
        // sentence rather than about where it happened to wrap. Prose here is
        // hard-wrapped at 80 columns, and re-wrapping a paragraph is a cosmetic
        // edit that must not fail a test about what the paragraph says.
        return WhitespaceRuns().Replace(text, " ");
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRuns();

    [Given("the observability guide")]
    public void GivenTheGuide() => this.guide = ReadGuide();

    [Given("a workflow whose steps read and write a secret through every path")]
    public void GivenASecretThroughEveryPath() =>

        // Written, read back, carried across a branch and retried. Every path
        // the engine has for touching workflow data, because the boundary has
        // to hold on all of them rather than on the one a scenario picked.
        world.Declare("order-fulfilment", 1, builder => builder
            .AddStep("capture", () => new SpecSteps.Writing("api-key", Secret))
            .AddStep("retry-once", () => new FailsOnce(world.Log, "retry-once"), RetryPolicy.FixedDelay(2, TimeSpan.Zero))
            .Fork(
                left => left.AddStep("use-left", () => new Reading(world.Captured, "api-key")),
                right => right.AddStep("use-right", () => new Reading(world.Captured, "api-key"))));

    [Given("a workflow that puts a secret in workflow data and then fails")]
    public void GivenASecretAndAFailure() =>
        world.Declare("order-fulfilment", 1, builder => builder
            .AddStep("capture", () => new SpecSteps.Writing("api-key", Secret))
                .WithCompensation(() => new SpecSteps.Recording(world.Log, "undo-capture"))
            .AddStep("charge", () => new SpecSteps.Throwing(world.Log, "charge")));

    [When("it runs to completion with logging, metrics and tracing all captured")]
    public async Task WhenItRuns() =>
        world.Instance = await world.Engine().StartAsync("order-fulfilment", 1);

    [When("it runs with logging, metrics and tracing all captured")]
    public async Task WhenItRunsAndFails() =>
        world.Instance = await world.Engine().StartAsync("order-fulfilment", 1);

    [Then("it names every metric with its type and labels")]
    public void ThenItNamesEveryMetric()
    {
        foreach (var outcome in new[] { "started", "completed", "failed", "cancelled", "compensated" })
        {
            Assert.Contains($"flowdeck_instances_{outcome}_total", this.guide, StringComparison.Ordinal);
        }

        Assert.Contains("definition_id", this.guide, StringComparison.Ordinal);
        Assert.Contains("definition_version", this.guide, StringComparison.Ordinal);

        // The label that separates a clean rollback from one that needs a
        // human. Absent from the guide, an operator alerts on the wrong thing.
        Assert.Contains("CompensationFailed", this.guide, StringComparison.Ordinal);
    }

    [Then("it names both spans and the attributes they carry")]
    public void ThenItNamesTheSpans()
    {
        Assert.Contains(EngineTracing.InstanceSpan, this.guide, StringComparison.Ordinal);
        Assert.Contains(EngineTracing.StepSpan, this.guide, StringComparison.Ordinal);
        Assert.Contains("workflow.step.attempt", this.guide, StringComparison.Ordinal);
        Assert.Contains("error.type", this.guide, StringComparison.Ordinal);
    }

    [Then("it names every log event with its level")]
    public void ThenItNamesEveryLogEvent()
    {
        // Every event the engine can emit. A table missing one is how an
        // operator writes an alert rule that never fires.
        foreach (var name in new[]
        {
            "InstanceStarted", "InstanceResumed", "InstanceSuspended", "InstanceCompleted",
            "InstanceCancelled", "InstanceFailed", "InstanceCompensated",
            "StepStarted", "StepFinished", "StepRetrying", "StepRolledBack", "RollbackFailed",
        })
        {
            Assert.Contains(name, this.guide, StringComparison.Ordinal);
        }

        Assert.Contains("Debug", this.guide, StringComparison.Ordinal);
        Assert.Contains("Warning", this.guide, StringComparison.Ordinal);
    }

    [Then("it shows how to scrape metrics and how to enable OTLP")]
    public void ThenItShowsHowToWireItUp()
    {
        Assert.Contains("/metrics", this.guide, StringComparison.Ordinal);
        Assert.Contains("OTEL_EXPORTER_OTLP_ENDPOINT", this.guide, StringComparison.Ordinal);

        // That leaving it unset switches tracing off entirely, rather than
        // exporting into the void.
        Assert.Contains("no pipeline is built", this.guide, StringComparison.OrdinalIgnoreCase);
    }

    [Then("it states that no workflow data reaches a log, span or metric")]
    public void ThenItStatesTheBoundary() =>
        Assert.Contains("No workflow data is emitted", this.guide, StringComparison.OrdinalIgnoreCase);

    [Then("it says a span is a leakier place than the store, and why")]
    public void ThenItSaysWhy()
    {
        // The reason, not only the rule. An operator who does not know why will
        // eventually decide their case is the exception.
        Assert.Contains("third-party", this.guide, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("never had database access", this.guide, StringComparison.OrdinalIgnoreCase);
    }

    [Then("it says that keys were rejected along with values")]
    public void ThenItSaysKeysToo() =>

        // The half a reader assumes is allowed. A key name is author-chosen and
        // can disclose on its own.
        Assert.Contains("Keys were considered and rejected", this.guide, StringComparison.OrdinalIgnoreCase);

    [Then("it names step duration and cluster health as deliberately absent")]
    public void ThenItNamesWhatIsAbsent()
    {
        Assert.Contains("deliberately not measured", this.guide, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Step duration", this.guide, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Cluster health", this.guide, StringComparison.OrdinalIgnoreCase);
    }

    [Then("the secret appears in no log entry")]
    public void ThenNoLogEntryLeaks()
    {
        Assert.NotEmpty(world.Logger.Entries);

        foreach (var entry in world.Logger.Entries)
        {
            AssertClean(entry.Message);

            foreach (var field in entry.State.Concat(entry.Scope))
            {
                AssertClean(field.Key);
                AssertClean(Text(field.Value));
            }
        }
    }

    [Then("it appears in no span")]
    public void ThenNoSpanLeaks()
    {
        Assert.NotEmpty(world.Spans.All);

        foreach (var span in world.Spans.All)
        {
            AssertClean(span.DisplayName);

            foreach (var tag in span.TagObjects)
            {
                AssertClean(tag.Key);
                AssertClean(Text(tag.Value));
            }

            AssertClean(span.StatusDescription ?? string.Empty);
        }
    }

    [Then("it appears in no measurement")]
    public void ThenNoMeasurementLeaks()
    {
        Assert.NotEmpty(world.Metrics.All);

        foreach (var measurement in world.Metrics.All)
        {
            AssertClean(measurement.Instrument);

            foreach (var tag in measurement.Tags)
            {
                AssertClean(tag.Key);
                AssertClean(Text(tag.Value));
            }
        }
    }

    [Then("the secret appears in nothing the engine emitted")]
    public void ThenNothingLeaksOnFailure()
    {
        // The failure path is where a leak is most likely: an exception message
        // is the natural place for a step to interpolate what it was working
        // on, and the engine copies that message into a log field and a span.
        this.ThenNoLogEntryLeaks();
        this.ThenNoSpanLeaks();
        this.ThenNoMeasurementLeaks();
    }

    [Then("the failure was still reported")]
    public void ThenTheFailureWasReported()
    {
        // Guards the assertions above from passing by emitting nothing at all.
        Assert.Equal(InstanceStatus.Compensated, world.Instance!.Status);
        Assert.NotEmpty(world.Logger.Named("InstanceFailed"));
        Assert.Equal(1, world.Metrics.Total("compensated"));
    }

    private static string Text(object? value) =>
        value switch
        {
            null => string.Empty,
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? string.Empty,
        };

    private static void AssertClean(string text) =>
        Assert.DoesNotContain(Secret, text, StringComparison.Ordinal);

    /// <summary>Fails its first execution, so a retry path is exercised.</summary>
    private sealed class FailsOnce(List<string> log, string name) : IStep
    {
        public ValueTask<Outcome> ExecuteAsync(IStepContext context, CancellationToken cancellationToken = default)
        {
            var previous = log.Count(entry => entry == name);
            log.Add(name);

            return previous < 1
                ? throw new InvalidOperationException($"{name} transient")
                : ValueTask.FromResult(Outcome.Next);
        }
    }

    /// <summary>Reads the secret back out, so a read path is exercised too.</summary>
    private sealed class Reading(Dictionary<string, object?> captured, string key) : IStep
    {
        public ValueTask<Outcome> ExecuteAsync(IStepContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);

            captured[key] = context.Data.TryGet<string>(key, out var value) ? value : null;

            return ValueTask.FromResult(Outcome.Next);
        }
    }
}
