# ADR-0007: Instances are recorded before execution

**Status:** Accepted · **Milestone:** M1 · **Issue:** #11

## Context

`WorkflowEngine.StartAsync` creates an instance, runs it to a stopping point,
and returns it. Once an instance store exists, the engine must decide when to
record the instance in it.

Recording on completion is tempting: the record is then final and never needs
updating.

## Decision

The instance is added to the store **before** the first step executes.

## Consequences

- An instance that suspends or fails partway is queryable. Those are precisely
  the instances an operator goes looking for.
- A long-running instance is visible while it runs, not only afterwards.
- **Superseded in part by #13.** While the store was in-memory, `GetInstance`
  returned the live object deliberately, so a caller could not act on stale
  state. Once the store became the source of truth that inverted: a query must
  read the store rather than hand back an in-process object, or an engine in
  another process would answer differently from this one.
  `GetInstanceAsync` now returns a projection of persisted state, and the
  returned instance carries no live `Exception` — only `ErrorType` and
  `ErrorMessage` survive persistence.
- "Record before execute" became "persist after every step" in #13, and the
  invariant carried over: progress is durable before more work is attempted.
- Validation must happen before recording, so a rejected start leaves nothing
  behind. Verified: the store receives no create after a
  `DefinitionNotFoundException` or an `InvalidInputTypeException`.

## Alternatives considered

**Record on completion.** Makes the store an archive of finished work and
invisible for anything in flight — useless for the dashboard M4 exists to build.

**Record on suspension only.** Covers resumable instances but still hides
running and failed ones.

**Return copies from the store.** Protects callers from concurrent mutation,
at the cost of them acting on stale state. Revisit when the store is durable
and reads may cross a process boundary.
