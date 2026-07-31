namespace FlowDeck.Core.Persistence;

/// <summary>
/// The durable form of a workflow instance.
/// </summary>
/// <remarks>
/// Deliberately separate from <see cref="WorkflowInstance"/>. The runtime object
/// holds a live <see cref="Exception"/>, which no provider can store portably;
/// this record holds the exception's type name and message instead. Keeping the
/// two apart also means the persisted shape can change without reshaping the
/// engine's working object, and vice versa.
///
/// Immutable: a save produces a new record with an incremented
/// <see cref="Revision"/> rather than mutating the caller's copy.
/// </remarks>
public sealed record WorkflowInstanceRecord
{
    public required Guid Id { get; init; }

    public required string DefinitionId { get; init; }

    public required int DefinitionVersion { get; init; }

    public required InstanceStatus Status { get; init; }

    /// <summary>Zero-based index of the step the instance is positioned at.</summary>
    public required int CurrentStepIndex { get; init; }

    /// <summary>How many times the current step has executed (ADR-0020).</summary>
    public int StepAttempts { get; init; }

    /// <summary>Step the instance is positioned at, or null once all have run.</summary>
    public string? CurrentStepName { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset? CompletedAt { get; init; }

    public string? FailedStepName { get; init; }

    /// <summary>
    /// Assembly-qualified-free type name of the failure, e.g.
    /// <c>InvalidOperationException</c>. Stored as text because an exception
    /// object is not portably serialisable across providers.
    /// </summary>
    public string? ErrorType { get; init; }

    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Workflow data at this checkpoint.
    /// </summary>
    /// <remarks>
    /// Values are <see cref="object"/> here. Deciding the serialisation format,
    /// and what happens to a value that cannot be serialised, is #15 - flagged
    /// as a known problem in ADR-0013 rather than discovered later.
    /// </remarks>
    public IReadOnlyDictionary<string, object?> Data { get; init; } =
        new Dictionary<string, object?>(StringComparer.Ordinal);

    /// <summary>
    /// The input the instance was started with, if any.
    /// </summary>
    /// <remarks>
    /// Part of instance state rather than of <see cref="Data"/>: ADR-0006 keeps
    /// input out of the author-controlled key space, and it must survive a
    /// restart or a resumed step would see null where it saw a value before.
    /// Serialisability is #15's problem, same as <see cref="Data"/>.
    /// </remarks>
    public object? Input { get; init; }

    /// <summary>
    /// Optimistic concurrency token. Incremented by the store on every save.
    /// </summary>
    /// <remarks>
    /// An <see cref="int"/> rather than a provider-specific row version, so the
    /// same conformance suite constrains both the in-memory and the EF Core
    /// provider. A caller saving a record whose revision is not the stored one
    /// is working from stale state and is rejected.
    /// </remarks>
    public int Revision { get; init; }
}

/// <summary>
/// One immutable entry in an instance's execution history.
/// </summary>
/// <remarks>
/// Append-only, per ADR-0013. History is written in the same operation as the
/// state checkpoint so the two cannot disagree after a crash.
/// </remarks>
public sealed record StepHistoryEntry
{
    public required Guid InstanceId { get; init; }

    /// <summary>Position within this instance's history, starting at 1.</summary>
    public required int Sequence { get; init; }

    public required string StepName { get; init; }

    public required DateTimeOffset StartedAt { get; init; }

    public required DateTimeOffset CompletedAt { get; init; }

    public required StepStatus Status { get; init; }

    public string? ErrorType { get; init; }

    public string? ErrorMessage { get; init; }
}

/// <summary>
/// Filter for listing instances.
/// </summary>
/// <remarks>
/// Exists so #25 is about HTTP paging rather than about inventing a query
/// model. Kept to what the M2 and M3 stories actually ask for.
/// </remarks>
public sealed record InstanceFilter
{
    /// <summary>Restrict to one status, or null for any.</summary>
    public InstanceStatus? Status { get; init; }

    /// <summary>Restrict to one definition id, or null for any.</summary>
    public string? DefinitionId { get; init; }

    public int Skip { get; init; }

    /// <summary>Maximum results. Null means no limit.</summary>
    public int? Take { get; init; }
}

/// <summary>
/// Thrown when a save is attempted from state that another writer has
/// superseded.
/// </summary>
public sealed class WorkflowStoreConcurrencyException(Guid instanceId, int expectedRevision, int actualRevision)
    : FlowDeckException(
        $"Workflow instance '{instanceId}' was modified concurrently: expected revision {expectedRevision}, found {actualRevision}.")
{
    public Guid InstanceId { get; } = instanceId;

    public int ExpectedRevision { get; } = expectedRevision;

    public int ActualRevision { get; } = actualRevision;
}

/// <summary>
/// Thrown when creating an instance whose id is already stored.
/// </summary>
public sealed class DuplicateInstanceException(Guid instanceId)
    : FlowDeckException($"A workflow instance with id '{instanceId}' already exists.")
{
    public Guid InstanceId { get; } = instanceId;
}
