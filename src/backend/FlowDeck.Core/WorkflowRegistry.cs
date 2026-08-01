using System.Collections.Concurrent;

namespace FlowDeck.Core;

/// <summary>
/// In-memory catalogue of the workflow definitions this engine node knows about.
/// </summary>
/// <remarks>
/// Registration normally happens once at startup, but lookup happens on every
/// instance start and may run on many threads at once, hence the concurrent
/// dictionary. Registration is deliberately fail-fast: a duplicate is a
/// deployment mistake, not a condition to recover from at runtime.
/// </remarks>
public sealed class WorkflowRegistry
{
    private readonly ConcurrentDictionary<DefinitionKey, IWorkflowDefinition> definitions = new();

    /// <summary>
    /// Adds a definition to the registry.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// <paramref name="definition"/> has a blank id or a non-positive version.
    /// </exception>
    /// <exception cref="DuplicateDefinitionException">
    /// The same id and version is already registered.
    /// </exception>
    public void Register(IWorkflowDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        if (string.IsNullOrWhiteSpace(definition.Id))
        {
            throw new ArgumentException(
                "A workflow definition must have a non-blank id.", nameof(definition));
        }

        if (definition.Version <= 0)
        {
            throw new ArgumentException(
                $"Workflow definition '{definition.Id}' must have a positive version, got {definition.Version}.",
                nameof(definition));
        }

        var key = new DefinitionKey(definition.Id, definition.Version);

        if (!this.definitions.TryAdd(key, definition))
        {
            throw new DuplicateDefinitionException(definition.Id, definition.Version);
        }
    }

    /// <summary>
    /// Removes a definition version from the registry.
    /// </summary>
    /// <returns>Whether it was registered.</returns>
    /// <remarks>
    /// <b>Internal on purpose.</b> Removing a version that instances are still
    /// running strands them - <c>ResumeAsync</c> and the dispatcher both resolve
    /// through this registry, so an unresumable instance is the result and
    /// nothing reports it.
    ///
    /// <para>
    /// Deciding whether a version is in use needs the store, which a lookup has
    /// no business holding, so the check lives on
    /// <see cref="WorkflowEngine.RetireAsync"/> and this stays unreachable from
    /// outside the assembly (ADR-0026 decision 3). A public <c>Unregister</c>
    /// would make that rule a convention.
    /// </para>
    /// </remarks>
    internal bool Unregister(string id, int version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        return this.definitions.TryRemove(new DefinitionKey(id, version), out _);
    }

    /// <summary>
    /// Resolves a definition by its exact id and version.
    /// </summary>
    /// <exception cref="DefinitionNotFoundException">No such definition.</exception>
    public IWorkflowDefinition Get(string id, int version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        if (!this.definitions.TryGetValue(new DefinitionKey(id, version), out var definition))
        {
            throw new DefinitionNotFoundException(id, version);
        }

        return definition;
    }

    /// <summary>
    /// Resolves the highest registered version of a definition.
    /// </summary>
    /// <exception cref="DefinitionNotFoundException">No such definition.</exception>
    public IWorkflowDefinition GetLatest(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        // Snapshotting via LINQ over a ConcurrentDictionary is safe here:
        // registration is a startup-time activity, so a torn read cannot
        // produce a version that was never registered.
        var latest = this.definitions
            .Where(entry => entry.Key.Id == id)
            .OrderByDescending(entry => entry.Key.Version)
            .Select(entry => entry.Value)
            .FirstOrDefault();

        return latest ?? throw new DefinitionNotFoundException(id);
    }

    /// <summary>
    /// Every registered definition, ordered by id then version.
    /// </summary>
    public IReadOnlyCollection<IWorkflowDefinition> GetAll() =>
        this.definitions
            .OrderBy(entry => entry.Key.Id, StringComparer.Ordinal)
            .ThenBy(entry => entry.Key.Version)
            .Select(entry => entry.Value)
            .ToArray();

    /// <summary>
    /// Composite identity of a definition. Ids are compared ordinally: a
    /// workflow id is a machine identifier, not display text, so culture must
    /// not influence whether two ids are the same.
    /// </summary>
    private readonly record struct DefinitionKey(string Id, int Version)
    {
        public bool Equals(DefinitionKey other) =>
            this.Version == other.Version
            && string.Equals(this.Id, other.Id, StringComparison.Ordinal);

        public override int GetHashCode() =>
            HashCode.Combine(StringComparer.Ordinal.GetHashCode(this.Id), this.Version);
    }
}
