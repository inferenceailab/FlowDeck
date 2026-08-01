using System.Diagnostics;
using System.Globalization;
using FlowDeck.Core;
using FlowDeck.Core.Cluster;
using FlowDeck.Core.Persistence;
using FlowDeck.Specs.Support;
using Reqnroll;

namespace FlowDeck.Specs.Steps;

/// <summary>
/// Binds Features/Performance/Baseline.feature.
/// </summary>
/// <remarks>
/// <b>A baseline, not a target</b> (ADR-0027 decision 2). Nothing here commits
/// FlowDeck to a figure; the numbers are reported and the floors exist only to
/// catch an order-of-magnitude regression.
///
/// <para>
/// The floors are deliberately loose. These run on GitHub-hosted runners whose
/// speed varies between runs, and a guard that fails for reasons unrelated to
/// the engine gets deleted rather than investigated - which leaves nothing at
/// all. They are set roughly a factor of ten below what a developer machine
/// measures, so the kind of change that would breach one is a change to
/// checkpointing or claiming, not a busy runner.
/// </para>
///
/// <para>
/// Measured against the in-memory store deliberately. The EF Core numbers would
/// mostly measure SQLite, and the question is what the <i>engine</i> costs.
/// </para>
/// </remarks>
[Binding]
[Scope(Feature = "Throughput baseline")]
public sealed class BaselineSteps(EngineContext world, ScenarioContext scenario)
{
    private double instancesPerSecond;
    private double oneStepMicroseconds;
    private double tenStepPerStepMicroseconds;
    private double backlogSeconds;
    private int recovered;

    /// <summary>
    /// The lowest rate that is not a tenfold regression.
    /// </summary>
    /// <remarks>
    /// A developer machine measures thousands per second against the in-memory
    /// store. Twenty is far below anything the engine should ever produce and
    /// far above anything a slow runner causes.
    /// </remarks>
    private const double RateFloor = 20;

    private void Report(string name, double value, string unit)
    {
        // Written to the scenario output so a CI run records the number even
        // when it passes. A baseline nobody can read after the fact is a floor
        // check wearing a baseline's name.
        scenario.ScenarioInfo.Arguments[name] = value;

        Console.WriteLine(
            $"[baseline] {name} = {value.ToString("F2", CultureInfo.InvariantCulture)} {unit}");
    }

    private void DeclareSteps(string id, int steps)
    {
        world.Declare(id, 1, builder =>
        {
            for (var i = 0; i < steps; i++)
            {
                var name = $"step-{i}";
                builder.AddStep(name, () => new SpecSteps.Recording(world.Log, name));
            }
        });
    }

    private async Task<double> RunAsync(string id, int instances)
    {
        var engine = world.Engine();

        // Warmed first, so the measurement is not dominated by JIT and the
        // first compile of the definition. Measuring those once and calling it
        // throughput would report a number nobody can reproduce.
        await engine.StartAsync(id, 1);
        world.Log.Clear();

        var stopwatch = Stopwatch.StartNew();

        for (var i = 0; i < instances; i++)
        {
            await engine.StartAsync(id, 1);
        }

        stopwatch.Stop();

        return stopwatch.Elapsed.TotalSeconds;
    }

    [Given("a definition with three steps")]
    public void GivenThreeSteps() => this.DeclareSteps("throughput", 3);

    [Given("a one-step definition and a ten-step definition")]
    public void GivenOneAndTenSteps()
    {
        this.DeclareSteps("one-step", 1);
        this.DeclareSteps("ten-step", 10);
    }

    [Given("fifty instances abandoned by a dead node")]
    public async Task GivenABacklog()
    {
        this.DeclareSteps("backlog", 2);

        var engine = world.Engine();

        for (var i = 0; i < 50; i++)
        {
            // Suspended, which is the state a dispatcher picks work up from,
            // and reached by running rather than by writing a record - a guess
            // about what the engine leaves behind proves nothing (#166).
            var instance = await engine.StartAsync("backlog", 1);
            var record = await world.Store.FindAsync(instance.Id);

            await world.Store.SaveAsync(record! with { Status = InstanceStatus.Suspended }, []);
        }
    }

    [When("two hundred instances are run")]
    public async Task WhenTwoHundredAreRun()
    {
        var seconds = await this.RunAsync("throughput", 200);

        this.instancesPerSecond = 200 / seconds;
    }

    [When("fifty instances of each are run")]
    public async Task WhenFiftyOfEachAreRun()
    {
        // Microseconds. In milliseconds an in-memory step rounds to 0.00 and
        // the "baseline" reports nothing anyone could compare against later.
        this.oneStepMicroseconds = await this.RunAsync("one-step", 50) / 50 * 1_000_000;
        this.tenStepPerStepMicroseconds = await this.RunAsync("ten-step", 50) / 50 * 1_000_000 / 10;
    }

    [When("a dispatcher recovers them")]
    public async Task WhenADispatcherRecoversThem()
    {
        var dispatcher = new WorkflowDispatcher(
            world.Engine(),
            world.Store,
            new ClusterOptions { NodeId = "recovering-node", LeaseDuration = TimeSpan.FromMinutes(5) });

        var stopwatch = Stopwatch.StartNew();

        // Polled until it stops finding work, because one poll claims a batch
        // rather than the whole backlog. Timing a single poll would report how
        // big the batch is, not how long the backlog takes.
        int claimed;

        do
        {
            claimed = await dispatcher.PollOnceAsync();
            this.recovered += claimed;
        }
        while (claimed > 0);

        stopwatch.Stop();

        this.backlogSeconds = stopwatch.Elapsed.TotalSeconds;
    }

    [Then("the measured rate is reported")]
    public void ThenTheRateIsReported()
    {
        this.Report("instances_per_second", this.instancesPerSecond, "instances/s");

        Assert.True(this.instancesPerSecond > 0, "no instances were run");
    }

    [Then("it is above the floor a tenfold regression would breach")]
    public void ThenItIsAboveTheFloor() =>
        Assert.True(
            this.instancesPerSecond > RateFloor,
            $"{this.instancesPerSecond:F1} instances/s is below the {RateFloor} floor");

    [Then("the per-step cost is reported")]
    public void ThenPerStepCostIsReported()
    {
        this.Report("one_step_us", this.oneStepMicroseconds, "us/instance");
        this.Report("ten_step_us_per_step", this.tenStepPerStepMicroseconds, "us/step");

        Assert.True(this.oneStepMicroseconds > 0);
    }

    [Then("a ten-step instance costs less than ten times a one-step one")]
    public void ThenPerStepCostAmortises() =>

        // Per *step*, so this compares like with like. A one-step instance pays
        // for creation as well as its step; a ten-step one spreads that over
        // ten, so the per-step figure must come out lower. If it does not,
        // something scales with step count that should not - which is the
        // regression this scenario is shaped to catch.
        Assert.True(
            this.tenStepPerStepMicroseconds < this.oneStepMicroseconds,
            $"per-step cost did not amortise: {this.tenStepPerStepMicroseconds:F1}us over ten steps "
            + $"vs {this.oneStepMicroseconds:F1}us for one");

    [Then("every one is recovered")]
    public void ThenAllAreRecovered() => Assert.Equal(50, this.recovered);

    [Then("the time to clear the backlog is reported")]
    public void ThenBacklogTimeIsReported()
    {
        this.Report("backlog_50_seconds", this.backlogSeconds, "s");

        // Thirty seconds for fifty in-memory instances is enormous. It is a
        // floor, not a target: anything approaching it means recovery has
        // started doing per-instance work it did not used to.
        Assert.True(
            this.backlogSeconds < 30,
            $"clearing a 50-instance backlog took {this.backlogSeconds:F1}s");
    }
}
