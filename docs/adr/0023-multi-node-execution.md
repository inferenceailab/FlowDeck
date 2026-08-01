# ADR-0023: Multi-node execution

**Status:** Accepted · **Milestone:** M6 · **Issue:** #39

## Context

FlowDeck runs on one node. Everything works until that node dies, at which point
its in-flight instances stop and nothing picks them up — four separate
limitations in the workflow guide point at this issue:

- a retry backoff blocks the calling task
- an instance left `Running` by a crash is never resumed
- a rollback interrupted by a crash does not resume
- single node only

The concurrency primitive is now known. [ADR-0013](0013-persistence-model.md)
made the instance record authoritative and #19 added a `Revision` token that the
store contract guarantees on every provider. That was the precondition for
deciding any of this.

## Decisions

### 1. A dispatcher recovers work; `StartAsync` still runs inline

**Decided by the maintainer, 2026-08-01.**

A background dispatcher on every node polls for instances that are **claimable**
— suspended, or abandoned by a node that died — claims one, and runs it.

`StartAsync` keeps today's behaviour: it executes the workflow on the caller's
thread and returns when the instance completes or suspends.

The alternative, making `StartAsync` enqueue and return immediately, is the
genuine distributed model: load spreads, and there is one execution path rather
than two. It was rejected on cost. It changes a public contract, and a large
share of the existing tests assume `StartAsync` runs to completion — reworking
them would be a great deal of churn for load-spreading nobody has asked for.

**What this does not do**, stated because the difference is easy to miss: an
instance started on a busy node stays on that node. This is recovery, not load
balancing.

### 2. Claiming is a lease with a heartbeat

**Decided by the maintainer, 2026-08-01.**

A node writes its identity and a lease expiry onto the instance record, and
renews while it works:

| Column | Meaning |
| --- | --- |
| `OwnerNodeId` | The node currently running this instance, or null |
| `LeaseExpiresAt` | When the claim lapses if not renewed |

An expired lease is what an orphan *is*. Claiming and orphan detection are one
mechanism rather than two that must agree with each other.

The alternative — claiming with the existing `Revision` token — needs no new
columns and no clocks. Rejected because it says nobody else has claimed the
instance, not whether the claimer is still alive, so orphan detection would need
a separate answer anyway.

### 3. The lease lives on the instance record

Not in a separate coordination store. ADR-0013 makes the instance record
authoritative, and a lease in a second store introduces a consistency problem
between two things that must never disagree: a node could hold a lease for an
instance whose state says otherwise.

It also keeps the deployment story unchanged — no second database, no Redis.

### 4. Nodes are symmetric

**Decided by the maintainer, 2026-08-01.**

Every node runs the same code and polls for the same work. No leader, no
election, no failover path, and no split-brain failure mode.

The cost is that every node polls the database, so query load grows with node
count. Acceptable: the poll is one indexed query on an interval measured in
seconds, and a homelab deployment is not going to run fifty nodes.

### 5. A node identifies itself, and a restart is a new node

Node identity is supplied by the host, defaulting to
`{MachineName}:{ProcessId}`.

A restarted process therefore gets a **new** identity and does not inherit its
predecessor's leases. That is correct rather than unfortunate: the old process's
in-flight work was abandoned when it died, and letting a new process silently
adopt those leases would skip exactly the recovery this exists to perform.

### 6. Losing a lease stops the loser at its next checkpoint

The dangerous case is a lease expiring while the owner is still working — a slow
step, a paused process, a clock that jumped. Two nodes then believe they own the
instance.

Every checkpoint is already guarded by `Revision`. A node that lost its lease
also lost the race to write, so its next `SaveAsync` raises
`WorkflowStoreConcurrencyException` and it stops.

**This bounds the damage; it does not prevent it.** Both nodes may have
*executed* the same step before either tried to write. Fencing means at most one
of them records progress, not that the step ran once.

That is a real hole in NFR-1, and it is the price of leases. It is mitigated,
not closed, by the same requirement retry already imposes: a step that may run
twice must be idempotent. The workflow guide says so for retry
([#108](../guides/defining-a-workflow.md#your-step-must-be-idempotent)); M6
extends that warning to cover lease expiry.

### 7. Leases assume roughly agreed clocks

Expiry is compared against each node's own clock through its injected
`TimeProvider`, not the database's.

The store is provider-agnostic — `EntityFrameworkCore.Relational` only — and
there is no portable way to ask for a server timestamp across SQLite, PostgreSQL
and SQL Server without provider-specific SQL, which ADR-0010 and the conformance
suite both push against.

So: nodes with badly skewed clocks will misjudge expiry. A node whose clock runs
fast will reclaim work that is still running. This is documented rather than
defended, and the mitigation is the ordinary one — run NTP.

## Consequences

- A crashed node's work is recovered without a human, which is the point.
- Two new columns on the instance record, so hosts upgrading need a migration
  (ADR-0015: FlowDeck ships none).
- The lease interval becomes an operational knob: too short and healthy work
  gets stolen, too long and recovery is slow.
- Every node polls, so an idle cluster still generates database traffic.
- `StartAsync` and the dispatcher are two execution paths into the same engine.
  They share `RunAsync`, but a reader has to know both exist.
- Duplicate step execution becomes possible under lease expiry where previously
  it was not. Named in decision 6 rather than buried.

## Deliberately not decided here

**Whether the dispatcher also drains on shutdown.** A rolling deploy that kills
a node mid-instance should ideally release the lease rather than wait for it to
lapse. That is its own story, because doing it properly means cooperating with
the host's shutdown timeout rather than just releasing on `Dispose`.

**Whether a blocking retry backoff moves to the dispatcher.** ADR-0020 left this
open and it stays open: a step waiting five minutes could park itself as
suspended and let the dispatcher pick it up later, freeing the worker. Worth
doing and not required for recovery, so it is not in the critical path.

## Alternatives considered

**Hangfire-style invisibility timeout.** A claimed job becomes invisible to
other workers for a fixed window. Effectively the same as a lease without
renewal — simpler, and it forces the timeout to be as long as the longest
possible step. Renewal lets the timeout be short and recovery fast.

**A distributed lock service** (Redis, Consul, ZooKeeper). Well-understood and a
second piece of infrastructure to deploy and operate. Rejected for a homelab
target where the database is already there.

**Leader election with work distribution.** Less polling and a natural home for
sweeps like retention. Rejected as substantially more machinery — election,
failover, split-brain — than a recovery mechanism needs.
