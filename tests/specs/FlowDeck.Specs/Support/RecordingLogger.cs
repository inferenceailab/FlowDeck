using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace FlowDeck.Specs.Support;

/// <summary>One entry the engine emitted, with the scope it was inside.</summary>
/// <param name="Level">Severity, so a scenario can assert a failure stands out.</param>
/// <param name="EventId">
/// The event's stable identity. Asserted in preference to message text, which is
/// prose and may be reworded without the meaning changing.
/// </param>
/// <param name="Message">The rendered message, for scenarios about wording.</param>
/// <param name="State">
/// The structured fields, by name. This is what a structured sink stores and
/// therefore what an operator can query on - asserting it rather than the
/// rendered string is asserting the thing that has to be right.
/// </param>
/// <param name="Scope">The merged scope in force when the entry was written.</param>
/// <param name="Exception">The exception attached, if any.</param>
public sealed record RecordedEntry(
    LogLevel Level,
    EventId EventId,
    string Message,
    IReadOnlyDictionary<string, object?> State,
    IReadOnlyDictionary<string, object?> Scope,
    Exception? Exception)
{
    /// <summary>
    /// Looks a field up in the entry or in the scope it was written inside.
    /// </summary>
    /// <remarks>
    /// One lookup across both on purpose. Whether the instance id arrives as a
    /// message field or through a scope is an implementation choice
    /// (ADR-0025 decision 5); that an operator can find it is the requirement.
    /// </remarks>
    public object? Field(string name)
    {
        if (this.State.TryGetValue(name, out var value))
        {
            return value;
        }

        return this.Scope.TryGetValue(name, out var scoped) ? scoped : null;
    }
}

/// <summary>
/// An <see cref="ILogger"/> that keeps what it was told.
/// </summary>
/// <remarks>
/// Scopes are tracked in an <see cref="AsyncLocal{T}"/> rather than a plain
/// stack, because branches run concurrently since M7 (ADR-0024) and a shared
/// stack would attribute one branch's scope to another's entries. An async-local
/// value is inherited at the fork and diverges after it, which is the shape a
/// per-branch scope actually has.
/// </remarks>
public sealed class RecordingLogger : ILogger
{
    private static readonly AsyncLocal<IReadOnlyDictionary<string, object?>?> Current = new();

    private readonly ConcurrentQueue<RecordedEntry> entries = new();

    /// <summary>Everything written so far, oldest first.</summary>
    public IReadOnlyList<RecordedEntry> Entries => [.. this.entries];

    /// <summary>Entries carrying a given event name, whatever their level.</summary>
    public IReadOnlyList<RecordedEntry> Named(string eventName) =>
        [.. this.entries.Where(entry => string.Equals(entry.EventId.Name, eventName, StringComparison.Ordinal))];

    public IDisposable BeginScope<TState>(TState state)
        where TState : notnull
    {
        var merged = new Dictionary<string, object?>(StringComparer.Ordinal);

        foreach (var pair in Current.Value ?? new Dictionary<string, object?>(StringComparer.Ordinal))
        {
            merged[pair.Key] = pair.Value;
        }

        foreach (var pair in Pairs(state))
        {
            merged[pair.Key] = pair.Value;
        }

        var restore = Current.Value;
        Current.Value = merged;

        return new Scope(() => Current.Value = restore);
    }

    /// <summary>
    /// Always enabled, so a scenario sees everything the engine chose to emit.
    /// </summary>
    /// <remarks>
    /// A recorder that filtered would make "the engine logged nothing" and "this
    /// recorder discarded it" the same observation, and the scenarios here exist
    /// to tell those apart.
    /// </remarks>
    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        ArgumentNullException.ThrowIfNull(formatter);

        this.entries.Enqueue(new RecordedEntry(
            logLevel,
            eventId,
            formatter(state, exception),
            Pairs(state),
            Current.Value ?? new Dictionary<string, object?>(StringComparer.Ordinal),
            exception));
    }

    /// <summary>
    /// Reads a log state's structured fields.
    /// </summary>
    /// <remarks>
    /// Both message state and scope state arrive as
    /// <c>IReadOnlyList&lt;KeyValuePair&lt;string, object?&gt;&gt;</c> when the
    /// message was written with a template, which is every case here. Anything
    /// else contributes nothing rather than being coerced into a field with an
    /// invented name.
    /// </remarks>
    private static Dictionary<string, object?> Pairs<TState>(TState state)
    {
        var pairs = new Dictionary<string, object?>(StringComparer.Ordinal);

        if (state is IEnumerable<KeyValuePair<string, object?>> fields)
        {
            foreach (var field in fields)
            {
                pairs[field.Key] = field.Value;
            }
        }

        return pairs;
    }

    private sealed class Scope(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}

/// <summary>Hands the same recorder to whatever asks for a typed logger.</summary>
public sealed class RecordingLogger<T>(RecordingLogger inner) : ILogger<T>
{
    public IDisposable BeginScope<TState>(TState state)
        where TState : notnull => inner.BeginScope(state);

    public bool IsEnabled(LogLevel logLevel) => inner.IsEnabled(logLevel);

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter) =>
        inner.Log(logLevel, eventId, state, exception, formatter);
}
