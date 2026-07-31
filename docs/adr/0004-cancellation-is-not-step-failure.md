# ADR-0004: Cancellation is not step failure

**Status:** Accepted · **Milestone:** M1 · **Issue:** #2

## Context

[ADR-0003](0003-step-executor-trust-boundary.md) makes `StepExecutor` convert
every exception into a failed result. `OperationCanceledException` is an
exception. Applied uniformly, a cancelled step becomes a failed step.

Cancellation happens on every graceful shutdown — every deployment, every
restart. Treating it as failure would mark healthy suspended instances as
`Failed` each time the engine stops.

## Decision

`OperationCanceledException` is rethrown rather than recorded as failure, when
the executor's own `CancellationToken` is the one that was signalled.

The condition matters: a step that throws `OperationCanceledException` for its
own reasons, with no cancellation requested, is a genuine failure and is
recorded as one.

## Consequences

- A deployment does not corrupt the status of in-flight instances.
- `Failed` retains its meaning: something went wrong with the work.
- Cancellation propagates to the caller of `StartAsync`/`ResumeAsync`, which
  must expect it during shutdown.
- An instance interrupted by cancellation is left in whatever state it had
  reached. Making that survive a restart is #14's problem.

## Alternatives considered

**Treat cancellation as failure.** Simple and uniform, and produces a fleet of
falsely-failed instances on every restart.

**Introduce an `InstanceStatus.Interrupted`.** More precise, but no story
requires distinguishing it yet, and a status nothing acts on is noise.

**Catch `OperationCanceledException` unconditionally.** Would hide a genuine
bug in a step that throws it spuriously.
