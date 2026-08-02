# Operating FlowDeck

The actions available when a workflow misbehaves, and what each one costs. For
whoever runs FlowDeck — [Observing FlowDeck](observing-flowdeck.md) is the other
half, and [Defining a workflow](defining-a-workflow.md) is for authors.

Settled by [ADR-0028](../adr/0028-operator-actions.md).

## The actions

| Action | Endpoint | Applies to | Reversible? |
| --- | --- | --- | --- |
| Resume | `POST /api/instances/{id}/resume` | A suspended instance | Yes — it just continues |
| Suspend | `POST /api/instances/{id}/suspend` | A running instance | Yes — resume it |
| Retry | `POST /api/instances/{id}/retry` | A finished instance | n/a — starts something new |
| Retry from the failing step | `POST /api/instances/{id}/retry-from-failed-step` | A failed instance | n/a — starts something new |
| Cancel | `POST /api/instances/{id}/cancel` | Anything in flight | **No** |
| Cancel and roll back | `POST /api/instances/{id}/cancel-and-roll-back` | Anything in flight | **No** |
| Bulk cancel | `POST /api/instances/bulk/cancel` | A filtered set | **No** |
| Bulk retry | `POST /api/instances/bulk/retry` | A filtered set | n/a |

**The two cancels are irreversible.** Terminal states are final
([ADR-0008](../adr/0008-terminal-states-are-final.md)), so a cancelled instance
stays cancelled — the way to continue that work is to retry it, which starts a
new one.

## Retry starts a new instance. The id changes

Neither retry reopens the instance you called it on. The original stays exactly
as it was — same status, same history — and a **new** instance is started, either
from the beginning or from the step that broke.

**So the instance id changes**, and that is the thing to know before you use it.
An alert, a ticket or a bookmark pointing at the failed instance still points at
the failed instance. The new one records `retriedFromInstanceId`, and the API
returns its id, so the chain is walkable — but nothing rewrites the old link.

This is deliberate. "This instance failed" is a fact, and an action that made it
retroactively untrue would rewrite the record you are using to decide what to do.

### Which retry to use

- **From the start** repeats everything. Use it when the completed steps are safe
  to run twice, or when a rollback already undid them.
- **From the failing step** skips what succeeded and carries over the workflow
  data the original had reached. Use it when repeating those steps would charge a
  card twice.

A **rolled-back** instance refuses retry-from-failing-step. Its completed steps
were deliberately undone, so continuing from the failure would run against a
world its workflow data no longer describes — the stock has been released and the
data still says it was reserved. Retry from the start instead.

## Suspend takes effect at the next step boundary

It does **not** stop the step that is running. The step in flight finishes, and
the instance parks before the next one starts.

The engine cannot interrupt a step: step bodies are your code, and FlowDeck
treats them as untrusted ([ADR-0003](../adr/0003-step-executor-trust-boundary.md)).
Stopping "now" would either be a lie or would abandon a step whose side effects
happen anyway — the same reason a failing branch does not abandon its siblings.

So after suspending a busy instance you will see it still `Running` for as long as
its current step takes. That is the request being honoured, not ignored.

## Cancel, or cancel and roll back

Two separate actions, because they are different decisions:

- **Cancel** stops the instance and leaves completed work alone. Use it when you
  are stopping something to fix forward.
- **Cancel and roll back** stops it *and* runs the compensating action of every
  step that completed, most recently first. Use it when the work should not have
  happened.

A rollback is best-effort ([ADR-0021](../adr/0021-compensation-semantics.md)): the
engine tries every action and reports honestly. `CompensationFailed` means some
step could not be undone and its effects are still in place — read the timeline
to find which.

If the workflow declared no compensating actions there is nothing to undo, and
the instance settles as `Cancelled` rather than `Compensated`.

## Bulk actions are best-effort and bounded

A bulk action applies to every instance matching a filter, and it is **not
atomic**. Each instance is attempted independently, and the response reports each
one:

```json
{
  "attempted": 5, "succeeded": 4, "failed": 1, "truncated": false,
  "results": [ { "instanceId": "…", "succeeded": false, "error": "…" } ]
}
```

**Read the per-item report.** The status code is 200 whether every item worked or
only one did; "four of five" is not something a status code can say.

At most 200 instances are attempted in one call. If more matched, `truncated` is
`true` — run it again to continue. That bound exists because an unbounded bulk
action is a denial-of-service vector behind a button.

Bulk retry always retries **from the start**, because it cannot know whether each
instance's completed steps are safe to skip. Use the per-instance route where
that judgement matters.

## What is not here

**Editing workflow data.** The most useful and most dangerous action of this kind,
and deliberately unbuilt: it is arbitrary mutation of state your steps are written
against, with no schema and nothing able to validate it. It stays out until
somebody can say what a safe edit is.

**An audit trail.** FlowDeck does not record *who* performed an action. It cannot:
the API has no authentication (#42), so there is no subject to name. Execution
history records what happened; who did it is not available, and a post-incident
review needs to know that before relying on it.

**A forceful terminate distinct from cancel.** Cancel is the only stop, and it
already does not wait for the running step.

## Known limitations

| Limitation | Tracked by |
| --- | --- |
| No authentication, so no record of who acted | #42 |
| Workflow data cannot be edited | — |
| Bulk actions are capped at 200 per call | — |
| Suspend does not interrupt the running step | — |

## See also

- [ADR-0028](../adr/0028-operator-actions.md) — why each action is shaped this way
- [Observing FlowDeck](observing-flowdeck.md) — what it emits while doing them
- [Defining a workflow](defining-a-workflow.md) — for authors
