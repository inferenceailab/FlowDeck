# ADR-0011: API vocabulary borrowed from WorkflowCore

**Status:** Accepted (open question resolved by [ADR-0012](0012-step-vocabulary-rename.md)) · **Milestone:** M1 · **Issues:** #2, #3

## Context

The project brief directs FlowDeck to cherry-pick design patterns from
WorkflowCore, Hangfire and Elsa v3. Patterns were duly borrowed. But the M1 API
went further than patterns: several public names are **verbatim matches** to
WorkflowCore's public API, and one interface has essentially the same shape.

No source code was copied. `FlowDeck.Core` was written against the
Given/When/Then scenarios in this repository's issues, without reading
WorkflowCore's source. The convergence comes from prior familiarity with that
library's vocabulary, not from transcription — which is worth stating plainly,
because the result looks similar either way.

### The specific overlaps

| FlowDeck | WorkflowCore | Overlap |
| --- | --- | --- |
| `IStep` | `IStep` | **Name identical.** Method differs: `ValueTask<Outcome> ExecuteAsync(IStepContext, CancellationToken)` vs `ExecutionResult Run(IStepExecutionContext)`. |
| `Outcome.Next` | `ExecutionResult.Next()` | **Concept and term identical.** Enum value vs factory method. |
| `Outcome.Persist` (now `Suspend`) | `ExecutionResult.Persist(...)` | **Concept and term identical.** |
| `IWorkflowBuilder` | `IWorkflowBuilder<TData>` | **Name identical**, shape differs. |
| `IStepContext` | `IStepExecutionContext` | Near-identical name, different members. |
| `IWorkflowDefinition` | `IWorkflow` | **Same shape**: `string Id`, `int Version`, `void Build(builder)`. Different name. |
| `IWorkflowDefinition<TInput>` | `IWorkflow<TData>` | Same generic pattern. `TInput` is input-only; `TData` is the whole data bag. |

The `IWorkflowDefinition` / `IWorkflow` row is the significant one. A developer
familiar with WorkflowCore would recognise the interface immediately. That is
convergence, not derivation, but the distinction is invisible from outside.

## Decision

Record the overlap explicitly rather than let it pass unremarked, and treat the
naming as an open question rather than a settled one.

The functional patterns — versioned identity, suspend/resume, an executor that
absorbs step exceptions — are **kept**. They are what the brief asked for, they
are sound, and they are common across the field rather than specific to any one
library.

The **verbatim names** are marked for review. They are cheap to change now:
one library, no external consumers, 89 tests.

## Consequences

- Anyone reading the API who knows WorkflowCore sees the resemblance and can now
  find the explanation instead of drawing their own conclusion.
- A future decision to differentiate the vocabulary has a recorded starting
  point.
- If the names stay, that becomes a deliberate choice — familiarity for
  developers moving from WorkflowCore — rather than an accident.
- FlowDeck's differentiation has to come from behaviour, not names. That case is
  made in [prior-art.md](../prior-art.md), which also honestly lists where
  FlowDeck is *not* different.
- The repository is public, so this overlap is visible whether documented or
  not. Documenting it is strictly better.

## Legal position

No code is copied, so WorkflowCore's MIT licence does not attach. API names and
interface shapes are generally not protected in the way implementation is.
There is no licensing defect here.

This ADR exists for **honesty and provenance**, not because a legal problem was
found. If code is ever adapted from WorkflowCore or any other project, that must
be recorded with the licence, the file and the upstream commit.

## Alternatives considered

**Leave it undocumented.** The resemblance would still be there, with no record
of whether it was deliberate. Worse in every scenario, including the one where
nobody ever notices.

**Rename everything immediately.** Renaming to avoid resemblance rather than to
improve clarity is cargo-culted differentiation. Any rename should be justified
on its own terms.

**Claim independent derivation.** It would not be true. The names came from
familiarity with WorkflowCore.

## Open question — resolved

The naming question this ADR left open was decided in
[ADR-0012](0012-step-vocabulary-rename.md): `IStepBody` became `IStep`,
`WorkflowStep` became `StepDeclaration`, and `Outcome.Persist` became
`Outcome.Suspend` — on clarity grounds, not differentiation.

The remaining convergences recorded above are **unchanged and still stand**, in
particular `IWorkflowDefinition` having the same shape as WorkflowCore's
`IWorkflow`. Renaming removed one verbatim collision; it did not make FlowDeck
an independent derivation, and this record exists so nobody later believes it
did.