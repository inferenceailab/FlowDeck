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
