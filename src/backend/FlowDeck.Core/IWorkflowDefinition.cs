namespace FlowDeck.Core;

/// <summary>
/// A workflow definition: the immutable blueprint an instance executes.
/// </summary>
/// <remarks>
/// Identity is the pair (<see cref="Id"/>, <see cref="Version"/>). Version is
/// part of the key rather than mutable metadata so that instances started
/// against an older definition keep running against exactly that definition
/// after a newer one is deployed.
/// </remarks>
public interface IWorkflowDefinition
{
    /// <summary>
    /// Stable, human-readable identifier, unique across definitions.
    /// </summary>
    string Id { get; }

    /// <summary>
    /// Monotonically increasing version for this <see cref="Id"/>.
    /// </summary>
    int Version { get; }

    /// <summary>
    /// Declares this workflow's steps, in execution order.
    /// </summary>
    /// <remarks>
    /// Called once per instance start rather than cached, so that a definition
    /// is free to compose its steps from constructor-injected dependencies.
    /// </remarks>
    void Build(IWorkflowBuilder builder);
}
