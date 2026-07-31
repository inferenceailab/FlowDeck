using FlowDeck.Core;
using FlowDeck.Core.Persistence;

namespace FlowDeck.Api;

/// <summary>
/// Stable <c>type</c> URIs for the problems this API reports.
/// </summary>
/// <remarks>
/// RFC 9457's <c>type</c> is the field a client is meant to branch on. Status
/// codes are too coarse - three different problems here map to 409, and a
/// client that wants to distinguish "already cancelled" from "another writer
/// won" cannot do it from the status alone, and should not be parsing prose out
/// of <c>detail</c>.
///
/// <para>
/// These are <b>identifiers first</b>. RFC 9457 does not require them to
/// resolve, but pointing them at the error documentation costs nothing and
/// turns an unfamiliar value in a log into something an operator can paste into
/// a browser.
/// </para>
///
/// <para>
/// <b>They are part of the API contract.</b> Changing one breaks clients
/// branching on it, exactly as renaming a field would.
/// </para>
/// </remarks>
public static class ProblemTypes
{
    private const string Base = "https://github.com/inferenceailab/FlowDeck/blob/main/docs/api-errors.md";

    public const string DefinitionNotFound = $"{Base}#definition-not-found";
    public const string InstanceNotFound = $"{Base}#instance-not-found";
    public const string InvalidStateTransition = $"{Base}#invalid-state-transition";
    public const string InvalidInput = $"{Base}#invalid-input";
    public const string MalformedRequest = $"{Base}#malformed-request";
    public const string ConcurrentModification = $"{Base}#concurrent-modification";
    public const string DuplicateInstance = $"{Base}#duplicate-instance";
    public const string InvalidDefinition = $"{Base}#invalid-definition";

    /// <summary>
    /// The <c>type</c> URI for an exception, or null if it is not a recognised
    /// problem.
    /// </summary>
    public static string? For(Exception exception) => exception switch
    {
        DefinitionNotFoundException => DefinitionNotFound,
        InstanceNotFoundException => InstanceNotFound,
        InvalidStateTransitionException => InvalidStateTransition,
        InvalidInputTypeException => InvalidInput,
        System.Text.Json.JsonException => MalformedRequest,
        BadHttpRequestException => MalformedRequest,
        WorkflowStoreConcurrencyException => ConcurrentModification,
        DuplicateInstanceException => DuplicateInstance,
        InvalidWorkflowDefinitionException => InvalidDefinition,
        _ => null,
    };
}
