using FlowDeck.Core;
using FlowDeck.Core.Persistence;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace FlowDeck.Api;

/// <summary>
/// Translates engine exceptions into HTTP responses.
/// </summary>
/// <remarks>
/// One place decides the mapping, so a client can rely on it rather than
/// inferring it per endpoint. Anything not listed is deliberately left to
/// surface as a 500: inventing a status code for an unrecognised fault would
/// dress a bug up as an expected condition.
///
/// <para>
/// Full RFC 9457 problem details - type URIs, validation payloads - is #27.
/// This establishes the status-code contract that #23's scenario needs, and
/// #27 fills in the body.
/// </para>
/// </remarks>
public sealed class FlowDeckExceptionHandler(IProblemDetailsService problemDetails) : IExceptionHandler
{
    /// <summary>
    /// Maps an engine exception to its status code, or null if unrecognised.
    /// </summary>
    /// <remarks>
    /// Public so the OpenAPI document (#28) and the API documentation (#61)
    /// describe the same mapping the code enforces, rather than a prose copy
    /// that drifts.
    /// </remarks>
    public static int? StatusCodeFor(Exception exception) => exception switch
    {
        DefinitionNotFoundException => StatusCodes.Status404NotFound,
        InstanceNotFoundException => StatusCodes.Status404NotFound,

        // The instance exists and the request is well-formed; it just cannot be
        // applied to the state the instance is in. That is a conflict, not a
        // bad request.
        InvalidStateTransitionException => StatusCodes.Status409Conflict,

        // The caller sent the wrong shape.
        InvalidInputTypeException => StatusCodes.Status400BadRequest,

        // A body that is not valid JSON is unambiguously the caller's mistake.
        // Left unmapped it surfaces as a 500, which tells a client to retry
        // against a server that is working perfectly.
        System.Text.Json.JsonException => StatusCodes.Status400BadRequest,
        BadHttpRequestException => StatusCodes.Status400BadRequest,

        // The definition is broken, which is a server-side deployment fault
        // rather than anything the caller did.
        InvalidWorkflowDefinitionException => StatusCodes.Status500InternalServerError,

        // Another writer moved first. A caller that re-reads and retries will
        // usually succeed, which is what 409 tells them.
        WorkflowStoreConcurrencyException => StatusCodes.Status409Conflict,
        DuplicateInstanceException => StatusCodes.Status409Conflict,

        _ => null,
    };

    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        if (StatusCodeFor(exception) is not { } statusCode)
        {
            // Unrecognised: let the default pipeline produce a 500 and log it.
            return false;
        }

        httpContext.Response.StatusCode = statusCode;

        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails = new ProblemDetails
            {
                Status = statusCode,
                Title = TitleFor(exception),

                // The field a client should branch on. Status codes are too
                // coarse: three problems here map to 409.
                Type = ProblemTypes.For(exception),

                // The engine's messages name the definition, instance or types
                // involved, which is exactly what an operator reading a log
                // needs. They contain no user data.
                Detail = exception.Message,

                // Which request this was about, per RFC 9457.
                Instance = $"{httpContext.Request.Method} {httpContext.Request.Path}",
            },
        }).ConfigureAwait(false);
    }

    private static string TitleFor(Exception exception) => exception switch
    {
        DefinitionNotFoundException => "Workflow definition not found",
        InstanceNotFoundException => "Workflow instance not found",
        InvalidStateTransitionException => "Invalid state transition",
        InvalidInputTypeException => "Invalid workflow input",
        InvalidWorkflowDefinitionException => "Invalid workflow definition",
        WorkflowStoreConcurrencyException => "Instance was modified concurrently",
        DuplicateInstanceException => "Instance already exists",
        System.Text.Json.JsonException => "Malformed request body",
        BadHttpRequestException => "Malformed request",
        _ => "Request failed",
    };
}
