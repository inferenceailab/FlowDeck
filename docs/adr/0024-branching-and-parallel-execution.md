# ADR-0024: Branching and parallel execution

**Status:** Accepted · **Milestone:** M7 · **Issues:** #40, #161

## Context

Until now a workflow has been a **straight line**. `AddStep` is the only
structural operation the builder offers, and an instance's position is a single
`int`. Everything downstream assumes it: `CurrentStepIndex`, `StepAttempts`,
compensation walking backwards from one index, recovery resuming at one place.

That was enough through M6, and it is not what a workflow engine is expected to
be. Real processes fork, converge, and take different paths on a condition.

This ADR settles the shape of that change. Three decisions were the maintainer's
and are marked.

## Decisions

### 1. Both a step-decided branch and a data predicate

**Decided by the maintainer, 2026-08-01.**

```csharp
builder
    .AddStep("check-stock", () => new CheckStock())
        .Branch("in-stock",  b => b.AddStep("charge", () => new Charge()))
        .Branch("backorder", b => b.AddStep("notify", () => new Notify()))

    // and, where the decision is a plain data check:
    .BranchWhen(data => data.Get<int>("total") > 1000,
        b => b.AddStep("manual-approval", () => new Approve()));
```

A step-decided branch keeps the decision beside the logic that made it and needs
nothing serialised. A predicate is declarative, so the visual view can *show*
why an edge was taken rather than drawing an unexplained fork.

The cost of having both is real and worth stating: two mechanisms doing one job,
two things to render, and two answers needed for "what happens when no branch
matches". That last question is settled once, in decision 6.

### 2. Branches execute genuinely concurrently

**Decided by the maintainer, 2026-08-01.**

Parallel branches run on separate tasks at the same time, so slow I/O overlaps
and a fan-out of three HTTP calls takes as long as the slowest rather than their
sum.

This **breaks the invariant everything since M2 has rested on.**
`WorkflowInstance` says, in a comment written for #39:

> Not thread-safe. One instance is executed by one worker at a time — the
> invariant #39 must preserve.

That becomes false. Rather than leave the comment lying, the machinery is made
honest — see decisions 3 and 4. The alternative considered was interleaving
branches on one worker, which preserves the invariant and delivers independence
without simultaneity; it was rejected because overlapping I/O is the point.

### 3. One writer: branches execute concurrently, checkpoints are serialised

Concurrency stops at the store. Branch tasks run in parallel, but every
checkpoint goes through a single per-instance writer that applies them in turn.

Without this, concurrent branches would each hold a stale `Revision` and every
save but one would be rejected — the optimistic-concurrency mechanism from #19
turned into a livelock by design rather than a race. Serialising the writes
keeps `Revision` meaning what it has always meant, keeps ADR-0013's "state and
history are written atomically" true, and still lets the slow part — the step
bodies — overlap.

So: **parallel where the time goes, sequential where the truth lives.**

### 4. Position becomes a set, not an index

`CurrentStepIndex` cannot describe an instance that is at three places at once.
The durable position becomes the set of active nodes, and `CurrentStepIndex` is
retained only as a projection for a linear workflow so existing consumers and
the dashboard keep working.

This is an ADR-0013 change: the instance record grows a structural field, so
every provider round-trips it and the conformance suite gains a case. That is
now the fourth field to reach that suite, and the reason is the same each time —
a field a provider silently drops is invisible until it matters.

`StepAttempts` moves with it: attempts are per active node, not per instance.

### 5. Workflow data becomes thread-safe

`WorkflowData` is a plain `Dictionary`. Two branches writing at once would
corrupt it, and the failure would look like anything except what it was.

It gains a lock. **Coarse rather than clever**: a workflow data bag is small and
written a handful of times per step, so contention is not the concern —
correctness is.

What a lock does **not** give the author is atomicity across two operations.
`Get` then `Set` from two branches is still a race the author has to think
about, exactly as it would be in any shared state. The guide says so rather than
implying the engine has solved it.

### 6. A join waits for every branch, and any failure fails the instance

**Decided by the maintainer, 2026-08-01.**

Every branch runs to completion. If one fails, the instance fails and
compensation unwinds what completed — including work done on sibling branches
that succeeded.

Consistent with how a failing step behaves today, and simplest to reason about.
The cost is that there is no way to express best-effort work that may fail
without stopping the workflow.

**When no branch matches**, the workflow takes no branch and continues past the
fork. Not an error: a conditional with no matching case is an ordinary shape,
and failing would make every branch set implicitly require a catch-all.

### 7. Compensation unwinds in reverse topological order

Reverse execution order is no longer well defined when two branches ran at once.
Compensation walks the graph backwards from the failure, and where branches are
concurrent their compensating actions are ordered by when each step **completed**
— most recent first, which is what "undo the most recent" means when there is no
single sequence.

Sibling branches' actions are still independent, so they may compensate in
either relative order. ADR-0021's rule that a failing compensating action does
not stop the rollback continues to apply.

## Consequences

- The engine gains real concurrency, and with it a class of bug it has never had.
  Every decision above is aimed at keeping that concurrency confined to step
  execution.
- An instance is no longer executed by one worker, so the M6 lease still
  protects the *instance* but no longer implies a single thread inside it.
- The instance record grows a structural field; hosts need a migration
  (ADR-0015 — FlowDeck ships none).
- Retry is per node and unchanged. Compensation ordering changes only where
  branches were concurrent.
- The visual view has a real graph to draw, which is what #40 was waiting for.
- A workflow author now has to think about shared data across branches. That is
  a genuine new burden and is documented, not glossed.

## Alternatives considered

**Interleaved execution on one worker.** Branches advance a step at a time in a
defined order. Preserves the single-worker invariant, deterministic to test, and
delivers independence without simultaneity. Rejected by the maintainer: it gives
no wall-clock benefit, and overlapping slow I/O is the reason to want parallel
steps at all.

**Predicates only, no step-decided branch.** One mechanism, fully declarative,
renderable and eventually editable. Rejected because a condition that needs C#
would have to be smuggled into workflow data by a preceding step, which is the
step-decided branch with extra steps.

**First-branch-wins joins.** Useful for redundant calls. Rejected because the
engine cannot cancel a step mid-execution, so an "abandoned" branch's side
effects still happen — the semantics would be a lie.
