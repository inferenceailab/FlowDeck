# Writing a persistence provider

FlowDeck stores instances through `IWorkflowStore`. Two providers ship:
`InMemoryWorkflowStore` and `EfCoreWorkflowStore`. This is how to write a third.

## The contract is the test suite, not the interface

`IWorkflowStore` is a signature. **`WorkflowStoreConformanceTests` is the
contract.** A provider is conformant because it passes that suite, not because
it compiles or because it was reviewed and looked reasonable.

```csharp
public sealed class MyWorkflowStoreTests : WorkflowStoreConformanceTests
{
    protected override Task<IWorkflowStore> CreateStoreAsync() =>
        Task.FromResult<IWorkflowStore>(new MyWorkflowStore(/* ... */));
}
```

That is the whole integration. `InMemoryWorkflowStoreTests` and
`EfCoreWorkflowStoreTests` are each three lines for exactly this reason: anything
asserted only in a provider's own tests is behaviour the contract does not
actually require.

The suite is not theatre. It has caught two real defects already: EF Core failed
eleven tests because SQLite refuses to `ORDER BY` a `DateTimeOffset`, and
mutation-testing the in-memory provider showed that removing its concurrency
check fails three tests and removing copy-on-read fails one.

## You probably do not need a new provider

If your database has an EF Core provider, `EfCoreWorkflowStore` already works —
it depends only on `EntityFrameworkCore.Relational`, so nothing in it is tied to
a particular database:

```csharp
var options = new DbContextOptionsBuilder<WorkflowDbContext>()
    .UseSqlServer(connectionString)     // or UseNpgsql, UseSqlite, …
    .Options;

var store = new EfCoreWorkflowStore(() => new WorkflowDbContext(options));
```

**Verify it rather than assuming it.** Add a subclass and run the suite:

```csharp
public sealed class MySqlConformanceTests : RelationalConformanceTests
{
    protected override string ConnectionStringVariable => "FLOWDECK_MYSQL";

    protected override string DatabaseName => "MySQL";

    protected override void Configure(
        DbContextOptionsBuilder<WorkflowDbContext> builder, string connectionString) =>
        builder.UseMySql(connectionString, ServerVersion.AutoDetect(connectionString));
}
```

Tests skip when the environment variable is absent, so `dotnet test` stays fast
by default and reports **skipped** — never a green tick for something that did
not run.

Write a genuinely new provider only for a store EF Core cannot reach: a document
database, a key-value store, an event log.

## What the store must guarantee

### 1. Checkpoint state is authoritative

Per [ADR-0013](../adr/0013-persistence-model.md), `WorkflowInstanceRecord` is the
truth. History is an append-only log written alongside it. Recovery reads the
record and **never replays history**.

### 2. State and history are written atomically

`SaveAsync` takes both. It must persist them together or not at all.

> A provider that cannot transact across both is not conformant. A crash between
> the two writes would leave an instance whose state disagrees with its own
> history.

`A_rejected_save_appends_no_history` asserts this directly.

### 3. Optimistic concurrency via `Revision`

`SaveAsync` compares the incoming `Revision` to the stored one. On mismatch it
raises `WorkflowStoreConcurrencyException` **and writes nothing** — no state, no
history.

`Revision` is an `int` rather than a provider-specific row version precisely so
one suite can constrain every provider.

### 4. Reads return copies, not live references

A caller must not be able to mutate persisted state by holding onto what a read
returned. The in-memory provider copies the `Data` dictionary for this reason; a
database-backed provider gets it for free.

Asserted by `Records_returned_by_the_store_are_not_live_references`.

### 5. Purge only touches terminal instances

`PurgeAsync` removes `Completed`, `Failed` and `Cancelled` instances that
finished before the cutoff, **with their history**. It must never remove a
`Running` or `Suspended` instance regardless of age — age is not evidence that
work is finished.

A terminal instance with a null `CompletedAt` is a data defect: leave it alone
rather than guessing its age.

### 6. `CountAsync` ignores paging

It honours `Status` and `DefinitionId`, and ignores `Skip` and `Take`. A count
that respected paging would always equal the page size and tell a caller
nothing.

### 7. Every field of the record round-trips

Including the ones that look like bookkeeping. A provider mapping row to record
by hand is where a field goes missing **without anything failing to compile** —
the code builds, the suite is green, and the value is quietly zero on every read.

The EF Core provider did exactly this with `StepAttempts` between #103 and #106:
the count was durable in the in-memory store and silently discarded by the
database one, which would have turned a bounded retry into an unbounded one
across a restart. `The_attempt_count_round_trips` is in the suite because of it.

If a field is not worth persisting, it does not belong on the record.

## Serialisation

If your store holds text rather than objects, use `WorkflowDataSerializer`. It
tags each value with its type and resolves types only from an allow-list — see
[ADR-0014](../adr/0014-workflow-data-serialisation.md). Do not roll your own:
resolving arbitrary type names from stored data is how deserialisation
vulnerabilities work.

Run the suite against your provider with serialisation enabled. That is what
catches a workflow storing something unserialisable **in the fast test suite**
rather than in production.

## Schema management

Per [ADR-0015](../adr/0015-migrations-are-owned-by-the-host.md), FlowDeck ships
no migrations — they are provider-specific. If your provider has a schema,
either expose a migrator or document how a host creates it.

Whatever you do, it must never drop or recreate anything. A "fix" that recreates
the schema destroys in-flight instances.

**Adding a field to the record is a schema change hosts must apply.** Because
FlowDeck ships no migrations, a host running the EF Core provider against a
database created before #106 needs a column added:

```sql
-- #106, #107
ALTER TABLE flowdeck_instances ADD StepAttempts   INTEGER NOT NULL DEFAULT 0;
ALTER TABLE flowdeck_history   ADD Attempt        INTEGER NOT NULL DEFAULT 1;

-- #143
ALTER TABLE flowdeck_instances ADD OwnerNodeId    VARCHAR(200) NULL;
ALTER TABLE flowdeck_instances ADD LeaseExpiresAt BIGINT       NULL;

CREATE INDEX ix_flowdeck_instances_claimable
    ON flowdeck_instances (Status, LeaseExpiresAt);
```

The defaults differ deliberately. Zero attempts on an instance means an instance
mid-retry when the upgrade lands gets a fresh allowance rather than a wrong one.
One on a history row means every execution recorded before #107 reads as a first
attempt — which is what it was, since retries did not exist.

The lease columns are **nullable with no default**: an instance in flight when
the upgrade lands is owned by nobody, which is true, and it becomes claimable at
once rather than looking held by a node that never existed.

`LeaseExpiresAt` is a `BIGINT` because every `DateTimeOffset` is stored as UTC
ticks — see the conversion note above. The index is what the dispatcher polls on
(#147); without it every node scans the table on every tick.

## Running the suite against a real database

The suite runs against the in-memory store and SQLite by default, which needs no
database and is why `dotnet test` is fast. **SQLite is not the deployment
target**, and the two do not behave identically — #17 lost eleven tests to
SQLite refusing to `ORDER BY` a `DateTimeOffset`.

Two subclasses run the same suite against a real database, skipped unless a
connection string is present:

```bash
# PostgreSQL — the homelab target
FLOWDECK_POSTGRES="Host=localhost;Database=flowdeck_test;Username=postgres;Password=..." dotnet test

# SQL Server
FLOWDECK_SQLSERVER="Server=localhost;Database=flowdeck_test;Trusted_Connection=True;TrustServerCertificate=True" dotnet test
```

Absent, they report **skipped** — never a green tick for something that did not
run.

**CI runs the PostgreSQL pass on every push**, through a runner service
container rather than a Testcontainers package. GitHub hosts the database
declaratively, so verifying the real target costs no dependency and leaves
[ADR-0010](../adr/0010-minimise-third-party-dependencies.md) alone.

### What running it against PostgreSQL found

Every conformance case passes. The provider depends only on
`EntityFrameworkCore.Relational`, and that design claim now has a tested one
behind it rather than only an argument.

The one divergence is in the **test harness**, not in FlowDeck:

| | In-memory | SQLite | PostgreSQL |
| --- | --- | --- | --- |
| Whole suite | ✅ | ✅ | ✅ |
| Several writers racing one instance | ✅ | **skipped** | ✅ |

A SQLite in-memory database lives only while a connection to it is open, so the
harness hands every `DbContext` the *same* connection — and Microsoft.Data.Sqlite
cannot run concurrent commands on one. It fails with
`SQLite Error 5: unable to delete/modify user-function due to active statements`
before reaching anything FlowDeck does.

So that case declares itself unsupported for that harness, with the reason
attached, and runs everywhere else. The opt-out is deliberately narrow — one
named case, not "skip what fails here" — because a provider that could opt out of
the contract would defeat the suite it is proving itself against.

## Checklist

- [ ] Subclass `WorkflowStoreConformanceTests`; all tests pass
- [ ] `SaveAsync` writes state and history atomically
- [ ] A rejected save writes nothing at all
- [ ] Reads return copies
- [ ] `PurgeAsync` spares in-flight instances and removes history with instances
- [ ] `CountAsync` ignores `Skip`/`Take`
- [ ] Every field of `WorkflowInstanceRecord` round-trips, bookkeeping included
- [ ] If text-backed, run the suite with `WorkflowDataSerializer`
- [ ] Schema creation never destroys existing data

## See also

- [Architecture](../architecture.md)
- [ADR-0013](../adr/0013-persistence-model.md) — persistence model
- [ADR-0014](../adr/0014-workflow-data-serialisation.md) — serialisation
- [ADR-0015](../adr/0015-migrations-are-owned-by-the-host.md) — migrations
