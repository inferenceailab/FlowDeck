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
