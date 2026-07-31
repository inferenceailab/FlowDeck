using FlowDeck.Core;
using FlowDeck.Core.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace FlowDeck.Api.Tests;

/// <summary>
/// Hosts the real API in-process for tests.
/// </summary>
/// <remarks>
/// Uses <c>WebApplicationFactory&lt;Program&gt;</c> against the shipping
/// composition root rather than a hand-built pipeline. A test that assembles
/// its own services proves nothing about the application that actually runs -
/// a missing registration in <c>Program.cs</c> would pass every test and fail
/// on startup.
/// </remarks>
public sealed class FlowDeckApiFactory : WebApplicationFactory<Program>
{
    private readonly List<IWorkflowDefinition> definitions = [];
    private IWorkflowStore? store;

    /// <summary>
    /// Registers a definition before the first request.
    /// </summary>
    public FlowDeckApiFactory With(IWorkflowDefinition definition)
    {
        this.definitions.Add(definition);
        return this;
    }

    /// <summary>
    /// Substitutes the workflow store, for tests that need it to misbehave.
    /// </summary>
    public FlowDeckApiFactory WithStore(IWorkflowStore replacement)
    {
        this.store = replacement;
        return this;
    }

    /// <summary>The engine the API is using, for arranging state directly.</summary>
    public WorkflowEngine Engine => this.Services.GetRequiredService<WorkflowEngine>();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ConfigureServices(services =>
        {
            // The registry is a singleton populated at startup by the host. A
            // test is a host, so it populates it the same way rather than
            // substituting a different registry type.
            services.AddSingleton(provider =>
            {
                var registry = new WorkflowRegistry();

                foreach (var definition in this.definitions)
                {
                    registry.Register(definition);
                }

                return registry;
            });

            if (this.store is { } replacement)
            {
                services.AddSingleton(replacement);
            }
        });
    }
}

/// <summary>A step that completes immediately.</summary>
public sealed class NoopStep : IStep
{
    public ValueTask<Outcome> ExecuteAsync(IStepContext context, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(Outcome.Next);
}

/// <summary>A step that parks the instance.</summary>
public sealed class SuspendingStep : IStep
{
    public ValueTask<Outcome> ExecuteAsync(IStepContext context, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(Outcome.Suspend);
}

/// <summary>A one-step workflow that runs to completion.</summary>
public sealed class SimpleWorkflow(string id = "simple", int version = 1) : IWorkflowDefinition
{
    public string Id => id;

    public int Version => version;

    public void Build(IWorkflowBuilder builder) => builder.AddStep("only", () => new NoopStep());
}

/// <summary>A one-step workflow that suspends.</summary>
public sealed class SuspendingWorkflow : IWorkflowDefinition
{
    public string Id => "suspending";

    public int Version => 1;

    public void Build(IWorkflowBuilder builder) => builder.AddStep("wait", () => new SuspendingStep());
}

/// <summary>Input for <see cref="TypedWorkflow"/>.</summary>
public sealed record OrderRequest(int Id);

/// <summary>A workflow requiring typed input, which it records.</summary>
public sealed class TypedWorkflow(List<int> seen) : IWorkflowDefinition<OrderRequest>
{
    public string Id => "typed";

    public int Version => 1;

    public void Build(IWorkflowBuilder builder) => builder.AddStep("read", () => new ReadsInput(seen));

    private sealed class ReadsInput(List<int> seen) : IStep
    {
        public ValueTask<Outcome> ExecuteAsync(IStepContext context, CancellationToken cancellationToken = default)
        {
            seen.Add(context.GetInput<OrderRequest>().Id);
            return ValueTask.FromResult(Outcome.Next);
        }
    }
}
