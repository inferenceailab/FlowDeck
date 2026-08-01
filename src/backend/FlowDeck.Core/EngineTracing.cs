using System.Diagnostics;

namespace FlowDeck.Core;

/// <summary>
/// The spans the engine opens.
/// </summary>
/// <remarks>
/// <see cref="ActivitySource"/> is in the BCL, so this costs no package
/// reference. OpenTelemetry is one consumer of it and the host's choice, not the
/// engine's (ADR-0025 decision 1).
///
/// <para>
/// <b>Nothing is created when nobody is listening.</b>
/// <see cref="ActivitySource.StartActivity(string, ActivityKind)"/> returns
/// <see langword="null"/> with no registered listener, so every call site here
/// is a null check on a host that exports nothing.
/// </para>
///
/// <para>
/// No workflow data reaches an attribute (ADR-0025 decision 3). A span leaves
/// the process for a backend that may be third-party, retained on someone
/// else's schedule and searchable by people who never had database access.
/// </para>
/// </remarks>
public sealed class EngineTracing : IDisposable
{
    /// <summary>
    /// The source name a host enables to receive FlowDeck's spans.
    /// </summary>
    /// <remarks>
    /// The assembly name, matching <see cref="EngineMetrics.MeterName"/> and the
    /// convention every collector expects, so one string switches both on.
    /// </remarks>
    public const string SourceName = "FlowDeck.Core";

    /// <summary>The span covering one run of an instance.</summary>
    public const string InstanceSpan = "workflow.instance";

    /// <summary>The span covering one execution of one step.</summary>
    public const string StepSpan = "workflow.step";

    private readonly ActivitySource source;

    public EngineTracing() => this.source = new ActivitySource(SourceName);

    /// <summary>The default every engine shares when a host supplies none.</summary>
    internal static EngineTracing Default { get; } = new();

    /// <summary>
    /// The source these spans are opened on.
    /// </summary>
    /// <remarks>
    /// <b>Internal</b>, for the same reason <see cref="EngineMetrics.Meter"/>
    /// is: a test listens to one engine's spans rather than to every engine in
    /// a process running scenarios in parallel, and what FlowDeck emits is a
    /// contract this project keeps rather than one callers extend.
    /// </remarks>
    internal ActivitySource Source => this.source;

    /// <summary>
    /// Opens the span covering a whole run.
    /// </summary>
    /// <param name="instance">The instance being run.</param>
    /// <param name="root">
    /// Whether to begin a new trace rather than continue the ambient one.
    /// </param>
    /// <remarks>
    /// An instance started over HTTP runs inline on the request thread, so the
    /// ambient <see cref="Activity"/> is the request's and this span parents to
    /// it for free - a slow endpoint and the step responsible then appear in one
    /// trace, which is the reason to do this at all.
    ///
    /// <para>
    /// A recovered instance is the opposite case and passes <c>root: true</c>.
    /// It has no caller and no inbound trace context; attaching it to whatever
    /// the dispatcher happened to have open would say the poll caused the work,
    /// and a trace that claims the wrong cause is worse than two traces.
    /// </para>
    /// </remarks>
    internal Activity? StartInstance(WorkflowInstance instance, bool root)
    {
        var ambient = Activity.Current;

        if (root)
        {
            // Clearing the ambient activity is the only way to force a root.
            // Passing a default ActivityContext does not do it: ActivitySource
            // reads that as "no parent specified" and falls back to
            // Activity.Current, so a resumed instance would silently hang off
            // whatever the dispatcher had open - which is the exact outcome
            // this parameter exists to prevent.
            Activity.Current = null;
        }

        var activity = this.source.StartActivity(InstanceSpan);

        if (activity is null)
        {
            // Nothing listening, so nothing was created. Put the ambient back
            // rather than leaving the caller's context cleared as a side effect
            // of instrumentation that did not happen.
            Activity.Current = ambient;
            return null;
        }

        activity.SetTag("workflow.instance.id", instance.Id);
        activity.SetTag("workflow.definition.id", instance.DefinitionId);
        activity.SetTag("workflow.definition.version", instance.DefinitionVersion);

        return activity;
    }

    /// <summary>
    /// Opens the span covering one execution of one step.
    /// </summary>
    /// <remarks>
    /// A child of whatever is ambient, which is the instance span - including
    /// inside a fork, because <see cref="Activity.Current"/> is async-local and
    /// each arm inherits it at the fork rather than seeing whichever sibling ran
    /// first (ADR-0024 made these genuinely concurrent).
    /// </remarks>
    internal Activity? StartStep(string stepName, int attempt, IReadOnlyList<string> branchPath)
    {
        var activity = this.source.StartActivity(StepSpan);

        activity?.SetTag("workflow.step.name", stepName);
        activity?.SetTag("workflow.step.attempt", attempt);

        // Only where there is one. A step on the top-level sequence has no
        // branch, and an empty attribute is a value a reader has to interpret.
        if (branchPath.Count > 0)
        {
            activity?.SetTag("workflow.branch", string.Join('/', branchPath));
        }

        return activity;
    }

    /// <summary>
    /// Marks a span as having failed, carrying the exception's type.
    /// </summary>
    /// <remarks>
    /// The type and message the engine already exposes over HTTP, never the
    /// exception object. <c>RecordException</c> would attach the stack trace and
    /// whatever an author's message interpolated, which is exactly the material
    /// decision 3 keeps out of an exported signal.
    /// </remarks>
    internal static void MarkFailed(Activity? activity, string? errorType, string? errorMessage)
    {
        if (activity is null)
        {
            return;
        }

        activity.SetStatus(ActivityStatusCode.Error, errorMessage);
        activity.SetTag("error.type", errorType);
    }

    public void Dispose() => this.source.Dispose();
}
