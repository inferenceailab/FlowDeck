using System.Net;
using FlowDeck.Core;
using FlowDeck.Specs.Support;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Trace;
using Reqnroll;

namespace FlowDeck.Specs.Steps;

/// <summary>
/// Binds Features/Observability/Export.feature.
/// </summary>
[Binding]
[Scope(Feature = "Serving metrics and exporting traces")]
public sealed class ExportSteps(ApiContext api) : IAsyncDisposable
{
    private StubCollector? collector;

    [Given("a host that has run two instances")]
    public async Task GivenTwoRuns()
    {
        api.Declare(new SpecWorkflow("order-fulfilment", 1, builder =>
            builder.AddStep("work", () => new SpecSteps.Recording([], "work"))));

        await api.Engine.StartAsync("order-fulfilment", 1);
        await api.Engine.StartAsync("order-fulfilment", 1);
    }

    [Given("a host that has run instances of two different definitions")]
    public async Task GivenTwoDefinitions()
    {
        api.Declare(new SpecWorkflow("order-fulfilment", 1, builder =>
            builder.AddStep("work", () => new SpecSteps.Recording([], "work"))));

        api.Declare(new SpecWorkflow("refunds", 2, builder =>
            builder.AddStep("work", () => new SpecSteps.Recording([], "work"))));

        await api.Engine.StartAsync("order-fulfilment", 1);
        await api.Engine.StartAsync("refunds", 2);
    }

    [Given("a host that has run nothing")]
    public void GivenNothingHasRun() =>
        api.Declare(new SpecWorkflow("order-fulfilment", 1, builder =>
            builder.AddStep("work", () => new SpecSteps.Recording([], "work"))));

    [Given("a host that has run an instance of a definition whose id contains a quote")]
    public async Task GivenAQuotedDefinitionId()
    {
        // Definition ids are author-chosen, so a label value can contain
        // anything a C# string can. One unescaped quote would end the label
        // early and make the whole scrape unparseable - every metric on the
        // endpoint lost, not one series.
        api.Declare(new SpecWorkflow("say \"hello\"", 1, builder =>
            builder.AddStep("work", () => new SpecSteps.Recording([], "work"))));

        await api.Engine.StartAsync("say \"hello\"", 1);
    }

    [Given("a host with no OTLP endpoint configured")]
    public void GivenNoOtlpEndpoint() =>
        api.Declare(new SpecWorkflow("order-fulfilment", 1, builder =>
            builder.AddStep("work", () => new SpecSteps.Recording([], "work"))));

    [Given("a collector listening on a local endpoint")]
    public async Task GivenACollector() => this.collector = await StubCollector.StartAsync();

    // The slash is escaped because a Cucumber Expression reads "/" as
    // alternation, which turns "GET /metrics" into a choice between two words.
    [When(@"I GET \/metrics")]
    public async Task WhenIGetMetrics() => await api.SendAsync(client => client.GetAsync("/metrics"));

    [When("an instance is started over HTTP")]
    public async Task WhenAnInstanceIsStartedOverHttp() =>
        await api.SendAsync(client =>
            client.PostAsync("/api/workflows/order-fulfilment/instances", content: null));

    [When("an instance is started over HTTP on a host configured to export to it")]
    public async Task WhenStartedOnAnExportingHost()
    {
        api.Declare(new SpecWorkflow("order-fulfilment", 1, builder =>
            builder.AddStep("work", () => new SpecSteps.Recording([], "work"))));

        // Configured the way an operator would, through the standard variable,
        // so this exercises the branch in Program.cs rather than a test switch.
        api.UseSetting("OTEL_EXPORTER_OTLP_ENDPOINT", this.collector!.Endpoint);
        api.UseSetting("OTEL_EXPORTER_OTLP_PROTOCOL", "http/protobuf");

        await api.SendAsync(client =>
            client.PostAsync("/api/workflows/order-fulfilment/instances", content: null));

        // The exporter batches, so without this the assertion would race the
        // batch timer and pass or fail on machine speed.
        // The exporter batches and delivers on its own thread, so a flush
        // starts the export rather than finishing it. Waiting for the delivery
        // with a bound is what makes this an assertion about export rather
        // than about how fast the machine is.
        api.Services.GetRequiredService<TracerProvider>().ForceFlush();
        await this.collector.WaitForExportAsync();
    }

    [Then("the response is Prometheus text format")]
    public void ThenTheResponseIsPrometheusText()
    {
        Assert.Equal(HttpStatusCode.OK, api.Response!.StatusCode);
        Assert.Equal("text/plain", api.Response.Content.Headers.ContentType?.MediaType);

        // The version parameter is how a collector knows which format it is
        // reading. Omitting it works today and is one silent change away from
        // not working.
        Assert.Contains(
            "version=0.0.4",
            api.Response.Content.Headers.ContentType?.ToString(),
            StringComparison.Ordinal);
    }

    [Then("it reports two started and two completed")]
    public void ThenItReportsTwoOfEach()
    {
        Assert.Contains(
            "flowdeck_instances_started_total{definition_id=\"order-fulfilment\",definition_version=\"1\"} 2",
            api.Body,
            StringComparison.Ordinal);

        Assert.Contains(
            "flowdeck_instances_completed_total{definition_id=\"order-fulfilment\",definition_version=\"1\"} 2",
            api.Body,
            StringComparison.Ordinal);
    }

    [Then("each definition appears as its own labelled series")]
    public void ThenEachDefinitionIsItsOwnSeries()
    {
        Assert.Contains(
            "flowdeck_instances_started_total{definition_id=\"order-fulfilment\",definition_version=\"1\"} 1",
            api.Body,
            StringComparison.Ordinal);

        Assert.Contains(
            "flowdeck_instances_started_total{definition_id=\"refunds\",definition_version=\"2\"} 1",
            api.Body,
            StringComparison.Ordinal);
    }

    [Then("the response succeeds and declares every counter with its type")]
    public void ThenCountersAreDeclaredWithNoSeries()
    {
        Assert.Equal(HttpStatusCode.OK, api.Response!.StatusCode);

        foreach (var outcome in new[] { "started", "completed", "failed", "cancelled", "compensated" })
        {
            Assert.Contains($"# TYPE flowdeck_instances_{outcome}_total counter", api.Body, StringComparison.Ordinal);
            Assert.Contains($"# HELP flowdeck_instances_{outcome}_total ", api.Body, StringComparison.Ordinal);
        }

        // Declared, but with no series yet. A counter reporting zero before
        // anything ran would be a value nobody recorded.
        Assert.DoesNotContain("_total{", api.Body, StringComparison.Ordinal);
    }

    [Then("the quote is escaped rather than ending the label early")]
    public void ThenTheQuoteIsEscaped() =>
        Assert.Contains(
            "definition_id=\"say \\\"hello\\\"\"",
            api.Body,
            StringComparison.Ordinal);

    [Then("it succeeds and nothing is exported")]
    public void ThenNothingIsExported()
    {
        Assert.Equal(HttpStatusCode.Accepted, api.Response!.StatusCode);

        // No pipeline at all, rather than one exporting into the void. A
        // TracerProvider registered with nowhere to send would retry, back off
        // and log about a collector the operator never asked for.
        Assert.Null(api.Services.GetService<TracerProvider>());
    }

    [Then("the collector receives the traces")]
    public void ThenTheCollectorReceivedThem()
    {
        Assert.Equal(HttpStatusCode.Accepted, api.Response!.StatusCode);

        Assert.True(
            this.collector!.Received > 0,
            "the collector received no export");

        // Not an empty batch. A request with no payload would satisfy the
        // count while carrying none of the spans this exists to ship.
        Assert.True(this.collector.TotalBytes > 0, "the export carried no payload");
    }

    public async ValueTask DisposeAsync()
    {
        if (this.collector is not null)
        {
            await this.collector.DisposeAsync().ConfigureAwait(false);
        }
    }
}
