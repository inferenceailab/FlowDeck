using FlowDeck.Api;
using FlowDeck.Core;
using FlowDeck.Core.Persistence;

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

var app = builder.Build();

app.UseExceptionHandler();

app.MapWorkflowEndpoints();

await app.RunAsync();

/// <summary>
/// Exposed so <c>WebApplicationFactory</c> can host the real application in
/// tests. Testing a copy of the composition root would prove nothing about the
/// one that ships.
/// </summary>
public partial class Program;
