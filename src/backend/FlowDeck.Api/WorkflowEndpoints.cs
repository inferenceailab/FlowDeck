using FlowDeck.Core;

namespace FlowDeck.Api;

/// <summary>
/// The body returned when an instance is accepted.
/// </summary>
/// <param name="InstanceId">Identifier for the new instance.</param>
/// <param name="Status">
/// Status the instance had reached when the request returned. It may already be
/// <c>Completed</c> for a short workflow, or <c>Suspended</c> for one that
/// parks immediately.
/// </param>
public sealed record StartInstanceResponse(Guid InstanceId, InstanceStatus Status);

/// <summary>
/// A registered workflow definition, as the API describes it.
/// </summary>
/// <param name="Id">Definition identifier.</param>
/// <param name="Version">Definition version.</param>
/// <param name="InputTypeName">
/// Name of the input type this definition requires, or <see langword="null"/> if
/// it takes none. The name only - not a schema, and never an assembly-qualified
/// name, which would tell a caller more about the deployment than it needs.
/// </param>
public sealed record WorkflowDefinitionResponse(string Id, int Version, string? InputTypeName);

/// <summary>
/// HTTP surface for starting and inspecting workflow instances.
/// </summary>
public static class WorkflowEndpoints
{
    /// <summary>Maps the workflow control-plane endpoints.</summary>
    public static IEndpointRouteBuilder MapWorkflowEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var workflows = endpoints.MapGroup("/api/workflows");

        workflows.MapPost("/{definitionId}/instances", StartAsync)
            .WithName("StartWorkflowInstance")
            .WithSummary("Starts a new instance of a workflow definition.");

        workflows.MapGet("", ListDefinitionsAsync)
            .WithName("ListWorkflowDefinitions")
            .WithSummary("Lists the workflow definitions this host has registered.");

        return endpoints;
    }

    /// <summary>
    /// Lists every registered definition, by id then version.
    /// </summary>
    /// <remarks>
    /// Answers the question an operator actually has after a deployment: "does
    /// this host know about the workflow I just shipped, at the version I
    /// expect?" Without it that is only discoverable by starting an instance,
    /// which has side effects.
    ///
    /// <para>
    /// Read-only. Definitions are C# classes registered at startup, so there is
    /// nothing to POST - the brief specifies steps implemented directly in C#,
    /// and #40 is where authoring over the wire would be decided.
    /// </para>
    /// </remarks>
    private static Task<IResult> ListDefinitionsAsync(WorkflowRegistry registry)
    {
        var definitions = registry.GetAll()
            .Select(definition => new WorkflowDefinitionResponse(
                definition.Id,
                definition.Version,
                definition.InputType?.Name))
            .ToArray();

        return Task.FromResult(Results.Ok(definitions));
    }

    /// <summary>
    /// Starts an instance of <paramref name="definitionId"/>.
    /// </summary>
    /// <remarks>
    /// Returns <c>202 Accepted</c> rather than <c>201 Created</c>. The instance
    /// exists, but the work it represents has not finished - and for a workflow
    /// that suspends, may not for days. <c>201</c> would imply the request's
    /// effect is complete.
    ///
    /// <para>
    /// The version defaults to the latest registered rather than being
    /// required. A caller starting a workflow usually wants "the current one",
    /// and forcing an explicit version would make every client redeploy on
    /// each version bump.
    /// </para>
    /// </remarks>
    private static async Task<IResult> StartAsync(
        string definitionId,
        WorkflowEngine engine,
        WorkflowRegistry registry,
        HttpContext http,
        int? version = null,
        CancellationToken cancellationToken = default)
    {
        var definition = version is { } requested
            ? registry.Get(definitionId, requested)
            : registry.GetLatest(definitionId);

        object? input = null;

        if (definition.InputType is { } inputType && http.Request.ContentLength > 0)
        {
            input = await http.Request
                .ReadFromJsonAsync(inputType, cancellationToken)
                .ConfigureAwait(false);
        }

        var instance = await engine
            .StartAsync(definition.Id, definition.Version, input, cancellationToken)
            .ConfigureAwait(false);

        // Location points at the instance resource so a caller can poll it
        // without constructing the URL itself.
        return Results.Accepted(
            $"/api/instances/{instance.Id}",
            new StartInstanceResponse(instance.Id, instance.Status));
    }
}
