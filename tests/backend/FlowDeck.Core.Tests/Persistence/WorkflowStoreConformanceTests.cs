using FlowDeck.Core;
using FlowDeck.Core.Persistence;

namespace FlowDeck.Core.Tests.Persistence;

/// <summary>
/// The contract every <see cref="IWorkflowStore"/> implementation must satisfy.
/// </summary>
/// <remarks>
/// Issue #16. This suite is the actual contract - <see cref="IWorkflowStore"/>
/// is only its signature. #17's EF Core provider subclasses this rather than
/// being trusted to behave the same way.
///
/// Kept provider-agnostic: no test may assume in-memory semantics, and any
/// setup a provider needs goes in <see cref="CreateStoreAsync"/>.
///
/// <para>
/// Tests are <c>[SkippableFact]</c> so a provider needing a database nobody has
/// configured reports as <b>skipped</b> rather than passed. A green tick that
/// means "not run" is worse than a red one - it is the same failure mode as the
/// CI job that silently skipped the whole backend build because it globbed
/// <c>*.sln</c> and the solution was a <c>.slnx</c>.
/// </para>
/// </remarks>
public abstract class WorkflowStoreConformanceTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 31, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Creates an empty store. Called once per test.</summary>
    protected abstract Task<IWorkflowStore> CreateStoreAsync();

    private static WorkflowInstanceRecord NewRecord(
        Guid? id = null,
        InstanceStatus status = InstanceStatus.Running,
        string definitionId = "order",
        DateTimeOffset? createdAt = null) => new()
        {
            Id = id ?? Guid.NewGuid(),
            DefinitionId = definitionId,
            DefinitionVersion = 1,
            Status = status,
            CurrentStepIndex = 0,
            CurrentStepName = "A",
            CreatedAt = createdAt ?? T0,
        };

    private static StepHistoryEntry NewHistory(Guid instanceId, string stepName) => new()
    {
        InstanceId = instanceId,
        Sequence = 0, // assigned by the store
        StepName = stepName,
        StartedAt = T0,
        CompletedAt = T0.AddSeconds(1),
        Status = StepStatus.Success,
        Attempt = 1,
    };

    // ------------------------------------------------------------- create

    [SkippableFact]
    public async Task A_created_instance_can_be_found()
    {
        var store = await this.CreateStoreAsync();
        var record = NewRecord();

        await store.CreateAsync(record);

        var found = await store.FindAsync(record.Id);

        Assert.NotNull(found);
        Assert.Equal(record.Id, found.Id);
        Assert.Equal("order", found.DefinitionId);
        Assert.Equal(InstanceStatus.Running, found.Status);
    }

    [SkippableFact]
    public async Task An_unknown_instance_is_reported_as_null_not_an_error()
    {
        var store = await this.CreateStoreAsync();

        Assert.Null(await store.FindAsync(Guid.NewGuid()));
    }

    [SkippableFact]
    public async Task Creating_the_same_id_twice_is_rejected()
    {
        var store = await this.CreateStoreAsync();
        var record = NewRecord();
        await store.CreateAsync(record);

        var ex = await Assert.ThrowsAsync<DuplicateInstanceException>(
            async () => await store.CreateAsync(record));

        Assert.Equal(record.Id, ex.InstanceId);
    }

    // --------------------------------------------------------------- save

    [SkippableFact]
    public async Task Saving_updates_state_and_increments_the_revision()
    {
        var store = await this.CreateStoreAsync();
        var record = NewRecord();
        await store.CreateAsync(record);

        var loaded = await store.FindAsync(record.Id);
        var saved = await store.SaveAsync(
            loaded! with { Status = InstanceStatus.Completed, CurrentStepName = null }, []);

        Assert.Equal(InstanceStatus.Completed, saved.Status);
        Assert.True(saved.Revision > loaded.Revision);

        var reloaded = await store.FindAsync(record.Id);
        Assert.Equal(InstanceStatus.Completed, reloaded!.Status);
        Assert.Equal(saved.Revision, reloaded.Revision);
    }

    [SkippableFact]
    public async Task Saving_from_a_stale_revision_is_rejected()
    {
        // Two writers load the same state; the second must lose.
        var store = await this.CreateStoreAsync();
        var record = NewRecord();
        await store.CreateAsync(record);

        var first = await store.FindAsync(record.Id);
        var second = await store.FindAsync(record.Id);

        await store.SaveAsync(first! with { CurrentStepName = "B" }, []);

        var ex = await Assert.ThrowsAsync<WorkflowStoreConcurrencyException>(
            async () => await store.SaveAsync(second! with { CurrentStepName = "C" }, []));

        Assert.Equal(record.Id, ex.InstanceId);
    }

    [SkippableFact]
    public async Task A_rejected_save_leaves_the_stored_state_untouched()
    {
        var store = await this.CreateStoreAsync();
        var record = NewRecord();
        await store.CreateAsync(record);

        var first = await store.FindAsync(record.Id);
        var stale = await store.FindAsync(record.Id);

        await store.SaveAsync(first! with { CurrentStepName = "B" }, []);

        await Assert.ThrowsAsync<WorkflowStoreConcurrencyException>(
            async () => await store.SaveAsync(stale! with { CurrentStepName = "C" }, []));

        var reloaded = await store.FindAsync(record.Id);
        Assert.Equal("B", reloaded!.CurrentStepName);
    }

    [SkippableFact]
    public async Task Saving_an_unknown_instance_is_rejected()
    {
        var store = await this.CreateStoreAsync();

        await Assert.ThrowsAsync<InstanceNotFoundException>(
            async () => await store.SaveAsync(NewRecord(), []));
    }

    // ------------------------------------------------------------ history

    [SkippableFact]
    public async Task History_is_appended_and_returned_in_order()
    {
        var store = await this.CreateStoreAsync();
        var record = NewRecord();
        await store.CreateAsync(record);

        var loaded = await store.FindAsync(record.Id);
        var afterA = await store.SaveAsync(loaded!, [NewHistory(record.Id, "A")]);
        await store.SaveAsync(afterA, [NewHistory(record.Id, "B"), NewHistory(record.Id, "C")]);

        var history = await store.GetHistoryAsync(record.Id);

        Assert.Equal(["A", "B", "C"], history.Select(entry => entry.StepName));
        Assert.Equal([1, 2, 3], history.Select(entry => entry.Sequence));
    }

    [SkippableFact]
    public async Task The_attempt_number_round_trips_on_history()
    {
        // #107, and the same lesson as #106: the row/record mapping is where a
        // field silently disappears. An attempt number that reads back as zero
        // makes a retried step indistinguishable from a re-entered one.
        var store = await this.CreateStoreAsync();
        var record = NewRecord();
        await store.CreateAsync(record);

        var loaded = await store.FindAsync(record.Id);

        await store.SaveAsync(loaded!, [
            NewHistory(record.Id, "charge") with { Attempt = 1, Status = StepStatus.Failed },
            NewHistory(record.Id, "charge") with { Attempt = 2, Status = StepStatus.Failed },
            NewHistory(record.Id, "charge") with { Attempt = 3 },
        ]);

        var history = await store.GetHistoryAsync(record.Id);

        Assert.Equal([1, 2, 3], history.Select(entry => entry.Attempt));
    }

    [SkippableFact]
    public async Task History_for_an_unknown_instance_is_empty()
    {
        var store = await this.CreateStoreAsync();

        Assert.Empty(await store.GetHistoryAsync(Guid.NewGuid()));
    }

    [SkippableFact]
    public async Task A_rejected_save_appends_no_history()
    {
        // The atomicity clause of the contract. If a concurrency failure could
        // still append history, an instance would end up with history for work
        // its state says never happened.
        var store = await this.CreateStoreAsync();
        var record = NewRecord();
        await store.CreateAsync(record);

        var first = await store.FindAsync(record.Id);
        var stale = await store.FindAsync(record.Id);

        await store.SaveAsync(first!, [NewHistory(record.Id, "A")]);

        await Assert.ThrowsAsync<WorkflowStoreConcurrencyException>(
            async () => await store.SaveAsync(stale!, [NewHistory(record.Id, "GHOST")]));

        var history = await store.GetHistoryAsync(record.Id);

        Assert.Equal(["A"], history.Select(entry => entry.StepName));
    }

    [SkippableFact]
    public async Task Existing_history_is_never_rewritten()
    {
        var store = await this.CreateStoreAsync();
        var record = NewRecord();
        await store.CreateAsync(record);

        var loaded = await store.FindAsync(record.Id);
        var afterA = await store.SaveAsync(loaded!, [NewHistory(record.Id, "A")]);
        var before = await store.GetHistoryAsync(record.Id);

        await store.SaveAsync(afterA, [NewHistory(record.Id, "B")]);
        var after = await store.GetHistoryAsync(record.Id);

        Assert.Equal(before[0].StepName, after[0].StepName);
        Assert.Equal(before[0].Sequence, after[0].Sequence);
        Assert.Equal(before[0].StartedAt, after[0].StartedAt);
    }

    // --------------------------------------------------------------- data

    [SkippableFact]
    public async Task Workflow_data_round_trips()
    {
        var store = await this.CreateStoreAsync();
        var record = NewRecord() with
        {
            Data = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["orderId"] = 42,
                ["customer"] = "acme",
            },
        };

        await store.CreateAsync(record);
        var found = await store.FindAsync(record.Id);

        Assert.Equal(2, found!.Data.Count);
        Assert.Equal(42, found.Data["orderId"]);
        Assert.Equal("acme", found.Data["customer"]);
    }

    [SkippableFact]
    public async Task A_null_data_value_round_trips_as_present()
    {
        // Matches WorkflowData's contract (ADR-0005): an explicit null is
        // distinct from an absent key, and persistence must not collapse them.
        var store = await this.CreateStoreAsync();
        var record = NewRecord() with
        {
            Data = new Dictionary<string, object?>(StringComparer.Ordinal) { ["note"] = null },
        };

        await store.CreateAsync(record);
        var found = await store.FindAsync(record.Id);

        Assert.True(found!.Data.ContainsKey("note"));
        Assert.Null(found.Data["note"]);
    }

    [SkippableFact]
    public async Task Failure_details_round_trip()
    {
        var store = await this.CreateStoreAsync();
        var record = NewRecord(status: InstanceStatus.Failed) with
        {
            FailedStepName = "charge",
            ErrorType = "InvalidOperationException",
            ErrorMessage = "card declined",
            CompletedAt = T0.AddMinutes(1),
        };

        await store.CreateAsync(record);
        var found = await store.FindAsync(record.Id);

        Assert.Equal("charge", found!.FailedStepName);
        Assert.Equal("InvalidOperationException", found.ErrorType);
        Assert.Equal("card declined", found.ErrorMessage);
        Assert.Equal(T0.AddMinutes(1), found.CompletedAt);
    }

    [SkippableFact]
    public async Task The_attempt_count_round_trips()
    {
        // #106. A store that drops this silently turns a bounded retry into an
        // unbounded one: every restart would reload zero attempts and the step
        // would run forever against a service that is already struggling.
        //
        // Part of the contract rather than one provider's test, because a
        // provider mapping row to record by hand is exactly where a field goes
        // missing without anything failing to compile.
        var store = await this.CreateStoreAsync();
        var record = NewRecord() with { StepAttempts = 2 };

        await store.CreateAsync(record);
        Assert.Equal(2, (await store.FindAsync(record.Id))!.StepAttempts);

        var loaded = await store.FindAsync(record.Id);
        await store.SaveAsync(loaded! with { StepAttempts = 3 }, []);

        Assert.Equal(3, (await store.FindAsync(record.Id))!.StepAttempts);
    }

    [SkippableFact]
    public async Task A_reset_attempt_count_round_trips_as_zero()
    {
        // The reset path matters as much as the increment. A store that only
        // ever wrote a non-zero value - by treating zero as "unset" and leaving
        // the column alone - would carry a stale count into the next step.
        var store = await this.CreateStoreAsync();
        var record = NewRecord() with { StepAttempts = 4 };
        await store.CreateAsync(record);

        var loaded = await store.FindAsync(record.Id);

        // Guards the assertion below from being vacuous: zero is the default,
        // so a store that never persisted the field at all would "pass" the
        // reset check while failing this one.
        Assert.Equal(4, loaded!.StepAttempts);

        await store.SaveAsync(loaded with { StepAttempts = 0 }, []);

        Assert.Equal(0, (await store.FindAsync(record.Id))!.StepAttempts);
    }

    [SkippableFact]
    public async Task The_active_node_set_round_trips()
    {
        // #163. The set is the position once branches exist (ADR-0024), so a
        // provider that dropped it would resume a forked instance at whichever
        // single node the projection happened to name and silently abandon the
        // others - a lost branch, not a visible error.
        var store = await this.CreateStoreAsync();
        var record = NewRecord() with
        {
            ActiveNodes =
            [
                new ActiveNode("audit", Attempts: 0, BranchPath: []),
                new ActiveNode("charge", Attempts: 2, BranchPath: ["payment"]),
                new ActiveNode("reserve", Attempts: 1, BranchPath: ["fulfilment", "warehouse"]),
            ],
        };

        await store.CreateAsync(record);

        var loaded = await store.FindAsync(record.Id);

        // Every field of every node, not just the count. Attempts are per node
        // because two branches retrying at once have two independent counts, and
        // a provider that stored only the names would satisfy a count check.
        Assert.Equal(record.ActiveNodes, loaded!.ActiveNodes);
    }

    [SkippableFact]
    public async Task A_cleared_active_node_set_round_trips_as_empty()
    {
        // The clearing path, same reasoning as the attempt-count reset: a store
        // that treated "empty" as "unset" and left the column alone would report
        // a completed instance as still active at its last step.
        var store = await this.CreateStoreAsync();
        var record = NewRecord() with { ActiveNodes = [ActiveNode.At("charge")] };
        await store.CreateAsync(record);

        var loaded = await store.FindAsync(record.Id);

        // Not vacuous: empty is the default, so a store that never persisted the
        // field would pass the clearing check while failing this one.
        Assert.Single(loaded!.ActiveNodes);

        await store.SaveAsync(loaded with { ActiveNodes = [] }, []);

        Assert.Empty((await store.FindAsync(record.Id))!.ActiveNodes);
    }

    [SkippableFact]
    public async Task The_active_node_set_comes_back_in_a_stable_order()
    {
        // Order is not significant - these are concurrent positions - but it has
        // to be *stable*, or two reads of an unchanged instance differ and a
        // caller diffing them sees a change that did not happen.
        var store = await this.CreateStoreAsync();
        var record = NewRecord() with
        {
            ActiveNodes = [ActiveNode.At("c"), ActiveNode.At("a"), ActiveNode.At("b")],
        };

        await store.CreateAsync(record);

        var first = await store.FindAsync(record.Id);
        var second = await store.FindAsync(record.Id);

        Assert.Equal(first!.ActiveNodes, second!.ActiveNodes);

        // Declaration order, specifically. A provider that sorted would also be
        // stable, but resuming would then re-enter branches in an order the
        // author never wrote, which reads as arbitrary in a timeline.
        Assert.Equal(["c", "a", "b"], first.ActiveNodes.Select(node => node.StepName));
    }

    [SkippableFact]
    public async Task Ownership_and_lease_round_trip()
    {
        // #143. Third field in a row to reach this suite because a provider
        // dropped one silently (#106, #107). An owner that reads back as null
        // makes every instance look claimable, so two nodes would run the same
        // work believing nobody had it.
        var store = await this.CreateStoreAsync();
        var record = NewRecord();
        await store.CreateAsync(record);

        var loaded = await store.FindAsync(record.Id);

        await store.SaveAsync(
            loaded! with { OwnerNodeId = "node-a", LeaseExpiresAt = T0.AddSeconds(30) },
            []);

        var owned = await store.FindAsync(record.Id);

        Assert.Equal("node-a", owned!.OwnerNodeId);
        Assert.Equal(T0.AddSeconds(30), owned.LeaseExpiresAt);
    }

    [SkippableFact]
    public async Task A_released_lease_round_trips_as_absent()
    {
        // Releasing matters as much as claiming: a store that only ever wrote a
        // non-null owner would leave every recovered instance looking owned by
        // a node that is gone.
        var store = await this.CreateStoreAsync();
        var record = NewRecord();
        await store.CreateAsync(record);

        var loaded = await store.FindAsync(record.Id);
        var owned = await store.SaveAsync(
            loaded! with { OwnerNodeId = "node-a", LeaseExpiresAt = T0.AddSeconds(30) },
            []);

        // Guards the assertion below from being vacuous.
        Assert.Equal("node-a", (await store.FindAsync(record.Id))!.OwnerNodeId);

        await store.SaveAsync(owned with { OwnerNodeId = null, LeaseExpiresAt = null }, []);

        var released = await store.FindAsync(record.Id);

        Assert.Null(released!.OwnerNodeId);
        Assert.Null(released.LeaseExpiresAt);
    }

    // ---------------------------------------------------------- claimable

    [SkippableFact]
    public async Task Claimable_work_excludes_live_leases_and_terminal_instances()
    {
        // #146. The query every node runs on a timer, so the contract has to
        // pin exactly what it offers. A provider that returned a live lease
        // would hand running work to a second node; one that returned a
        // terminal instance would "recover" a workflow that finished.
        var store = await this.CreateStoreAsync();

        var unowned = NewRecord(status: InstanceStatus.Suspended, createdAt: T0);
        var lapsed = NewRecord(status: InstanceStatus.Running, createdAt: T0.AddMinutes(1)) with
        {
            OwnerNodeId = "dead-node",
            LeaseExpiresAt = T0.AddSeconds(-1),
        };
        var live = NewRecord(status: InstanceStatus.Running, createdAt: T0.AddMinutes(2)) with
        {
            OwnerNodeId = "busy-node",
            LeaseExpiresAt = T0.AddMinutes(10),
        };
        var finished = NewRecord(status: InstanceStatus.Completed, createdAt: T0.AddMinutes(3)) with
        {
            CompletedAt = T0.AddMinutes(4),
            OwnerNodeId = "dead-node",
            LeaseExpiresAt = T0.AddSeconds(-1),
        };

        foreach (var record in new[] { unowned, lapsed, live, finished })
        {
            await store.CreateAsync(record);
        }

        var claimable = await store.FindClaimableAsync(T0, limit: 10);

        Assert.Equal([unowned.Id, lapsed.Id], claimable.Select(record => record.Id));
    }

    [SkippableFact]
    public async Task Claimable_work_comes_oldest_first_and_respects_the_limit()
    {
        // Oldest first, so work abandoned longest ago is recovered rather than
        // starved by a steady arrival of newer instances. The limit matters
        // because every claimed instance is a lease the node must renew.
        var store = await this.CreateStoreAsync();

        var oldest = NewRecord(status: InstanceStatus.Suspended, createdAt: T0);
        var middle = NewRecord(status: InstanceStatus.Suspended, createdAt: T0.AddMinutes(1));
        var newest = NewRecord(status: InstanceStatus.Suspended, createdAt: T0.AddMinutes(2));

        foreach (var record in new[] { newest, oldest, middle })
        {
            await store.CreateAsync(record);
        }

        var claimable = await store.FindClaimableAsync(T0.AddHours(1), limit: 2);

        Assert.Equal([oldest.Id, middle.Id], claimable.Select(record => record.Id));
    }

    // --------------------------------------------------------------- list

    [SkippableFact]
    public async Task Listing_returns_newest_first()
    {
        var store = await this.CreateStoreAsync();
        var older = NewRecord(createdAt: T0);
        var newer = NewRecord(createdAt: T0.AddHours(1));

        await store.CreateAsync(older);
        await store.CreateAsync(newer);

        var listed = await store.ListAsync(new InstanceFilter());

        Assert.Equal([newer.Id, older.Id], listed.Select(record => record.Id));
    }

    [SkippableFact]
    public async Task Listing_filters_by_status()
    {
        var store = await this.CreateStoreAsync();
        await store.CreateAsync(NewRecord(status: InstanceStatus.Failed));
        await store.CreateAsync(NewRecord(status: InstanceStatus.Completed));
        await store.CreateAsync(NewRecord(status: InstanceStatus.Failed));

        var failed = await store.ListAsync(new InstanceFilter { Status = InstanceStatus.Failed });

        Assert.Equal(2, failed.Count);
        Assert.All(failed, record => Assert.Equal(InstanceStatus.Failed, record.Status));
    }

    [SkippableFact]
    public async Task Listing_filters_by_definition_id()
    {
        var store = await this.CreateStoreAsync();
        await store.CreateAsync(NewRecord(definitionId: "order"));
        await store.CreateAsync(NewRecord(definitionId: "shipment"));

        var orders = await store.ListAsync(new InstanceFilter { DefinitionId = "order" });

        Assert.Single(orders);
        Assert.Equal("order", orders[0].DefinitionId);
    }

    [SkippableFact]
    public async Task Listing_pages_with_skip_and_take()
    {
        var store = await this.CreateStoreAsync();

        for (var i = 0; i < 5; i++)
        {
            await store.CreateAsync(NewRecord(createdAt: T0.AddMinutes(i)));
        }

        var page = await store.ListAsync(new InstanceFilter { Skip = 1, Take = 2 });

        Assert.Equal(2, page.Count);

        // Newest first, so skipping one starts at the second newest.
        Assert.Equal(T0.AddMinutes(3), page[0].CreatedAt);
        Assert.Equal(T0.AddMinutes(2), page[1].CreatedAt);
    }

    [SkippableFact]
    public async Task Counting_ignores_paging_but_honours_filters()
    {
        // #25 must report a total alongside a page. A count that respected Take
        // would always equal the page size and tell a caller nothing.
        var store = await this.CreateStoreAsync();

        for (var i = 0; i < 5; i++)
        {
            await store.CreateAsync(NewRecord(status: InstanceStatus.Failed));
        }

        await store.CreateAsync(NewRecord(status: InstanceStatus.Completed));

        var count = await store.CountAsync(
            new InstanceFilter { Status = InstanceStatus.Failed, Skip = 1, Take = 2 });

        Assert.Equal(5, count);
    }

    [SkippableFact]
    public async Task Listing_an_empty_store_returns_empty_not_null()
    {
        var store = await this.CreateStoreAsync();

        Assert.Empty(await store.ListAsync(new InstanceFilter()));
        Assert.Equal(0, await store.CountAsync(new InstanceFilter()));
    }

    // -------------------------------------------------------------- purge

    [SkippableFact]
    public async Task Purging_removes_terminal_instances_older_than_the_cutoff()
    {
        var store = await this.CreateStoreAsync();
        var old = NewRecord(status: InstanceStatus.Completed) with { CompletedAt = T0 };
        var recent = NewRecord(status: InstanceStatus.Completed) with { CompletedAt = T0.AddDays(40) };

        await store.CreateAsync(old);
        await store.CreateAsync(recent);

        var removed = await store.PurgeAsync(T0.AddDays(30));

        Assert.Equal(1, removed);
        Assert.Null(await store.FindAsync(old.Id));
        Assert.NotNull(await store.FindAsync(recent.Id));
    }

    [SkippableFact]
    public async Task Purging_never_removes_an_in_flight_instance()
    {
        // Age is not evidence that work is finished. Deleting a suspended
        // instance would destroy work that is merely waiting.
        var store = await this.CreateStoreAsync();
        var ancientRunning = NewRecord(status: InstanceStatus.Running, createdAt: T0.AddYears(-1));
        var ancientSuspended = NewRecord(status: InstanceStatus.Suspended, createdAt: T0.AddYears(-1));

        await store.CreateAsync(ancientRunning);
        await store.CreateAsync(ancientSuspended);

        var removed = await store.PurgeAsync(T0.AddYears(1));

        Assert.Equal(0, removed);
        Assert.NotNull(await store.FindAsync(ancientRunning.Id));
        Assert.NotNull(await store.FindAsync(ancientSuspended.Id));
    }

    [SkippableFact]
    public async Task Purging_removes_failed_and_cancelled_instances_too()
    {
        var store = await this.CreateStoreAsync();

        foreach (var status in new[] { InstanceStatus.Completed, InstanceStatus.Failed, InstanceStatus.Cancelled })
        {
            await store.CreateAsync(NewRecord(status: status) with { CompletedAt = T0 });
        }

        Assert.Equal(3, await store.PurgeAsync(T0.AddDays(1)));
        Assert.Empty(await store.ListAsync(new InstanceFilter()));
    }

    [SkippableFact]
    public async Task Purging_removes_the_history_of_purged_instances()
    {
        // History outliving its instance would leak storage forever and orphan
        // rows nothing can join back to.
        var store = await this.CreateStoreAsync();
        var record = NewRecord();
        await store.CreateAsync(record);

        var loaded = await store.FindAsync(record.Id);
        await store.SaveAsync(
            loaded! with { Status = InstanceStatus.Completed, CompletedAt = T0 },
            [NewHistory(record.Id, "A")]);

        await store.PurgeAsync(T0.AddDays(1));

        Assert.Empty(await store.GetHistoryAsync(record.Id));
    }

    [SkippableFact]
    public async Task Purging_is_idempotent()
    {
        var store = await this.CreateStoreAsync();
        await store.CreateAsync(NewRecord(status: InstanceStatus.Completed) with { CompletedAt = T0 });

        Assert.Equal(1, await store.PurgeAsync(T0.AddDays(1)));
        Assert.Equal(0, await store.PurgeAsync(T0.AddDays(1)));
    }

    [SkippableFact]
    public async Task A_terminal_instance_without_a_completion_time_is_not_purged()
    {
        // Defensive: a null CompletedAt on a terminal instance is a data defect,
        // and guessing its age would delete something on the strength of a bug.
        var store = await this.CreateStoreAsync();
        var malformed = NewRecord(status: InstanceStatus.Completed) with { CompletedAt = null };

        await store.CreateAsync(malformed);

        Assert.Equal(0, await store.PurgeAsync(T0.AddYears(10)));
        Assert.NotNull(await store.FindAsync(malformed.Id));
    }

    // -------------------------------------------------------- isolation

    [SkippableFact]
    public async Task Records_returned_by_the_store_are_not_live_references()
    {
        // A provider that hands back its own stored object would let a caller
        // mutate persisted state without saving - and would behave differently
        // from a database-backed provider, defeating the point of this suite.
        var store = await this.CreateStoreAsync();
        var record = NewRecord();
        await store.CreateAsync(record);

        var first = await store.FindAsync(record.Id);
        var second = await store.FindAsync(record.Id);

        Assert.NotSame(first, second);
    }
}
