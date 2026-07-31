using FlowDeck.Core;
using FlowDeck.Core.Persistence;

namespace FlowDeck.Core.Tests.Persistence;

/// <summary>
/// Issue #20 - Purge completed instances after a retention period.
///
/// Scenario: Instances older than the retention window are purged
/// Scenario: In-flight instances are never purged
/// </summary>
public class RetentionTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 31, 12, 0, 0, TimeSpan.Zero);

    private static WorkflowInstanceRecord Terminal(DateTimeOffset completedAt, InstanceStatus status = InstanceStatus.Completed) => new()
    {
        Id = Guid.NewGuid(),
        DefinitionId = "order",
        DefinitionVersion = 1,
        Status = status,
        CurrentStepIndex = 0,
        CreatedAt = completedAt.AddMinutes(-5),
        CompletedAt = completedAt,
    };

    private static WorkflowInstanceRecord InFlight(DateTimeOffset createdAt, InstanceStatus status) => new()
    {
        Id = Guid.NewGuid(),
        DefinitionId = "order",
        DefinitionVersion = 1,
        Status = status,
        CurrentStepIndex = 0,
        CurrentStepName = "A",
        CreatedAt = createdAt,
    };

    [Fact]
    public async Task Instances_older_than_the_retention_window_are_purged()
    {
        // Given retention is configured to 30 days
        // And a completed instance finished 31 days ago
        var store = new InMemoryWorkflowStore();
        var old = Terminal(Now.AddDays(-31));
        await store.CreateAsync(old);

        var purger = new InstancePurger(store, RetentionPolicy.Days(30), new TestTimeProvider(Now));

        // When the purge job runs
        var removed = await purger.PurgeAsync();

        // Then that instance is removed
        Assert.Equal(1, removed);
        Assert.Null(await store.FindAsync(old.Id));
    }

    [Fact]
    public async Task In_flight_instances_are_never_purged()
    {
        // Given a suspended instance created 90 days ago
        var store = new InMemoryWorkflowStore();
        var suspended = InFlight(Now.AddDays(-90), InstanceStatus.Suspended);
        var running = InFlight(Now.AddDays(-90), InstanceStatus.Running);

        await store.CreateAsync(suspended);
        await store.CreateAsync(running);

        var purger = new InstancePurger(store, RetentionPolicy.Days(30), new TestTimeProvider(Now));

        // When the purge job runs
        var removed = await purger.PurgeAsync();

        // Then that instance is retained
        Assert.Equal(0, removed);
        Assert.NotNull(await store.FindAsync(suspended.Id));
        Assert.NotNull(await store.FindAsync(running.Id));
    }

    [Fact]
    public async Task An_instance_inside_the_window_is_kept()
    {
        // Boundary: 29 days old under a 30 day policy stays.
        var store = new InMemoryWorkflowStore();
        var recent = Terminal(Now.AddDays(-29));
        await store.CreateAsync(recent);

        var purger = new InstancePurger(store, RetentionPolicy.Days(30), new TestTimeProvider(Now));

        Assert.Equal(0, await purger.PurgeAsync());
        Assert.NotNull(await store.FindAsync(recent.Id));
    }

    [Fact]
    public async Task Failed_and_cancelled_instances_are_purged_like_completed_ones()
    {
        var store = new InMemoryWorkflowStore();
        await store.CreateAsync(Terminal(Now.AddDays(-40), InstanceStatus.Failed));
        await store.CreateAsync(Terminal(Now.AddDays(-40), InstanceStatus.Cancelled));

        var purger = new InstancePurger(store, RetentionPolicy.Days(30), new TestTimeProvider(Now));

        Assert.Equal(2, await purger.PurgeAsync());
    }

    [Fact]
    public async Task Purged_instances_take_their_history_with_them()
    {
        var store = new InMemoryWorkflowStore();
        var record = Terminal(Now.AddDays(-40));
        await store.CreateAsync(record);

        var loaded = await store.FindAsync(record.Id);
        await store.SaveAsync(loaded!, [new StepHistoryEntry
        {
            InstanceId = record.Id,
            Sequence = 0,
            StepName = "A",
            StartedAt = Now.AddDays(-40),
            CompletedAt = Now.AddDays(-40),
            Status = StepStatus.Success,
        }]);

        await new InstancePurger(store, RetentionPolicy.Days(30), new TestTimeProvider(Now)).PurgeAsync();

        Assert.Empty(await store.GetHistoryAsync(record.Id));
    }

    [Fact]
    public async Task Running_the_purge_twice_removes_nothing_the_second_time()
    {
        var store = new InMemoryWorkflowStore();
        await store.CreateAsync(Terminal(Now.AddDays(-40)));

        var purger = new InstancePurger(store, RetentionPolicy.Days(30), new TestTimeProvider(Now));

        Assert.Equal(1, await purger.PurgeAsync());
        Assert.Equal(0, await purger.PurgeAsync());
    }

    [Fact]
    public void A_non_positive_retention_is_refused()
    {
        // Zero would mean "delete everything terminal on the next sweep", which
        // is almost certainly a configuration mistake rather than an intent.
        Assert.Throws<ArgumentOutOfRangeException>(() => RetentionPolicy.Days(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => RetentionPolicy.Days(-1));
    }

    [Fact]
    public async Task The_purge_moves_with_the_clock()
    {
        // An instance safe today falls out of the window later, without any
        // configuration change.
        var store = new InMemoryWorkflowStore();
        var record = Terminal(Now.AddDays(-29));
        await store.CreateAsync(record);

        var clock = new TestTimeProvider(Now);
        var purger = new InstancePurger(store, RetentionPolicy.Days(30), clock);

        Assert.Equal(0, await purger.PurgeAsync());

        clock.Advance(TimeSpan.FromDays(2));

        Assert.Equal(1, await purger.PurgeAsync());
    }
}
