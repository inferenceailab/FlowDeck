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
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<FlowDeckExceptionHandler>();

// Readiness depends on the store; liveness deliberately does not. A node whose
// database is down should leave rotation, not be restarted.
builder.Services.AddHealthChecks()
    .AddCheck<WorkflowStoreHealthCheck>("workflow-store", tags: ["ready"]);

var app = builder.Build();

app.UseExceptionHandler();

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
