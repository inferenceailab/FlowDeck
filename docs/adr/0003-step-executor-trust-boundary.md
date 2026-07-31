# ADR-0003: StepExecutor is the trust boundary

**Status:** Accepted · **Milestone:** M1 · **Issue:** #2

## Context

Step bodies are business code written by workflow authors. They can throw
anything, at any time. The engine's execution loop drives instances forward and
must not be taken down by one misbehaving step.

## Decision

`StepExecutor` is the single place in the engine that catches exceptions. It
invokes a step body and converts everything — success, suspension, or an
exception — into a `StepExecutionResult`.

The execution loop above it never uses `try`/`catch`.

The step's exception is stored **unwrapped** on the result and on the instance.

## Consequences

- The execution loop reads as straight-line logic.
- Failure handling lives in one place, so retry (#37) and compensation (#38)
  have one obvious insertion point.
- Callers matching on a specific exception type do not have to unwrap, and the
  original stack trace survives for the operator.
- Everything that can go wrong is data, which makes it testable without
  provoking real faults.
- A step that hangs is *not* handled by this. Timeouts remain an open problem.

## Alternatives considered

**Catch in the execution loop.** Mixes orchestration with error translation and
means every future change to the loop must preserve the handler.

**Let exceptions propagate to the caller of `StartAsync`.** Makes a workflow
failing — a normal outcome — into an exception the caller must handle, and
leaves the instance in an undefined state.

**Wrap step exceptions in a `StepFailedException`.** Uniform, but forces
unwrapping before matching and buries the stack trace an operator needs.
