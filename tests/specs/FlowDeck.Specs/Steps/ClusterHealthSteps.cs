using FlowDeck.Core;
using FlowDeck.Core.Cluster;
using FlowDeck.Core.Persistence;
using FlowDeck.Specs.Support;
using Reqnroll;

namespace FlowDeck.Specs.Steps;

/// <summary>
/// Binds Features/Observability/ClusterHealth.feature.
/// </summary>
[Binding]
[Scope(Feature = "Cluster health")]
public sealed class ClusterHealthSteps(EngineContext world, ApiContext api)
{
    private readonly TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private Task<WorkflowInstance>? blocked;
    private string first = string.Empty;

    [Given("a host running an instance blocked inside a step")]
    public async Task GivenABlockedRun()
    {
        api.Declare(new SpecWorkflow("orders", 1, builder =>
            builder.AddStep("work", () => new Blocking(this.entered, this.release.Task))));

        this.blocked = api.Engine.StartAsync("orders", 1);

        // Waited for rather than slept on, so the scrape happens while the
        // instance is genuinely mid-step. A sleep would make this a race the
        // scenario usually wins.
        await this.entered.Task;
    }

    [Given("a host that has finished an instance")]
    public async Task GivenAFinishedRun()
    {
        api.Declare(new SpecWorkflow("orders", 1, builder =>
            builder.AddStep("work", () => new SpecSteps.Recording([], "work"))));

        await api.Engine.StartAsync("orders", 1);
    }

    [Given("a host whose instance failed")]
    public async Task GivenAFailedRun()
    {
        api.Declare(new SpecWorkflow("orders", 1, builder =>
            builder.AddStep("charge", () => new SpecSteps.Throwing([], "charge"))));

        await api.Engine.StartAsync("orders", 1);
    }

    [Given("two instances abandoned by a node that stopped")]
    public async Task GivenTwoAbandoned()
    {
        world.Declare("orders", 1, builder =>
            builder.AddStep("work", () => new SpecSteps.Recording(world.Log, "work")));

        var engine = world.Engine();

        for (var i = 0; i < 2; i++)
        {
            world.Declare("orders", 1, builder =>
                builder.AddStep("work", () => new SpecSteps.Suspending(world.Log, "work")));

            var instance = await engine.StartAsync("orders", 1);
            var record = await world.Store.FindAsync(instance.Id);

            await world.Store.SaveAsync(record! with { Status = InstanceStatus.Suspended }, []);
        }
    }

    [When(@"I GET \/metrics")]
    public async Task WhenIGetMetrics()
    {
        await api.SendAsync(client => client.GetAsync("/metrics"));

        if (this.blocked is not null)
        {
            // Released after the scrape, so the assertion is about a genuinely
            // in-flight run rather than one that finished while the request was
            // being served.
            this.release.SetResult();
            await this.blocked;
        }
    }

    [When(@"I GET \/metrics twice")]
    public async Task WhenIScrapeTwice()
    {
        await api.SendAsync(client => client.GetAsync("/metrics"));
        this.first = api.Body;

        await api.SendAsync(client => client.GetAsync("/metrics"));

        // Released only now, so both scrapes saw the same in-flight run. A
        // gauge reading zero twice would prove nothing: accumulating zeros is
        // indistinguishable from replacing them.
        this.release.SetResult();
        await this.blocked!;
    }

    [When("a dispatcher recovers them")]
    public async Task WhenRecovered()
    {
        var dispatcher = new WorkflowDispatcher(
            world.Engine(),
            world.Store,
            new ClusterOptions { NodeId = "node-b", LeaseDuration = TimeSpan.FromMinutes(5) },
            metrics: world.Metrics.Metrics);

        while (await dispatcher.PollOnceAsync() > 0)
        {
            // Until the backlog is clear: one poll claims a batch, not the lot.
        }
    }

    [Then("it reports one instance executing")]
    public void ThenOneExecuting() =>
        Assert.Contains("flowdeck_instances_executing 1", api.Body, StringComparison.Ordinal);

    [Then("it reports no instances executing")]
    public void ThenNoneExecuting() =>

        // Zero, not absent. A gauge that vanished when idle would make "nothing
        // running" indistinguishable from "the node is not reporting".
        Assert.Contains("flowdeck_instances_executing 0", api.Body, StringComparison.Ordinal);

    [Then("the recovery counter reports two, tagged with the node that did it")]
    public void ThenTwoRecoveries()
    {
        var recovered = world.Metrics.Instrument("flowdeck.instances.recovered");

        Assert.Equal(2, (long)recovered.Sum(measurement => measurement.Value));
        Assert.All(recovered, measurement => Assert.Equal("node-b", measurement.Tags["node.id"]));
    }

    [Then("both scrapes report one instance executing")]
    public void ThenBothScrapesAgree()
    {
        // A gauge reports what is true now. Accumulating successive readings
        // would turn "one instance running" into two on the second scrape -
        // invisible on a single scrape, and it reads as a node twice as busy as
        // it is.
        Assert.Contains("flowdeck_instances_executing 1", this.first, StringComparison.Ordinal);
        Assert.Contains("flowdeck_instances_executing 1", api.Body, StringComparison.Ordinal);
    }

    [Then("the executing metric is declared a gauge")]
    public void ThenItIsAGauge() =>
        Assert.Contains("# TYPE flowdeck_instances_executing gauge", api.Body, StringComparison.Ordinal);

    [Then("it carries no _total suffix")]
    public void ThenNoTotalSuffix() =>

        // _total marks a counter. Labelling a gauge as one tells every
        // dashboard to compute a rate over something that goes down as well as
        // up.
        Assert.DoesNotContain("flowdeck_instances_executing_total", api.Body, StringComparison.Ordinal);

    /// <summary>Signals when it starts, then waits to be let go.</summary>
    private sealed class Blocking(TaskCompletionSource entered, Task release) : IStep
    {
        public async ValueTask<Outcome> ExecuteAsync(
            IStepContext context,
            CancellationToken cancellationToken = default)
        {
            entered.TrySetResult();

            await release.ConfigureAwait(false);

            return Outcome.Next;
        }
    }
}
