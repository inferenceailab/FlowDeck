using FlowDeck.Api;
using FlowDeck.Core;
using FlowDeck.Core.Cluster;
using FlowDeck.Core.Persistence;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenTelemetry.Exporter;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

// A registry per host. Definitions are registered at startup by whatever hosts
// FlowDeck; the API only resolves them.
builder.Services.AddSingleton<WorkflowRegistry>();

// Registered so the engine, the dispatcher and the API all judge time the same
// way. A test can substitute it; without a registration each would silently
// fall back to TimeProvider.System and disagree about lease expiry.
builder.Services.TryAddSingleton(TimeProvider.System);

// Defaults to in-memory so the API runs and is testable with no database. A
// real host replaces this registration with EfCoreWorkflowStore.
builder.Services.AddSingleton<IWorkflowStore>(_ => new InMemoryWorkflowStore(new WorkflowDataSerializer()));

// One per host, disposed with it. The engine would otherwise fall back to its
// shared default, which is correct but leaves this host unable to hand the same
// meter to the scrape endpoint (#189).
builder.Services.AddSingleton<EngineMetrics>();
builder.Services.AddSingleton<EngineTracing>();

builder.Services.AddSingleton(provider => new WorkflowEngine(
    provider.GetRequiredService<WorkflowRegistry>(),
    provider.GetService<TimeProvider>(),
    provider.GetRequiredService<IWorkflowStore>(),

    // Resolved rather than left null, so the engine's instrumentation reaches
    // whatever sinks the host configured. The engine works without it - a null
    // logger is silent, not broken (ADR-0025 decision 1) - which is why this is
    // the host's line to write rather than the engine's to require.
    logger: provider.GetService<ILogger<WorkflowEngine>>(),
    metrics: provider.GetRequiredService<EngineMetrics>(),
    tracing: provider.GetRequiredService<EngineTracing>()));

// How this node behaves in a cluster. Validated at startup rather than on first
// poll, so a lease shorter than its own renewal interval fails the deployment
// instead of producing a cluster that quietly thrashes.
builder.Services.AddSingleton(_ =>
{
    var options = builder.Configuration.GetSection("FlowDeck:Cluster").Get<ClusterOptions>()
        ?? new ClusterOptions();

    options.Validate();

    return options;
});

builder.Services.AddSingleton(provider => new WorkflowDispatcher(
    provider.GetRequiredService<WorkflowEngine>(),
    provider.GetRequiredService<IWorkflowStore>(),
    provider.GetRequiredService<ClusterOptions>(),
    provider.GetService<TimeProvider>(),
    provider.GetRequiredService<EngineMetrics>()));

// Every node runs one, and they are all the same - no leader and no election
// (ADR-0023). This recovers work whose node died; it does not spread load.
builder.Services.AddHostedService<DispatcherHostedService>();

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
// Enums serialise by name rather than ordinal, declared on the types
// themselves in FlowDeck.Core rather than configured here. Found by generating
// a frontend client: the document declared InstanceStatus as `integer`, so the
// API was returning `"status": 2` - unreadable in a dashboard, and an ordinal
// that would silently change meaning if a status were ever inserted mid-enum.

// #28: a machine-readable description of the API, so clients can be generated
// rather than hand-written against prose.
builder.Services.AddOpenApi();

builder.Services.AddHealthChecks()
    .AddCheck<WorkflowStoreHealthCheck>("workflow-store", tags: ["ready"]);

// Scraped, always. The homelab runs two containers and no collector, so metrics
// that appeared only once an operator had built an observability stack would
// leave the default deployment observing nothing (ADR-0025 decision 4).
builder.Services.AddSingleton(provider =>
    new PrometheusExposition(provider.GetRequiredService<EngineMetrics>()));

// Tracing is the opposite: wired only when there is somewhere to send it.
// Standard OTEL_ variable first, so an operator configures FlowDeck the way
// they configure everything else; the FlowDeck: section is there for a host
// that keeps its settings in one file.
var otlpEndpoint = builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]
    ?? builder.Configuration["FlowDeck:Otlp:Endpoint"];

if (!string.IsNullOrWhiteSpace(otlpEndpoint))
{
    // gRPC by default, which is what the OTLP specification says and what a
    // collector on 4317 expects. Overridable because a homelab behind a proxy
    // that only speaks HTTP/1.1 is a real deployment, and the alternative is
    // telling that operator their traces cannot be exported.
    var protocol = builder.Configuration["OTEL_EXPORTER_OTLP_PROTOCOL"] switch
    {
        "http/protobuf" => OtlpExportProtocol.HttpProtobuf,
        _ => OtlpExportProtocol.Grpc,
    };

    var target = new Uri(otlpEndpoint);

    // OTEL_EXPORTER_OTLP_ENDPOINT is a *base* endpoint: the OTLP specification
    // says an http/protobuf exporter appends the signal's path to it, and a
    // collector's documented address is the base. The SDK does that appending
    // only for endpoints it read from the environment itself - one set through
    // OtlpExporterOptions is taken literally - so setting it here means doing
    // it here. Without this, every export POSTs to the collector's root and is
    // silently 404'd.
    if (protocol == OtlpExportProtocol.HttpProtobuf && target.AbsolutePath is "/" or "")
    {
        target = new Uri(target, "v1/traces");
    }

    builder.Services.AddOpenTelemetry()
        .WithTracing(tracing => tracing
            .AddSource(EngineTracing.SourceName)

            // The request half of the trace #188 exists to produce, and a
            // correctness requirement rather than a nicety: the default sampler
            // is ParentBased, so a workflow span whose parent is an unrecorded
            // request activity is not recorded either and nothing reaches the
            // collector at all.
            .AddAspNetCoreInstrumentation()
            .AddOtlpExporter(otlp =>
            {
                otlp.Endpoint = target;
                otlp.Protocol = protocol;
            }));
}

var app = builder.Build();

// Resolved now rather than on the first scrape. The exposition is a
// MeterListener, so one created lazily would have missed every measurement
// recorded before somebody first called /metrics - and the first scrape after
// a deploy would under-report exactly the runs an operator was checking on.
_ = app.Services.GetRequiredService<PrometheusExposition>();

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

// Served unconditionally, not only in Development. This API is deployed to a
// homelab behind the operator's own network boundary, and a description a
// client cannot fetch from the running server is a description that goes stale.
app.MapOpenApi();

// Prometheus text format, rendered by hand from FlowDeck's own meter. Outside
// the OpenAPI document deliberately: it is scraped by a collector that knows
// the exposition format, not called by a generated client.
app.MapGet("/metrics", (PrometheusExposition exposition) =>
        Results.Text(exposition.Render(), PrometheusExposition.ContentType))
    .ExcludeFromDescription();

app.MapWorkflowEndpoints();
app.MapInstanceEndpoints();

await app.RunAsync();

/// <summary>
/// Exposed so <c>WebApplicationFactory</c> can host the real application in
/// tests. Testing a copy of the composition root would prove nothing about the
/// one that ships.
/// </summary>
public partial class Program
{
    // Never instantiated - it exists only as a type argument for
    // WebApplicationFactory<Program>. A private constructor says so.
    private Program()
    {
    }
}
