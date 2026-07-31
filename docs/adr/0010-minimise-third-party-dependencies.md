# ADR-0010: Minimise third-party dependencies

**Status:** Accepted · **Milestone:** Phase 2 · **Issues:** #44, #8

## Context

This repository enforces an unusually strict supply-chain posture: GitHub
Actions are pinned to full commit SHAs and restricted to GitHub-owned and
verified creators, secret scanning and push protection are on, and every commit
is signed.

That posture is inconsistent if the same project takes casual package
dependencies elsewhere.

## Decision

Take a third-party dependency only when the capability is genuinely substantial.
Specifically:

- **Container build and push uses the `docker` CLI**, not
  `docker/build-push-action`. Zero third-party actions in the pipelines.
- ~~**`TestTimeProvider` is hand-rolled**, roughly fifteen lines, rather than
  taking `Microsoft.Extensions.TimeProvider.Testing`.~~ **Reversed 2026-07-31.**
  Retry (#105) needs controllable *timers*, not just a clock, so `Task.Delay`
  with a `TimeProvider` does not sleep for real. Overriding only `GetUtcNow`
  left `CreateTimer` falling through to the base, and a retry test genuinely
  waited three seconds while its comment claimed otherwise. The package is now
  taken — test-only, never reaching a shipped artefact.

  This is the principle working, not failing: the capability needed grew from
  one thing to two, so the judgement changed. A rule that could not be revisited
  when the facts changed would be dogma.
- **`.NET`, `xUnit` and eventually `EF Core`** are accepted: each provides
  something substantial that reimplementing would be reckless.

## Consequences

- The dependency surface an attacker can reach through is small.
- Some wheels get reinvented, and occasionally that turns out to be wrong.
  `TestTimeProvider` was one: hand-rolling it was defensible when a clock was
  all that was needed, and became indefensible the moment timers were. The cost
  of the mistake was a test that slept and lied about it.
- Pipelines lose conveniences the marketplace actions provide, such as
  buildx layer caching. Acceptable for a homelab deployment; revisit if build
  times become painful.
- Judgement is required per case, so this ADR is a principle rather than a rule.
  "Substantial" is deliberately not defined numerically.

## Alternatives considered

**Use marketplace actions freely.** Faster to write, and each one is arbitrary
code with access to the runner and its tokens. Inconsistent with pinning
everything else to a SHA.

**Forbid all third-party dependencies.** Would mean writing a test framework
and an ORM. Not a serious position.
