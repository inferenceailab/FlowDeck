using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FlowDeck.Specs.Support;

/// <summary>
/// A real HTTP endpoint standing in for an OTLP collector.
/// </summary>
/// <remarks>
/// Records that an export arrived and how many bytes it carried. It does not
/// decode protobuf, and the scenario it serves does not claim it does: what is
/// being asserted is that configuring an endpoint makes FlowDeck's spans leave
/// the process and reach it.
///
/// <para>
/// A real socket rather than a substituted exporter, because the thing worth
/// testing is the wiring in <c>Program.cs</c> - whether a configured endpoint
/// produces a pipeline that exports. A fake exporter would test the fake.
/// </para>
///
/// <para>
/// <c>http/protobuf</c> so this can be an ordinary ASP.NET endpoint. gRPC is
/// the production default and hosting a gRPC receiver here would add a package
/// to prove the same thing.
/// </para>
/// </remarks>
public sealed class StubCollector : IAsyncDisposable
{
    private readonly WebApplication app;
    private readonly List<int> exports = [];
    private readonly Lock gate = new();

    private StubCollector(WebApplication app) => this.app = app;

    /// <summary>Where to point an exporter.</summary>
    public string Endpoint { get; private set; } = string.Empty;

    /// <summary>How many exports have arrived.</summary>
    public int Received
    {
        get
        {
            lock (this.gate)
            {
                return this.exports.Count;
            }
        }
    }

    /// <summary>Bytes carried by the exports so far.</summary>
    public int TotalBytes
    {
        get
        {
            lock (this.gate)
            {
                return this.exports.Sum();
            }
        }
    }

    /// <summary>
    /// Waits for an export to arrive, or gives up.
    /// </summary>
    /// <remarks>
    /// <c>ForceFlush</c> hands a batch to the exporter; delivery happens on the
    /// exporter's own thread and completes a moment later. Polling with a bound
    /// keeps the assertion about whether an export arrives rather than about how
    /// fast the machine is - and a fixed sleep long enough to be reliable would
    /// be long enough to be felt on every run.
    /// </remarks>
    public async Task WaitForExportAsync(TimeSpan? within = null)
    {
        var deadline = DateTimeOffset.UtcNow + (within ?? TimeSpan.FromSeconds(10));

        while (this.Received == 0 && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(25).ConfigureAwait(false);
        }
    }

    public static async Task<StubCollector> StartAsync()
    {
        var builder = WebApplication.CreateBuilder();

        // Port zero, so scenarios running in parallel never collide on a fixed
        // one. Loopback only: this listens for nothing but its own test.
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();

        var app = builder.Build();
        var collector = new StubCollector(app);

        app.MapPost("/v1/traces", async (HttpRequest request) =>
        {
            using var buffer = new MemoryStream();
            await request.Body.CopyToAsync(buffer).ConfigureAwait(false);

            collector.Record((int)buffer.Length);

            // An empty ExportTraceServiceResponse is a valid success, and the
            // exporter only needs a 200 to consider the batch delivered.
            return Results.Bytes([], "application/x-protobuf");
        });

        await app.StartAsync().ConfigureAwait(false);

        collector.Endpoint = app.Urls.First();

        return collector;
    }

    private void Record(int bytes)
    {
        lock (this.gate)
        {
            this.exports.Add(bytes);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await this.app.StopAsync().ConfigureAwait(false);
        await this.app.DisposeAsync().ConfigureAwait(false);
    }
}
