using FlowDeck.Core.Persistence;

namespace FlowDeck.Core;

/// <summary>
/// A single execution of a workflow definition, as callers see it.
/// </summary>
/// <remarks>
/// The runtime view of a <see cref="WorkflowInstanceRecord"/>. The record is
/// what is stored; this is what the engine drives and returns.
///
/// <para>
/// The difference that matters is <see cref="Error"/>: an instance that failed
/// in this process carries the live exception, while one loaded from the store
/// carries only <see cref="ErrorType"/> and <see cref="ErrorMessage"/>, because
/// an exception is not portably storable. Callers that need the exception object
/// must not assume a reloaded instance has one.
/// </para>
///
/// <para>
/// <b>One instance is no longer executed by one thread.</b> That invariant held
/// from M2 until ADR-0024 made parallel branches genuinely concurrent. The M6
/// lease still gives one <i>node</i> the instance, but inside that node several
/// branches run at once, so the fields the engine mutates while a fork is in
/// flight are written from more than one thread.
/// </para>
///
/// <para>
/// What keeps that safe is not this class: it is that concurrency is confined
/// to step execution. Branch tasks touch shared instance state at two points
/// only - the failure fields, taken by whichever branch fails first, and the
/// checkpoint, which every branch reaches through a single writer. The linear
/// position fields belong to the top-level sequence, and no branch touches them.
/// </para>
/// </remarks>
public sealed class WorkflowInstance
{
    internal WorkflowInstance(Guid id, string definitionId, int definitionVersion, DateTimeOffset createdAt)
    {
        this.Id = id;
        this.DefinitionId = definitionId;
        this.DefinitionVersion = definitionVersion;
        this.CreatedAt = createdAt;
        this.Status = InstanceStatus.Running;
    }

    /// <summary>Unique identifier for this execution.</summary>
    public Guid Id { get; }

    /// <summary>Id of the definition being executed.</summary>
    public string DefinitionId { get; }

    /// <summary>
    /// Version of the definition being executed. Pinned at start so a later
    /// deployment cannot change what an in-flight instance is running.
    /// </summary>
    public int DefinitionVersion { get; }

    /// <summary>Current lifecycle state.</summary>
    public InstanceStatus Status { get; internal set; }

    /// <summary>Zero-based index of the step the instance is positioned at.</summary>
    public int CurrentStepIndex { get; internal set; }

    /// <summary>
    /// How many times the current step has executed.
    /// </summary>
    /// <remarks>
    /// Belongs to the instance's position, not the instance as a whole: "this
    /// instance failed 5 times" is not actionable, "step charge failed 3 times"
    /// is. Reset when execution advances past the step (ADR-0020).
    /// </remarks>
    public int StepAttempts { get; internal set; }

    /// <summary>
    /// Name of the step the instance is positioned at, or <see langword="null"/>
    /// once every step has been executed.
    /// </summary>
    public string? CurrentStepName { get; internal set; }

    /// <summary>When the instance was created.</summary>
    public DateTimeOffset CreatedAt { get; }

    /// <summary>
    /// When the instance reached a terminal state, or <see langword="null"/>
    /// while it is still in flight.
    /// </summary>
    public DateTimeOffset? CompletedAt { get; internal set; }

    /// <summary>
    /// The failure that halted this instance, when it failed **in this process**.
    /// </summary>
    /// <remarks>
    /// The step's own exception, unwrapped. Always <see langword="null"/> on an
    /// instance loaded from the store - use <see cref="ErrorType"/> and
    /// <see cref="ErrorMessage"/>, which survive persistence.
    /// </remarks>
    public Exception? Error { get; internal set; }

    /// <summary>Type name of the failure, or null. Survives persistence.</summary>
    public string? ErrorType { get; internal set; }

    /// <summary>Message of the failure, or null. Survives persistence.</summary>
    public string? ErrorMessage { get; internal set; }

    /// <summary>
    /// Name of the step that failed, or <see langword="null"/> if none has.
    /// </summary>
    /// <remarks>
    /// Recorded separately from <see cref="CurrentStepName"/> so the failure
    /// point survives once execution position moves on - which it will once
    /// retries (#37) and compensation (#38) exist.
    /// </remarks>
    public string? FailedStepName { get; internal set; }

    /// <summary>
    /// Optimistic concurrency token from the store.
    /// </summary>
    /// <remarks>
    /// Readable so a caller can tell whether the instance it holds is still
    /// current. Only the engine advances it - a save from a stale instance is
    /// rejected by the store (#19).
    /// </remarks>
    public int Revision { get; internal set; }

    /// <summary>
    /// The node holding this instance, or <see langword="null"/> if none.
    /// </summary>
    /// <remarks>
    /// Carried through <see cref="ToRecord"/> and back, so a checkpoint
    /// preserves the lease rather than clearing it.
    ///
    /// <para>
    /// Omitting it meant every save wiped the owner: a node lost its claim on
    /// the first step it completed, and a peer could take the instance out from
    /// under it while it was still running. The engine does not otherwise care
    /// about ownership — it simply must not destroy it.
    /// </para>
    /// </remarks>
    /// <summary>
    /// The instance this one was started to retry, or null if it was not.
    /// </summary>
    /// <remarks>
    /// Retry leaves the original untouched and starts a new instance
    /// (ADR-0028 decision 2), so this is the only thing linking the two. An
    /// operator following an alert to a failed instance uses it to find what
    /// was done about it.
    /// </remarks>
    public Guid? RetriedFromInstanceId { get; internal set; }

    public string? OwnerNodeId { get; internal set; }

    /// <summary>When this node's claim lapses if it is not renewed.</summary>
    public DateTimeOffset? LeaseExpiresAt { get; internal set; }

    /// <summary>
    /// Every node this instance is currently at.
    /// </summary>
    /// <remarks>
    /// Empty for a terminal instance, and one entry for an in-flight linear one.
    ///
    /// <para>
    /// <b>This is the answer for callers that have no run in hand</b> - one that
    /// cancelled the instance, or read it back from the store. It is derived
    /// from the linear position, so it describes a straight line exactly and
    /// cannot describe a fork: an instance at three places at once has no single
    /// <see cref="CurrentStepName"/> to derive from.
    /// </para>
    ///
    /// <para>
    /// While an instance is executing, the running fork supplies the real set to
    /// <see cref="ToRecord"/> instead, because only it knows how many branches
    /// are in flight (#164). The stored record is therefore right about a fork
    /// even though this property could not have said so.
    /// </para>
    /// </remarks>
    public IReadOnlyList<ActiveNode> ActiveNodes =>

        // A failed instance keeps CurrentStepName pointing at the step that
        // failed, so operators can still see where it stopped. That is a
        // gravestone, not a position: checking terminal first is what keeps it
        // from being reported as somewhere the instance is still running.
        this.IsTerminal || this.CurrentStepName is null
            ? []
            : [new ActiveNode(this.CurrentStepName, this.StepAttempts, BranchPath: [])];

    /// <summary>
    /// Whether this instance has reached a state from which it will not
    /// continue on its own.
    /// </summary>
    public bool IsTerminal => this.Status
        is InstanceStatus.Completed
        or InstanceStatus.Failed
        or InstanceStatus.Cancelled
        or InstanceStatus.Compensated
        or InstanceStatus.CompensationFailed;

    /// <summary>Projects this instance into its durable form.</summary>
    /// <param name="activeNodes">
    /// Where the instance is, as the executing run knows it, or
    /// <see langword="null"/> to derive it from the linear position.
    /// </param>
    /// <remarks>
    /// The run supplies the set while an instance is executing, because only it
    /// knows how many branches are in flight. Everything else - cancelling,
    /// reading an instance back - has no run and gets the derived answer, which
    /// is right for a straight line and is all those callers describe.
    /// </remarks>
    internal WorkflowInstanceRecord ToRecord(
        IWorkflowData data,
        object? input,
        IReadOnlyList<ActiveNode>? activeNodes = null) => new()
        {
            Id = this.Id,
            DefinitionId = this.DefinitionId,
            DefinitionVersion = this.DefinitionVersion,
            Status = this.Status,
            CurrentStepIndex = this.CurrentStepIndex,
            StepAttempts = this.StepAttempts,
            CurrentStepName = this.CurrentStepName,
            CreatedAt = this.CreatedAt,
            CompletedAt = this.CompletedAt,
            FailedStepName = this.FailedStepName,
            ErrorType = this.ErrorType,
            ErrorMessage = this.ErrorMessage,
            ActiveNodes = activeNodes ?? this.ActiveNodes,
            Data = data.Snapshot(),
            Input = input,
            Revision = this.Revision,
            RetriedFromInstanceId = this.RetriedFromInstanceId,
            OwnerNodeId = this.OwnerNodeId,
            LeaseExpiresAt = this.LeaseExpiresAt,
        };

    /// <summary>Rebuilds an instance from its durable form.</summary>
    internal static WorkflowInstance FromRecord(WorkflowInstanceRecord record) =>
        new(record.Id, record.DefinitionId, record.DefinitionVersion, record.CreatedAt)
        {
            Status = record.Status,
            CurrentStepIndex = record.CurrentStepIndex,
            StepAttempts = record.StepAttempts,
            RetriedFromInstanceId = record.RetriedFromInstanceId,
            OwnerNodeId = record.OwnerNodeId,
            LeaseExpiresAt = record.LeaseExpiresAt,
            CurrentStepName = record.CurrentStepName,
            CompletedAt = record.CompletedAt,
            FailedStepName = record.FailedStepName,
            ErrorType = record.ErrorType,
            ErrorMessage = record.ErrorMessage,
            Revision = record.Revision,

            // Error stays null: an exception object cannot be reconstructed
            // from stored text, and pretending otherwise would produce a
            // fabricated stack trace.
            //
            // ActiveNodes is not read either, because it is still derived from
            // the position restored above and re-deriving it gives the same
            // answer. That stops being true in #164, when the set is what the
            // engine advances - at which point this must read the stored set or
            // a recovered fork resumes down one branch only.
        };
}
