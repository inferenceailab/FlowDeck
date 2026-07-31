using System.Collections.Concurrent;

namespace FlowDeck.Core;

/// <summary>
/// Thrown when an instance is requested that the engine does not know about.
/// </summary>
public sealed class InstanceNotFoundException(Guid instanceId)
    : FlowDeckException($"No workflow instance with id '{instanceId}' is known.")
{
    public Guid InstanceId { get; } = instanceId;
}

/// <summary>
/// Where the engine keeps the instances it has started.
/// </summary>
/// <remarks>
/// In-memory and unbounded, which is correct for M1 and wrong for production:
/// instances are lost on restart (#14) and accumulate without limit (#20).
/// Both are known and tracked rather than papered over here.
/// </remarks>
public interface IInstanceStore
{
    /// <summary>Records a newly created instance.</summary>
    void Add(WorkflowInstance instance);

    /// <summary>Retrieves an instance by id.</summary>
    /// <exception cref="InstanceNotFoundException">No such instance.</exception>
    WorkflowInstance Get(Guid instanceId);

    /// <summary>Retrieves an instance by id if it exists.</summary>
    bool TryGet(Guid instanceId, out WorkflowInstance? instance);

    /// <summary>Every known instance, most recently created first.</summary>
    IReadOnlyCollection<WorkflowInstance> GetAll();
}

/// <inheritdoc cref="IInstanceStore"/>
public sealed class InMemoryInstanceStore : IInstanceStore
{
    private readonly ConcurrentDictionary<Guid, WorkflowInstance> instances = new();

    public void Add(WorkflowInstance instance)
    {
        ArgumentNullException.ThrowIfNull(instance);

        if (!this.instances.TryAdd(instance.Id, instance))
        {
            // Ids come from Guid.NewGuid, so this indicates a bug in the engine
            // rather than anything a caller did.
            throw new InvalidOperationException(
                $"An instance with id '{instance.Id}' is already recorded.");
        }
    }

    public WorkflowInstance Get(Guid instanceId) =>
        this.instances.TryGetValue(instanceId, out var instance)
            ? instance
            : throw new InstanceNotFoundException(instanceId);

    public bool TryGet(Guid instanceId, out WorkflowInstance? instance) =>
        this.instances.TryGetValue(instanceId, out instance);

    // Newest first: an operator opening a dashboard cares about what just
    // happened, not what happened first.
    public IReadOnlyCollection<WorkflowInstance> GetAll() =>
        this.instances.Values
            .OrderByDescending(instance => instance.CreatedAt)
            .ToArray();
}
