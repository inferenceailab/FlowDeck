# ADR-0029: Suspending inside a branch

**Status:** Accepted · **Milestone:** M10 · **Issues:** #179

**Amends** [ADR-0024](0024-branching-and-parallel-execution.md), which left this
open.

## Context

`Outcome.Suspend` from a step inside a branch currently **fails the instance**
with a `NotSupportedException`. An author who needs to wait for something inside
a fork has to restructure the workflow so the wait happens on the top-level
sequence.

The guard was added by #164, and the reason it gave has not been true since #166:

> the instance's durable position while a fork is open is the branching step, so
> resuming would re-run every branch step that had already completed

#163 made the position a set of `ActiveNode`s carrying branch paths, and #166
taught `ResumeAsync` to resume from that set. The machinery this needs already
exists and is exercised by every crash-recovery scenario. The guard outlived its
justification, which is why #179 exists.

What genuinely blocked it is a question #164 never had to answer: **what does
`Suspended` mean while sibling branches are still running?**

## Decision

**Decided by the maintainer, 2026-08-02.**

A branch that suspends does not stop its siblings. They run to completion, and
the instance settles as `Suspended` at the join. Resuming re-enters only the
branch that parked.

This mirrors the failure rule in ADR-0024 decision 6 exactly:

| A branch that… | Siblings | Instance settles as |
| --- | --- | --- |
| fails | run to completion | `Failed`, then compensation |
| suspends | run to completion | `Suspended` |

**One rule for both**, which is the point. A branch cannot abandon its siblings
mid-step — abandoning one would not stop its side effects, it would only stop
FlowDeck recording them — and that argument does not become weaker because the
branch parked rather than broke.

## Consequences

- `Suspended` keeps meaning one thing: *nothing is executing, and something can
  resume it*. It never describes an instance with work still in flight.
- A suspended fork's stored position is the set of active nodes it already
  maintains. The parked branch's cursor names the step that suspended; finished
  branches are absent, exactly as after a crash.
- Resume re-enters only the parked branch, because the resumption logic matches
  stored nodes to sequences by step name (#166). No new mechanism.
- **A suspend inside a fork is not immediate.** The instance stays `Running`
  until the slowest sibling finishes. An operator watching will see it running
  after the step that suspended has returned — the same lag a failing branch
  already has.
- The guide's known-limitations entry for #179 goes.

## Alternatives considered

**Suspend immediately, siblings keep running.** The most responsive, and it makes
`Suspended` mean "partly executing", which every consumer of the status — the
dashboard, the dispatcher's claimable query, `IsTerminal` — would then have to
handle. Rejected: a status that means two things is a status nobody can act on.

**Keep failing, and document it as permanent.** Honest and free. Rejected by the
maintainer: it leaves an author who needs to wait inside a fork restructuring
their workflow around an engine limitation, which is the kind of thing that makes
the branching feature feel provisional.

**Cancel the siblings when one suspends.** Superficially tidy. Rejected for the
reason abandoning a branch is always rejected here: the engine cannot stop a step
mid-execution, so the side effects happen anyway and only the record of them is
lost.
