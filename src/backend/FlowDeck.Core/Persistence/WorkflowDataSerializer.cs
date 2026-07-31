using System.Text.Json;

namespace FlowDeck.Core.Persistence;

/// <summary>
/// Thrown when workflow data cannot be stored or restored.
/// </summary>
public sealed class WorkflowDataSerializationException : FlowDeckException
{
    public WorkflowDataSerializationException(string key, Type type)
        : base($"Workflow data key '{key}' holds {type.Name}, which is not an allowed persisted type. "
             + "Register it with WorkflowDataSerializerOptions.Allow<T>() or store a simpler value.")
    {
        this.Key = key;
        this.TypeName = type.Name;
    }

    public WorkflowDataSerializationException(string key, string typeName)
        : base($"Workflow data key '{key}' was stored as type '{typeName}', which is not an allowed persisted type. "
             + "It may have been written by a build that allowed more types than this one.")
    {
        this.Key = key;
        this.TypeName = typeName;
    }

    public string Key { get; }

    public string TypeName { get; }
}

/// <summary>
/// Controls which types may be written to, and read from, persisted workflow
/// data.
/// </summary>
/// <remarks>
/// An allow-list, not a deny-list. Deserialising a type named in stored data is
/// the classic remote-code-execution vector: whoever can write to the store
/// chooses which type gets constructed. Resolving only names on this list means
/// a tampered row can at worst produce a type the application already trusts.
///
/// See ADR-0014.
/// </remarks>
public sealed class WorkflowDataSerializerOptions
{
    private readonly Dictionary<string, Type> allowed = new(StringComparer.Ordinal);

    public WorkflowDataSerializerOptions()
    {
        // Primitives an author will reach for without thinking about
        // persistence. Everything else must be opted in deliberately.
        this.Allow<string>();
        this.Allow<bool>();
        this.Allow<byte>();
        this.Allow<short>();
        this.Allow<int>();
        this.Allow<long>();
        this.Allow<float>();
        this.Allow<double>();
        this.Allow<decimal>();
        this.Allow<Guid>();
        this.Allow<DateTime>();
        this.Allow<DateTimeOffset>();
        this.Allow<TimeSpan>();
        this.Allow<byte[]>();
    }

    /// <summary>Permits <typeparamref name="T"/> in persisted workflow data.</summary>
    public WorkflowDataSerializerOptions Allow<T>()
    {
        var type = typeof(T);
        this.allowed[Key(type)] = type;
        return this;
    }

    internal bool IsAllowed(Type type) => this.allowed.ContainsKey(Key(type));

    internal bool TryResolve(string typeName, out Type type) =>
        this.allowed.TryGetValue(typeName, out type!);

    /// <summary>
    /// The stored name for a type. Deliberately not assembly-qualified: a
    /// stored row must not survive only as long as an assembly version does.
    /// </summary>
    internal static string Key(Type type) => type.FullName ?? type.Name;
}

/// <summary>
/// Converts workflow data to and from JSON for text-based providers.
/// </summary>
/// <remarks>
/// Each value is stored with its type name alongside its JSON, because a
/// workflow's data shape is author-defined and only known at runtime. Without
/// the tag, <c>42</c> and <c>"42"</c> are indistinguishable on the way back in.
///
/// Nulls are preserved with a null type tag, so an explicitly cleared value
/// stays distinct from an absent key - the contract ADR-0005 established.
/// </remarks>
public sealed class WorkflowDataSerializer(WorkflowDataSerializerOptions? options = null)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.General);

    private readonly WorkflowDataSerializerOptions options = options ?? new WorkflowDataSerializerOptions();

    /// <summary>
    /// Serialises workflow data.
    /// </summary>
    /// <exception cref="WorkflowDataSerializationException">
    /// A value's type is not on the allow-list.
    /// </exception>
    public string Serialize(IReadOnlyDictionary<string, object?> data)
    {
        ArgumentNullException.ThrowIfNull(data);

        var envelopes = new Dictionary<string, Envelope>(StringComparer.Ordinal);

        foreach (var (key, value) in data)
        {
            if (value is null)
            {
                envelopes[key] = new Envelope(null, null);
                continue;
            }

            var type = value.GetType();

            if (!this.options.IsAllowed(type))
            {
                throw new WorkflowDataSerializationException(key, type);
            }

            envelopes[key] = new Envelope(
                WorkflowDataSerializerOptions.Key(type),
                JsonSerializer.Serialize(value, type, Json));
        }

        return JsonSerializer.Serialize(envelopes, Json);
    }

    /// <summary>
    /// Restores workflow data.
    /// </summary>
    /// <exception cref="WorkflowDataSerializationException">
    /// A stored type name is not on the allow-list. Never resolves an arbitrary
    /// type by name.
    /// </exception>
    public IReadOnlyDictionary<string, object?> Deserialize(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        if (json.Length == 0)
        {
            return new Dictionary<string, object?>(StringComparer.Ordinal);
        }

        var envelopes = JsonSerializer.Deserialize<Dictionary<string, Envelope>>(json, Json)
            ?? [];

        var data = new Dictionary<string, object?>(StringComparer.Ordinal);

        foreach (var (key, envelope) in envelopes)
        {
            if (envelope.Type is null || envelope.Value is null)
            {
                data[key] = null;
                continue;
            }

            if (!this.options.TryResolve(envelope.Type, out var type))
            {
                throw new WorkflowDataSerializationException(key, envelope.Type);
            }

            data[key] = JsonSerializer.Deserialize(envelope.Value, type, Json);
        }

        return data;
    }

    /// <summary>A value plus the type it must be restored as.</summary>
    private sealed record Envelope(string? Type, string? Value);
}
