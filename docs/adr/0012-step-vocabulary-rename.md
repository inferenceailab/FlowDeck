# ADR-0012: Rename the step vocabulary for clarity

**Status:** Accepted · **Milestone:** M1 · **Supersedes the open question in**
[ADR-0011](0011-api-vocabulary-borrowed-from-workflowcore.md)

## Context

[ADR-0011](0011-api-vocabulary-borrowed-from-workflowcore.md) recorded that
several M1 public names were verbatim matches to WorkflowCore's API, and left
the question of renaming deliberately open.

Two separate arguments were available:

1. **Differentiation** — rename so FlowDeck does not look derivative.
2. **Clarity** — rename because the names are inaccurate on their own terms.

Renaming purely for (1) is cargo-culted differentiation: it changes appearance
without improving anything. The decision therefore rested on whether (2) held
independently.

It did, most sharply for `Outcome.Persist`: the value was named after
persistence that **the engine does not perform** — there is no persistence layer
until M2 — while the state it actually produces is `InstanceStatus.Suspended`.
A reader had to know that `Persist` means "suspend" and that no persisting
happens. That is a defect regardless of where the name came from.

## Decision

| Before | After | Justification |
| --- | --- | --- |
| `Outcome.Persist` | `Outcome.Suspend` | Names the effect (`InstanceStatus.Suspended`) rather than an unimplemented implementation detail |
| `IStepBody` | `IStep` | "Body" is only meaningful relative to a step *declaration*; the type is the step |
| `WorkflowStep` (record) | `StepDeclaration` | Frees `IStep`, and describes what it is — the declaration, not the work |
| `WorkflowStep.BodyFactory` | `StepDeclaration.Factory` | Follows from the above |

`Outcome.Next` was **kept**. It is accurate and the alternative (`Continue`)
was no better.

`IStepContext` was **kept**. It is close to WorkflowCore's
`IStepExecutionContext` but is the plainly correct name for what it is.

## Consequences

- `Outcome` now reads consistently against `InstanceStatus`: `Suspend` produces
  `Suspended`. The previous mismatch is gone.
- The verbatim `IStepBody` collision with WorkflowCore is removed as a
  side effect. That was **not** the justification, and the remaining
  convergences documented in ADR-0011 — particularly `IWorkflowDefinition`
  having the same shape as WorkflowCore's `IWorkflow` — are unchanged and
  remain recorded.
- No external consumers existed, so no deprecation path was needed. Doing this
  after M3 publishes an HTTP API would have been considerably more expensive.
- The usage guide's samples are compiled tests, so the rename could not silently
  leave the documentation stale — the build would have failed.
- `Outcome.Suspend` may read oddly once persistence exists in M2, since
  suspending will then imply persisting. That is acceptable: the enum names the
  *instruction to the engine*, not the mechanism.

## Alternatives considered

**Keep the names for familiarity.** A developer moving from WorkflowCore would
find `IStepBody` and `Persist` recognisable. Rejected: `Persist` is misleading
to everyone else, and optimising an API for migrants from one specific library
is a narrow objective.

**Rename everything WorkflowCore-adjacent, including `IWorkflowBuilder` and
`IStepContext`.** Rejected: those names are correct. Changing them would be
differentiation for its own sake, which ADR-0011 explicitly argued against.

**Defer until M3.** Rejected: cost only rises once an HTTP API and generated
clients depend on the vocabulary.
