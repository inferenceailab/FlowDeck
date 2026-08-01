using FlowDeck.Core;
using Microsoft.AspNetCore.Http.HttpResults;

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
/// One branch leaving a step, as the API describes it.
/// </summary>
/// <param name="Name">
/// What the step returns to select this branch, or for a fork arm a label.
/// </param>
/// <param name="IsConditional">
/// Whether a condition over workflow data selects this branch, rather than the
/// step naming it.
/// </param>
/// <param name="IsParallel">
/// Whether this is one arm of a fork, in which case every arm runs.
/// </param>
/// <param name="Steps">The branch body, in declaration order.</param>
/// <remarks>
/// <b>The condition itself is not here, and cannot be.</b> It is a compiled
/// delegate, so nothing can recover what it tests. A caller learns that a
/// branch is decided by data rather than by the step, which is enough to draw
/// the two apart and is the most that is true.
/// </remarks>
public sealed record WorkflowBranchResponse(
    string Name,
    bool IsConditional,
    bool IsParallel,
    IReadOnlyList<WorkflowStepResponse> Steps);

/// <summary>
/// One declared step, as the API describes it.
/// </summary>
/// <param name="MaxAttempts">
/// How many times the step may execute in total, including the first. One for a
/// step that does not retry, so a client rendering "N attempts" never has to
/// special-case zero — the same reason step history reports attempt 1.
/// </param>
/// <param name="HasCompensation">
/// Whether the step declares an action that undoes it. Not <c>compensated</c>:
/// on an instance that word means rollback already ran, and a definition has
/// not run at all.
/// </param>
/// <param name="Branches">
/// Branches leaving this step, or empty for a plain sequential step.
/// </param>
/// <remarks>
/// The retry <i>policy</i> is reduced to its attempt count. Delays are jittered
/// and capped, so the schedule a definition would produce is not knowable until
/// it runs; the attempt count is the part that is a property of the definition.
/// </remarks>
public sealed record WorkflowStepResponse(
    string Name,
    int MaxAttempts,
    bool HasCompensation,
    IReadOnlyList<WorkflowBranchResponse> Branches)
{
    /// <summary>Projects a declared step, and everything below it, for the wire.</summary>
    public static WorkflowStepResponse From(StepDeclaration step)
    {
        ArgumentNullException.ThrowIfNull(step);

        return new WorkflowStepResponse(
            step.Name,
            step.RetryPolicy.MaxAttempts,

            // Null means the step declares no undo, which is different from an
            // undo that does nothing — so this is presence, not truthiness of
            // some flag the author set.
            step.Compensation is not null,
            [.. step.Branches.Select(From)]);
    }

    private static WorkflowBranchResponse From(BranchDeclaration branch) =>
        new(
            branch.Name,
            branch.Condition is not null,
            branch.IsParallel,
            [.. branch.Steps.Select(From)]);
}

/// <summary>
/// A definition together with the shape it declares.
/// </summary>
/// <remarks>
/// Separate from <see cref="WorkflowDefinitionResponse"/> rather than the list
/// growing a <c>steps</c> field. Listing answers "is my workflow deployed", is
/// hit on every dashboard load, and would otherwise compile every registered
/// definition to answer it.
/// </remarks>
public sealed record WorkflowDefinitionDetailResponse(
    string Id,
    int Version,
    string? InputTypeName,
    IReadOnlyList<WorkflowStepResponse> Steps);

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

        workflows.MapGet("/{definitionId}", GetDefinitionAsync)
            .WithName("GetWorkflowDefinition")
            .WithSummary("Describes one definition: the steps it declares and the branches leaving them.");

        return endpoints;
    }

    /// <summary>
    /// Describes one definition's shape.
    /// </summary>
    /// <remarks>
    /// The list endpoint says a workflow exists; nothing said what it does, so
    /// there was nothing for a dashboard to render but a name.
    ///
    /// <para>
    /// The version defaults to the latest registered, matching how an instance
    /// is started — an operator looking at a workflow means the one that would
    /// run now, and pinning stays available for reading an older shape an
    /// in-flight instance is still executing.
    /// </para>
    ///
    /// <para>
    /// An unknown id raises <see cref="DefinitionNotFoundException"/>, which
    /// the handler maps to <c>404</c>. Checking here as well would put the
    /// mapping in a second place.
    /// </para>
    /// </remarks>
    private static Task<Ok<WorkflowDefinitionDetailResponse>> GetDefinitionAsync(
        string definitionId,
        WorkflowRegistry registry,
        int? version = null)
    {
        var definition = version is { } requested
            ? registry.Get(definitionId, requested)
            : registry.GetLatest(definitionId);

        // Compiled per request rather than cached. Build is allowed to compose
        // steps from injected dependencies, so its result is not guaranteed
        // stable for a given definition - and the engine already pays this on
        // every instance start, which is far more frequent than this.
        var steps = WorkflowGraph.Of(definition);

        return Task.FromResult(TypedResults.Ok(new WorkflowDefinitionDetailResponse(
            definition.Id,
            definition.Version,
            definition.InputType?.Name,
            [.. steps.Select(WorkflowStepResponse.From)])));
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
    private static Task<Ok<WorkflowDefinitionResponse[]>> ListDefinitionsAsync(WorkflowRegistry registry)
    {
        var definitions = registry.GetAll()
            .Select(definition => new WorkflowDefinitionResponse(
                definition.Id,
                definition.Version,
                definition.InputType?.Name))
            .ToArray();

        return Task.FromResult(TypedResults.Ok(definitions));
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
    private static async Task<Accepted<StartInstanceResponse>> StartAsync(
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
        return TypedResults.Accepted(
            $"/api/instances/{instance.Id}",
            new StartInstanceResponse(instance.Id, instance.Status));
    }
}
