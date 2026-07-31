# ADR-0020: Retry semantics

**Status:** Accepted · **Milestone:** M5 · **Issue:** #37

## Context

#37 lists five questions that had to be answered before retry could be
decomposed into stories. They interlock — the answer to "what is retried"
decides what an attempt counter counts, which decides what has to be persisted.

Answering them separately would have produced a design that only appears
coherent.

## Decisions

### 1. Policy is declared per step, with a workflow-level default

A payment call and a local computation do not want the same policy. Per-step is
therefore necessary. A workflow-level default exists so the common case is
declared once rather than repeated on every step.

```csharp
builder.WithRetryPolicy(RetryPolicy.ExponentialBackoff(maxAttempts: 3));
builder.AddStep("charge", () => new ChargeCard(), RetryPolicy.None);
```

**No retry unless declared.** Silently retrying a step an author believed ran
once is worse than not retrying: it converts a visible failure into duplicated
side effects.

### 2. Exponential backoff with jitter, behind a strategy the caller supplies

Fixed delay retries a downstream outage in lockstep. Exponential without jitter
synchronises every instance that failed at the same moment, so they all retry
together and hit the recovering service simultaneously.

`RetryPolicy` therefore computes a delay from the attempt number, and the
built-in strategies are fixed, exponential, and exponential-with-jitter. Jitter
is the default anyone should use.

### 3. Attempts are counted per step, and persisted

The counter belongs to the instance's position, not to the instance as a whole:
"this instance has failed 5 times" is not actionable, "step `charge` has failed
3 times" is.

It is **persisted with the checkpoint** (ADR-0013). An in-memory counter would
reset on restart, so a host recycling during an outage would retry forever.

The counter resets when execution advances past the step. A step re-entered
after a resume starts fresh — that is a new arrival at the step, not a
continuation of an earlier failure.

### 4. On exhaustion the instance fails

No dead-letter state. `Failed` already means "stopped and needs a human", and a
second terminal-ish state would need its own transitions, its own dashboard
treatment and its own semantics in every query — for no behaviour that `Failed`
plus the attempt count in history does not already express.

Compensation (#38) hooks in **before** the instance becomes terminal, because
ADR-0008 makes terminal states final and nothing may run afterwards.

### 5. A retry re-runs the whole step

Resuming inside a step would require step-internal checkpoints, which do not
exist and would mean a step could no longer be a plain method.

**This makes idempotency the author's responsibility, and it is the most
consequential thing in this ADR.** A step that charges a card and is retried
charges twice. The engine cannot detect that; only the author can prevent it,
with an idempotency key or a check-before-act.

This must be stated wherever retry is documented, not buried here.

## Consequences

- Retry is opt-in, so existing workflows behave exactly as before.
- Attempt state is durable, so retries survive a restart and cannot loop
  forever because a host recycled.
- **Every retried step must be idempotent.** The engine offers no protection.
  The usage guide has to say so loudly.
- History records each attempt as its own entry (#18 already appends per
  execution), so "failed 3 times, 2s apart" is visible rather than inferred.
- A delay between attempts means an instance is *waiting*, which the current
  engine has no way to express — it runs to completion, suspension or failure
  on the calling thread. This is the part of retry that is genuinely new
  machinery, not just policy.
- No dead-letter queue, no manual "retry this instance" — the latter is #66's
  operator actions, and depends on this.

## Alternatives considered

**Retry on by default with a sensible policy.** What Hangfire does, and
appropriate there because a job is usually idempotent by construction. A
workflow step is arbitrary author code; defaulting to retry would duplicate side
effects in workflows nobody reviewed for it.

**Policy per workflow only.** Simpler, and wrong: the steps within a workflow
have genuinely different failure characteristics.

**Polly for the policy engine.** Mature and well designed. Rejected under
[ADR-0010](0010-minimise-third-party-dependencies.md): FlowDeck needs a delay
computed from an attempt number, which is a small amount of arithmetic, and
Polly's model assumes retries inside a process rather than across a durable
checkpoint.

**A dead-letter state.** Revisit if operators need to distinguish "failed once"
from "failed and exhausted retries" in a way the attempt count cannot express.

## Open, deliberately

Whether a delayed retry should occupy a worker or release it is left to the
stories. The current engine is synchronous, so the first implementation will
block; making it release is a scheduling question that overlaps #39.
