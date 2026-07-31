using FlowDeck.Api;
using FlowDeck.Core;
using FlowDeck.Core.Persistence;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;

var builder = WebApplication.CreateBuilder(args);

// A registry per host. Definitions are registered at startup by whatever hosts
// FlowDeck; the API only resolves them.
builder.Services.AddSingleton<WorkflowRegistry>();

// Defaults to in-memory so the API runs and is testable with no database. A
// real host replaces this registration with EfCoreWorkflowStore.
builder.Services.AddSingleton<IWorkflowStore>(_ => new InMemoryWorkflowStore(new WorkflowDataSerializer()));

builder.Services.AddSingleton(provider => new WorkflowEngine(
    provider.GetRequiredService<WorkflowRegistry>(),
    provider.GetService<TimeProvider>(),
    provider.GetRequiredService<IWorkflowStore>()));

// Engine faults map to status codes in one place, so a client can rely on the
// mapping rather than inferring it per endpoint.
builder.Services.AddProblemDetails(options =>
    options.CustomizeProblemDetails = context =>
    {
        // Correlates a response a user is looking at with the server-side logs
        // for that request. Without it, "it returned a 500" is unactionable.
        context.ProblemDetails.Extensions["traceId"] =
            System.Diagnostics.Activity.Current?.Id ?? context.HttpContext.TraceIdentifier;

        // Applies to responses the framework produces without an exception -
        // routing 404s, parameter-binding 400s - which would otherwise carry no
        // instance at all.
        context.ProblemDetails.Instance ??=
            $"{context.HttpContext.Request.Method} {context.HttpContext.Request.Path}";
    });
builder.Services.AddExceptionHandler<FlowDeckExceptionHandler>();

// Readiness depends on the store; liveness deliberately does not. A node whose
// database is down should leave rotation, not be restarted.
builder.Services.AddHealthChecks()
    .AddCheck<WorkflowStoreHealthCheck>("workflow-store", tags: ["ready"]);

var app = builder.Build();

app.UseExceptionHandler();

// Without this, a status code produced without an exception - a routing 404, a
// parameter-binding 400 - returns an empty body. Clients would get problem
// details for some errors and nothing for others, which is not a contract.
app.UseStatusCodePages();

// Liveness: the process is up and the pipeline responds. No dependencies, so a
// database outage cannot trigger a restart loop that makes recovery harder.
app.MapHealthChecks("/health/live", new HealthCheckOptions
{
    Predicate = _ => false,
});

// Readiness: this node can actually serve requests.
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready"),
});

app.MapWorkflowEndpoints();
app.MapInstanceEndpoints();

await app.RunAsync();

/// <summary>
/// Exposed so <c>WebApplicationFactory</c> can host the real application in
/// tests. Testing a copy of the composition root would prove nothing about the
/// one that ships.
/// </summary>
public partial class Program;
