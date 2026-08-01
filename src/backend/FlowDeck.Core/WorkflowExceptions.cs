namespace FlowDeck.Core;

/// <summary>
/// Base type for every error raised by the FlowDeck engine, so that callers can
/// distinguish engine faults from faults thrown by workflow step code.
/// </summary>
public abstract class FlowDeckException : Exception
{
    protected FlowDeckException(string message)
        : base(message)
    {
    }

    protected FlowDeckException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Thrown when registering a definition whose (id, version) pair is already
/// registered. Silently overwriting would let a deployment change the meaning
/// of a version that in-flight instances are already executing.
/// </summary>
public sealed class DuplicateDefinitionException(string definitionId, int version)
    : FlowDeckException($"A workflow definition '{definitionId}' version {version} is already registered.")
{
    public string DefinitionId { get; } = definitionId;

    public int Version { get; } = version;
}

/// <summary>
/// Thrown when a definition is requested that was never registered.
/// </summary>
public sealed class DefinitionNotFoundException : FlowDeckException
{
    public DefinitionNotFoundException(string definitionId, int version)
        : base($"No workflow definition '{definitionId}' version {version} is registered.")
    {
        DefinitionId = definitionId;
        Version = version;
    }

    public DefinitionNotFoundException(string definitionId)
        : base($"No workflow definition '{definitionId}' is registered.")
    {
        DefinitionId = definitionId;
    }

    public string DefinitionId { get; }

    /// <summary>
    /// The requested version, or <see langword="null"/> when any version would
    /// have been acceptable.
    /// </summary>
    public int? Version { get; }
}

/// <summary>
/// Thrown when a definition version is retired while instances still hold it.
/// </summary>
/// <remarks>
/// Carries the count rather than only refusing. "Refused" on its own leaves an
/// operator with no next step; the number tells them whether to wait or to go
/// and cancel something (ADR-0026 decision 2).
/// </remarks>
public sealed class DefinitionInUseException : FlowDeckException
{
    public DefinitionInUseException(string definitionId, int version, int activeInstances)
        : base(
            $"Workflow definition '{definitionId}' version {version} cannot be retired: "
            + $"{activeInstances} instance(s) are still running it.")
    {
        this.DefinitionId = definitionId;
        this.Version = version;
        this.ActiveInstances = activeInstances;
    }

    public string DefinitionId { get; }

    public int Version { get; }

    /// <summary>How many non-terminal instances are still holding the version.</summary>
    public int ActiveInstances { get; }
}
