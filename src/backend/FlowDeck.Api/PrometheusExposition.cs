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
/// Deliberately <b>not</b> a general exporter. It renders the two instrument
/// shapes FlowDeck emits - counters and one duration histogram - and nothing
/// else. #189 declined to invent histogram rendering before there was a
/// histogram to render; #198 added one, so it exists now rather than
/// speculatively.
/// </para>
/// </remarks>
public sealed class PrometheusExposition : IDisposable
{
    private readonly MeterListener listener;
    private readonly Lock gate = new();

    /// <summary>Running totals, by instrument then by label set.</summary>
    private readonly Dictionary<string, Series> series = new(StringComparer.Ordinal);

    /// <summary>Bucketed observations, by instrument then by label set.</summary>
    private readonly Dictionary<string, Distribution> distributions = new(StringComparer.Ordinal);

    /// <summary>Latest gauge readings, by instrument then by label set.</summary>
    private readonly Dictionary<string, Series> gauges = new(StringComparer.Ordinal);

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
                    // the endpoint names every instrument before anything has
                    // run. A scrape returning nothing at all is
                    // indistinguishable from a broken endpoint, and the first
                    // thing an operator does after deploying is scrape it.
                    if (instrument is Histogram<double>)
                    {
                        this.distributions[instrument.Name] =
                            new Distribution(instrument.Description ?? string.Empty);
                    }
                    else if (instrument.IsObservable)
                    {
                        this.gauges[instrument.Name] = new Series(instrument.Description ?? string.Empty);
                    }
                    else
                    {
                        this.series[instrument.Name] = new Series(instrument.Description ?? string.Empty);
                    }
                }

                listener.EnableMeasurementEvents(instrument);
            },
        };

        this.listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            var labels = Labels(tags);

            lock (this.gate)
            {
                if (this.gauges.TryGetValue(instrument.Name, out var gauge))
                {
                    // Replaced, not accumulated. A gauge reports what is true
                    // now; adding successive readings would turn "three
                    // instances running" into a running total of every scrape.
                    gauge.Totals[labels] = value;
                    return;
                }

                if (!this.series.TryGetValue(instrument.Name, out var found))
                {
                    return;
                }

                found.Totals[labels] = found.Totals.GetValueOrDefault(labels) + value;
            }
        });

        this.listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
        {
            var labels = Labels(tags);

            lock (this.gate)
            {
                if (!this.distributions.TryGetValue(instrument.Name, out var found))
                {
                    return;
                }

                if (!found.Observations.TryGetValue(labels, out var bucketed))
                {
                    bucketed = new Buckets();
                    found.Observations[labels] = bucketed;
                }

                bucketed.Observe(value);
            }
        });

        this.listener.Start();
    }

    /// <summary>The content type a Prometheus scrape expects.</summary>
    public const string ContentType = "text/plain; version=0.0.4; charset=utf-8";

    /// <summary>Renders everything recorded so far.</summary>
    public string Render()
    {
        // Observable instruments are read here rather than pushed, so a gauge
        // reports the value at scrape time. Without this the endpoint would
        // serve whatever was last observed - for an idle node, a number from
        // whenever it last happened to be collected.
        this.listener.RecordObservableInstruments();

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

            foreach (var (instrument, recorded) in this.gauges.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                // No _total suffix: that convention marks a counter, and
                // labelling a gauge as one tells every dashboard to compute a
                // rate over something that goes down as well as up.
                var name = instrument.Replace('.', '_');

                builder.Append("# HELP ").Append(name).Append(' ').AppendLine(recorded.Help);
                builder.Append("# TYPE ").Append(name).AppendLine(" gauge");

                foreach (var (labels, value) in recorded.Totals.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                {
                    builder
                        .Append(name)
                        .Append(labels)
                        .Append(' ')
                        .AppendLine(value.ToString(CultureInfo.InvariantCulture));
                }
            }

            foreach (var (instrument, recorded) in this.distributions.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                // The unit belongs in the name for a Prometheus histogram, and
                // the meter records seconds (EngineMetrics).
                var name = $"{instrument.Replace('.', '_')}_seconds";

                builder.Append("# HELP ").Append(name).Append(' ').AppendLine(recorded.Help);
                builder.Append("# TYPE ").Append(name).AppendLine(" histogram");

                foreach (var (labels, bucketed) in recorded.Observations.OrderBy(p => p.Key, StringComparer.Ordinal))
                {
                    Write(builder, name, labels, bucketed);
                }
            }
        }

        return builder.ToString();
    }

    /// <summary>Renders one label set's buckets, sum and count.</summary>
    private static void Write(StringBuilder builder, string name, string labels, Buckets bucketed)
    {
        // Cumulative, which is what a Prometheus bucket means: it holds
        // everything at or below its edge, not only what fell between it and
        // the previous one. Rendering per-bucket counts instead produces a
        // chart that is wrong in a way nothing errors about.
        var cumulative = 0L;

        for (var i = 0; i < EngineMetrics.BucketBoundaries.Count; i++)
        {
            cumulative += bucketed.Counts[i];

            builder
                .Append(name)
                .Append("_bucket")
                .Append(WithLabel(labels, "le", Format(EngineMetrics.BucketBoundaries[i])))
                .Append(' ')
                .AppendLine(cumulative.ToString(CultureInfo.InvariantCulture));
        }

        // +Inf is required, and its value is the total count. A histogram
        // without it is rejected by the collector rather than merely losing its
        // last bucket.
        builder
            .Append(name)
            .Append("_bucket")
            .Append(WithLabel(labels, "le", "+Inf"))
            .Append(' ')
            .AppendLine(bucketed.Count.ToString(CultureInfo.InvariantCulture));

        builder.Append(name).Append("_sum").Append(labels).Append(' ').AppendLine(Format(bucketed.Sum));

        builder
            .Append(name)
            .Append("_count")
            .Append(labels)
            .Append(' ')
            .AppendLine(bucketed.Count.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Renders a double the way a collector will read it.
    /// </summary>
    /// <remarks>
    /// Invariant culture and never scientific notation. A collector reading
    /// <c>1,5</c> or <c>1E-05</c> rejects the whole scrape, and the machine that
    /// produces the first is a European developer laptop rather than CI - so it
    /// would be found in production.
    /// </remarks>
    private static string Format(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);

    /// <summary>Adds one label to an already-rendered label set.</summary>
    private static string WithLabel(string labels, string name, string value)
    {
        var pair = $"{name}=\"{Escape(value)}\"";

        return labels.Length == 0 ? $"{{{pair}}}" : $"{labels[..^1]},{pair}}}";
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

    private sealed class Distribution(string help)
    {
        public string Help { get; } = help;

        public Dictionary<string, Buckets> Observations { get; } = new(StringComparer.Ordinal);
    }

    /// <summary>One label set's observations, bucketed as they arrive.</summary>
    /// <remarks>
    /// Bucketed here rather than kept as raw values. Storing every observation
    /// would grow without bound for as long as the process runs, which is the
    /// one thing a metrics endpoint must not do.
    /// </remarks>
    private sealed class Buckets
    {
        public long[] Counts { get; } = new long[EngineMetrics.BucketBoundaries.Count];

        public long Count { get; private set; }

        public double Sum { get; private set; }

        public void Observe(double value)
        {
            this.Count++;
            this.Sum += value;

            for (var i = 0; i < EngineMetrics.BucketBoundaries.Count; i++)
            {
                if (value <= EngineMetrics.BucketBoundaries[i])
                {
                    this.Counts[i]++;
                    return;
                }
            }

            // Above every edge, so it lands in no bucket - and is still in
            // Count, which is what +Inf reports. A slow step belongs there.
        }
    }
}
