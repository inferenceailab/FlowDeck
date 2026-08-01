using System.Diagnostics;
using FlowDeck.Core;

namespace FlowDeck.Specs.Support;

/// <summary>
/// Listens to one <see cref="EngineTracing"/> and keeps the spans it opened.
/// </summary>
/// <remarks>
/// Filtered to a specific source <b>instance</b> rather than to the source name,
/// for the same reason <see cref="MeterCapture"/> is: scenarios run in parallel
/// and every engine in the process publishes a source called
/// <c>FlowDeck.Core</c>.
///
/// <para>
/// <see cref="ActivitySamplingResult.AllDataAndRecorded"/> rather than
/// <c>PropagationData</c>. Without it <c>StartActivity</c> returns an activity
/// that drops every tag, and each assertion about an attribute would fail for a
/// reason that has nothing to do with the engine.
/// </para>
/// </remarks>
public sealed class ActivityCapture : IDisposable
{
    private readonly List<Activity> finished = [];
    private readonly Lock gate = new();
    private readonly ActivityListener listener;

    public ActivityCapture()
    {
        this.Tracing = new EngineTracing();

        this.listener = new ActivityListener
        {
            ShouldListenTo = source => ReferenceEquals(source, this.Tracing.Source),
            Sample = (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                lock (this.gate)
                {
                    this.finished.Add(activity);
                }
            },
        };

        ActivitySource.AddActivityListener(this.listener);
    }

    /// <summary>The tracing this capture listens to, for handing to an engine.</summary>
    public EngineTracing Tracing { get; }

    /// <summary>Every span that has closed, in the order it closed.</summary>
    public IReadOnlyList<Activity> All
    {
        get
        {
            lock (this.gate)
            {
                return [.. this.finished];
            }
        }
    }

    /// <summary>Closed spans of one operation name.</summary>
    public IReadOnlyList<Activity> Named(string operationName) =>
        [.. this.All.Where(activity => string.Equals(activity.OperationName, operationName, StringComparison.Ordinal))];

    /// <summary>The single instance span, which every scenario here expects.</summary>
    public Activity Instance => this.Named(EngineTracing.InstanceSpan).Single();

    /// <summary>Closed step spans.</summary>
    public IReadOnlyList<Activity> Steps => this.Named(EngineTracing.StepSpan);

    /// <summary>A tag's value, or null where the span does not carry it.</summary>
    public static object? Tag(Activity activity, string name)
    {
        ArgumentNullException.ThrowIfNull(activity);

        return activity.GetTagItem(name);
    }

    public void Dispose()
    {
        this.listener.Dispose();
        this.Tracing.Dispose();
    }
}
