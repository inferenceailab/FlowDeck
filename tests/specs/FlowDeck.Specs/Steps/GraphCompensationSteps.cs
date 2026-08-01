using FlowDeck.Core;
using FlowDeck.Specs.Support;
using Reqnroll;

namespace FlowDeck.Specs.Steps;

/// <summary>
/// Binds Features/Graph/GraphCompensation.feature.
/// </summary>
[Binding]
[Scope(Feature = "Compensating a graph")]
public sealed class GraphCompensationSteps(EngineContext world)
{
    /// <summary>Compensating actions that ran, in the order they ran.</summary>
    private readonly List<string> undone = [];

    [Given("a fork where one branch throws and the other completed a compensated step")]
    public void GivenAForkWithOneThrowingBranch() =>
        world.Declare("sibling-undo", 1, builder => builder
            .AddStep("split", () => new SpecSteps.Recording(world.Log, "split"))
            .Fork(
                good => good
                    .AddStep("reserve", () => new SpecSteps.Recording(world.Log, "reserve"))
                    .WithCompensation(() => new Undoing(this.undone, "reserve")),
                bad => bad.AddStep("charge", () => new SpecSteps.Throwing(world.Log, "charge"))));

    [Given("three compensated steps that completed in a known order")]
    public void GivenThreeStepsCompletingInAKnownOrder() =>

        // The order is enforced, not hoped for. Two arms racing would make the
        // expected rollback order a coin toss, and a scenario that asserts an
        // order has to be able to state what that order was.
        world.Declare("ordered-undo", 1, builder => builder
            .AddStep("split", () => new SpecSteps.Recording(world.Log, "split"))
            .Fork(
                first => first
                    .AddStep("a1", () => new AfterHistory(world, "a1", waitFor: null))
                    .WithCompensation(() => new Undoing(this.undone, "a1"))
                    .AddStep("a2", () => new AfterHistory(world, "a2", waitFor: "b1"))
                    .WithCompensation(() => new Undoing(this.undone, "a2")),
                second => second
                    .AddStep("b1", () => new AfterHistory(world, "b1", waitFor: "a1"))
                    .WithCompensation(() => new Undoing(this.undone, "b1")))
            .AddStep("boom", () => new SpecSteps.Throwing(world.Log, "boom")));

    [Given("a conditional workflow that took the in-stock branch")]
    public void GivenAConditionalWorkflowThatTookInStock() =>
        world.Declare("untaken", 1, builder => builder
            .AddStep("check-stock", () => new SpecSteps.Writing("stock", "in-stock"))
            .BranchWhen(
                "in-stock",
                data => data.Get<string>("stock") == "in-stock",
                taken => taken
                    .AddStep("ship", () => new SpecSteps.Recording(world.Log, "ship"))
                    .WithCompensation(() => new Undoing(this.undone, "ship")))
            .BranchWhen(
                "backorder",
                data => data.Get<string>("stock") == "backorder",
                untaken => untaken
                    .AddStep("notify", () => new SpecSteps.Recording(world.Log, "notify"))
                    .WithCompensation(() => new Undoing(this.undone, "notify")))
            .AddStep("boom", () => new SpecSteps.Throwing(world.Log, "boom")));

    [Given("a compensated step that failed twice before succeeding")]
    public void GivenAStepThatRetried() =>
        world.Declare("retried-undo", 1, builder => builder
            .AddStep(
                "charge",
                () => new FailsTwice(world.Log),
                RetryPolicy.FixedDelay(maxAttempts: 5, TimeSpan.Zero))
            .WithCompensation(() => new Undoing(this.undone, "charge"))
            .AddStep("boom", () => new SpecSteps.Throwing(world.Log, "boom")));

    [Given("a fork whose branches both declare compensation and one undo throws")]
    public void GivenAForkWhereOneUndoThrows() =>
        world.Declare("undo-throws", 1, builder => builder
            .AddStep("split", () => new SpecSteps.Recording(world.Log, "split"))
            .Fork(
                left => left
                    .AddStep("reserve", () => new SpecSteps.Recording(world.Log, "reserve"))
                    .WithCompensation(() => new Undoing(this.undone, "reserve")),
                right => right
                    .AddStep("label", () => new SpecSteps.Recording(world.Log, "label"))
                    .WithCompensation(() => new ThrowingUndo(this.undone, "label")))
            .AddStep("boom", () => new SpecSteps.Throwing(world.Log, "boom")));

    [When("the instance fails")]
    [When("the instance fails afterwards")]
    public async Task WhenTheInstanceFails()
    {
        var declaration = world.Only;

        world.Instance = await world.Engine().StartAsync(declaration.Id, declaration.Version);

        Assert.True(world.Instance.IsTerminal, "the instance did not reach a terminal state");
    }

    [Then("the sibling branch's compensating action runs")]
    public void ThenTheSiblingIsCompensated()
    {
        // The whole point of the story. Under the index walk this replaced, a
        // branch step was not in the top-level sequence at all, so nothing on a
        // sibling branch was ever undone.
        Assert.Equal(["reserve"], this.undone);
        Assert.Equal(InstanceStatus.Compensated, world.Instance!.Status);
    }

    [Then("their compensating actions run in the reverse of that order")]
    public void ThenTheyUndoInReverseCompletionOrder()
    {
        // Completion order was a1, b1, a2 - deliberately not declaration order,
        // which would be a1, a2, b1. An implementation that walked the graph
        // depth-first would produce the wrong answer here and the right one for
        // any workflow that happened to complete in declaration order.
        Assert.Equal(["a1", "b1", "a2"], world.Log.Where(name => name != "split" && name != "boom"));
        Assert.Equal(["a2", "b1", "a1"], this.undone);
    }

    [Then("no step on the backorder branch is compensated")]
    public void ThenNoUntakenStepIsCompensated()
    {
        Assert.DoesNotContain("notify", this.undone);

        // Not vacuous: the branch that *was* taken is undone, so this is about
        // the untaken one specifically rather than about compensation being off.
        Assert.Equal(["ship"], this.undone);
    }

    [Then("its compensating action runs exactly once")]
    public void ThenTheUndoRunsOnce()
    {
        // Three history entries for "charge", one effect to reverse. Undoing it
        // per attempt would be the compensation equivalent of the duplicate side
        // effects retry exists to bound.
        Assert.Equal(["charge"], this.undone);
    }

    [Then("the other branch is still compensated")]
    public void ThenTheOtherBranchIsStillCompensated()
    {
        // ADR-0021: a failing compensating action does not stop the rollback.
        // Both ran; one of them threw.
        Assert.Contains("reserve", this.undone);
        Assert.Contains("label", this.undone);
    }

    [Then("the instance status becomes CompensationFailed")]
    public void ThenTheStatusIsCompensationFailed() =>
        Assert.Equal(InstanceStatus.CompensationFailed, world.Instance!.Status);

    /// <summary>Records that it undid a step.</summary>
    private sealed class Undoing(List<string> undone, string name) : IStep
    {
        public ValueTask<Outcome> ExecuteAsync(
            IStepContext context,
            CancellationToken cancellationToken = default)
        {
            lock (undone)
            {
                undone.Add(name);
            }

            return ValueTask.FromResult(Outcome.Next);
        }
    }

    /// <summary>Records that it ran, then throws.</summary>
    private sealed class ThrowingUndo(List<string> undone, string name) : IStep
    {
        public ValueTask<Outcome> ExecuteAsync(
            IStepContext context,
            CancellationToken cancellationToken = default)
        {
            lock (undone)
            {
                undone.Add(name);
            }

            throw new InvalidOperationException($"undoing '{name}' failed");
        }
    }

    /// <summary>
    /// Waits until another step is recorded in history, then runs.
    /// </summary>
    /// <remarks>
    /// Waits on <b>history</b>, not on a signal the other step raises. A signal
    /// fires when the other step body returns, which is before its checkpoint
    /// is written - so the two steps would race for the writer and the recorded
    /// completion order would be whichever won. That version passed on a
    /// developer machine and failed on CI, which is exactly the failure a test
    /// about ordering must not have.
    ///
    /// <para>
    /// The arms still overlap: this waits for a condition, it does not run the
    /// branches one after another.
    /// </para>
    /// </remarks>
    private sealed class AfterHistory(EngineContext world, string name, string? waitFor) : IStep
    {
        public async ValueTask<Outcome> ExecuteAsync(
            IStepContext context,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);

            for (var attempt = 0; waitFor is not null && attempt < 400; attempt++)
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

    /// <summary>Throws on its first two attempts, then succeeds.</summary>
    private sealed class FailsTwice(List<string> log) : IStep
    {
        public ValueTask<Outcome> ExecuteAsync(
            IStepContext context,
            CancellationToken cancellationToken = default)
        {
            lock (log)
            {
                log.Add("charge");
            }

            if (log.Count(name => name == "charge") <= 2)
            {
                throw new InvalidOperationException("gateway timed out");
            }

            return ValueTask.FromResult(Outcome.Next);
        }
    }
}
