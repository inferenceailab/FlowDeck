# API error contract

Every error response is [RFC 9457][rfc] problem details, served as
`application/problem+json`.

```json
{
  "type": "https://github.com/inferenceailab/FlowDeck/blob/main/docs/api-errors.md#instance-not-found",
  "title": "Workflow instance not found",
  "status": 404,
  "detail": "No workflow instance with id '3f2a…' is known.",
  "instance": "GET /api/instances/3f2a…",
  "traceId": "00-8c1f…-01"
}
```

## Branch on `type`, not on `status`

`status` is too coarse. **Three different problems map to 409**, and a client
that needs to tell "already cancelled" from "another writer won" cannot do it
from the status — and should not be parsing prose out of `detail`.

`type` values are **part of the API contract**. Changing one breaks clients
branching on it, exactly as renaming a field would.

`traceId` correlates the response with server-side logs. Quote it in a bug
report.

## Problems

### definition-not-found

**404.** No workflow definition is registered with that id, or not at the
version requested.

A version typo fails exactly as loudly as an id typo — identity is
`(id, version)`, see [ADR-0001](adr/0001-definition-identity-includes-version.md).
Check `GET /api/workflows` for what this host actually has.

### instance-not-found

**404.** No instance with that id. It may never have existed, or it may have
been purged by retention (#20).

### invalid-state-transition

**409.** The request is well-formed but cannot apply to the state the instance
is in — cancelling something already `Completed`, `Failed` or `Cancelled`.

Terminal states are final, see [ADR-0008](adr/0008-terminal-states-are-final.md).
`detail` names both the current and attempted state, so a dashboard can say
"already completed" rather than "request failed".

**Not retryable.** Re-sending produces the same result.

### invalid-input

**400.** The body does not match the input type the definition declares — wrong
shape, or absent when one is required.

Validation rejects both directions: supplying input to a workflow that declares
none also fails, rather than being silently discarded. See
[ADR-0006](adr/0006-input-type-is-declared.md).

`GET /api/workflows` reports which definitions need input.

### malformed-request

**400.** The body is not valid JSON, or the request could not be parsed at all.

### concurrent-modification

**409.** Another writer modified the instance first.

**Retryable** — re-read the instance and reapply. This is the only 409 that is.

### duplicate-instance

**409.** An instance with that id already exists. Indicates an engine bug rather
than anything a caller did; instance ids are server-generated.

### invalid-definition

**500.** A registered definition is not executable — no steps, or duplicate step
names.

A **server-side deployment fault**, not the caller's mistake, which is why it is
5xx despite being detected during a request. Nothing the caller can change will
help.

## Unrecognised failures

Anything not listed above surfaces as a plain **500** with no `type`.

That is deliberate: inventing a `type` for an unrecognised fault would dress a
bug up as an expected condition, and clients would start branching on something
that means "we do not know".

## Status codes at a glance

| Status | Problems |
| --- | --- |
| 400 | `invalid-input`, `malformed-request` |
| 404 | `definition-not-found`, `instance-not-found` |
| 409 | `invalid-state-transition`, `concurrent-modification`, `duplicate-instance` |
| 500 | `invalid-definition`, anything unrecognised |

The mapping is enforced in one place — `FlowDeckExceptionHandler.StatusCodeFor`
— so this table describes what the code does rather than a copy that drifts.

[rfc]: https://www.rfc-editor.org/rfc/rfc9457
