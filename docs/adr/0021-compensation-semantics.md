# ADR-0021: Compensation semantics

**Status:** Accepted · **Milestone:** M5 · **Issue:** #38

## Context

A workflow that fails partway has already done things. An order workflow that
charged a card and then failed to reserve stock has taken money for goods it
cannot ship. The engine cannot leave that alone and call it a failure.

Compensation is the reverse pass: for each step that already ran, invoke the
action the author declared to undo it.

Scope is FlowDeck's own steps only. Coordinating a distributed transaction
across services FlowDeck does not control is a different problem, split into
#111 and deliberately unscheduled — see
[requirements](../requirements.md#sagas-decided-2026-07-31).

This ADR settles the six questions #38 raised. Four were put to the maintainer
rather than decided here, and are marked below.

## Decisions

### 1. A compensating action is declared beside its step

```csharp
builder
    .AddStep("charge", () => new Charge())
        .WithCompensation(() => new Refund())
    .AddStep("ship", () => new Ship());
```

`WithCompensation` applies to the **most recently declared step**, not to steps
declared after it. This is deliberately unlike `WithRetryPolicy`, which sets a
forward default — a retry policy is a sensible thing to apply broadly, and an
undo action is specific to the one thing it undoes. A compensation "default"
would be a category error.

A compensating action is an `IStep`. It gets the same context, the same data,
and the same trust boundary as a forward step: it is author code the engine
invokes, and if it throws, the engine catches it (ADR-0003).

### 2. Compensation is automatic — declaring the action is the opt-in

**Decided by the maintainer, 2026-07-31.**

If a step has a compensating action and the workflow fails, the action runs.
There is no second switch to enable.

The alternative — requiring `CompensateOnFailure()` as well — creates a trap: a
workflow carrying declared-but-disabled undo actions *looks* protected and is
not. Nobody reads a rollback action and expects it to be inert.

No existing workflow changes behaviour, because none declare actions.

### 3. Rollback runs in reverse execution order

Undo the most recent first. Later steps may depend on what earlier steps did, so
releasing stock before refunding the charge that paid for it inverts a
dependency the forward pass established.

Reverse order is what WorkflowCore and Elsa both do, and what a stack of undo
operations means everywhere else. Deviating would surprise for no gain.

Steps with no compensating action are skipped, not treated as failures.

### 4. A failing compensating action does not stop the rollback

**Decided by the maintainer, 2026-07-31.**

The engine continues to the next action and records every failure.

The reasoning is that stopping leaves *more* un-undone work than continuing. If
`release-stock` fails, refusing to then refund the card does not make the
situation safer — it adds a second unresolved side effect to the first. And an
operator opening the instance gets one complete picture of what is and is not
rolled back, rather than a picture that stops at the first problem.

The cost is real and worth naming: the engine keeps acting after a signal that
something is wrong. If `release-stock` failed because the inventory service is
down, the refund may fail for the same reason. Continuing is the better default,
not a safe one.

Every compensating action's outcome is recorded in history like any other step
execution, so "one of two undone" is a fact rather than an inference.

### 5. A step that exhausted its retries is compensated exactly once

**Decided by the maintainer, 2026-07-31.**

Three failed attempts produce one compensating action, not three and not zero.

*Not zero*, because a step that never reported success may still have had an
effect. This is precisely the charge-then-timeout case
[the guide warns about](../guides/defining-a-workflow.md#your-step-must-be-idempotent):
the money moved and the response was lost. Skipping compensation for failed
steps would be wrong exactly where it matters most.

*Not three*, because [#108](../guides/defining-a-workflow.md#your-step-must-be-idempotent)
already requires a retried step to be idempotent, which in practice means the
attempts shared one idempotency key and therefore one side effect. One undo
covers it. Three refunds would be two too many.

This makes idempotency load-bearing in a second place: the same requirement that
makes retry safe is what makes single compensation correct.

### 6. Compensation produces its own terminal statuses

**Decided by the maintainer, 2026-07-31.**

| Status | Meaning |
| --- | --- |
| `Failed` | A step failed and nothing was rolled back |
| `Compensated` | A step failed and every compensating action succeeded |
| `CompensationFailed` | A step failed and at least one action also failed |

`Failed` and `Compensated` are different facts. An operator triaging a list
needs to tell "broke, needs a human" from "broke, cleaned itself up" — folding
them together makes the list less useful exactly when it is being scanned under
pressure.

All three are terminal, so ADR-0008 still holds: compensation runs *before* the
instance reaches a terminal state, never after. The instance is `Running` while
rolling back.

The cost is that every consumer switching on status gains cases: the API, the
dashboard filters, the status colours. C# makes the backend ones compile errors;
the dashboard needs deliberate work, and that is a story rather than an
afterthought.

## Consequences

- A workflow author can undo work without writing an outer try/catch around
  the engine.
- Three new terminal statuses reach the API contract and the dashboard. This is
  a breaking change for any consumer that exhaustively matches `InstanceStatus`.
- Compensation is best-effort by design. The engine tries everything and reports
  honestly; it does not guarantee the world is back to where it started, and no
  engine can.
- The forward pass and the reverse pass are asymmetric: forward stops at the
  first failure, reverse continues past one. Both are deliberate and both are
  documented, but it is a genuine inconsistency to hold in your head.

## Deliberately not decided

**Does cancelling an instance compensate it?** Raised as its own issue rather
than folded in here. An operator cancelling a workflow may be stopping it to fix
forward, in which case an automatic rollback would destroy work they intended to
keep. It could equally be the thing they wanted. The question belongs with the
management actions in #66, where "cancel" and "cancel and roll back" can be
distinct operator choices, rather than being settled by the engine's default.

Until then, **cancellation does not compensate.**

## Alternatives considered

**A separate compensation handler per workflow**, rather than per step. One
method receiving the failure and deciding what to undo. More flexible, and it
puts the undo logic far from the thing it undoes — the two drift, and the drift
is silent.

**Compensation as a first-class saga coordinator** with participant
registration, an outbox and idempotency keys at the engine level. That is #111.
Building it now would produce an abstraction fitted to an imagined case.

**Reusing `Failed` with a flag.** `CompensationOutcome` beside the status,
keeping status meaning "did it work". Expresses partial rollback cleanly and
costs no new enum cases. Rejected because it makes the common operator question
— "what still needs me?" — a two-field query.
