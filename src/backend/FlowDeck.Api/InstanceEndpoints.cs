using FlowDeck.Core;

namespace FlowDeck.Api;

/// <summary>
/// A workflow instance as the API represents it.
/// </summary>
/// <remarks>
/// A deliberate projection of <see cref="WorkflowInstance"/> rather than the
/// engine type serialised directly. The engine's <c>Error</c> is a live
/// <see cref="Exception"/>: serialising it would leak stack traces and internal
/// type names to any caller. Only the type name and message cross the boundary.
/// </remarks>
public sealed record InstanceResponse(
    Guid Id,
    string DefinitionId,
    int DefinitionVersion,
    InstanceStatus Status,
    int CurrentStepIndex,
    string? CurrentStepName,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt,
    string? FailedStepName,
    string? ErrorType,
    string? ErrorMessage)
{
    /// <summary>Projects an engine instance for the wire.</summary>
    public static InstanceResponse From(WorkflowInstance instance)
    {
        ArgumentNullException.ThrowIfNull(instance);

        return new InstanceResponse(
            instance.Id,
            instance.DefinitionId,
            instance.DefinitionVersion,
            instance.Status,
            instance.CurrentStepIndex,
            instance.CurrentStepName,
            instance.CreatedAt,
            instance.CompletedAt,
            instance.FailedStepName,
            instance.ErrorType,
            instance.ErrorMessage);
    }
}

/// <summary>
/// HTTP surface for inspecting and operating on instances.
/// </summary>
public static class InstanceEndpoints
{
    /// <summary>Maps the instance endpoints.</summary>
    public static IEndpointRouteBuilder MapInstanceEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var instances = endpoints.MapGroup("/api/instances");

        instances.MapGet("/{instanceId:guid}", GetAsync)
            .WithName("GetWorkflowInstance")
            .WithSummary("Retrieves a single workflow instance.");

        return endpoints;
    }

    /// <summary>
    /// Retrieves one instance.
    /// </summary>
    /// <remarks>
    /// The route constrains <c>instanceId</c> to a GUID, so a malformed id is a
    /// 404 from routing rather than a 400 from parsing. Both are defensible;
    /// 404 is chosen because "no such instance" is true either way and it
    /// avoids leaking which id formats the server considers plausible.
    /// </remarks>
    private static async Task<IResult> GetAsync(
        Guid instanceId,
        WorkflowEngine engine,
        CancellationToken cancellationToken = default)
    {
        // GetInstanceAsync throws InstanceNotFoundException, which the handler
        // maps to 404 with problem details. Checking here as well would
        // duplicate the mapping in a second place.
        var instance = await engine.GetInstanceAsync(instanceId, cancellationToken).ConfigureAwait(false);

        return Results.Ok(InstanceResponse.From(instance));
    }
}
