using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace FlowDeck.Core;

/// <summary>
/// What the engine counts.
/// </summary>
/// <remarks>
/// <see cref="Meter"/> is in the BCL, so this costs no package reference.
/// OpenTelemetry is one consumer of it and the host's choice, not the engine's
/// (ADR-0025 decision 1).
///
/// <para>
/// <b>Lifecycle counters only</b>, deliberately. Step duration, retry counts and
/// cluster gauges were each considered and deferred, with the reasons recorded
/// in ADR-0025 decision 2 rather than left as things nobody thought of.
/// </para>
///
/// <para>
/// Every tag here is engine-assigned or author-declared metadata. No workflow
/// data reaches a tag - not a value, not a key, not a count of keys - which is
/// the boundary ADR-0025 decision 3 draws and a scenario asserts. Metric tags
/// are the worst place of all to leak into: they are cardinality, so a value
/// that varies per instance would also destroy the backend it reached.
/// </para>
/// </remarks>
public sealed class EngineMetrics : IDisposable
{
    /// <summary>
    /// The meter name a host enables to receive FlowDeck's metrics.
    /// </summary>
    /// <remarks>
    /// The assembly name, which is the convention every collector expects, so a
    /// host names something it already knows rather than an invented string.
    /// </remarks>
    public const string MeterName = "FlowDeck.Core";

    private readonly Meter meter;
    private readonly Counter<long> started;
    private readonly Counter<long> completed;
    private readonly Counter<long> failed;
    private readonly Counter<long> cancelled;
    private readonly Counter<long> compensated;
    private readonly Histogram<double> stepDuration;
    private readonly Counter<long> retries;
    private readonly Counter<long> compensations;

    public EngineMetrics()
    {
        this.meter = new Meter(MeterName);

        this.started = this.meter.CreateCounter<long>(
            "flowdeck.instances.started",
            unit: "{instance}",
            description: "Instances started.");

        this.completed = this.meter.CreateCounter<long>(
            "flowdeck.instances.completed",
            unit: "{instance}",
            description: "Instances that ran every step and finished.");

        this.failed = this.meter.CreateCounter<long>(
            "flowdeck.instances.failed",
            unit: "{instance}",
            description: "Instances that failed and were not rolled back.");

        this.cancelled = this.meter.CreateCounter<long>(
            "flowdeck.instances.cancelled",
            unit: "{instance}",
            description: "Instances an operator stopped.");

        this.compensated = this.meter.CreateCounter<long>(
            "flowdeck.instances.compensated",
            unit: "{instance}",
            description: "Instances that failed and rolled back, tagged with how the rollback ended.");

        this.stepDuration = this.meter.CreateHistogram<double>(
            "flowdeck.steps.duration",

            // Seconds, not milliseconds. Prometheus and the OpenTelemetry
            // semantic conventions both use base units, and a histogram named
            // in the wrong one is a dashboard nobody can compare against
            // anything else. The engine's *logs* stay in milliseconds, where a
            // human reads them.
            unit: "s",
            description: "How long each step execution took.",
            advice: new InstrumentAdvice<double> { HistogramBucketBoundaries = BucketBoundaries });

        this.retries = this.meter.CreateCounter<long>(
            "flowdeck.steps.retried",
            unit: "{attempt}",
            description: "Step attempts beyond the first.");

        this.compensations = this.meter.CreateCounter<long>(
            "flowdeck.compensations",
            unit: "{action}",
            description: "Compensating actions run, tagged with whether each undid its step.");
    }

    /// <summary>
    /// The bucket edges for <c>flowdeck.steps.duration</c>, in seconds.
    /// </summary>
    /// <remarks>
    /// Published so the scrape endpoint renders the same edges the meter was
    /// told to use. Two lists would drift, and the drift would be silent: the
    /// histogram would still render, with buckets that did not match what was
    /// measured.
    ///
    /// <para>
    /// Chosen for the range a step actually occupies: sub-millisecond for an
    /// in-memory step, seconds for an HTTP call, and a long tail because a step
    /// waiting on something slow is exactly the case an operator is looking
    /// for.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<double> BucketBoundaries { get; } =
        [0.001, 0.005, 0.01, 0.05, 0.1, 0.5, 1, 5, 10, 30];

    /// <summary>
    /// Records how long one step execution took.
    /// </summary>
    /// <remarks>
    /// Per execution, not per step, so a step retried three times contributes
    /// three observations. Averaging them into one would hide the thing worth
    /// seeing - that it took three goes.
    ///
    /// <para>
    /// Tagged with the outcome, because a step that fails fast and a step that
    /// succeeds slowly are different problems and their durations should not
    /// share a series.
    /// </para>
    /// </remarks>
    public void StepFinished(WorkflowInstance instance, string stepName, StepStatus status, double seconds)
    {
        ArgumentNullException.ThrowIfNull(instance);

        this.stepDuration.Record(
            seconds,
            [.. Tags(instance), new("step.name", stepName), new("outcome", status.ToString())]);
    }

    /// <summary>
    /// Counts a retry that is about to be attempted.
    /// </summary>
    /// <remarks>
    /// Attempts <b>beyond the first</b>, so an ordinary run contributes nothing
    /// and the counter reads as "how much trouble is this having" rather than
    /// "how much work is it doing". A step that never fails would otherwise be
    /// indistinguishable from one retried on every execution.
    ///
    /// <para>
    /// Tagged by step name, which is where the answer is: "something is
    /// retrying" is a fact an operator already has from the failure rate, and
    /// <i>which step</i> is the part they cannot get anywhere else without
    /// reading history per instance.
    /// </para>
    /// </remarks>
    public void StepRetried(WorkflowInstance instance, string stepName)
    {
        ArgumentNullException.ThrowIfNull(instance);

        this.retries.Add(1, [.. Tags(instance), new("step.name", stepName)]);
    }

    /// <summary>
    /// Counts one compensating action, whether or not it undid its step.
    /// </summary>
    /// <remarks>
    /// Per action, where <c>flowdeck.instances.compensated</c> is per instance.
    /// The instance counter says a rollback happened and how it ended; this says
    /// how much of it succeeded, which for a partial rollback - the outcome that
    /// always needs a human (ADR-0021) - is the difference between "one undo
    /// failed" and "nine did".
    /// </remarks>
    public void Compensated(WorkflowInstance instance, string stepName, bool undone)
    {
        ArgumentNullException.ThrowIfNull(instance);

        this.compensations.Add(
            1,
            [.. Tags(instance), new("step.name", stepName), new("outcome", undone ? "undone" : "failed")]);
    }

    /// <summary>
    /// The default every engine shares when a host supplies none.
    /// </summary>
    /// <remarks>
    /// Shared rather than one per engine, so a host that constructs two engines
    /// does not publish two meters of the same name and leave an operator
    /// summing them. Held for the life of the process, which is what a meter's
    /// lifetime is.
    /// </remarks>
    internal static EngineMetrics Default { get; } = new();

    /// <summary>
    /// The meter these instruments live on.
    /// </summary>
    /// <remarks>
    /// <b>Internal.</b> A test needs it to listen to one engine's measurements
    /// rather than to every engine in the process, which matters because
    /// scenarios run in parallel and every engine publishes a meter of the same
    /// name. Public it would invite a consumer to create instruments on
    /// FlowDeck's meter, and what FlowDeck emits is a contract this project
    /// keeps (ADR-0025 decision 2) rather than one callers extend.
    /// </remarks>
    internal Meter Meter => this.meter;

    /// <summary>
    /// Whether an instrument belongs to <b>these</b> metrics.
    /// </summary>
    /// <remarks>
    /// A <see cref="MeterListener"/> is process-wide, and every
    /// <see cref="EngineMetrics"/> in a process publishes a meter of the same
    /// name - a second engine, or another test's. Matching on the name
    /// therefore silently aggregates strangers' measurements, which reads as an
    /// inflated count rather than as a bug.
    ///
    /// <para>
    /// Exposed as a question rather than by publishing the meter, so a caller
    /// can filter without being handed something to create instruments on.
    /// </para>
    /// </remarks>
    public bool Owns(Instrument instrument)
    {
        ArgumentNullException.ThrowIfNull(instrument);

        return ReferenceEquals(instrument.Meter, this.meter);
    }

    /// <summary>Counts an instance that has just been created.</summary>
    public void InstanceStarted(WorkflowInstance instance)
    {
        ArgumentNullException.ThrowIfNull(instance);

        this.started.Add(1, Tags(instance));
    }

    /// <summary>
    /// Counts an instance that has reached a state it will not leave.
    /// </summary>
    /// <remarks>
    /// One method rather than five, taking the status the engine settled on.
    /// Five call sites would be five chances to count an outcome under the
    /// wrong name, and the mapping is the thing worth having in one place.
    ///
    /// <para>
    /// A rollback is counted as <c>compensated</c> whether or not it managed
    /// everything, with an <c>outcome</c> tag saying which. Folding a partial
    /// rollback into the failure count would hide the one outcome that always
    /// needs a human (ADR-0021); giving it a sixth counter would make "how many
    /// rolled back" a sum an operator has to remember to do.
    /// </para>
    /// </remarks>
    public void InstanceSettled(WorkflowInstance instance)
    {
        ArgumentNullException.ThrowIfNull(instance);

        var counter = instance.Status switch
        {
            InstanceStatus.Completed => this.completed,
            InstanceStatus.Failed => this.failed,
            InstanceStatus.Cancelled => this.cancelled,
            InstanceStatus.Compensated or InstanceStatus.CompensationFailed => this.compensated,

            // Suspended is not terminal, and nothing else is reachable here.
            // Counting it would report an instance as finished while it waits.
            _ => null,
        };

        if (counter is null)
        {
            return;
        }

        if (counter == this.compensated)
        {
            counter.Add(1, [.. Tags(instance), new("outcome", instance.Status.ToString())]);
            return;
        }

        counter.Add(1, Tags(instance));
    }

    public void Dispose() => this.meter.Dispose();

    /// <summary>
    /// What every measurement is tagged with.
    /// </summary>
    /// <remarks>
    /// Id and version, and nothing per-instance. A tag whose value varies per
    /// instance would give a time-series backend one series per run, which is
    /// how a metrics pipeline is brought down rather than how it is used - the
    /// instance id belongs on a log and a span, where it already is.
    /// </remarks>
    private static TagList Tags(WorkflowInstance instance) =>
    [
        new("definition.id", instance.DefinitionId),
        new("definition.version", instance.DefinitionVersion),
    ];
}
