using FlowDeck.Core;
using FlowDeck.Core.Persistence;
using FlowDeck.Specs.Support;
using Reqnroll;

namespace FlowDeck.Specs.Steps;

/// <summary>
/// Binds Features/Graph/Parallel.feature.
/// </summary>
/// <remarks>
/// Scoped, because "an instance is started" is already bound unscoped for the
/// M1 features and means something slightly different here: it starts the run
/// without waiting for it, so a Then can observe an instance while it is still
/// mid-fork. A scenario about overlap cannot assert on overlap it has already
/// waited out.
/// </remarks>
[Binding]
[Scope(Feature = "Concurrent branches")]
public sealed class ParallelSteps(EngineContext world)
{
    /// <summary>Keys each branch writes in the contention scenario.</summary>
    private const int Hammered = 500;

    /// <summary>Released by a Then once it has observed what it came to observe.</summary>
    private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly Dictionary<string, TaskCompletionSource> entered =
        new(StringComparer.Ordinal);

    private Task<WorkflowInstance>? run;

    [Given("a fork into two steps that each block until released")]
    public void GivenAForkIntoTwoBlockingSteps()
    {
        this.entered["left"] = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        this.entered["right"] = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        world.Declare("blocking-fork", 1, builder => builder
            .AddStep("split", () => new SpecSteps.Recording(world.Log, "split"))
            .Fork(
                left => left.AddStep("left", () => new Blocking(this.entered["left"], this.release.Task)),
                right => right.AddStep("right", () => new Blocking(this.entered["right"], this.release.Task))));
    }

    [Given("a fork into two branches of different lengths")]
    public void GivenAForkIntoBranchesOfDifferentLengths() =>
        world.Declare("uneven-fork", 1, builder => builder
            .AddStep("split", () => new SpecSteps.Recording(world.Log, "split"))
            .Fork(
                slow => slow
                    .AddStep("slow-1", () => new Yielding(world.Log, "slow-1"))
                    .AddStep("slow-2", () => new Yielding(world.Log, "slow-2"))
                    .AddStep("slow-3", () => new Yielding(world.Log, "slow-3")),
                quick => quick.AddStep("quick-1", () => new Yielding(world.Log, "quick-1")))
            .AddStep("after", () => new SpecSteps.Recording(world.Log, "after")));

    [Given("a fork where one branch throws and the other succeeds")]
    public void GivenAForkWhereOneBranchThrows() =>
        world.Declare("failing-fork", 1, builder => builder
            .AddStep("split", () => new SpecSteps.Recording(world.Log, "split"))
            .Fork(
                bad => bad.AddStep("bad", () => new SpecSteps.Throwing(world.Log, "bad")),
                good => good
                    .AddStep("good-1", () => new Yielding(world.Log, "good-1"))
                    .AddStep("good-2", () => new Yielding(world.Log, "good-2"))
                    .AddStep("good-3", () => new Yielding(world.Log, "good-3"))));

    [Given("a fork into two branches that each complete several steps")]
    public void GivenAForkIntoTwoBusyBranches() =>
        world.Declare("busy-fork", 1, builder => builder
            .AddStep("split", () => new SpecSteps.Recording(world.Log, "split"))
            .Fork(
                left => left
                    .AddStep("l1", () => new Yielding(world.Log, "l1"))
                    .AddStep("l2", () => new Yielding(world.Log, "l2"))
                    .AddStep("l3", () => new Yielding(world.Log, "l3"))
                    .AddStep("l4", () => new Yielding(world.Log, "l4")),
                right => right
                    .AddStep("r1", () => new Yielding(world.Log, "r1"))
                    .AddStep("r2", () => new Yielding(world.Log, "r2"))
                    .AddStep("r3", () => new Yielding(world.Log, "r3"))
                    .AddStep("r4", () => new Yielding(world.Log, "r4"))));

    [Given("a fork into two branches that each write a different key")]
    public void GivenAForkIntoTwoWritingBranches() =>
        world.Declare("writing-fork", 1, builder => builder
            .AddStep("split", () => new SpecSteps.Recording(world.Log, "split"))
            .Fork(
                left => left.AddStep("write-left", () => new WritingAfterYield("left", "L")),
                right => right.AddStep("write-right", () => new WritingAfterYield("right", "R")))
            .AddStep("read", () => new ReadingBoth(world.Captured)));

    [Given("a fork into two branches that each write hundreds of keys")]
    public void GivenAForkIntoTwoHeavyWritingBranches() =>
        world.Declare("hammering-fork", 1, builder => builder
            .AddStep("split", () => new SpecSteps.Recording(world.Log, "split"))
            .Fork(
                left => left.AddStep("hammer-left", () => new Hammering("left", Hammered)),
                right => right.AddStep("hammer-right", () => new Hammering("right", Hammered)))
            .AddStep("collect", () => new CollectingAll(world.Captured)));

    [Then("every key written by either branch is readable")]
    public async Task ThenEveryKeyIsReadable()
    {
        world.Instance = await this.run!;

        var data = (IReadOnlyDictionary<string, object?>)world.Captured["all"]!;

        // Both branches' keys, all of them. An unguarded Dictionary written from
        // two threads loses entries and can corrupt its buckets outright, so
        // this fails either by coming up short or by throwing during the run.
        Assert.Equal(Hammered * 2, data.Count(entry => entry.Key.StartsWith("k-", StringComparison.Ordinal)));
    }

    [Given("a step with two conditional branches and data selecting the second")]
    public void GivenTwoConditionalBranchesSelectingTheSecond() => this.DeclareChoice("second");

    [Given("a step with two conditional branches and data selecting neither")]
    public void GivenTwoConditionalBranchesSelectingNeither() => this.DeclareChoice("neither");

    [When("an instance is started")]
    public void WhenAnInstanceIsStarted()
    {
        var declaration = world.Only;

        // Started, not awaited. A scenario about two steps running at the same
        // moment has to observe them while they are running; awaiting here
        // would mean every Then ran after the fork had already closed.
        this.run = world.Engine().StartAsync(declaration.Id, declaration.Version);
    }

    [Then("both steps are running at the same moment")]
    public async Task ThenBothStepsAreRunningAtTheSameMoment()
    {
        // Neither step can return until it is released, so both signalling
        // entry proves they were inside their bodies together. A fork that ran
        // its arms one after another would never see the second signal, and
        // this would time out rather than pass quietly.
        await this.BothEnteredAsync();

        this.release.SetResult();

        world.Instance = await this.run!;

        Assert.Equal(InstanceStatus.Completed, world.Instance.Status);
    }

    [Then("the stored position names both branch steps")]
    public async Task ThenTheStoredPositionNamesBothBranchSteps()
    {
        await this.BothEnteredAsync();

        // Read while the fork is open. Once it closes the instance is at one
        // place again, and the assertion would be about the wrong moment.
        var stored = await this.StoredWhileForkedAsync();

        Assert.Equal(
            ["left", "right"],
            stored.ActiveNodes.Select(node => node.StepName).Order(StringComparer.Ordinal));

        world.Captured["stored"] = stored;
    }

    [Then("each names the branch it belongs to")]
    public async Task ThenEachNamesItsBranch()
    {
        var stored = (WorkflowInstanceRecord)world.Captured["stored"]!;

        // Fork arms are auto-named branch-1 and branch-2 in declaration order.
        // Without the path, two nodes on different arms would be indistinguish-
        // able wherever step names alone are not enough - which is exactly what
        // recovery (#166) has to tell apart.
        Assert.Equal(
            ["branch-1"],
            stored.ActiveNodes.Single(node => node.StepName == "left").BranchPath);

        Assert.Equal(
            ["branch-2"],
            stored.ActiveNodes.Single(node => node.StepName == "right").BranchPath);

        this.release.SetResult();
        world.Instance = await this.run!;
    }

    [Then("the step after the join runs only once both branches have finished")]
    public async Task ThenTheStepAfterTheJoinRunsLast()
    {
        world.Instance = await this.run!;

        Assert.Equal("after", world.Log[^1]);

        // Both arms' last steps precede it, not just one. A join that waited for
        // the first arm to finish would still put "after" last whenever the
        // other arm happened to be quicker.
        Assert.Contains("slow-3", world.Log);
        Assert.Contains("quick-1", world.Log);
    }

    [Then("the other branch still runs to completion")]
    public async Task ThenTheOtherBranchStillCompletes()
    {
        world.Instance = await this.run!;

        // Every step of the surviving arm, not merely its first. The join waits
        // for all arms, so a sibling's failure must not abandon this one
        // part-way (ADR-0024 decision 6).
        Assert.Contains("good-1", world.Log);
        Assert.Contains("good-2", world.Log);
        Assert.Contains("good-3", world.Log);
    }

    [Then("the instance status becomes Failed")]
    public void ThenTheInstanceStatusBecomesFailed()
    {
        Assert.Equal(InstanceStatus.Failed, world.Instance!.Status);
        Assert.Equal("bad", world.Instance.FailedStepName);
    }

    [Then("every checkpoint is written")]
    public async Task ThenEveryCheckpointIsWritten()
    {
        world.Instance = await this.run!;

        var history = await world.Store.GetHistoryAsync(world.Instance.Id);

        // One entry per step execution. A checkpoint that was rejected would
        // take its history with it, because ADR-0013 writes the two together.
        Assert.Equal(
            ["l1", "l2", "l3", "l4", "r1", "r2", "r3", "r4", "split"],
            history.Select(entry => entry.StepName).Order(StringComparer.Ordinal));

        // Sequence numbers are dense: nine entries numbered 1..9, so no save
        // landed twice and none was skipped.
        Assert.Equal(Enumerable.Range(1, 9), history.Select(entry => entry.Sequence));
    }

    [Then("no concurrency exception is raised")]
    public void ThenNoConcurrencyExceptionIsRaised()
    {
        Assert.Equal(InstanceStatus.Completed, world.Instance!.Status);

        // The failure this scenario exists for is a rejected save, which
        // surfaces as the run throwing rather than as a failed step. Asserting
        // Completed above would already catch it; naming it here says what the
        // scenario is about.
        Assert.Null(world.Instance.Error);
    }

    [Then("both values are readable after the join")]
    public async Task ThenBothValuesAreReadable()
    {
        world.Instance = await this.run!;

        Assert.Equal("L", world.Captured["left"]);
        Assert.Equal("R", world.Captured["right"]);
    }

    [Then("only the second branch runs")]
    public async Task ThenOnlyTheSecondBranchRuns()
    {
        world.Instance = await this.run!;

        Assert.DoesNotContain("first-branch", world.Log);
        Assert.Contains("second-branch", world.Log);
    }

    [Then("no branch runs")]
    public async Task ThenNoBranchRuns()
    {
        world.Instance = await this.run!;

        Assert.DoesNotContain("first-branch", world.Log);
        Assert.DoesNotContain("second-branch", world.Log);
    }

    [Then("the step after the branches still runs")]
    public void ThenTheStepAfterTheBranchesStillRuns()
    {
        // A choice with no match continues past the fork rather than failing
        // (ADR-0024 decision 6). An instance that stopped here would leave the
        // rest of the workflow silently unexecuted.
        Assert.Contains("after", world.Log);
        Assert.Equal(InstanceStatus.Completed, world.Instance!.Status);
    }

    private void DeclareChoice(string selector) =>
        world.Declare("choice", 1, builder => builder
            .AddStep("decide", () => new SpecSteps.Writing("route", selector))
            .BranchWhen(
                "first",
                data => data.Get<string>("route") == "first",
                first => first.AddStep("first-branch", () => new SpecSteps.Recording(world.Log, "first-branch")))
            .BranchWhen(
                "second",
                data => data.Get<string>("route") == "second",
                second => second.AddStep("second-branch", () => new SpecSteps.Recording(world.Log, "second-branch")))
            .AddStep("after", () => new SpecSteps.Recording(world.Log, "after")));

    /// <summary>
    /// Waits for both blocking steps to report that they are inside their
    /// bodies, or fails the scenario rather than hanging the suite.
    /// </summary>
    private async Task BothEnteredAsync()
    {
        var both = Task.WhenAll(this.entered["left"].Task, this.entered["right"].Task);

        // A timeout rather than an unbounded wait: if the arms are serialised
        // this never completes, and a hung test says far less than a failed one.
        var finished = await Task.WhenAny(both, Task.Delay(TimeSpan.FromSeconds(10)));

        Assert.True(
            ReferenceEquals(finished, both),
            "the two branch steps were never inside their bodies at the same moment");
    }

    /// <summary>Reads the record written while the fork is still open.</summary>
    private async Task<WorkflowInstanceRecord> StoredWhileForkedAsync()
    {
        // Both steps have signalled entry, but the checkpoint naming them was
        // written just before each body ran and the second may not have landed
        // yet. Polled rather than slept on: a fixed delay would be either flaky
        // or slow, and this is neither.
        for (var attempt = 0; attempt < 200; attempt++)
        {
            var records = await world.Store.ListAsync(new InstanceFilter());
            var record = records.SingleOrDefault();

            if (record is not null && record.ActiveNodes.Count == 2)
            {
                return record;
            }

            await Task.Delay(25);
        }

        Assert.Fail("the stored position never named both branches");

        // Unreachable: Assert.Fail always throws. Present because the compiler
        // cannot know that.
        throw new InvalidOperationException();
    }

    /// <summary>Signals that it has started, then waits to be let go.</summary>
    private sealed class Blocking(TaskCompletionSource entered, Task release) : IStep
    {
        public async ValueTask<Outcome> ExecuteAsync(
            IStepContext context,
            CancellationToken cancellationToken = default)
        {
            entered.TrySetResult();

            await release.WaitAsync(cancellationToken).ConfigureAwait(false);

            return Outcome.Next;
        }
    }

    /// <summary>
    /// Records its name after yielding, so branches interleave.
    /// </summary>
    /// <remarks>
    /// The yield is the point. A step that completes synchronously would let one
    /// arm run to the end before the other started, and a scenario about
    /// colliding checkpoints would never produce a collision to survive.
    /// </remarks>
    private sealed class Yielding(List<string> log, string name) : IStep
    {
        public async ValueTask<Outcome> ExecuteAsync(
            IStepContext context,
            CancellationToken cancellationToken = default)
        {
            await Task.Yield();

            lock (log)
            {
                log.Add(name);
            }

            return Outcome.Next;
        }
    }

    /// <summary>Writes one key after yielding, so the two writes overlap.</summary>
    private sealed class WritingAfterYield(string key, string value) : IStep
    {
        public async ValueTask<Outcome> ExecuteAsync(
            IStepContext context,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);

            await Task.Yield();

            context.Data.Set(key, value);

            return Outcome.Next;
        }
    }

    /// <summary>
    /// Writes many keys, yielding between them, to make two branches genuinely
    /// contend for the data bag.
    /// </summary>
    /// <remarks>
    /// Two branches writing one key each is what an author does and proves
    /// nothing about thread safety: the odds of the two writes landing inside
    /// the same dictionary operation are negligible, so the scenario passes with
    /// the lock removed. Hundreds of writes with a yield between them do
    /// contend, which is what makes removing the lock show up as a failure
    /// rather than as luck.
    /// </remarks>
    private sealed class Hammering(string prefix, int count) : IStep
    {
        public async ValueTask<Outcome> ExecuteAsync(
            IStepContext context,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);

            for (var i = 0; i < count; i++)
            {
                context.Data.Set($"k-{prefix}-{i}", i);

                await Task.Yield();
            }

            return Outcome.Next;
        }
    }

    /// <summary>Snapshots the whole bag once the join has closed.</summary>
    private sealed class CollectingAll(Dictionary<string, object?> captured) : IStep
    {
        public ValueTask<Outcome> ExecuteAsync(
            IStepContext context,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);

            captured["all"] = context.Data.Snapshot();

            return ValueTask.FromResult(Outcome.Next);
        }
    }

    /// <summary>Reads both branch keys once the join has closed.</summary>
    private sealed class ReadingBoth(Dictionary<string, object?> captured) : IStep
    {
        public ValueTask<Outcome> ExecuteAsync(
            IStepContext context,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);

            captured["left"] = context.Data.Get<string>("left");
            captured["right"] = context.Data.Get<string>("right");

            return ValueTask.FromResult(Outcome.Next);
        }
    }
}
