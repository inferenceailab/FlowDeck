# ADR-0018: Signals and typed services, no state-management library

**Status:** Accepted · **Milestone:** M4 · **Issue:** #62

## Context

The dashboard needs to hold server state — a list of instances, one instance's
detail, a polling refresh — and reflect it in the UI. Angular projects reach for
NgRx or a similar store almost reflexively.

## Decision

**Angular signals plus typed services. No state-management library.**

- A service per API resource, returning signals.
- Components consume signals directly; no store, no actions, no reducers, no
  effects.
- The API client is **generated from the OpenAPI document** (#28) rather than
  hand-written, so a backend change that breaks the contract breaks the build.

## Rationale

The dashboard's state is almost entirely **server state with a short lifetime**:
fetch a list, show it, refresh it. There is no cross-component shared state
worth centralising, no optimistic updates, no undo, no offline queue — the
problems a store exists to solve.

Adding NgRx would introduce actions, reducers, effects and selectors for what is
currently "call the API, put the result in a signal". Every future contributor
would then pay that indirection for a dashboard with four views.

## Consequences

- Less ceremony. A view is a service call and a signal.
- **No time-travel debugging or centralised action log.** If the dashboard
  grows features where that matters — bulk operations from #66, optimistic
  cancel — this decision should be revisited rather than worked around.
- Signals are framework-native, so no dependency (ADR-0010).
- Testing is direct: call the service, assert on the signal.
- **The risk is drift.** Without a store, nothing structurally prevents state
  logic leaking into components. The mitigation is convention — components
  render, services fetch — and conventions erode. A reviewer should watch for
  `HttpClient` used directly in a component.
- The generated client means the frontend cannot silently diverge from the API.
  It also means **regenerating is a required step** when the API changes, which
  a contributor will forget at least once.

## Alternatives considered

**NgRx.** Mature and well understood. Substantial ceremony for a dashboard with
no cross-cutting state, and the ceremony is paid on every feature.

**NgRx SignalStore.** Lighter, and closer to what signals already do natively —
which is the argument against needing it.

**Plain RxJS with `BehaviorSubject`.** What signals replaced. Would work, and
means manual subscription management and more `async` pipes.

**A hand-written API client.** Avoids a generation step, and guarantees the
frontend and backend drift the moment someone changes a DTO.

## Revisit when

- Bulk operations (#66) introduce genuine cross-view state
- Optimistic updates are wanted, so a change must be tracked before it is
  confirmed
- More than one view needs to react to the same mutation
