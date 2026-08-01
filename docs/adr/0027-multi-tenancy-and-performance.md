# ADR-0027: Multi-tenancy is out of scope; performance is a measured baseline

**Status:** Accepted · **Milestone:** M9 · **Issues:** #43

## Context

#43 asked two questions that have sat open since Phase 1: whether FlowDeck serves
multiple tenants, and what throughput is acceptable.

Neither has an answer in the codebase, and the absence has a cost of its own. An
open scope question is a thing every later design has to leave room for, and
"performance" with no number is a topic nobody can be wrong about.

Both decisions here were the maintainer's.

## Decisions

### 1. Multi-tenancy is out of scope for v1

**Decided by the maintainer, 2026-08-01.**

FlowDeck is a workflow engine for a homelab with one operator. It does not
isolate tenants, and no part of it will pretend to.

This is a decision rather than an omission, and the difference matters: a reader
who finds no tenant column can currently only guess whether that is deliberate.

**What would have to change**, recorded so that a future decision starts from
facts rather than a blank page:

| Area | Change |
| --- | --- |
| Instance record | A tenant field, round-tripped by every provider and the conformance suite |
| Claiming | `FindClaimableAsync` scoped per tenant, or fairness across tenants becomes a lottery |
| Every store query | `ListAsync`, `CountAsync`, `PurgeAsync`, history — a missed filter is a cross-tenant read |
| Registry | Whether a definition is global or per-tenant, which is its own question |
| API | Tenant resolution from the caller, which needs #42 first |
| Dashboard | Tenant selection, and an operator's scope within it |

The sharpest of those is the third. Row-level isolation makes every query a
correctness boundary, and a filter someone forgets is a data leak rather than a
bug — a failure mode that does not announce itself.

**Multi-tenancy is blocked on authentication (#42) regardless.** There is no way
to attribute a request to a tenant when there is no way to attribute it to
anybody.

### 2. Performance is a measured baseline and a regression guard, not a target

**Decided by the maintainer, 2026-08-01.**

What ships is a number that exists: throughput measured on stated hardware,
recorded, and guarded by a test that fails if it degrades sharply.

A committed target was rejected. FlowDeck has no real workload yet, so a number
invented now would be either trivially met or arbitrary, and either way it would
become something to defend rather than something to learn from.

**The guard is deliberately loose.** It runs on GitHub-hosted runners whose speed
varies between runs, so a tight bound would fail for reasons that have nothing to
do with the engine — and a flaky guard is worse than none, because the first
response to it is to delete it. It is set to catch an order-of-magnitude
regression: the kind a change to checkpointing or claiming would cause, not the
kind a busy runner causes.

### 3. What gets measured

Taken without asking. Three numbers, chosen because each answers a question an
operator actually has:

- **Instances per second, end to end** — the headline, and the one that moves when
  anything on the hot path changes.
- **Checkpoint cost per step** — the engine writes after every step (ADR-0013), so
  this is where throughput is spent and where a regression would first appear.
- **Backlog recovery** — how long a dispatcher takes to pick up abandoned work.
  M6's machinery is the part with no visible cost today.

Measured against the in-memory store, deliberately. The EF Core provider's numbers
would mostly measure SQLite, and the question here is what the *engine* costs.

## Consequences

- A reader can tell that single-tenancy is a decision, and what reversing it costs.
- "Is it fast enough" becomes a number with hardware attached rather than an
  opinion.
- The guard will catch a large regression and will not catch a small one. That is
  the trade being made, not an oversight.
- Known bottlenecks are named rather than measured away: the retry backoff blocks
  its branch (ADR-0020), every step costs a round trip, and the dispatcher polls
  on an interval. None of these is fixed here.

## Alternatives considered

**Row-level multi-tenancy now.** One database, a tenant column, filters
everywhere. Rejected: it is the largest correctness surface in the project for a
requirement nobody has, and it cannot be built honestly before #42 exists.

**A stated throughput SLO.** Gives the project something to hold itself to.
Rejected by the maintainer as premature — see decision 2.

**A full load-testing harness.** More faithful than an in-process benchmark, and
it needs infrastructure the homelab does not have and CI cannot run. Rejected as
disproportionate to a project whose deployment is two containers.
