using System.Diagnostics;
using FlowDeck.Core;
using FlowDeck.Specs.Support;
using Reqnroll;

namespace FlowDeck.Specs.Steps;

/// <summary>
/// Binds Features/Observability/Tracing.feature.
/// </summary>
[Binding]
[Scope(Feature = "Instance and step tracing")]
public sealed class TracingSteps(EngineContext world) : IDisposable
{
    private const string Secret = "sk-live-do-not-emit-this";

    /// <summary>
    /// A source standing in for whatever opened a span before FlowDeck ran.
    /// </summary>
    /// <remarks>
    /// A caller's span, not FlowDeck's, so the scenario proves the engine
    /// continues a trace it did not start - which over HTTP is the request.
    /// Listened to separately, because the engine's capture deliberately
    /// ignores sources that are not its own.
    /// </remarks>
    private readonly ActivitySource caller = new("FlowDeck.Specs.Caller");
    private ActivityListener? callerListener;
    private ActivityTraceId callerTrace;
    private ActivityTraceId unrelatedTrace;

    [Given("a definition \"(.*)\" version (.*) with one step")]
    public void GivenADefinition(string id, int version) =>
        world.Declare(id, version, builder =>
            builder.AddStep("work", () => new SpecSteps.Recording(world.Log, "work")));

    [Given("a definition with steps \"(.*)\" and \"(.*)\"")]
    public void GivenTwoSteps(string first, string second) =>
        world.Declare("order-fulfilment", 3, builder => builder
            .AddStep(first, () => new SpecSteps.Recording(world.Log, first))
            .AddStep(second, () => new SpecSteps.Recording(world.Log, second)));

    [Given("a step that fails twice and then succeeds")]
    public void GivenAStepThatFailsTwice() =>
        world.Declare("order-fulfilment", 3, builder => builder
            .AddStep(
                "charge",
                () => new FailsThenSucceeds(world.Log, "charge", failures: 2),
                RetryPolicy.FixedDelay(3, TimeSpan.Zero)));

    [Given("a definition whose only step throws")]
    public void GivenAThrowingStep() =>
        world.Declare("order-fulfilment", 3, builder =>
            builder.AddStep("charge", () => new SpecSteps.Throwing(world.Log, "charge")));

    [Given("a definition that forks into steps \"(.*)\" and \"(.*)\"")]
    public void GivenAFork(string left, string right) =>
        world.Declare("order-fulfilment", 3, builder => builder
            .AddStep("split", () => new SpecSteps.Recording(world.Log, "split"))
            .Fork(
                a => a.AddStep(left, () => new SpecSteps.Recording(world.Log, left)),
                b => b.AddStep(right, () => new SpecSteps.Recording(world.Log, right))));

    [Given("a definition whose step writes a secret into workflow data")]
    public void GivenAStepWritingASecret() =>
        world.Declare("order-fulfilment", 3, builder =>
            builder.AddStep("capture", () => new SpecSteps.Writing("api-key", Secret)));

    [Given("a suspended instance inside a caller's span")]
    public async Task GivenASuspendedInstanceInsideACallerSpan()
    {
        world.Declare("order-fulfilment", 3, builder =>
            builder.AddStep("wait", () => new SpecSteps.Suspending(world.Log, "wait")));

        this.ListenToCaller();

        using var span = this.caller.StartActivity("caller");

        this.callerTrace = Activity.Current!.TraceId;
        world.Instance = await world.Engine().StartAsync("order-fulfilment", 3);
    }

    [When("an instance is started")]
    public async Task WhenAnInstanceIsStarted() =>
        world.Instance = await world.Engine().StartAsync("order-fulfilment", 3);

    [When("an instance is started inside a caller's span")]
    public async Task WhenStartedInsideACallerSpan()
    {
        this.ListenToCaller();

        using var span = this.caller.StartActivity("caller");

        this.callerTrace = Activity.Current!.TraceId;
        world.Instance = await world.Engine().StartAsync("order-fulfilment", 3);
    }

    [When("it is resumed inside an unrelated span")]
    public async Task WhenResumedInsideAnUnrelatedSpan()
    {
        using var span = this.caller.StartActivity("dispatcher-poll");

        this.unrelatedTrace = Activity.Current!.TraceId;
        await world.Engine().ResumeAsync(world.Instance!.Id);
    }

    [Then("a workflow.instance span records the instance id, definition id and version")]
    public void ThenTheInstanceSpanCarriesIdentity()
    {
        var span = world.Spans.Instance;

        Assert.Equal(world.Instance!.Id, ActivityCapture.Tag(span, "workflow.instance.id"));
        Assert.Equal("order-fulfilment", ActivityCapture.Tag(span, "workflow.definition.id"));
        Assert.Equal(3, ActivityCapture.Tag(span, "workflow.definition.version"));
    }

    [Then("each step has a workflow.step span whose parent is the instance span")]
    public void ThenStepsAreChildrenOfTheInstance()
    {
        Assert.Equal(
            ["reserve", "charge"],
            world.Spans.Steps.Select(span => ActivityCapture.Tag(span, "workflow.step.name")));

        // The parent, not merely the same trace. Two spans in one trace can be
        // siblings, and the shape is what makes a step attributable to the run
        // that caused it.
        Assert.All(
            world.Spans.Steps,
            span => Assert.Equal(world.Spans.Instance.SpanId, span.ParentSpanId));
    }

    [Then("there are three step spans, numbered by attempt")]
    public void ThenARetriedStepHasASpanPerAttempt() =>

        // Per attempt, not per step. One span averaging three attempts hides
        // that the step is failing at all, which is the thing worth seeing.
        Assert.Equal(
            [1, 2, 3],
            world.Spans.Steps.Select(span => ActivityCapture.Tag(span, "workflow.step.attempt")));

    [Then("the two that failed are marked as errors")]
    public void ThenTheFailedAttemptsAreErrors()
    {
        Assert.Equal(
            [ActivityStatusCode.Error, ActivityStatusCode.Error, ActivityStatusCode.Unset],
            world.Spans.Steps.Select(span => span.Status));

        // The run recovered, so nothing above it failed. An instance span
        // marked by a transient attempt would page someone for a workflow that
        // worked.
        Assert.Equal(ActivityStatusCode.Unset, world.Spans.Instance.Status);
    }

    [Then("that step's span is marked an error carrying the exception type")]
    public void ThenTheStepSpanIsAnError()
    {
        var span = world.Spans.Steps.Single();

        Assert.Equal(ActivityStatusCode.Error, span.Status);
        Assert.Equal(nameof(InvalidOperationException), ActivityCapture.Tag(span, "error.type"));
    }

    [Then("the instance span is marked an error too")]
    public void ThenTheInstanceSpanIsAnError() =>
        Assert.Equal(ActivityStatusCode.Error, world.Spans.Instance.Status);

    [Then("the instance span belongs to the caller's trace")]
    public void ThenTheInstanceContinuesTheCallersTrace()
    {
        Assert.Equal(this.callerTrace, world.Spans.Instance.TraceId);

        // And hangs off it rather than merely sharing an id.
        Assert.NotEqual(default, world.Spans.Instance.ParentSpanId);
    }

    [Then("the resumed instance span belongs to neither trace")]
    public void ThenTheResumedInstanceIsItsOwnTrace()
    {
        // Two instance spans: the one that suspended and the one that resumed.
        var instances = world.Spans.Named(EngineTracing.InstanceSpan);
        var resumed = instances[^1];

        // Not the poll's trace. A dispatcher recovering abandoned work did not
        // cause that work, and a trace claiming the wrong cause is worse than
        // two traces.
        Assert.NotEqual(this.unrelatedTrace, resumed.TraceId);

        // And not the original caller's either. That trace ended when the
        // request that started the instance returned.
        Assert.NotEqual(this.callerTrace, resumed.TraceId);
        Assert.Equal(default, resumed.ParentSpanId);
    }

    [Then("both branch step spans have the instance span as their parent")]
    public void ThenBranchStepsParentToTheInstance()
    {
        // Async-local ambient state is inherited at the fork, so each arm sees
        // the instance span. Getting this wrong parents one arm's spans to
        // whichever sibling happened to start first, which is invisible until
        // someone reads a trace.
        Assert.All(
            world.Spans.Steps,
            span => Assert.Equal(world.Spans.Instance.SpanId, span.ParentSpanId));

        Assert.Equal(3, world.Spans.Steps.Count);
    }

    [Then("each carries the branch it ran on")]
    public void ThenBranchStepsCarryTheirBranch()
    {
        Assert.Equal("branch-1", Branch("left"));
        Assert.Equal("branch-2", Branch("right"));

        // Absent on the top-level step, not empty. A step that never forked has
        // no branch to name.
        Assert.Null(Branch("split"));

        object? Branch(string stepName) => ActivityCapture.Tag(
            world.Spans.Steps.Single(span =>
                Equals(ActivityCapture.Tag(span, "workflow.step.name"), stepName)),
            "workflow.branch");
    }

    [Then("no attribute on any span contains that secret")]
    public void ThenNoAttributeLeaksTheSecret()
    {
        Assert.NotEmpty(world.Spans.All);

        Assert.All(world.Spans.All, span =>
            Assert.All(span.TagObjects, tag =>
            {
                Assert.DoesNotContain(Secret, tag.Key, StringComparison.Ordinal);
                Assert.DoesNotContain(Secret, tag.Value?.ToString() ?? string.Empty, StringComparison.Ordinal);
            }));
    }

    public void Dispose()
    {
        this.callerListener?.Dispose();
        this.caller.Dispose();
    }

    /// <summary>
    /// Makes the stand-in caller's spans real.
    /// </summary>
    /// <remarks>
    /// Without a listener <c>StartActivity</c> returns null, there is no ambient
    /// activity, and the scenario would assert that FlowDeck failed to continue
    /// a trace that never existed.
    /// </remarks>
    private void ListenToCaller()
    {
        this.callerListener = new ActivityListener
        {
            ShouldListenTo = source => ReferenceEquals(source, this.caller),
            Sample = (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
        };

        ActivitySource.AddActivityListener(this.callerListener);
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
