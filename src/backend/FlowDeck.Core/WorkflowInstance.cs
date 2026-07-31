namespace FlowDeck.Core;

/// <summary>
/// A single execution of a workflow definition.
/// </summary>
/// <remarks>
/// Mutable by design: the engine advances one instance through its steps, and
/// this object is what will later be handed to the persistence layer (#13).
/// It is not thread-safe - an instance is executed by one worker at a time,
/// which is the invariant the distributed execution epic (#39) must preserve.
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
    /// The failure that halted this instance, when it failed.
    /// </summary>
    /// <remarks>
    /// The step's own exception, unwrapped. Wrapping would force callers to
    /// unwrap before matching on the failure, and would bury the stack trace an
    /// operator needs to diagnose it.
    /// </remarks>
    public Exception? Error { get; internal set; }

    /// <summary>
    /// Name of the step that failed, or <see langword="null"/> if none has.
    /// </summary>
    /// <remarks>
    /// Recorded separately from <see cref="CurrentStepName"/> so that the
    /// failure point survives once execution position moves on - which it will
    /// once retries (#37) and compensation (#38) exist.
    /// </remarks>
    public string? FailedStepName { get; internal set; }

    /// <summary>
    /// Whether this instance has reached a state from which it will not
    /// continue on its own.
    /// </summary>
    public bool IsTerminal => this.Status
        is InstanceStatus.Completed
        or InstanceStatus.Failed
        or InstanceStatus.Cancelled;
}
