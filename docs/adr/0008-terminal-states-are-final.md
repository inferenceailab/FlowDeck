# ADR-0008: Terminal states are final

**Status:** Accepted · **Milestone:** M1 · **Issue:** #12

## Context

An instance reaches `Completed`, `Failed` or `Cancelled` and stops. An operator
may then issue a cancel — because they were looking at a stale dashboard, or
clicked twice, or scripted it.

## Decision

No operation moves an instance out of a terminal state.
`Cancel` on a terminal instance raises `InvalidStateTransitionException`
carrying `From` and `To`. Cancelling an already-cancelled instance is refused
for the same reason.

Cancellation also **drops the instance's runtime state**, which is what makes it
binding rather than advisory: there is nothing left to resume from.
`CurrentStepName` is deliberately preserved so an operator can still see where
the instance stopped.

## Consequences

- History cannot be rewritten. A failed instance keeps its recorded cause.
- A duplicate cancel does not overwrite the first cancellation timestamp, so
  the audit trail does not lie about when work stopped.
- Callers must handle a refused transition. The exception names both states, so
  the HTTP layer can map it to `409 Conflict` (#26) without inspecting messages.
- Retry (#37) cannot simply un-fail an instance; it must act before the instance
  becomes terminal, or model a new attempt explicitly. That constraint is
  intended.

## Alternatives considered

**Make cancel idempotent.** Friendlier to scripts, but the second call would
either overwrite the first timestamp — corrupting the audit trail — or silently
do nothing, which is indistinguishable from success to the caller.

**Allow cancelling a failed instance.** No use case, and it destroys the
recorded failure cause.

**Return a status rather than throwing.** Callers ignore return values;
exceptions for genuinely exceptional transitions are harder to miss.
