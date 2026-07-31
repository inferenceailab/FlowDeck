using FlowDeck.Core;
using Microsoft.AspNetCore.Http.HttpResults;
using FlowDeck.Core.Persistence;

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
/// One page of instances.
/// </summary>
/// <param name="Items">The instances on this page.</param>
/// <param name="Total">
/// How many instances match the filter, ignoring paging. A client cannot render
/// "page 3 of 12" from a page alone.
/// </param>
/// <param name="Page">One-based page number.</param>
/// <param name="PageSize">Maximum items per page.</param>
public sealed record InstancePage(
    IReadOnlyList<InstanceResponse> Items,
    int Total,
    int Page,
    int PageSize);

/// <summary>
/// One step execution, as the API represents it.
/// </summary>
/// <param name="Sequence">Position in this instance's history, from 1.</param>
/// <param name="DurationMs">
/// How long the step took. Computed here rather than left to each client, so
/// every consumer agrees on it.
/// </param>
public sealed record StepHistoryResponse(
    int Sequence,
    string StepName,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    double DurationMs,
    StepStatus Status,
    string? ErrorType,
    string? ErrorMessage)
{
    /// <summary>Projects a stored history entry for the wire.</summary>
    public static StepHistoryResponse From(StepHistoryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        return new StepHistoryResponse(
            entry.Sequence,
            entry.StepName,
            entry.StartedAt,
            entry.CompletedAt,
            (entry.CompletedAt - entry.StartedAt).TotalMilliseconds,
            entry.Status,
            entry.ErrorType,
            entry.ErrorMessage);
    }
}

/// <summary>
/// HTTP surface for inspecting and operating on instances.
/// </summary>
public static class InstanceEndpoints
{
    /// <summary>
    /// Largest page a caller may request.
    /// </summary>
    /// <remarks>
    /// An unbounded <c>pageSize</c> lets one request pull the whole table,
    /// which is a denial-of-service vector long before it is a slow dashboard.
    /// Requests above this are clamped rather than rejected: the caller still
    /// gets data, and <c>PageSize</c> in the response says what they actually
    /// got.
    /// </remarks>
    public const int MaxPageSize = 200;

    /// <summary>Default page size when none is supplied.</summary>
    public const int DefaultPageSize = 50;

    /// <summary>Maps the instance endpoints.</summary>
    public static IEndpointRouteBuilder MapInstanceEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var instances = endpoints.MapGroup("/api/instances");

        instances.MapGet("/{instanceId:guid}", GetAsync)
            .WithName("GetWorkflowInstance")
            .WithSummary("Retrieves a single workflow instance.");

        instances.MapGet("", ListAsync)
            .WithName("ListWorkflowInstances")
            .WithSummary("Lists workflow instances, newest first.");

        instances.MapPost("/{instanceId:guid}/cancel", CancelAsync)
            .WithName("CancelWorkflowInstance")
            .WithSummary("Stops a workflow instance permanently.");

        instances.MapGet("/{instanceId:guid}/history", GetHistoryAsync)
            .WithName("GetWorkflowInstanceHistory")
            .WithSummary("Reads an instance's execution history, in order.");

        return endpoints;
    }

    /// <summary>
    /// Reads an instance's execution history.
    /// </summary>
    /// <remarks>
    /// Returns an empty array for an unknown instance rather than 404. History
    /// removed by retention (#20) is not an exceptional case, and the store
    /// already behaves this way - a 404 here would make a purged instance look
    /// like a client error.
    ///
    /// <para>
    /// Unpaged. A workflow with thousands of attempts would return a large
    /// array, which becomes real once retries (#37) exist; no workflow today
    /// produces enough entries to justify the interface now. Recorded in
    /// <c>docs/api.md</c> rather than left as a surprise.
    /// </para>
    /// </remarks>
    private static async Task<Ok<StepHistoryResponse[]>> GetHistoryAsync(
        Guid instanceId,
        WorkflowEngine engine,
        CancellationToken cancellationToken = default)
    {
        var history = await engine.GetHistoryAsync(instanceId, cancellationToken).ConfigureAwait(false);

        return TypedResults.Ok(history.Select(StepHistoryResponse.From).ToArray());
    }

    /// <summary>
    /// Cancels an instance.
    /// </summary>
    /// <remarks>
    /// <c>POST /cancel</c> rather than <c>DELETE</c> on the instance. Cancelling
    /// does not remove anything - the instance stays queryable, keeps its
    /// history and keeps the step it stopped at. <c>DELETE</c> would promise
    /// removal, and #20's purge is the thing that actually removes.
    ///
    /// <para>
    /// Returns <c>202 Accepted</c> for symmetry with starting: the instance has
    /// been told to stop, and the engine records that immediately. A caller
    /// wanting confirmation re-reads the instance.
    /// </para>
    ///
    /// <para>
    /// Cancelling a terminal instance raises
    /// <see cref="InvalidStateTransitionException"/>, which the handler maps to
    /// <c>409 Conflict</c> - the request is well-formed but cannot apply to the
    /// state the instance is in.
    /// </para>
    /// </remarks>
    private static async Task<Accepted<InstanceResponse>> CancelAsync(
        Guid instanceId,
        WorkflowEngine engine,
        CancellationToken cancellationToken = default)
    {
        var instance = await engine.CancelAsync(instanceId, cancellationToken).ConfigureAwait(false);

        return TypedResults.Accepted(
            $"/api/instances/{instance.Id}",
            InstanceResponse.From(instance));
    }

    /// <summary>
    /// Lists instances, newest first, with paging and optional filters.
    /// </summary>
    /// <remarks>
    /// Paging is one-based because it is user-facing: a dashboard shows "page
    /// 1", not "page 0". The offset arithmetic lives here rather than in every
    /// client.
    /// </remarks>
    private static async Task<Ok<InstancePage>> ListAsync(
        WorkflowEngine engine,
        InstanceStatus? status = null,
        string? definitionId = null,
        int page = 1,
        int pageSize = DefaultPageSize,
        CancellationToken cancellationToken = default)
    {
        // Clamped rather than rejected. A client that asks for page 0 or 10,000
        // items has made a mistake it can recover from, and the response states
        // what it actually got.
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, MaxPageSize);

        var filter = new InstanceFilter
        {
            Status = status,
            DefinitionId = definitionId,
            Skip = (page - 1) * pageSize,
            Take = pageSize,
        };

        var instances = await engine.ListInstancesAsync(filter, cancellationToken).ConfigureAwait(false);
        var total = await engine.CountInstancesAsync(filter, cancellationToken).ConfigureAwait(false);

        return TypedResults.Ok(new InstancePage(
            [.. instances.Select(InstanceResponse.From)],
            total,
            page,
            pageSize));
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
    private static async Task<Ok<InstanceResponse>> GetAsync(
        Guid instanceId,
        WorkflowEngine engine,
        CancellationToken cancellationToken = default)
    {
        // GetInstanceAsync throws InstanceNotFoundException, which the handler
        // maps to 404 with problem details. Checking here as well would
        // duplicate the mapping in a second place.
        var instance = await engine.GetInstanceAsync(instanceId, cancellationToken).ConfigureAwait(false);

        return TypedResults.Ok(InstanceResponse.From(instance));
    }
}
