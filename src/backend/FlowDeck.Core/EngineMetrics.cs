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
