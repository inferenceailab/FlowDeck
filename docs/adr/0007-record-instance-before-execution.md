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
- The stored object is mutated in place as execution proceeds, so the store
  holds live state rather than a final snapshot. `GetInstance` returns the live
  object deliberately — a copy would let a caller act on stale state while the
  engine advances the real one.
- Once persistence lands (#13), "record before execute" becomes "persist after
  every step", and the invariant carries over: progress is durable before more
  work is attempted.
- Validation must happen before recording, so a rejected start leaves nothing
  behind. Verified: `GetInstances()` is empty after a `DefinitionNotFoundException`.

## Alternatives considered

**Record on completion.** Makes the store an archive of finished work and
invisible for anything in flight — useless for the dashboard M4 exists to build.

**Record on suspension only.** Covers resumable instances but still hides
running and failed ones.

**Return copies from the store.** Protects callers from concurrent mutation,
at the cost of them acting on stale state. Revisit when the store is durable
and reads may cross a process boundary.
