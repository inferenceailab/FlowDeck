namespace FlowDeck.Core;

/// <summary>
/// Thrown when an instance is requested that does not exist.
/// </summary>
/// <remarks>
/// Lives in the root namespace rather than alongside the store contract because
/// callers of <see cref="WorkflowEngine"/> encounter it without ever referencing
/// persistence types.
/// </remarks>
public sealed class InstanceNotFoundException(Guid instanceId)
    : FlowDeckException($"No workflow instance with id '{instanceId}' is known.")
{
    public Guid InstanceId { get; } = instanceId;
}
