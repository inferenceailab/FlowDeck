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
    private readonly Dictionary<string, string> settings = new(StringComparer.Ordinal);

    private SpecApiFactory? factory;
    private HttpClient? client;
    private IWorkflowStore? store;
    private TimeProvider? clock;

    /// <summary>The response the last When produced.</summary>
    public HttpResponseMessage? Response { get; set; }

    /// <summary>The body of that response, read once and kept.</summary>
    public string Body { get; set; } = string.Empty;

    /// <summary>An instance a Given arranged, for later steps to refer to.</summary>
    public Guid InstanceId { get; set; }

    public void Declare(IWorkflowDefinition definition) => this.definitions.Add(definition);

    /// <summary>Replaces the store, for the readiness scenarios.</summary>
    public void UseStore(IWorkflowStore replacement) => this.store = replacement;

    /// <summary>
    /// Pins the host's clock.
    /// </summary>
    /// <remarks>
    /// Lease expiry is judged against this, so without it "expired" and "live"
    /// would depend on when the suite happened to run.
    /// </remarks>
    public void UseClock(TimeProvider replacement) => this.clock = replacement;

    /// <summary>
    /// Sets a host configuration value before the API starts.
    /// </summary>
    /// <remarks>
    /// The same channel an operator uses - an environment variable read through
    /// <c>IConfiguration</c> - rather than a test-only switch. Whether tracing is
    /// wired is decided in <c>Program.cs</c> from configuration, so a scenario
    /// that bypassed configuration would prove nothing about the deployment.
    /// </remarks>
    public void UseSetting(string key, string value) => this.settings[key] = value;

    /// <summary>The service provider of the running host, for flushing exporters.</summary>
    public IServiceProvider Services => this.Started().Services;

    /// <summary>
    /// The registry the running API populated.
    /// </summary>
    /// <remarks>
    /// For scenarios that build a <i>second</i> engine over the same store -
    /// which is what "a host that has since gone" means. Taking the running
    /// host's registry rather than rebuilding one keeps the two engines
    /// executing the same definitions.
    /// </remarks>
    public WorkflowRegistry Registry => this.Started().Services.GetRequiredService<WorkflowRegistry>();

    /// <summary>The engine the running API is using, for arranging state.</summary>
    public WorkflowEngine Engine => this.Started().Services.GetRequiredService<WorkflowEngine>();

    /// <summary>
    /// The store the running API is using.
    /// </summary>
    /// <remarks>
    /// Seeding through this rather than a store of the scenario's own: a
    /// fixture the API cannot read would be asserting against a different
    /// world.
    /// </remarks>
    public IWorkflowStore RunningStore => this.Started().Services.GetRequiredService<IWorkflowStore>();

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
            this.factory = new SpecApiFactory(this.definitions, this.store, this.clock, this.settings);
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

    private sealed class SpecApiFactory(
        IReadOnlyList<IWorkflowDefinition> definitions,
        IWorkflowStore? store,
        TimeProvider? clock,
        IReadOnlyDictionary<string, string> settings)
        : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            ArgumentNullException.ThrowIfNull(builder);

            foreach (var (key, value) in settings)
            {
                builder.UseSetting(key, value);
            }

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

                if (clock is not null)
                {
                    services.AddSingleton(clock);
                }
            });
        }
    }
}
