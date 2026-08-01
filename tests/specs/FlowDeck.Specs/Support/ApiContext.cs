using FlowDeck.Core;
using FlowDeck.Core.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace FlowDeck.Specs.Support;

/// <summary>
/// Hosts the real API in-process for a scenario.
/// </summary>
/// <remarks>
/// <c>WebApplicationFactory&lt;Program&gt;</c> against the shipping composition
/// root, not a hand-built pipeline. A scenario that assembled its own services
/// would prove nothing about the application that actually runs: a missing
/// registration in <c>Program.cs</c> would pass every scenario and fail on
/// startup.
///
/// <para>
/// Definitions are declared before the first request, because the registry is
/// a singleton built at startup. A scenario adding one afterwards would be
/// describing a host that does not exist.
/// </para>
/// </remarks>
public sealed class ApiContext : IDisposable
{
    private readonly List<IWorkflowDefinition> definitions = [];

    private SpecApiFactory? factory;
    private HttpClient? client;
    private IWorkflowStore? store;

    /// <summary>The response the last When produced.</summary>
    public HttpResponseMessage? Response { get; set; }

    /// <summary>The body of that response, read once and kept.</summary>
    public string Body { get; set; } = string.Empty;

    /// <summary>An instance a Given arranged, for later steps to refer to.</summary>
    public Guid InstanceId { get; set; }

    public void Declare(IWorkflowDefinition definition) => this.definitions.Add(definition);

    /// <summary>Replaces the store, for the readiness scenarios.</summary>
    public void UseStore(IWorkflowStore replacement) => this.store = replacement;

    /// <summary>The engine the running API is using, for arranging state.</summary>
    public WorkflowEngine Engine => this.Started().Services.GetRequiredService<WorkflowEngine>();

    public HttpClient Client
    {
        get
        {
            this.Started();
            return this.client!;
        }
    }

    /// <summary>Sends a request and keeps the response and its body.</summary>
    public async Task SendAsync(Func<HttpClient, Task<HttpResponseMessage>> send)
    {
        ArgumentNullException.ThrowIfNull(send);

        this.Response?.Dispose();
        this.Response = await send(this.Client).ConfigureAwait(false);
        this.Body = await this.Response.Content.ReadAsStringAsync().ConfigureAwait(false);
    }

    private SpecApiFactory Started()
    {
        if (this.factory is null)
        {
            this.factory = new SpecApiFactory(this.definitions, this.store);
            this.client = this.factory.CreateClient();
        }

        return this.factory;
    }

    public void Dispose()
    {
        this.Response?.Dispose();
        this.client?.Dispose();
        this.factory?.Dispose();
    }

    private sealed class SpecApiFactory(IReadOnlyList<IWorkflowDefinition> definitions, IWorkflowStore? store)
        : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            ArgumentNullException.ThrowIfNull(builder);

            builder.ConfigureServices(services =>
            {
                // The registry is a singleton the host populates at startup. A
                // scenario is a host, so it populates it the same way rather
                // than substituting a different registry type.
                services.AddSingleton(_ =>
                {
                    var registry = new WorkflowRegistry();

                    foreach (var definition in definitions)
                    {
                        registry.Register(definition);
                    }

                    return registry;
                });

                if (store is not null)
                {
                    services.AddSingleton(store);
                }
            });
        }
    }
}
