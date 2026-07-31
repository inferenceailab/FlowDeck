# ADR-0002: Step bodies come from factories

**Status:** Accepted · **Milestone:** M1 · **Issue:** #3

## Context

A definition declares its steps. The engine needs an `IStepBody` to execute for
each one. The simplest approach is for the definition to hand over a step
instance directly.

Step bodies are author-written classes. Nothing stops an author holding state
in a field — a counter, a cached lookup, a partially built result. If two
instances of the same workflow share one body object, they share that state.

## Decision

`IWorkflowBuilder.AddStep` takes a `Func<IStepBody>` rather than an
`IStepBody`. The engine invokes the factory once per step execution.

## Consequences

- Two concurrent instances of the same workflow cannot share step state,
  structurally rather than by convention.
- Authors who *want* shared state must be explicit about it by closing over a
  shared object, which is visible at the declaration site.
- One allocation per step execution. Negligible against the cost of the work a
  step actually does.
- A definition's `Build` is called per instance start rather than cached, so a
  definition may compose steps from injected dependencies.

## Alternatives considered

**Pass step instances directly.** Simplest, and wrong: a stateful step silently
corrupts concurrent instances. The failure is intermittent and load-dependent —
the worst kind to diagnose.

**Require step bodies to be stateless.** Unenforceable. A rule the compiler
cannot check is a rule that will be broken.

**Resolve steps from a DI container per execution.** Achieves the same
isolation but couples the core engine to a container. Deferred; a factory can
be backed by a container later without changing this interface.
