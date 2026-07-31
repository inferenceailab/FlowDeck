# HTTP API

The control plane for starting, inspecting and stopping workflow instances.

> **No authentication exists.** Anything that can reach this API can start,
> inspect and cancel workflows. Treat network reachability as the only access
> control until #42 lands, and do not expose it beyond a trusted boundary. This
> is stated first because it is the most important thing about the current API.

## The OpenAPI document is the reference

`GET /openapi/v1.json` describes every endpoint, generated from the code that
serves it. **Prefer it over this page** for exact shapes — a hand-written
reference drifts, and a test asserts the document covers every routed endpoint
so it cannot.

This page covers what a generated document cannot: why things are shaped the way
they are, and what a client should do about each failure.

## Endpoints

| Method | Path | Purpose |
| --- | --- | --- |
| `GET` | `/api/workflows` | List registered definitions |
| `POST` | `/api/workflows/{definitionId}/instances` | Start an instance |
| `GET` | `/api/instances` | List instances, newest first |
| `GET` | `/api/instances/{instanceId}` | Get one instance |
| `POST` | `/api/instances/{instanceId}/cancel` | Stop an instance |
| `GET` | `/api/instances/{instanceId}/history` | Read the step-by-step execution history |
| `GET` | `/health/live` | Liveness probe |
| `GET` | `/health/ready` | Readiness probe |
| `GET` | `/openapi/v1.json` | OpenAPI description |

## Starting an instance

```http
POST /api/workflows/order-fulfilment/instances
Content-Type: application/json

{ "id": 7 }
```

```http
202 Accepted
Location: /api/instances/3f2a…

{ "instanceId": "3f2a…", "status": "Completed" }
```

**202, not 201.** The instance exists, but the work it represents has not
finished — and for a workflow that suspends, may not for days. `201` would claim
the request's effect is complete.

**`status` is where the instance actually reached**, not a placeholder. A short
workflow may already be `Completed`; one that parks is `Suspended`. Do not
assume `Running`.

**Version is optional.** `?version=2` pins; omitting it selects the latest
registered. A client that pinned would need redeploying on every version bump.

**Send a body only if the definition needs one.** `GET /api/workflows` reports
`inputTypeName`. Sending input to a workflow that declares none is a **400**,
not silently ignored — see [ADR-0006](adr/0006-input-type-is-declared.md).

## Listing instances

```http
GET /api/instances?status=Failed&definitionId=order-fulfilment&page=1&pageSize=50
```

```json
{ "items": [ … ], "total": 137, "page": 1, "pageSize": 50 }
```

**`total` ignores paging but honours filters**, so a client can render "page 3
of 12". A count that respected `pageSize` would always equal the page size.

**Paging is one-based**, because it is user-facing.

**`pageSize` is capped at 200 and clamped, not rejected.** An unbounded page
size lets one request pull the whole table. The `pageSize` in the response says
what you actually got.

**A page past the end is empty but still reports `total`**, so an over-paging
client can tell it went too far rather than concluding the data vanished.

## Cancelling

```http
POST /api/instances/3f2a…/cancel
```

**`POST /cancel`, not `DELETE`.** Cancelling removes nothing — the instance
stays queryable, keeps its history and keeps the step it stopped at. Removal is
retention's job (#20).

Cancelling a `Completed`, `Failed` or already-`Cancelled` instance is a **409**.
Terminal states are final — see
[ADR-0008](adr/0008-terminal-states-are-final.md).

## Execution history

```http
GET /api/instances/3f2a…/history
```

```json
[
  { "sequence": 1, "stepName": "validate", "startedAt": "…", "completedAt": "…",
    "durationMs": 12.4, "status": "Success", "attempt": 1,
    "errorType": null, "errorMessage": null },
  { "sequence": 2, "stepName": "charge", "startedAt": "…", "completedAt": "…",
    "durationMs": 840.1, "status": "Failed", "attempt": 1,
    "errorType": "InvalidOperationException", "errorMessage": "card declined" },
  { "sequence": 3, "stepName": "charge", "startedAt": "…", "completedAt": "…",
    "durationMs": 810.7, "status": "Success", "attempt": 2,
    "errorType": null, "errorMessage": null }
]
```

Append-only and in execution order. One entry per step **execution**, so a step
re-entered after a resume appears twice — that is what actually happened, and
collapsing it would misreport the number of attempts.

**`attempt` starts at 1**, including for a step with no retry policy, so a client
rendering "attempt N" never has to special-case zero.

It counts *retries*, not *executions*. A step re-entered after a resume reports
`attempt: 1` again — the step never failed, and numbering it 2 would report a
failure that did not happen. `sequence` still increments, so the two rows remain
distinguishable.

**An unknown instance returns an empty array, not 404.** History removed by
retention is not a client error, and a 404 would make a purged instance look
like a mistake by the caller.

**`durationMs` is computed server-side**, so every client agrees on it rather
than each subtracting timestamps and rounding differently.

**Unpaged.** A workflow with thousands of attempts would return a large array,
which becomes real once retries (#37) exist. No workflow today produces enough
entries to justify the interface, and saying so is better than a silent
surprise.

## Errors

Every failure is RFC 9457 problem details. **Branch on `type`, not `status`** —
three distinct problems map to 409.

Full contract: **[API error contract](api-errors.md)**.

The status-code mapping is enforced in one place,
`FlowDeckExceptionHandler.StatusCodeFor`, so the documentation describes what
the code does rather than a copy that drifts.

## Health probes

| Probe | Checks | Store down |
| --- | --- | --- |
| `/health/live` | the process responds | **200** |
| `/health/ready` | the workflow store is reachable | **503** |

**They are deliberately different.** A node whose database is unreachable is
running correctly and cannot serve: it should leave rotation, not be restarted.
Wiring a restart to liveness during a database outage produces a restart loop
across every node and makes recovery harder.

Neither body exposes exception text — probe endpoints are usually
unauthenticated.

## What this API does not do yet

| Missing | Consequence | Tracked |
| --- | --- | --- |
| **Authentication and authorisation** | Anything reachable can start or cancel workflows | #42 |
| Resume a suspended instance | A suspended workflow cannot be completed over HTTP at all | #68 |
| Retry or re-run | Cancel is the only operator action | #66 |
| Read execution history | The engine records it; nothing exposes it | — |
| Rate limiting | A client can start instances as fast as it can send | — |
| Registering definitions | Definitions are C# classes registered at startup | #40 |

The resume gap is the sharpest: an instance that suspends can only be continued
by code holding the engine object, in-process.

## See also

- [API error contract](api-errors.md)
- [Architecture](architecture.md)
- [Defining a workflow](guides/defining-a-workflow.md)
