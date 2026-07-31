using FlowDeck.Core;
using FlowDeck.Core.Persistence;

namespace FlowDeck.Core.Tests.Persistence;

/// <summary>
/// Issue #19 - Detect concurrent modification of an instance.
///
/// Scenario: Stale write is rejected
/// </summary>
/// <remarks>
/// The store-level contract is already covered by the conformance suite (#16).
/// These tests cover the level above it: what an engine does when another
/// writer has moved the instance on. That is the case #39 has to build on, so
/// the behaviour needs pinning before multi-node execution assumes it.
/// </remarks>
public class ConcurrentModificationTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 31, 12, 0, 0, TimeSpan.Zero);

    private sealed class NoopStep : IStep
    {
        public ValueTask<Outcome> ExecuteAsync(IStepContext context, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Outcome.Next);
    }

    private sealed class SuspendOnce(HashSet<Guid> seen) : IStep
    {
        public ValueTask<Outcome> ExecuteAsync(IStepContext context, CancellationToken cancellationToken = default)
        {
            lock (seen)
            {
                return ValueTask.FromResult(seen.Add(context.InstanceId) ? Outcome.Suspend : Outcome.Next);
            }
        }
    }

    private sealed class TwoStep(Func<IStep> a, Func<IStep> b) : IWorkflowDefinition
    {
        public string Id => "two-step";

        public int Version => 1;

        public void Build(IWorkflowBuilder builder)
        {
            builder.AddStep("A", a);
            builder.AddStep("B", b);
        }
    }

    private static WorkflowEngine HostOver(IWorkflowStore store, HashSet<Guid> seen) =>
        NewEngine(store, new TwoStep(() => new SuspendOnce(seen), () => new NoopStep()));

    private static WorkflowEngine NewEngine(IWorkflowStore store, IWorkflowDefinition definition)
    {
        var registry = new WorkflowRegistry();
        registry.Register(definition);
        return new WorkflowEngine(registry, store: store);
    }

    [Fact]
    public async Task A_stale_write_is_rejected_and_the_stored_state_survives()
    {
        // Given an instance loaded at one revision
        // And another writer has since saved
        var store = new InMemoryWorkflowStore();
        var record = new WorkflowInstanceRecord
        {
            Id = Guid.NewGuid(),
            DefinitionId = "two-step",
            DefinitionVersion = 1,
            Status = InstanceStatus.Running,
            CurrentStepIndex = 0,
            CurrentStepName = "A",
            CreatedAt = T0,
        };

        await store.CreateAsync(record);

        var first = await store.FindAsync(record.Id);
        var stale = await store.FindAsync(record.Id);

        await store.SaveAsync(first! with { CurrentStepName = "B" }, []);

        // When the first writer saves
        // Then a ConcurrencyException is raised
        var ex = await Assert.ThrowsAsync<WorkflowStoreConcurrencyException>(
            async () => await store.SaveAsync(stale! with { CurrentStepName = "GHOST" }, []));

        Assert.Equal(record.Id, ex.InstanceId);
        Assert.True(ex.ActualRevision > ex.ExpectedRevision);

        // And the stored state remains at the newer revision
        var reloaded = await store.FindAsync(record.Id);
        Assert.Equal("B", reloaded!.CurrentStepName);
    }

    [Fact]
    public async Task Two_engines_resuming_the_same_instance_do_not_both_succeed()
    {
        // The case #39 must build on. Without a concurrency token, both hosts
        // would execute the same step and its side effects would happen twice.
        var store = new InMemoryWorkflowStore();
        var seen = new HashSet<Guid>();

        var started = await HostOver(store, seen).StartAsync("two-step", 1);
        Assert.Equal(InstanceStatus.Suspended, started.Status);

        var hostA = HostOver(store, seen);
        var hostB = HostOver(store, seen);

        var results = await Task.WhenAll(
            Attempt(() => hostA.ResumeAsync(started.Id)),
            Attempt(() => hostB.ResumeAsync(started.Id)));

        // Exactly one wins. That is the guarantee NFR-1 depends on: the step
        // and its side effects must not run twice.
        Assert.Equal(1, results.Count(outcome => outcome is null));

        // The loser is refused by one of two independent defences, depending on
        // how the race interleaved:
        //
        //   - it loaded *after* the winner changed the status, so the resume
        //     precondition rejects it   -> InvalidStateTransitionException
        //   - it loaded *before*, ran, and lost the save
        //                               -> WorkflowStoreConcurrencyException
        //
        // Asserting only the second would make this test pass or fail on
        // timing. Both are correct refusals; what matters is that one loses.
        var loser = results.Single(outcome => outcome is not null);

        Assert.True(
            loser is WorkflowStoreConcurrencyException or InvalidStateTransitionException,
            $"loser failed with an unexpected {loser!.GetType().Name}: {loser.Message}");

        static async Task<Exception?> Attempt(Func<Task> action)
        {
            try
            {
                await action();
                return null;
            }
            catch (Exception ex)
            {
                return ex;
            }
        }
    }

    [Fact]
    public async Task The_instance_ends_in_a_coherent_state_after_a_conflict()
    {
        // A losing writer must not leave the instance half-updated. It either
        // completed under the winner or is still resumable - never mangled.
        var store = new InMemoryWorkflowStore();
        var seen = new HashSet<Guid>();

        var started = await HostOver(store, seen).StartAsync("two-step", 1);

        var hostA = HostOver(store, seen);
        var hostB = HostOver(store, seen);

        await Task.WhenAll(
            Swallow(() => hostA.ResumeAsync(started.Id)),
            Swallow(() => hostB.ResumeAsync(started.Id)));

        var final = await hostA.GetInstanceAsync(started.Id);

        Assert.True(
            final.Status is InstanceStatus.Completed or InstanceStatus.Suspended,
            $"instance left in {final.Status}");

        static async Task Swallow(Func<Task> action)
        {
            try
            {
                await action();
            }
            catch (WorkflowStoreConcurrencyException)
            {
                // the loser lost the save
            }
            catch (InvalidStateTransitionException)
            {
                // the loser was refused before it started
            }
        }
    }

    [Fact]
    public async Task Cancelling_from_stale_state_is_rejected_rather_than_overwriting()
    {
        // An operator acting on a dashboard that has gone stale must not
        // silently undo whatever happened since it was rendered.
        var store = new InMemoryWorkflowStore();
        var seen = new HashSet<Guid>();

        var started = await HostOver(store, seen).StartAsync("two-step", 1);

        // Another host advances the instance to completion.
        await HostOver(store, seen).ResumeAsync(started.Id);

        var completed = await HostOver(store, seen).GetInstanceAsync(started.Id);
        Assert.Equal(InstanceStatus.Completed, completed.Status);

        // An operator still looking at the stale "Suspended" view clicks cancel.
        // It is refused on the current state, not applied to the stale one.
        await Assert.ThrowsAsync<InvalidStateTransitionException>(
            async () => await HostOver(store, seen).CancelAsync(started.Id));

        // And the completion stands.
        Assert.Equal(
            InstanceStatus.Completed,
            (await HostOver(store, seen).GetInstanceAsync(started.Id)).Status);
    }

    [Fact]
    public async Task Each_checkpoint_advances_the_revision_monotonically()
    {
        var store = new InMemoryWorkflowStore();
        var registry = new WorkflowRegistry();
        registry.Register(new TwoStep(() => new NoopStep(), () => new NoopStep()));
        var engine = new WorkflowEngine(registry, store: store);

        var instance = await engine.StartAsync("two-step", 1);

        // create (1) plus one checkpoint per step plus the completion save.
        Assert.True(instance.Revision >= 4, $"expected at least 4 revisions, got {instance.Revision}");
    }
}
