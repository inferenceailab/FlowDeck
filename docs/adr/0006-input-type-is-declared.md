# ADR-0006: Input type is declared, not reflected

**Status:** Accepted · **Milestone:** M1 · **Issue:** #10

## Context

A workflow may require input. The engine must reject a mismatched start, and
the future HTTP layer (#23) must reject a malformed request body *without*
starting an instance.

The engine holds definitions as `IWorkflowDefinition`. Discovering that a
concrete type implements `IWorkflowDefinition<OrderRequest>` requires walking
its interfaces reflectively.

## Decision

`IWorkflowDefinition` exposes `Type? InputType`, defaulting to `null`.
`IWorkflowDefinition<TInput>` overrides it via a **default interface member** to
return `typeof(TInput)`.

Validation rejects both directions: a typed definition started without input,
and an untyped definition given input.

## Consequences

- The input type is available without reflection and without an instance.
- Existing definitions compile unchanged — the member is defaulted. Contrast
  with #3, where adding `Build` forced updates to #1's test doubles.
- `InputType` is reachable only through the interface, not the concrete type.
  That is how default interface members work, and it usefully stops
  implementers shadowing it with an unrelated property.
- Supplying input to a workflow that declares none is an error rather than
  ignored, so an author cannot believe input was delivered when nothing reads it.
- Input is not persisted yet, so a resumed instance after restart will lose it.
  Folded into #15.

## Alternatives considered

**Reflect over implemented interfaces.** Works, but costs a reflection walk per
start and puts the contract somewhere the compiler cannot see it.

**Untyped `object` input with no validation.** Defers every failure into step
code, where the cause is furthest from the symptom.

**Store input in `IWorkflowData` under a reserved key.** Reuses existing
machinery, but collides with author-chosen keys and makes the input contract
invisible on the definition.
