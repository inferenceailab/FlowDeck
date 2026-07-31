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
- **`TestTimeProvider` is hand-rolled**, roughly fifteen lines, rather than
  taking `Microsoft.Extensions.TimeProvider.Testing`.
- **`.NET`, `xUnit` and eventually `EF Core`** are accepted: each provides
  something substantial that reimplementing would be reckless.

## Consequences

- The dependency surface an attacker can reach through is small.
- Some wheels get reinvented. `TestTimeProvider` is one; it costs maintenance
  that a package would have absorbed.
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
