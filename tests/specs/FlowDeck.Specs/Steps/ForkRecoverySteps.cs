using FlowDeck.Core;
using FlowDeck.Core.Cluster;
using FlowDeck.Core.Persistence;
using FlowDeck.Specs.Support;
using Microsoft.Extensions.Time.Testing;
using Reqnroll;

namespace FlowDeck.Specs.Steps;

/// <summary>
/// Binds Features/Graph/ForkRecovery.feature.
/// </summary>
/// <remarks>
/// The crashed state is <b>taken from a real run</b> rather than written by
/// hand. A hand-written record is a guess about what the engine leaves behind,
/// and recovery that works against a guess proves nothing about recovery from
/// a crash. Here a fork is run to completion against a store that keeps every
/// checkpoint, and the checkpoint matching the moment the scenario describes is
/// replayed into a fresh store as the state a dead node left.
/// </remarks>
[Binding]
[Scope(Feature = "Recovering a forked instance")]
public sealed class ForkRecoverySteps(EngineContext world)
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly FakeTimeProvider clock = new(T0);

    private InMemoryWorkflowStore? recovered;
    private Capturing? duringRecovery;
    private Guid subject;

    /// <summary>The store a Given seeded, asserted rather than assumed.</summary>
    private InMemoryWorkflowStore Recovered =>
        this.recovered ?? throw new InvalidOperationException("No scenario step seeded a crashed instance.");

    [Given("a forked instance whose first branch completed before the crash")]
    public Task GivenOneBranchFinished() =>

        // Gated so b1 cannot finish before a1 is durable, which makes "the
        // first branch had completed" a fact rather than a coin toss.
        this.CrashAtAsync(
            builder => builder
                .AddStep("split", () => new Logged(world.Log, "split"))
                .Fork(
                    first => first.AddStep("a1", () => new Logged(world.Log, "a1")),
                    second => second
                        .AddStep("b1", () => new AfterHistory(world, "b1", waitFor: "a1"))
                        .AddStep("b2", () => new Logged(world.Log, "b2")))
                .AddStep("after", () => new Logged(world.Log, "after")),
            ["b2"]);

    [Given("a forked instance with completed steps on both branches")]
    public Task GivenBothBranchesPartlyDone() =>
        this.CrashAtAsync(
            builder => builder
                .AddStep("split", () => new Logged(world.Log, "split"))
                .Fork(
                    first => first
                        .AddStep("a1", () => new Logged(world.Log, "a1"))
                        .AddStep("a2", () => new AfterHistory(world, "a2", waitFor: "b1")),
                    second => second
                        .AddStep("b1", () => new AfterHistory(world, "b1", waitFor: "a1"))
                        .AddStep("b2", () => new AfterHistory(world, "b2", waitFor: "a2")))
                .AddStep("after", () => new Logged(world.Log, "after")),
            ["a2", "b2"]);

    [When("another node recovers it")]
    public async Task WhenAnotherNodeRecoversIt()
    {
        var options = new ClusterOptions { NodeId = "node-b", LeaseDuration = TimeSpan.FromSeconds(30) };

        // A fresh engine over the recovered store. The log was cleared when the
        // crash state was taken, so everything in it from here on is work this
        // node did - which is what the Thens need to tell re-executed from
        // resumed.
        this.duringRecovery = new Capturing(this.Recovered);

        var dispatcher = new WorkflowDispatcher(
            new WorkflowEngine(world.BuildRegistry(), this.clock, this.duringRecovery),
            this.duringRecovery,
            options,
            this.clock);

        Assert.Equal(1, await dispatcher.PollOnceAsync());
    }

    [Then("only the unfinished branch resumes")]
    public async Task ThenOnlyTheUnfinishedBranchResumes()
    {
        // b2 and the join, nothing from the arm that had finished. Recovery
        // that re-ran a1 would be worse than no recovery: its side effects had
        // already happened.
        Assert.Equal(["b2", "after"], world.Log);

        await this.AssertCompletedAsync();
    }

    [Then("no completed step runs again")]
    public async Task ThenNoCompletedStepRunsAgain()
    {
        Assert.DoesNotContain("split", world.Log);
        Assert.DoesNotContain("a1", world.Log);
        Assert.DoesNotContain("b1", world.Log);

        // Not vacuous: the steps that had *not* finished did run, so this is
        // about completed work specifically rather than about recovery having
        // done nothing at all.
        Assert.Contains("a2", world.Log);
        Assert.Contains("b2", world.Log);

        await this.AssertCompletedAsync();
    }

    [Then("the step that opened the fork does not run again")]
    public void ThenTheForkingStepDoesNotRunAgain() =>

        // Its branches were already open when the crash happened, so it had
        // finished doing whatever it does - only the join was outstanding. The
        // position pointed at it, which is why re-running it was the obvious
        // wrong answer.
        Assert.DoesNotContain("split", world.Log);

    [Then("no checkpoint it writes names a step of the finished branch")]
    public void ThenNoCheckpointNamesTheFinishedBranch()
    {
        var positions = this.duringRecovery!.Checkpoints
            .SelectMany(point => point.Record.ActiveNodes)
            .Select(node => node.StepName)
            .ToList();

        // Not only "a1 does not run again" - the instance must never be
        // *recorded* at a1 either. A fork that opened every arm and let the
        // finished ones fall straight through would satisfy the execution
        // assertions while publishing a position that had already been left,
        // and anything watching the instance would see it go backwards.
        Assert.DoesNotContain("a1", positions);

        // Not vacuous: the unfinished branch is recorded, so this is about the
        // finished one specifically.
        Assert.Contains("b2", positions);
    }

    [Given("a conditional instance that crashed inside the branch it took")]
    public Task GivenAConditionalInstanceThatCrashed() =>
        this.CrashAtAsync(
            builder => builder
                .AddStep("decide", () => new SpecSteps.Writing("route", "north"))
                .BranchWhen(
                    "north",
                    data => data.Get<string>("route") == "north",
                    north => north
                        .AddStep("n1", () => new Logged(world.Log, "n1"))
                        .AddStep("n2", () => new Logged(world.Log, "n2")))
                .BranchWhen(
                    "south",
                    data => data.Get<string>("route") == "south",
                    south => south.AddStep("s1", () => new Logged(world.Log, "s1")))
                .AddStep("after", () => new Logged(world.Log, "after")),
            ["n2"]);

    [Given("the data that chose it has since changed")]
    public async Task GivenTheDeciderDataHasChanged()
    {
        var stored = await this.Recovered.FindAsync(this.subject);

        // A step on the branch could have written this, or an operator could
        // have. Either way the predicate that chose the branch no longer holds,
        // which is the case re-evaluating it on recovery would get wrong.
        await this.Recovered.SaveAsync(
            stored! with
            {
                Data = new Dictionary<string, object?>(StringComparer.Ordinal) { ["route"] = "south" },
            },
            []);
    }

    [Then("it resumes on the branch it had taken")]
    public async Task ThenItResumesOnTheBranchItTook()
    {
        // The stored position decides, not the predicate. Re-evaluating would
        // send the instance down "south" - a path it never took, whose steps
        // would then run against half-done work from the path it did.
        Assert.Equal(["n2", "after"], world.Log);

        await this.AssertCompletedAsync();
    }

    [Then("the step after the join runs once and the instance completes")]
    public async Task ThenTheJoinStillCloses()
    {
        Assert.Single(world.Log, name => name == "after");

        await this.AssertCompletedAsync();
    }

    /// <summary>
    /// Runs a workflow to completion, keeping every checkpoint, and seeds a
    /// fresh store from the one whose active set matches
    /// <paramref name="activeAtCrash"/>.
    /// </summary>
    private async Task CrashAtAsync(Action<IWorkflowBuilder> build, string[] activeAtCrash)
    {
        world.Declare("forked", 1, build);

        var capture = new Capturing(world.Store);

        await new WorkflowEngine(world.BuildRegistry(), store: capture).StartAsync("forked", 1);

        var crash = capture.Checkpoints.FirstOrDefault(point =>
            point.Record.ActiveNodes
                .Select(node => node.StepName)
                .Order(StringComparer.Ordinal)
                .SequenceEqual(activeAtCrash.Order(StringComparer.Ordinal), StringComparer.Ordinal));

        // The engine has to have produced this state. If it never did, the
        // scenario would be recovering from a position no crash could leave and
        // would prove nothing - so the failure names every position it did see.
        if (crash is null)
        {
            Assert.Fail(
                $"no checkpoint was active at exactly [{string.Join(", ", activeAtCrash)}]; saw "
                + string.Join(
                    " | ",
                    capture.Checkpoints.Select(point =>
                        "[" + string.Join(", ", point.Record.ActiveNodes.Select(node => node.StepName)) + "]")));
        }

        this.subject = crash.Record.Id;
        this.recovered = new InMemoryWorkflowStore();

        // Left Running by a node that died, with a lapsed lease. That is what
        // makes it claimable, and it is the only thing about this record the
        // scenario invents.
        var abandoned = crash.Record with
        {
            Revision = 1,
            Status = InstanceStatus.Running,
            OwnerNodeId = "dead-node",
            LeaseExpiresAt = T0.AddSeconds(-1),
        };

        await this.recovered.CreateAsync(abandoned);
        await this.recovered.SaveAsync(abandoned, crash.History);

        world.Log.Clear();
    }

    private async Task AssertCompletedAsync()
    {
        var stored = await this.Recovered.FindAsync(this.subject);

        Assert.Equal(InstanceStatus.Completed, stored!.Status);
    }

    /// <summary>Records into the shared log and into the recovery log.</summary>
    private sealed class Logged(List<string> log, string name) : IStep
    {
        public ValueTask<Outcome> ExecuteAsync(
            IStepContext context,
            CancellationToken cancellationToken = default)
        {
            lock (log)
            {
                log.Add(name);
            }

            return ValueTask.FromResult(Outcome.Next);
        }
    }

    /// <summary>
    /// Waits until another step is recorded in history, then runs.
    /// </summary>
    /// <remarks>
    /// Waits on history rather than on a signal, for the same reason the graph
    /// compensation scenarios do: a signal fires when the other step body
    /// returns, which is before its checkpoint is written, so the two arms
    /// would race for the writer and the recorded position would be whichever
    /// won.
    /// </remarks>
    private sealed class AfterHistory(EngineContext world, string name, string waitFor) : IStep
    {
        public async ValueTask<Outcome> ExecuteAsync(
            IStepContext context,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);

            for (var attempt = 0; attempt < 400; attempt++)
            {
                var history = await world.Store.GetHistoryAsync(context.InstanceId, cancellationToken)
                    .ConfigureAwait(false);

                if (history.Any(entry => entry.StepName == waitFor))
                {
                    break;
                }

                await Task.Delay(25, cancellationToken).ConfigureAwait(false);
            }

            lock (world.Log)
            {
                world.Log.Add(name);
            }

            return Outcome.Next;
        }
    }

    /// <summary>
    /// Delegates to a real store and keeps every checkpoint with the history as
    /// it stood at that moment.
    /// </summary>
    private sealed class Capturing(IWorkflowStore inner) : IWorkflowStore
    {
        private readonly List<Checkpoint> checkpoints = [];

        public IReadOnlyList<Checkpoint> Checkpoints => this.checkpoints;

        public async Task CreateAsync(WorkflowInstanceRecord record, CancellationToken cancellationToken = default)
        {
            await inner.CreateAsync(record, cancellationToken);

            lock (this.checkpoints)
            {
                this.checkpoints.Add(new Checkpoint(record, []));
            }
        }

        public async Task<WorkflowInstanceRecord> SaveAsync(
            WorkflowInstanceRecord record,
            IReadOnlyList<StepHistoryEntry> history,
            CancellationToken cancellationToken = default)
        {
            var result = await inner.SaveAsync(record, history, cancellationToken);

            // Sequence numbers are assigned by the store, so the entries the
            // engine handed over do not carry them. Read back rather than
            // guessed: a replayed history with invented sequences would be a
            // different record from the one the crash left.
            var written = await inner.GetHistoryAsync(record.Id, cancellationToken);

            lock (this.checkpoints)
            {
                this.checkpoints.Add(new Checkpoint(result, written));
            }

            return result;
        }

        public Task<WorkflowInstanceRecord?> FindAsync(Guid instanceId, CancellationToken cancellationToken = default) =>
            inner.FindAsync(instanceId, cancellationToken);

        public Task<IReadOnlyList<StepHistoryEntry>> GetHistoryAsync(
            Guid instanceId,
            CancellationToken cancellationToken = default) =>
            inner.GetHistoryAsync(instanceId, cancellationToken);

        public Task<IReadOnlyList<WorkflowInstanceRecord>> ListAsync(
            InstanceFilter filter,
            CancellationToken cancellationToken = default) =>
            inner.ListAsync(filter, cancellationToken);

        public Task<int> CountAsync(InstanceFilter filter, CancellationToken cancellationToken = default) =>
            inner.CountAsync(filter, cancellationToken);

        public Task<int> PurgeAsync(DateTimeOffset completedBefore, CancellationToken cancellationToken = default) =>
            inner.PurgeAsync(completedBefore, cancellationToken);

        public Task<IReadOnlyList<WorkflowInstanceRecord>> FindClaimableAsync(
            DateTimeOffset asOf,
            int limit,
            CancellationToken cancellationToken = default) =>
            inner.FindClaimableAsync(asOf, limit, cancellationToken);

        internal sealed record Checkpoint(WorkflowInstanceRecord Record, IReadOnlyList<StepHistoryEntry> History);
    }
}
