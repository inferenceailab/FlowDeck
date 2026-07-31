# ADR-0005: Workflow data reads are checked, not cast

**Status:** Accepted · **Milestone:** M1 · **Issue:** #5

## Context

Steps share state through `IWorkflowData`. The shape of that state is defined
by the workflow author and only known at runtime, so values are stored as
`object`.

A step reading `Get<string>("orderId")` when the key holds an `int` is an
authoring bug. The question is how it surfaces.

## Decision

Reads are checked. `Get<T>` raises `WorkflowDataTypeMismatchException` naming
the key, the requested type and the actual type. A missing key raises
`WorkflowDataKeyNotFoundException` naming the key. `TryGet<T>` reports absence
without throwing.

A stored `null` is distinct from an absent key: `Contains` is true and `TryGet`
succeeds for a value explicitly set to null.

Keys compare ordinally, consistent with [ADR-0001](0001-definition-identity-includes-version.md).

## Consequences

- A wrong-type read produces a message an author can act on, instead of an
  `InvalidCastException` with no indication of which key was involved.
- Clearing a value is distinguishable from never writing it, so a step can tell
  "explicitly none" from "not yet computed".
- Every read costs a type check. Irrelevant next to the work a step does.
- Typed accessors do not prevent two steps disagreeing about a key's type; they
  only make the disagreement legible. A typed data contract per workflow would
  fix that properly and is not yet warranted.

## Alternatives considered

**Return `default` on a missing key.** Silently turns a typo into a zero or
null, which then flows into business logic. The failure surfaces far from its
cause.

**Expose `IDictionary<string, object?>` directly.** No guard rails, and the
live dictionary escapes, which would break persistence (#15).

**Generic per-workflow data class.** Compile-time safety, but forces every
workflow to declare a data type up front and complicates persistence. Worth
revisiting if wrong-type reads become common.
