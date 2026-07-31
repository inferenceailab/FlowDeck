using FlowDeck.Core;

namespace FlowDeck.Specs.Support;

/// <summary>
/// Step bodies the feature files describe.
/// </summary>
/// <remarks>
/// Shared across features because scenarios describe steps in the same terms -
/// "a step that throws", "steps A, B and C". Defining them once means a
/// scenario's wording maps to one implementation rather than several that have
/// quietly diverged.
/// </remarks>
public static class SpecSteps
{
    /// <summary>Records its name and advances.</summary>
    public sealed class Recording(List<string> log, string name) : IStep
    {
        public ValueTask<Outcome> ExecuteAsync(IStepContext context, CancellationToken cancellationToken = default)
        {
            log.Add(name);
            return ValueTask.FromResult(Outcome.Next);
        }
    }

    /// <summary>Records its name, then throws.</summary>
    public sealed class Throwing(List<string> log, string name, Exception? error = null) : IStep
    {
        public ValueTask<Outcome> ExecuteAsync(IStepContext context, CancellationToken cancellationToken = default)
        {
            log.Add(name);
            throw error ?? new InvalidOperationException($"{name} failed");
        }
    }

    /// <summary>Parks the instance at this step, every time it runs.</summary>
    public sealed class Suspending(List<string> log, string name) : IStep
    {
        public ValueTask<Outcome> ExecuteAsync(IStepContext context, CancellationToken cancellationToken = default)
        {
            log.Add(name);
            return ValueTask.FromResult(Outcome.Suspend);
        }
    }

    /// <summary>Writes one value into the workflow data.</summary>
    public sealed class Writing(string key, object? value) : IStep
    {
        public ValueTask<Outcome> ExecuteAsync(IStepContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);

            context.Data.Set(key, value);
            return ValueTask.FromResult(Outcome.Next);
        }
    }

    /// <summary>Reads one value out of the workflow data.</summary>
    public sealed class Reading<T>(string key, Action<T?> capture) : IStep
    {
        public ValueTask<Outcome> ExecuteAsync(IStepContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);

            capture(context.Data.TryGet<T>(key, out var value) ? value : default);
            return ValueTask.FromResult(Outcome.Next);
        }
    }

    /// <summary>Reads the instance's typed input.</summary>
    public sealed class ReadingInput<T>(Action<T?> capture) : IStep
    {
        public ValueTask<Outcome> ExecuteAsync(IStepContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);

            capture(context.Input is T typed ? typed : default);
            return ValueTask.FromResult(Outcome.Next);
        }
    }
}

/// <summary>
/// A definition assembled by a scenario rather than written as a class.
/// </summary>
/// <remarks>
/// Feature files describe workflows in prose - "steps A, B and C", "one step
/// that succeeds" - so the steps have to build one at runtime. A fixed set of
/// hand-written definitions would mean every new scenario needs a new class,
/// and the feature file would stop being the specification.
/// </remarks>
public sealed class SpecWorkflow(string id, int version, Action<IWorkflowBuilder> declare) : IWorkflowDefinition
{
    public string Id => id;

    public int Version => version;

    public void Build(IWorkflowBuilder builder) => declare(builder);
}

/// <summary>A definition declaring a typed input.</summary>
public sealed class SpecWorkflow<TInput>(string id, int version, Action<IWorkflowBuilder> declare)
    : IWorkflowDefinition<TInput>
{
    public string Id => id;

    public int Version => version;

    public void Build(IWorkflowBuilder builder) => declare(builder);
}

/// <summary>The typed input the input scenarios use.</summary>
public sealed record OrderRequest(int Id);
