# FlowDeck — Requirements

## Vision

A generic, resilient workflow execution engine for .NET, with a dashboard that
makes running workflows legible to an operator.

Workflow steps are written in C#. The engine's job is everything around that
code: sequencing it, giving it somewhere to put state, surviving restarts,
retrying what is worth retrying, and showing an operator what happened.

## Inspiration

The architecture deliberately borrows rather than invents:

| Source | What is taken from it |
| --- | --- |
| **WorkflowCore** | Step abstraction, saga and compensation shape |
| **Hangfire** | Durable job state, distributed locking, dashboard framing |
| **Elsa v3** | Definition versioning, bookmarks and suspension, designer concepts |
| **Octopus Deploy** | Dashboard UX, process visualisation, run inspection |

## Scope

### In scope

- Workflow definitions authored in C#, identified by id and version
- Durable instance state that survives process restart
- Retry policies and compensation for failed steps
- Execution across multiple engine nodes without double-running an instance
- HTTP control plane for starting, querying and cancelling instances
- Angular dashboard for monitoring and operating instances
- Deployment to a self-managed homelab environment

### Out of scope for v1

- A hosted or multi-tenant SaaS offering
- Workflow authoring by non-developers
- Cross-language step implementations
- Long-term analytics or reporting beyond execution history

### Unresolved scope question: sagas

The brief names compensation patterns, and #38 covers undoing **this workflow's
own** completed steps. It has never been decided whether FlowDeck also
coordinates distributed transactions across **external** services — participants
with their own local transactions and compensating actions.

The two are materially different. External coordination brings idempotency keys,
at-least-once delivery and participant registration, and would need its own epic
rather than sharing #38.

Until decided, treat sagas as **out of scope** and compensation as in scope. The
distinction is recorded on #38.

### Explicitly deferred

Visual workflow authoring is deferred to M7 and may begin read-only. The brief
states steps are implemented directly in C# initially, so a designer that
*writes* definitions is a later question, not a v1 requirement.

## Functional Requirements

### FR-1 — Definition and execution

| ID | Requirement | Milestone |
| --- | --- | --- |
| FR-1.1 | A workflow is declared in C# with a stable id and version | M1 ✅ |
| FR-1.2 | A workflow declares an ordered sequence of named steps | M1 ✅ |
| FR-1.3 | Steps execute in declaration order | M1 ✅ |
| FR-1.4 | A step may complete, suspend, or fail | M1 ✅ |
| FR-1.5 | Steps share typed state within an instance | M1 ✅ |
| FR-1.6 | A workflow may accept strongly typed input | M1 ✅ |
| FR-1.7 | An instance may be cancelled by an operator | M1 ✅ |

### FR-2 — Durability

| ID | Requirement | Milestone |
| --- | --- | --- |
| FR-2.1 | Instance state is persisted after every step | M2 |
| FR-2.2 | An interrupted instance resumes after process restart | M2 |
| FR-2.3 | Execution history is append-only and auditable | M2 |
| FR-2.4 | Concurrent modification of an instance is detected | M2 |
| FR-2.5 | Completed instances are purged after a retention period | M2 |

### FR-3 — Resilience

| ID | Requirement | Milestone |
| --- | --- | --- |
| FR-3.1 | Failed steps retry according to a declared policy | M5 |
| FR-3.2 | Completed steps can be compensated on later failure | M5 |
| FR-3.3 | Multiple nodes share execution without double-running | M6 |

### FR-4 — Control plane and dashboard

| ID | Requirement | Milestone |
| --- | --- | --- |
| FR-4.1 | Instances can be started, queried and cancelled over HTTP | M3 |
| FR-4.2 | Instance lists support paging and filtering | M3 |
| FR-4.3 | An operator can see instance status and step timeline | M4 |
| FR-4.4 | The dashboard updates without manual refresh | M4 |
| FR-4.5 | Workflows and runs are visually represented | M7 |

### FR-5 — Operator control

| ID | Requirement | Milestone |
| --- | --- | --- |
| FR-5.1 | An operator can cancel an instance | M1 ✅ |
| FR-5.2 | A suspended instance can be resumed, including after a restart | M10 (#68) |
| FR-5.3 | A failed instance can be retried | M10 (#66) |
| FR-5.4 | A completed instance can be re-run | M10 (#66) |
| FR-5.5 | Actions can be applied to a filtered set, not one at a time | M10 (#66) |

FR-5.2 is a **gap, not a plan**: `ResumeAsync` already exists in the engine but
arrived as a side effect of #12 with no story, no HTTP endpoint and no dashboard
exposure. A suspended workflow is currently only completable from inside the
process that started it.

### FR-5a — Supported databases

FlowDeck's EF Core provider depends only on `EntityFrameworkCore.Relational`,
so the host selects its database with the usual `UseX` call. No FlowDeck package
is needed per database.

| Database | Status | Verified by |
| --- | --- | --- |
| SQLite | ✅ supported | conformance suite, runs by default |
| PostgreSQL | ✅ supported | conformance suite, opt-in via `FLOWDECK_POSTGRES` |
| SQL Server | ✅ supported | conformance suite, opt-in via `FLOWDECK_SQLSERVER` |
| Others (MySQL, Oracle …) | should work | **unverified** — add a subclass to find out |

"Supported" means **the conformance suite passes against it**, not that the code
compiles. Anything in the last row is a design claim only.

Per-database notes that are tuning rather than correctness:

- **SQL Server** clusters the primary key by default, and instance ids are
  random `Guid`s. At volume that causes page splits and index fragmentation.
  A host that cares should make the PK non-clustered or use sequential ids.
- **PostgreSQL** stores `DataJson` as `text`. A host wanting to query inside
  workflow data would map it to `jsonb`, which FlowDeck does not require.

### FR-6 — Definition versioning

| ID | Requirement | Milestone |
| --- | --- | --- |
| FR-6.1 | Identity is `(Id, Version)`; versions coexist | M1 ✅ |
| FR-6.2 | An instance pins its definition version at start | M1 ✅ |
| FR-6.3 | In-flight instances of a superseded version have defined behaviour | M9 (#67) |
| FR-6.4 | A definition version can be retired safely | M9 (#67) |

## Non-Functional Requirements

### NFR-1 — Correctness

An instance must never execute the same step twice as a result of engine
behaviour. This is the single most important property: workflow steps have side
effects, and duplicate execution is worse than no execution.

Consequence: suspension leaves the instance positioned **on** the suspending
step, and resume re-enters it rather than skipping ahead. See
[ADR-0007](adr/0007-record-instance-before-execution.md).

### NFR-2 — Diagnosability

A failed instance must record what failed, where, and when, without an operator
needing to reproduce it. Step exceptions are preserved unwrapped, the failing
step name is recorded separately from execution position, and all timestamps
are UTC.

### NFR-3 — Trust boundaries

Workflow step code is author-written and treated as untrusted by the engine.
A step throwing must never unwind the engine's execution loop. See
[ADR-0003](adr/0003-step-executor-trust-boundary.md).

### NFR-4 — Testability

No component may require wall-clock time or an external service to test. The
engine takes an injectable `TimeProvider` and an injectable instance store.

### NFR-5 — Supply chain

Third-party dependencies are minimised deliberately. GitHub Actions are pinned
to full commit SHAs and restricted to GitHub-owned and verified creators;
container images build through the Docker CLI rather than marketplace actions.
A test-only clock was hand-rolled rather than taking a package dependency.

### NFR-6 — Deployment

The system deploys to self-managed infrastructure with no cloud dependency. CI
and CD both run on a self-hosted runner.

## Methodology Constraints

From the project brief, binding on all work:

- **BDD** — every user story carries Given/When/Then acceptance criteria
- **TDD** — no feature code without a failing test first

Where a story turns out to be already satisfied by earlier work, that is
reported honestly rather than staged as a false RED. Three M1 stories (#7, #8,
#9) fell into this category and their pull requests say so.

## Traceability

Requirements map to GitHub issues and milestones. The
[implementation plan](implementation-plan.md) tracks current status.
