namespace FlowDeck.Core;

/// <summary>
/// Thrown when an operation would move an instance to a state it cannot
/// legally reach from its current one.
/// </summary>
/// <remarks>
/// Terminal states are final. Allowing a completed or failed instance to be
/// cancelled would rewrite history and lose the recorded cause, and silently
/// accepting a second cancellation would overwrite the first timestamp, making
/// the audit trail lie about when work actually stopped.
/// </remarks>
public sealed class InvalidStateTransitionException(Guid instanceId, InstanceStatus from, InstanceStatus to)
    : FlowDeckException($"Workflow instance '{instanceId}' cannot move from {from} to {to}.")
{
    public Guid InstanceId { get; } = instanceId;

    public InstanceStatus From { get; } = from;

    public InstanceStatus To { get; } = to;
}
