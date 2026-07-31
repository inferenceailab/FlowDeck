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
/// Not thread-safe. One instance is executed by one worker at a time - the
/// invariant #39 must preserve.
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
    /// Whether this instance has reached a state from which it will not
    /// continue on its own.
    /// </summary>
    public bool IsTerminal => this.Status
        is InstanceStatus.Completed
        or InstanceStatus.Failed
        or InstanceStatus.Cancelled;

    /// <summary>Projects this instance into its durable form.</summary>
    internal WorkflowInstanceRecord ToRecord(IWorkflowData data, object? input) => new()
    {
        Id = this.Id,
        DefinitionId = this.DefinitionId,
        DefinitionVersion = this.DefinitionVersion,
        Status = this.Status,
        CurrentStepIndex = this.CurrentStepIndex,
        CurrentStepName = this.CurrentStepName,
        CreatedAt = this.CreatedAt,
        CompletedAt = this.CompletedAt,
        FailedStepName = this.FailedStepName,
        ErrorType = this.ErrorType,
        ErrorMessage = this.ErrorMessage,
        Data = data.Snapshot(),
        Input = input,
        Revision = this.Revision,
    };

    /// <summary>Rebuilds an instance from its durable form.</summary>
    internal static WorkflowInstance FromRecord(WorkflowInstanceRecord record) =>
        new(record.Id, record.DefinitionId, record.DefinitionVersion, record.CreatedAt)
        {
            Status = record.Status,
            CurrentStepIndex = record.CurrentStepIndex,
            CurrentStepName = record.CurrentStepName,
            CompletedAt = record.CompletedAt,
            FailedStepName = record.FailedStepName,
            ErrorType = record.ErrorType,
            ErrorMessage = record.ErrorMessage,
            Revision = record.Revision,

            // Error stays null: an exception object cannot be reconstructed
            // from stored text, and pretending otherwise would produce a
            // fabricated stack trace.
        };
}
