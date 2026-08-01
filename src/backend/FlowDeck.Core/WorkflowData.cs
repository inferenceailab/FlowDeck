namespace FlowDeck.Core;

/// <summary>
/// Thrown when a step reads a workflow data key that was never written.
/// </summary>
public sealed class WorkflowDataKeyNotFoundException(string key)
    : FlowDeckException($"Workflow data has no value for key '{key}'.")
{
    public string Key { get; } = key;
}

/// <summary>
/// Thrown when a step reads a workflow data key at the wrong type.
/// </summary>
public sealed class WorkflowDataTypeMismatchException(string key, Type requestedType, Type actualType)
    : FlowDeckException(
        $"Workflow data key '{key}' holds {actualType.Name}, but was read as {requestedType.Name}.")
{
    public string Key { get; } = key;

    public Type RequestedType { get; } = requestedType;

    public Type ActualType { get; } = actualType;
}

/// <summary>
/// The mutable key-value state shared by the steps of one workflow instance.
/// </summary>
/// <remarks>
/// One store per instance - never shared between instances, and safe to use
/// from several at once <b>within</b> one instance: ADR-0024 makes parallel
/// branches genuinely concurrent, so two of them may write at the same moment.
///
/// What the implementation does <b>not</b> give an author is atomicity across
/// two calls. <c>Get</c> then <c>Set</c> from two branches is still a race the
/// author has to think about, exactly as it would be in any shared state.
///
/// Values are held as <see cref="object"/> because a workflow's data shape is
/// author-defined and only known at runtime. Reads are checked, so a mistake
/// surfaces as a named exception rather than an <see cref="InvalidCastException"/>
/// from inside step code.
/// </remarks>
public interface IWorkflowData
{
    /// <summary>Writes a value, replacing any existing value for the key.</summary>
    void Set<T>(string key, T value);

    /// <summary>Reads a value.</summary>
    /// <exception cref="WorkflowDataKeyNotFoundException">The key was never written.</exception>
    /// <exception cref="WorkflowDataTypeMismatchException">The key holds another type.</exception>
    T Get<T>(string key);

    /// <summary>
    /// Reads a value if present and of the requested type.
    /// </summary>
    bool TryGet<T>(string key, out T value);

    /// <summary>Whether the key has been written, even if written as null.</summary>
    bool Contains(string key);

    /// <summary>
    /// A point-in-time copy of the contents, for persistence (#15).
    /// </summary>
    IReadOnlyDictionary<string, object?> Snapshot();
}

/// <inheritdoc cref="IWorkflowData"/>
public sealed class WorkflowData : IWorkflowData
{
    // Ordinal comparison: a data key is a machine identifier chosen by the
    // workflow author, not display text. Culture must not decide whether two
    // keys are the same.
    private readonly Dictionary<string, object?> values = new(StringComparer.Ordinal);

    /// <summary>
    /// Guards every read and write, because parallel branches share this bag.
    /// </summary>
    /// <remarks>
    /// Coarse rather than clever, per ADR-0024 decision 5. A workflow data bag
    /// is small and written a handful of times per step, so contention is not
    /// the concern - correctness is. A ConcurrentDictionary would be finer
    /// grained and would still not make Snapshot a consistent point in time,
    /// which is what a checkpoint needs.
    /// </remarks>
    private readonly Lock gate = new();

    public WorkflowData()
    {
    }

    /// <summary>
    /// Rehydrates a store from a snapshot, as persistence (#15) will need.
    /// </summary>
    public WorkflowData(IReadOnlyDictionary<string, object?> initial)
    {
        ArgumentNullException.ThrowIfNull(initial);

        foreach (var (key, value) in initial)
        {
            this.values[key] = value;
        }
    }

    public void Set<T>(string key, T value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        lock (this.gate)
        {
            this.values[key] = value;
        }
    }

    public T Get<T>(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        object? value;

        lock (this.gate)
        {
            if (!this.values.TryGetValue(key, out value))
            {
                throw new WorkflowDataKeyNotFoundException(key);
            }
        }

        // A stored null satisfies any reference or nullable target.
        if (value is null)
        {
            return default!;
        }

        if (value is not T typed)
        {
            throw new WorkflowDataTypeMismatchException(key, typeof(T), value.GetType());
        }

        return typed;
    }

    public bool TryGet<T>(string key, out T value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        object? stored;
        bool found;

        lock (this.gate)
        {
            found = this.values.TryGetValue(key, out stored);
        }

        if (found)
        {
            if (stored is null)
            {
                value = default!;
                return true;
            }

            if (stored is T typed)
            {
                value = typed;
                return true;
            }
        }

        value = default!;
        return false;
    }

    public bool Contains(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        lock (this.gate)
        {
            return this.values.ContainsKey(key);
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Copied under the lock, so a checkpoint taken while a branch is writing
    /// records a state that existed rather than a half-applied one.
    /// </remarks>
    public IReadOnlyDictionary<string, object?> Snapshot()
    {
        lock (this.gate)
        {
            return new Dictionary<string, object?>(this.values, StringComparer.Ordinal);
        }
    }
}
