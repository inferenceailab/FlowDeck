# ADR-0013: Persistence model — checkpoint state plus append-only history

**Status:** Accepted · **Milestone:** M2 · **Issues:** #60, blocks #13

## Context

M2 makes instance state durable. Before writing #13, the shape of that
durability has to be decided, because #19 (concurrency detection), #20 (purge),
#25 (paged, filtered listing) and #39 (distributed execution) all depend on the
answer, and reversing it later is expensive.

Three models were considered.

**Checkpoint.** After each step, write the instance's current state: status,
step position, workflow data. Recovery loads the latest state and continues.

**Event log.** Append immutable events — `InstanceStarted`, `StepCompleted`,
`StepFailed`, `InstanceSuspended`. Current state is a fold over the events.
Recovery replays them. This is Temporal's model.

**Hybrid.** Checkpoint state as the authoritative record, with a separate
append-only history table written alongside it.

## Decision

**Hybrid: checkpoint state is authoritative; an append-only history table
records what happened.**

- The instance state row holds status, definition id and version, step position,
  serialised workflow data, timestamps, failure information, and a concurrency
  token.
- The history table holds one immutable row per step execution: step name,
  start, end, outcome, error.
- Recovery reads the state row. It never replays history.
- History is written in the same transaction as the state update, so the two
  cannot disagree.

## Rationale

The decision follows from the stories as written, not from taste.

| Requirement | Checkpoint | Event log | Hybrid |
| --- | --- | --- | --- |
| #14 resume after restart | direct | replay | direct |
| #18 append-only history | needs a second table | inherent | second table (explicit) |
| #19 concurrency detection | version column | expected sequence number | version column |
| #20 purge after retention | simple delete | awkward — deleting events rewrites truth | simple delete |
| #25 list with paging and status filter | direct query | needs a projection | direct query |
| #22 crash mid-step | last checkpoint stands | last event stands | last checkpoint stands |

Two points decided it.

**Event sourcing imposes determinism on step authors.** Replay-based recovery
requires step code to avoid clocks, randomness and uncontrolled I/O, or to route
them through the engine. The brief specifies steps implemented directly in C#
with no such constraint. Adopting replay would mean imposing a rule nobody asked
for on every workflow author, and one the compiler cannot enforce.

**#18 asks for history as a first-class requirement anyway.** With an event log,
history is a by-product and the audit table comes free — but so does the
obligation to build a projection for #25 and to solve retention (#20) without
falsifying the log. With a checkpoint, history is an explicit table that costs
one extra write and answers #18, #25 and #20 directly.

A pure checkpoint model was rejected only because #18 requires history
regardless; the hybrid is a checkpoint model with that requirement named
honestly rather than bolted on later.

## Consequences

- **`IInstanceStore` becomes `IWorkflowStore` with async signatures and a
  concurrency token.** ADR-0009 predicted this change; #16 makes it.
- **A conformance suite defines the contract** (#16), so #17's EF Core provider
  is verified against the same tests as the in-memory one rather than trusted.
- **Workflow data must be serialisable.** Today `IWorkflowData` holds arbitrary
  `object`. #15 has to decide the serialisation format and what happens to a
  value that cannot be serialised — that is now a known problem rather than a
  discovery mid-implementation.
- **No time-travel debugging.** Intermediate states cannot be reconstructed
  beyond what history records. Accepted; no story asks for it.
- **The concurrency token is the foundation for #39.** Multi-node claiming will
  build on optimistic concurrency over the state row rather than on leases over
  an event stream. That constrains #39's design, deliberately and in writing.
- **History and state must be written atomically.** A provider that cannot offer
  a transaction across both is not conformant. The conformance suite must test
  this, or a crash between the two writes will produce state that disagrees with
  its own history.

## Alternatives considered

**Pure event log (Temporal model).** Strongest audit story and the most elegant
concurrency model. Rejected primarily for the determinism constraint on step
authors, which contradicts the brief, and secondarily because #20's retention
requirement fits badly with an append-only log that is supposed to be the truth.

**Pure checkpoint, no history.** Simplest and cheapest. Rejected because #18
requires an append-only execution history explicitly, and retrofitting it is
harder than including it now.

**Checkpoint with history derived from status changes.** Avoids the second
write by inferring history from state transitions. Rejected: a step that runs
and advances within one checkpoint interval leaves no trace, so history would
be lossy in exactly the common case.

## Revisit if

- A story appears requiring reconstruction of an instance's state at an
  arbitrary past point. That is event sourcing's home ground and would justify
  reopening this.
- #39 finds that optimistic concurrency over a state row cannot express the
  claiming semantics multi-node execution needs.
