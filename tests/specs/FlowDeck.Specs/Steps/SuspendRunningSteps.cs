using FlowDeck.Core;
using FlowDeck.Core.Persistence;
using FlowDeck.Specs.Support;
using Reqnroll;

namespace FlowDeck.Specs.Steps;

/// <summary>
/// Binds Features/Engine/SuspendRunning.feature.
/// </summary>
/// <remarks>
/// The first step blocks until the scenario releases it, so a suspend can
/// genuinely arrive <i>while</i> the instance is executing. Anything less would
/// be testing a suspend of an idle instance, which is the easy half.
/// </remarks>
[Binding]
[Scope(Feature = "Suspending a running instance")]
public sealed class SuspendRunningSteps(EngineContext world)
{
    private readonly TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private Task<WorkflowInstance>? run;
    private WorkflowEngine? built;

    /// <summary>
    /// The engine a Given built, asserted rather than assumed.
    /// </summary>
    /// <remarks>
    /// Every step here acts on the <i>same</i> engine: EngineContext builds a
    /// fresh registry per call, and suspending through a second one would act
    /// on an instance the first is still running.
    /// </remarks>
    private WorkflowEngine Engine =>
        this.built ?? throw new InvalidOperationException("No scenario step built an engine.");

    [Given("a running instance blocked inside its first step")]
    public async Task GivenABlockedInstance()
    {
        world.Declare("orders", 1, builder => builder
            .AddStep("first", () => new Blocking(world.Log, "first", this.entered, this.release.Task))
            .AddStep("second", () => new SpecSteps.Recording(world.Log, "second")));

        this.built = world.Engine();
        this.run = this.Engine.StartAsync("orders", 1);

        // Waited for, not slept on. The suspend has to arrive while the step is
        // executing, and a sleep would make that a race the scenario usually
        // wins rather than always.
        await this.entered.Task;
    }

    [Given("a completed instance")]
    public async Task GivenACompletedInstance()
    {
        world.Declare("orders", 1, builder =>
            builder.AddStep("work", () => new SpecSteps.Recording(world.Log, "work")));

        this.built = world.Engine();
        world.Instance = await this.Engine.StartAsync("orders", 1);
    }

    [Given("an instance that has parked on its own")]
    public async Task GivenASelfSuspendedInstance()
    {
        world.Declare("orders", 1, builder =>
            builder.AddStep("wait", () => new SpecSteps.Suspending(world.Log, "wait")));

        this.built = world.Engine();
        world.Instance = await this.Engine.StartAsync("orders", 1);
    }

    [When("it is suspended and the step is released")]
    public async Task WhenSuspendedThenReleased()
    {
        await this.Engine.SuspendAsync(await this.RunningIdAsync());

        this.release.SetResult();

        world.Instance = await this.run!;
    }

    [When("it is suspended")]
    public async Task WhenSuspended() =>
        await world.CapturingErrorAsync(() => this.Engine.SuspendAsync(world.Instance!.Id));

    [When("it is resumed")]
    public async Task WhenResumed() =>
        world.Instance = await world.RestartedHost().ResumeAsync(world.Instance!.Id);

    [Then("the first step finished")]
    public void ThenTheFirstStepFinished() =>

        // Not abandoned. The engine cannot stop author code mid-execution, and
        // pretending otherwise would leave side effects it had not recorded.
        Assert.Contains("first", world.Log);

    [Then("the second step never started")]
    public void ThenTheSecondNeverStarted() => Assert.DoesNotContain("second", world.Log);

    [Then("the instance is Suspended")]
    public void ThenItIsSuspended() => Assert.Equal(InstanceStatus.Suspended, world.Instance!.Status);

    [Then("the second step runs")]
    public void ThenTheSecondRuns() => Assert.Contains("second", world.Log);

    [Then("it runs to completion rather than parking again")]
    public void ThenItCompletes() =>

        // The request is cleared as it is honoured. Left set, a resume would
        // park at the very next boundary and an operator would think resume
        // was broken.
        Assert.Equal(InstanceStatus.Completed, world.Instance!.Status);

    [Then("the stored request is cleared")]
    public async Task ThenTheStoredRequestIsCleared()
    {
        var stored = await world.Store.FindAsync(world.Instance!.Id);

        // Asserted on the record, not merely inferred from the run completing.
        // A flag left set is invisible until some later writer bumps the
        // revision, at which point the instance parks for no reason anybody
        // can trace.
        Assert.False(stored!.SuspendRequested);
    }

    [Then("the call fails saying it has already finished")]
    public void ThenItIsRefused() =>
        Assert.IsType<InvalidStateTransitionException>(world.Error);

    /// <summary>
    /// The id of the instance currently running.
    /// </summary>
    /// <remarks>
    /// Read from the store rather than from the task, because the task does not
    /// complete until the instance does - and the whole point here is to act on
    /// it while it is still going. The instance record exists before the first
    /// step runs (ADR-0007), which is what makes that possible.
    /// </remarks>
    private async Task<Guid> RunningIdAsync()
    {
        var listed = await world.Store.ListAsync(new InstanceFilter { ActiveOnly = true });

        return listed.Single().Id;
    }

    /// <summary>Signals when it starts, then waits to be let go.</summary>
    private sealed class Blocking(
        List<string> log,
        string name,
        TaskCompletionSource entered,
        Task release) : IStep
    {
        public async ValueTask<Outcome> ExecuteAsync(
            IStepContext context,
            CancellationToken cancellationToken = default)
        {
            entered.TrySetResult();

            await release.ConfigureAwait(false);

            // Recorded on the way out, so "the step finished" means it ran to
            // the end rather than merely having been entered.
            log.Add(name);

            return Outcome.Next;
        }
    }
}
