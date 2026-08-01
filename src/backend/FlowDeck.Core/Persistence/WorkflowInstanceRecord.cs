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
/// <summary>
/// One place an instance is currently executing.
/// </summary>
/// <param name="StepName">
/// The step this node sits on. Names are unique across the whole graph (#162),
/// so a name identifies a node without needing a path.
/// </param>
/// <param name="Attempts">
/// Executions of this step so far, including the one in progress. Per node
/// rather than per instance, because a fork whose branches both retry has two
/// independent counts (ADR-0024).
/// </param>
/// <param name="BranchPath">
/// The branches taken to reach this node, outermost first, or empty for a node
/// on the top-level sequence.
/// </param>
/// <remarks>
/// Carries the step <b>name</b> rather than an index. An index only means
/// something relative to a sequence, and a node inside a branch is not in the
/// top-level one — so an index would need a path to interpret it, which is the
/// name in a less readable form.
/// </remarks>
public sealed record ActiveNode(string StepName, int Attempts, IReadOnlyList<string> BranchPath)
{
    /// <summary>A node on the top-level sequence, on its first attempt.</summary>
    /// <remarks>
    /// A factory rather than a second constructor. Providers serialise this type
    /// as JSON, and a record with two constructors gives the deserialiser no way
    /// to choose between them - it refuses rather than guessing. Annotating one
    /// with <c>[JsonConstructor]</c> would work and would put a serialisation
    /// concern on a domain type to buy shorter test setup.
    /// </remarks>
    public static ActiveNode At(string stepName) => new(stepName, Attempts: 0, BranchPath: []);

    /// <summary>
    /// Structural equality, including the branch path element by element.
    /// </summary>
    /// <remarks>
    /// The compiler-generated version compares <see cref="BranchPath"/> by
    /// reference, because that is what <see cref="IReadOnlyList{T}"/> does. Two
    /// nodes describing the same position would then be unequal whenever the
    /// lists were separate objects - which is always, once one side has been
    /// through a store.
    ///
    /// <para>
    /// That is not only a test inconvenience. Anything comparing an instance's
    /// position to the one it last saw - a checkpoint deciding whether the set
    /// moved, a client diffing two polls - would see a change on every read.
    /// </para>
    /// </remarks>
    public bool Equals(ActiveNode? other) =>
        other is not null
        && string.Equals(this.StepName, other.StepName, StringComparison.Ordinal)
        && this.Attempts == other.Attempts
        && this.BranchPath.SequenceEqual(other.BranchPath, StringComparer.Ordinal);

    public override int GetHashCode()
    {
        var hash = default(HashCode);

        hash.Add(this.StepName, StringComparer.Ordinal);
        hash.Add(this.Attempts);

        // The path's contents, matching Equals. Hashing the list object would
        // put equal nodes in different buckets and break every hashed lookup.
        foreach (var branch in this.BranchPath)
        {
            hash.Add(branch, StringComparer.Ordinal);
        }

        return hash.ToHashCode();
    }
}

public sealed record WorkflowInstanceRecord
{
    public required Guid Id { get; init; }

    public required string DefinitionId { get; init; }

    public required int DefinitionVersion { get; init; }

    public required InstanceStatus Status { get; init; }

    /// <summary>
    /// Zero-based index of the step the instance is positioned at.
    /// </summary>
    /// <remarks>
    /// A <b>projection</b> of <see cref="ActiveNodes"/> since ADR-0024, kept
    /// because the dashboard, the API and every existing consumer read it. It
    /// describes a straight-line workflow exactly and a forked one only
    /// partially — an instance at three places at once has no single index, and
    /// this reports the first.
    ///
    /// <para>
    /// Read <see cref="ActiveNodes"/> when the answer has to be right for a
    /// graph. This field is retained for compatibility, not because it is
    /// sufficient.
    /// </para>
    /// </remarks>
    public required int CurrentStepIndex { get; init; }

    /// <summary>How many times the current step has executed (ADR-0020).</summary>
    /// <remarks>
    /// Also a projection: attempts are counted per active node, because a fork
    /// whose two branches are both retrying has two independent counts. This
    /// reports the first node's.
    /// </remarks>
    public int StepAttempts { get; init; }

    /// <summary>Step the instance is positioned at, or null once all have run.</summary>
    public string? CurrentStepName { get; init; }

    /// <summary>
    /// Every node this instance is currently at.
    /// </summary>
    /// <remarks>
    /// The position ADR-0024 makes authoritative. One entry for a sequential
    /// workflow; one per branch while a fork is in flight; empty once the
    /// instance is terminal.
    ///
    /// <para>
    /// Durable ahead of being authoritative. The engine still advances an index
    /// and projects this from it (#163); #164 inverts that once branches
    /// actually execute. Persisting the set first means the inversion changes
    /// the engine alone rather than every store provider at the same time.
    /// </para>
    ///
    /// <para>
    /// Order is not significant — these are concurrent positions, not a
    /// sequence — but a provider must return them in a stable order, so that
    /// reading an unchanged instance twice does not appear to change it.
    /// </para>
    /// </remarks>
    public IReadOnlyList<ActiveNode> ActiveNodes { get; init; } = [];

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

    /// <summary>
    /// The node currently running this instance, or <see langword="null"/> if
    /// no node holds it.
    /// </summary>
    /// <remarks>
    /// Held on the instance record rather than in a separate coordination
    /// store, so a lease can never disagree with the state it guards
    /// (ADR-0023). A restarted process gets a new identity and does not inherit
    /// its predecessor's claims — that work was abandoned when the process
    /// died, and adopting the leases would skip the recovery they exist for.
    /// </remarks>
    public string? OwnerNodeId { get; init; }

    /// <summary>
    /// When this node's claim lapses if it is not renewed.
    /// </summary>
    /// <remarks>
    /// An expired lease is what an orphan <b>is</b>: claiming and orphan
    /// detection are one mechanism rather than two that have to agree.
    ///
    /// <para>
    /// Compared against each node's own clock, not the database's, because the
    /// store is provider-agnostic and there is no portable server timestamp.
    /// Badly skewed clocks therefore misjudge expiry — documented in ADR-0023
    /// rather than defended.
    /// </para>
    /// </remarks>
    public DateTimeOffset? LeaseExpiresAt { get; init; }
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

    /// <summary>
    /// Which attempt at this step this execution was, starting at 1.
    /// </summary>
    /// <remarks>
    /// One for a step that never retried, so a timeline reads the same either
    /// way and a client never has to special-case zero.
    ///
    /// <para>
    /// Without it, three entries for one step are ambiguous: a step retried
    /// three times and a step re-entered three times by successive resumes
    /// produce identical history. Re-entry after a suspension is deliberately
    /// <b>not</b> counted as an attempt - the step never failed, so numbering
    /// it as attempt two would report a failure that did not happen.
    /// </para>
    /// </remarks>
    public int Attempt { get; init; } = 1;

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

    /// <summary>
    /// Restrict to one definition version, or null for any.
    /// </summary>
    /// <remarks>
    /// Only meaningful alongside <see cref="DefinitionId"/> - a version number
    /// on its own names nothing, since two unrelated workflows both have a v1.
    /// Not enforced here, because a filter that threw would make the store
    /// responsible for a caller's mistake; it simply matches nothing useful.
    /// </remarks>
    public int? DefinitionVersion { get; init; }

    /// <summary>
    /// Restrict to instances that can still execute.
    /// </summary>
    /// <remarks>
    /// Non-terminal: <see cref="InstanceStatus.Running"/> or
    /// <see cref="InstanceStatus.Suspended"/>. A separate flag rather than a
    /// second status field, because "still going" is two statuses today and
    /// would silently mean the wrong thing if a third were added.
    ///
    /// <para>
    /// This is what "is anything still using this definition version" asks
    /// (ADR-0026 decision 4). A terminal instance keeps its definition version
    /// forever - history is not rewritten - so counting those would mean no
    /// version could ever be retired.
    /// </para>
    /// </remarks>
    public bool ActiveOnly { get; init; }

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

/// <summary>
/// How many instances are still running one definition version.
/// </summary>
/// <param name="DefinitionId">The definition.</param>
/// <param name="DefinitionVersion">The version those instances pinned at start.</param>
/// <param name="ActiveInstances">
/// Non-terminal instances holding it. Never zero: a version nothing is running
/// is absent from the result rather than reported as an empty row.
/// </param>
public sealed record DefinitionUsage(string DefinitionId, int DefinitionVersion, int ActiveInstances);
