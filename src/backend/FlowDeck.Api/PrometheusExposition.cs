using System.Diagnostics.Metrics;
using System.Globalization;
using System.Text;
using FlowDeck.Core;

namespace FlowDeck.Api;

/// <summary>
/// Renders FlowDeck's counters in Prometheus text exposition format.
/// </summary>
/// <remarks>
/// <b>Hand-rolled rather than exported by a package.</b>
/// <c>OpenTelemetry.Exporter.Prometheus.AspNetCore</c> has never shipped a
/// stable version - every release since 1.5 is <c>-beta.1</c> - and NFR-5's
/// supply-chain posture is not one to spend on a prerelease that reaches the
/// deployed image. A <see cref="MeterListener"/> over FlowDeck's own meter and a
/// text format that has not changed in years is a small thing to own
/// (ADR-0025 decision 4).
///
/// <para>
/// This is the opposite call from OTLP, which stays a package: re-implementing a
/// wire protocol would be the reckless kind of hand-rolling.
/// </para>
///
/// <para>
/// Deliberately <b>not</b> a general exporter. It knows about counters because
/// counters are all FlowDeck emits (ADR-0025 decision 2). A histogram would need
/// buckets and a different rendering, and inventing that before there is one to
/// render would be guessing at the shape.
/// </para>
/// </remarks>
public sealed class PrometheusExposition : IDisposable
{
    private readonly MeterListener listener;
    private readonly Lock gate = new();

    /// <summary>Running totals, by instrument then by label set.</summary>
    private readonly Dictionary<string, Series> series = new(StringComparer.Ordinal);

    public PrometheusExposition(EngineMetrics metrics)
    {
        ArgumentNullException.ThrowIfNull(metrics);

        this.listener = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                // This host's metrics, by identity. A MeterListener is
                // process-wide and every EngineMetrics publishes a meter of the
                // same name, so matching on the name would aggregate a second
                // engine's measurements into this one's series - which reads as
                // an inflated count rather than as a bug.
                if (!metrics.Owns(instrument))
                {
                    return;
                }

                lock (this.gate)
                {
                    // Registered on publication, not on first measurement, so
                    // the endpoint names every counter before anything has run.
                    // A scrape returning nothing at all is indistinguishable
                    // from a broken endpoint, and the first thing an operator
                    // does after deploying is scrape it.
                    this.series[instrument.Name] = new Series(instrument.Description ?? string.Empty);
                }

                listener.EnableMeasurementEvents(instrument);
            },
        };

        this.listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            var labels = Labels(tags);

            lock (this.gate)
            {
                if (!this.series.TryGetValue(instrument.Name, out var found))
                {
                    return;
                }

                found.Totals[labels] = found.Totals.GetValueOrDefault(labels) + value;
            }
        });

        this.listener.Start();
    }

    /// <summary>The content type a Prometheus scrape expects.</summary>
    public const string ContentType = "text/plain; version=0.0.4; charset=utf-8";

    /// <summary>Renders everything recorded so far.</summary>
    public string Render()
    {
        var builder = new StringBuilder();

        lock (this.gate)
        {
            foreach (var (instrument, recorded) in this.series.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                // Prometheus counters end in _total by convention, and dots are
                // not legal in a metric name.
                var name = $"{instrument.Replace('.', '_')}_total";

                builder.Append("# HELP ").Append(name).Append(' ').AppendLine(recorded.Help);
                builder.Append("# TYPE ").Append(name).AppendLine(" counter");

                foreach (var (labels, total) in recorded.Totals.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                {
                    builder
                        .Append(name)
                        .Append(labels)
                        .Append(' ')
                        .AppendLine(total.ToString(CultureInfo.InvariantCulture));
                }
            }
        }

        return builder.ToString();
    }

    public void Dispose() => this.listener.Dispose();

    /// <summary>
    /// Renders a measurement's tags as a Prometheus label set.
    /// </summary>
    /// <remarks>
    /// Sorted, so one label set is one series however the tags were ordered at
    /// the call site. Unsorted, the same measurement recorded two ways would
    /// appear as two series that never add up.
    /// </remarks>
    private static string Labels(ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        if (tags.Length == 0)
        {
            return string.Empty;
        }

        var pairs = new List<string>(tags.Length);

        foreach (var tag in tags)
        {
            var name = tag.Key.Replace('.', '_');
            pairs.Add($"{name}=\"{Escape(tag.Value?.ToString() ?? string.Empty)}\"");
        }

        pairs.Sort(StringComparer.Ordinal);

        return $"{{{string.Join(',', pairs)}}}";
    }

    /// <summary>
    /// Escapes a label value.
    /// </summary>
    /// <remarks>
    /// Not cosmetic. Label values here carry the definition id, which is
    /// author-chosen: a quote or a backslash in one would produce a scrape
    /// response the collector cannot parse, taking out every metric on the
    /// endpoint rather than the one series.
    /// </remarks>
    private static string Escape(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("\"", "\\\"", StringComparison.Ordinal)
        .Replace("\n", "\\n", StringComparison.Ordinal);

    private sealed class Series(string help)
    {
        public string Help { get; } = help;

        public Dictionary<string, long> Totals { get; } = new(StringComparer.Ordinal);
    }
}
