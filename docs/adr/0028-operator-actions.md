# ADR-0028: Operator actions

**Status:** Accepted · **Milestone:** M10 · **Issues:** #66, #68, #124

## Context

FlowDeck has **one** operator action: cancel. For an engine whose dashboard is
modelled on Octopus Deploy and Hangfire — both of which offer rich operator
control — that is a gap rather than a scoping decision.

`ResumeAsync` is the sharpest symptom. It exists because #12 needed it to prove
"no further steps execute", and it has no story, no HTTP endpoint and no
dashboard exposure. A suspended workflow is therefore only completable from
inside the process that started it, by code holding the engine instance. That is
not a feature; it is an accident with a public signature.

This ADR settles what an operator can do when a workflow misbehaves at 2am, and
the guardrails that stop those actions making things worse. Four decisions were
the maintainer's.

## Decisions

### 1. Four actions ship: resume, retry, suspend, and bulk

**Decided by the maintainer, 2026-08-02.**

| Action | What it does |
| --- | --- |
| **Resume** | Continues a suspended instance. Closes #68 |
| **Retry** | Starts a new instance from a failed one, from the start or from the failing step |
| **Suspend** | Parks a running instance without cancelling it |
| **Bulk** | Applies cancel or retry across a filtered set |

**Not shipping: editing workflow data on a suspended instance.** #66 lists it as
the most useful and most dangerous action on its list, and that is the right
reading. It is arbitrary mutation of state a definition's steps are written
against, with no schema, no validation and no way for an author to defend
themselves. It stays unbuilt until somebody can say what a *safe* edit is.

### 2. Retry creates a new linked instance. Terminal states stay final

**Decided by the maintainer, 2026-08-02.**

Both retry modes create a **new** instance and leave the original exactly as it
was:

- **From the start** — a fresh run with the original's input.
- **From the failing step** — a run that begins at the step that failed, seeded
  with the workflow data the original had reached.

Both record what they were retried from, so an operator can follow the chain.

The alternative — reopening the failed instance in place — was considered and
rejected. It contradicts [ADR-0008](0008-terminal-states-are-final.md), which is
not a rule to bend for convenience: "this instance failed" is a fact, and an
action that makes it retroactively untrue rewrites the record an operator is
using to decide what to do.

The cost is real and worth naming: **the instance id changes.** An operator
following a link, an alert, or a support ticket gets a different id back. That is
why both retries return the new id and why the new instance carries
`RetriedFromInstanceId` — the chain has to be walkable in both directions or the
id change is just lost context.

### 3. Cancel and cancel-with-rollback are two actions, not a flag

**Decided by the maintainer, 2026-08-02.**

`CancelAsync` continues to stop an instance without rolling it back.
`CancelAndCompensateAsync` stops it and unwinds what completed. Two methods, two
endpoints, two buttons.

An operator cancelling to fix forward would be destroyed by an automatic
rollback; one abandoning work wants exactly that. [ADR-0021](0021-compensation-semantics.md)
declined to settle it by default and #124 has carried the question since.

A boolean flag was rejected. An irreversible, destructive option should be chosen
by picking the thing you want, not by remembering to set a parameter — and the
dashboard can then word each consequence plainly rather than explaining a
checkbox.

### 4. Suspend takes effect at the next step boundary

**Decided by the maintainer, 2026-08-02.**

`SuspendAsync` on a running instance parks it. It does **not** interrupt the step
that is executing.

The engine cannot cancel a step mid-execution — step bodies are author code
across a trust boundary ([ADR-0003](0003-step-executor-trust-boundary.md)) — so
"suspend now" would either be a lie or would abandon a step whose side effects
still happen. The step in flight finishes, and the instance suspends before the
next one starts.

This is the same honesty as [ADR-0024](0024-branching-and-parallel-execution.md)
decision 6: the engine does not claim to stop work it cannot stop.

### 5. Bulk actions are best-effort with a per-item report

Taken without asking. A bulk cancel across fifty instances is fifty independent
operations against fifty independent concurrency tokens; making them atomic would
mean holding a transaction across all of them, which no provider contract here
promises and which would serialise the whole engine behind one operator click.

So: each item is attempted, each result is reported, and the response says which
succeeded and which did not and why. A bulk action that half-worked and said
nothing is worse than no bulk action at all — the operator would have to
re-derive the state by hand.

**Bounded**, and the bound is the API's existing page cap. An unbounded bulk
action is a denial-of-service vector behind a button.

### 6. Every action is refused on a terminal instance, in one place

Taken without asking. Resume, retry-from-failing-step, suspend and both cancels
all need the instance to be in a state the action means something for, and
[ADR-0008](0008-terminal-states-are-final.md) already says what terminal means.

The check is one guard rather than four, so a status added later cannot be
accidentally permitted by three of them. Retry-from-start is the exception and is
deliberately *only* available on a terminal instance — retrying something still
running would start a duplicate.

### 7. There is no audit trail, and that is #42's fault

Taken without asking, and recorded because its absence is conspicuous.

#66 asks whether there is an audit of who did what. There cannot be: the API has
no authentication (#42, deferred to M11), so "operator cancelled this" has no
subject to name. The actions here record **what** happened in execution history;
**who** waits for identity to exist.

## Consequences

- A suspended workflow becomes completable from outside the process that started
  it, which it has never been.
- The failed-instance graveyard becomes actionable without an operator writing
  code against the engine.
- Instance records grow a `RetriedFromInstanceId`. That is an ADR-0013 change, so
  every provider round-trips it and the conformance suite gains a case — the
  sixth field to reach it.
- The API surface roughly doubles in operator actions, and each is a POST that
  mutates state without authentication. That is not new, and it is more reason
  #42 matters.
- Bulk actions can partially succeed. Every consumer has to read the per-item
  report rather than the status code.

## Alternatives considered

**Reopening a failed instance in place.** Keeps the id an operator is already
looking at. Rejected: it makes a terminal status retroactively untrue and every
consumer of history has to tolerate that.

**A `compensate` flag on cancel.** One endpoint, one parameter, smallest surface.
Rejected by the maintainer — a destructive option behind a flag is one a tired
operator sets wrongly.

**Atomic bulk actions.** A single transaction over the selected set. Rejected:
no store contract here promises cross-instance transactions, and it would
serialise the engine behind one click.

**Editing workflow data.** The most requested action of this kind in comparable
tools. Rejected for now — arbitrary mutation of state that a definition's steps
are written against, with nothing able to validate it.
