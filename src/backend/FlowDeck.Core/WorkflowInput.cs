namespace FlowDeck.Core;

/// <summary>
/// Thrown when the input supplied at start does not match what the definition
/// declares.
/// </summary>
/// <param name="ExpectedType">
/// The declared input type, or <see langword="null"/> when the definition takes
/// no input.
/// </param>
/// <param name="ActualType">
/// The supplied input's type, or <see langword="null"/> when none was supplied.
/// </param>
public sealed class InvalidInputTypeException(string definitionId, Type? expectedType, Type? actualType)
    : FlowDeckException(Describe(definitionId, expectedType, actualType))
{
    public string DefinitionId { get; } = definitionId;

    public Type? ExpectedType { get; } = expectedType;

    public Type? ActualType { get; } = actualType;

    private static string Describe(string definitionId, Type? expected, Type? actual) => (expected, actual) switch
    {
        (null, not null) =>
            $"Workflow '{definitionId}' takes no input, but {actual.Name} was supplied.",
        (not null, null) =>
            $"Workflow '{definitionId}' requires input of type {expected.Name}, but none was supplied.",
        (not null, not null) =>
            $"Workflow '{definitionId}' requires input of type {expected.Name}, but {actual.Name} was supplied.",
        _ => $"Workflow '{definitionId}' received unexpected input.",
    };
}

/// <summary>
/// A workflow definition that requires input of a specific type.
/// </summary>
/// <remarks>
/// Declaring the type on the interface lets the engine reject a mismatched
/// start before any step runs, and lets the HTTP layer (#23) validate a request
/// body without starting an instance.
/// </remarks>
public interface IWorkflowDefinition<TInput> : IWorkflowDefinition
{
    /// <inheritdoc />
    Type? IWorkflowDefinition.InputType => typeof(TInput);
}

/// <summary>
/// Step-side access to the input an instance was started with.
/// </summary>
public static class StepContextExtensions
{
    /// <summary>
    /// Reads the instance input.
    /// </summary>
    /// <exception cref="InvalidInputTypeException">
    /// The instance has no input, or its input is not a <typeparamref name="TInput"/>.
    /// </exception>
    public static TInput GetInput<TInput>(this IStepContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Input is null)
        {
            throw new InvalidInputTypeException(context.StepName, typeof(TInput), null);
        }

        if (context.Input is not TInput typed)
        {
            throw new InvalidInputTypeException(context.StepName, typeof(TInput), context.Input.GetType());
        }

        return typed;
    }
}
