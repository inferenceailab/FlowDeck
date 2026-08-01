using System.Diagnostics.Metrics;
using FlowDeck.Core;

namespace FlowDeck.Specs.Support;

/// <summary>One measurement the engine recorded.</summary>
public sealed record Measurement(string Instrument, long Value, IReadOnlyDictionary<string, object?> Tags);

/// <summary>
/// Listens to one <see cref="EngineMetrics"/> and keeps what it recorded.
/// </summary>
/// <remarks>
/// Filtered to a specific meter <b>instance</b>, not to the meter name.
/// Scenarios in different feature classes run in parallel and every engine in
/// the process publishes a meter called <c>FlowDeck.Core</c>, so a listener
/// matching on the name would count another scenario's instances and fail
/// intermittently - the worst kind of failure to leave in a suite.
/// </remarks>
public sealed class MeterCapture : IDisposable
{
    private readonly List<Measurement> measurements = [];
    private readonly Lock gate = new();
    private readonly MeterListener listener;

    public MeterCapture()
    {
        this.Metrics = new EngineMetrics();

        this.listener = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (ReferenceEquals(instrument.Meter, this.Metrics.Meter))
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            },
        };

        this.listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            var recorded = new Dictionary<string, object?>(StringComparer.Ordinal);

            foreach (var tag in tags)
            {
                recorded[tag.Key] = tag.Value;
            }

            lock (this.gate)
            {
                this.measurements.Add(new Measurement(instrument.Name, value, recorded));
            }
        });

        this.listener.Start();
    }

    /// <summary>The metrics this capture listens to, for handing to an engine.</summary>
    public EngineMetrics Metrics { get; }

    /// <summary>Everything recorded so far.</summary>
    public IReadOnlyList<Measurement> All
    {
        get
        {
            lock (this.gate)
            {
                return [.. this.measurements];
            }
        }
    }

    /// <summary>
    /// Measurements of one instrument, by its short name.
    /// </summary>
    /// <remarks>
    /// Takes <c>completed</c> rather than <c>flowdeck.instances.completed</c>,
    /// so a scenario reads as the outcome it is about. The full names are
    /// asserted once, in the scenario whose subject they are.
    /// </remarks>
    public IReadOnlyList<Measurement> Of(string outcome) =>
    [
        .. this.All.Where(measurement => string.Equals(
            measurement.Instrument,
            $"flowdeck.instances.{outcome}",
            StringComparison.Ordinal)),
    ];

    /// <summary>What one counter totals.</summary>
    public long Total(string outcome) => this.Of(outcome).Sum(measurement => measurement.Value);

    public void Dispose()
    {
        this.listener.Dispose();
        this.Metrics.Dispose();
    }
}
